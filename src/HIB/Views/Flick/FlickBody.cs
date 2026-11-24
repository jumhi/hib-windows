// H.I.B — the Flick card's physics: a port of the Mac's damped-spring FlickBody (FlickCardModel.swift),
// in screen coordinates (DIPs, y down). The card rises from the bottom edge (like the Mac), overshoots a little and
// settles with a spring; tilt leans into the arc; dismissal shrinks it toward where the file lives.
using System;

namespace HIB.Views.Flick;

public enum FlickBodyMode { Flying, Landed, Dismissing, Flung }

public struct FlickBody
{
    public double X, Y;          // card centre
    public double Vx, Vy;
    public double Rot;           // degrees, clockwise
    public double RotVel;
    public double TargetX, TargetY, TargetRot;
    public double Scale;
    public double ScaleVel;
    /// <summary>The size the spring settles at; 0 means full size (a fanned bundle's cards may be smaller).</summary>
    public double TargetScale;
    public double Alpha;
    public FlickBodyMode Mode;

    public double DismissFromX, DismissFromY, DismissToX, DismissToY, DismissT;

    // Same tuning as the Mac: a 1.5–4k pt/s launch overshoots by ~50–80 pt and settles in under a second.
    public const double Stiffness = 130;
    public static readonly double Damping = 2 * 0.56 * Math.Sqrt(130);
    public const double RotStiffness = 95;
    public static readonly double RotDamping = 2 * 0.42 * Math.Sqrt(95);

    /// <summary>
    /// A body rising from the bottom edge of an area <paramref name="areaWidth"/> × <paramref name="areaHeight"/>
    /// (DIPs, area-relative), exactly like the Mac's card:
    /// <paramref name="exitXRatio"/>, <paramref name="velocity"/> and <paramref name="angle"/> come from the
    /// sender's flickStart (angle: 0 = straight, negative = left, positive = right).
    /// </summary>
    public static FlickBody Launch(double areaWidth, double areaHeight, double cardHeight, double exitXRatio, double velocity, double angle,
        double targetX, double targetY, int slot)
    {
        var ratio = Math.Clamp(exitXRatio, 0.05, 0.95);
        var a = Math.Clamp(angle, -35, 35);
        var speed = Math.Clamp(Math.Abs(velocity) * 0.9, 1400, 4200);
        var rad = a * Math.PI / 180;
        var residual = Math.Clamp(a * 0.12, -4, 4) - (slot % 3 - 1) * 1.6;
        return new FlickBody
        {
            X = areaWidth * ratio,
            Y = areaHeight + cardHeight * 0.55,
            Vx = speed * Math.Sin(rad),
            Vy = -speed * Math.Cos(rad),
            Rot = a,
            RotVel = speed * Math.Sin(rad) * 0.02,
            TargetX = targetX,
            TargetY = targetY,
            TargetRot = residual,
            Scale = 0.92,
            Alpha = 1,
            Mode = FlickBodyMode.Flying,
        };
    }

    /// <summary>
    /// A card that appears where a file was dropped (Roam, SPACE.md §9.5): it starts at the point, a little small, and
    /// springs to full size with a slight tilt — no flight.
    /// </summary>
    public static FlickBody DropAt(double x, double y, int slot) => new()
    {
        X = x,
        Y = y,
        TargetX = x,
        TargetY = y,
        Rot = 0,
        TargetRot = -(slot % 3 - 1) * 1.6,
        Scale = 0.78,
        Alpha = 1,
        Mode = FlickBodyMode.Flying,
    };

    /// <summary>Advances by <paramref name="dt"/> seconds; returns true while still moving.</summary>
    public bool Step(double dt)
    {
        switch (Mode)
        {
            case FlickBodyMode.Landed:
                return false;
            case FlickBodyMode.Flying:
            {
                var ax = -Stiffness * (X - TargetX) - Damping * Vx;
                var ay = -Stiffness * (Y - TargetY) - Damping * Vy;
                Vx += ax * dt;
                Vy += ay * dt;
                X += Vx * dt;
                Y += Vy * dt;
                // Sideways acceleration nudges the tilt so the card leans into its arc.
                var ra = -RotStiffness * (Rot - TargetRot) - RotDamping * RotVel + ax * 0.0025;
                RotVel += ra * dt;
                Rot += RotVel * dt;
                var ts = TargetScale > 0 ? TargetScale : 1;
                var sa = -220 * (Scale - ts) - 2 * 0.5 * Math.Sqrt(220) * ScaleVel;
                ScaleVel += sa * dt;
                Scale += ScaleVel * dt;
                var settled = Math.Abs(X - TargetX) < 0.6 && Math.Abs(Y - TargetY) < 0.6
                              && Math.Sqrt(Vx * Vx + Vy * Vy) < 9
                              && Math.Abs(Rot - TargetRot) < 0.25 && Math.Abs(RotVel) < 2
                              && Math.Abs(Scale - ts) < 0.004;
                if (settled)
                {
                    X = TargetX; Y = TargetY; Vx = Vy = 0; Rot = TargetRot; RotVel = 0; Scale = ts; ScaleVel = 0;
                    Mode = FlickBodyMode.Landed;
                    return false;
                }
                return true;
            }
            case FlickBodyMode.Dismissing:
            {
                DismissT = Math.Min(1, DismissT + dt / 0.42);
                var e = DismissT * DismissT * DismissT; // ease-in cubic
                X = DismissFromX + (DismissToX - DismissFromX) * e;
                Y = DismissFromY + (DismissToY - DismissFromY) * e;
                Scale = (TargetScale > 0 ? TargetScale : 1) * (1 - 0.82 * e);
                Alpha = 1 - DismissT * DismissT;
                return DismissT < 1;
            }
            case FlickBodyMode.Flung:
                X += Vx * dt;
                Y += Vy * dt;
                var f = Math.Pow(0.9, dt * 60);
                Vx *= f;
                Vy *= f;
                Rot += RotVel * dt;
                Alpha -= dt * 3;
                return Alpha > 0;
        }
        return false;
    }

    /// <summary>Integrates a frame in ≤ 1/240 s sub-steps (survives hitches without exploding).</summary>
    public bool Advance(double frameSeconds)
    {
        var dt = Math.Clamp(frameSeconds, 1.0 / 240, 1.0 / 30);
        var steps = Math.Max(1, (int)Math.Ceiling(dt / (1.0 / 240)));
        var h = dt / steps;
        var moving = false;
        for (var i = 0; i < steps; i++) moving = Step(h);
        return moving;
    }
}
