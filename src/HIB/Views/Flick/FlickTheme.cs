// H.I.B — the Mac's theme tokens (SPACE.md §6, airsync-mac HIBTheme.swift), local to the Flick card and
// the shelf so these windows don't depend on App.axaml resources. Navy + cyan + lime; lavender only as a
// faint glow.
using Avalonia;
using Avalonia.Media;

namespace HIB.Views.Flick;

public static class FlickTheme
{
    public static readonly Color Ink = Color.FromRgb(0x09, 0x0C, 0x21);
    public static readonly Color Midnight = Color.FromRgb(0x11, 0x15, 0x33);
    public static readonly Color Panel = Color.FromRgb(0x13, 0x18, 0x38);
    public static readonly Color Cyan = Color.FromRgb(0x20, 0xD9, 0xFF);
    public static readonly Color Lime = Color.FromRgb(0xBA, 0xFF, 0x38);
    public static readonly Color Lavender = Color.FromRgb(0x7A, 0x6B, 0xFF);
    public static readonly Color Danger = Color.FromRgb(0xFF, 0x5A, 0x5F);

    public static Color A(Color c, double alpha) => Color.FromArgb((byte)System.Math.Round(255 * System.Math.Clamp(alpha, 0, 1)), c.R, c.G, c.B);

    public static IBrush Solid(Color c, double alpha = 1) => new SolidColorBrush(A(c, alpha));

    public static LinearGradientBrush Linear(RelativePoint start, RelativePoint end, params (Color c, double alpha, double offset)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var (c, a, o) in stops) b.GradientStops.Add(new GradientStop(A(c, a), o));
        return b;
    }

    public static readonly RelativePoint TopLeft = new(0, 0, RelativeUnit.Relative);
    public static readonly RelativePoint BottomRight = new(1, 1, RelativeUnit.Relative);
    public static readonly RelativePoint Top = new(0.5, 0, RelativeUnit.Relative);
    public static readonly RelativePoint Bottom = new(0.5, 1, RelativeUnit.Relative);

    /// <summary>The Mac card face: panel → ink, top to bottom.</summary>
    public static IBrush CardFill() => Linear(Top, Bottom, (Panel, 1, 0), (Ink, 1, 1));

    /// <summary>The card's hairline: white → cyan, brighter while hovered.</summary>
    public static IBrush CardStroke(bool hovered) => Linear(TopLeft, BottomRight, (Colors.White, hovered ? 0.55 : 0.28, 0), (Cyan, 0.25, 1));

    /// <summary>The Mac glass panel (hibPanel): panel 86% → midnight 62%, stroke cyan 30% → white 8% → lavender 18%.</summary>
    public static IBrush GlassFill(double strength = 1) => Linear(TopLeft, BottomRight, (Panel, 0.86 * strength, 0), (Midnight, 0.62 * strength + (1 - strength) * 0.3, 1));
    public static IBrush GlassStroke() => Linear(TopLeft, BottomRight, (Cyan, 0.30, 0), (Colors.White, 0.08, 0.5), (Lavender, 0.18, 1));

    // Glyphs (24×24 Material-style paths).
    public static readonly Geometry Image = Geometry.Parse("M21,19V5C21,3.89 20.1,3 19,3H5A2,2 0 0,0 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19M8.5,13.5L11,16.5L14.5,12L19,18H5L8.5,13.5Z");
    public static readonly Geometry Video = Geometry.Parse("M17,10.5V7A1,1 0 0,0 16,6H4A1,1 0 0,0 3,7V17A1,1 0 0,0 4,18H16A1,1 0 0,0 17,17V13.5L21,17.5V6.5L17,10.5Z");
    public static readonly Geometry File = Geometry.Parse("M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M13,9V3.5L18.5,9H13Z");
    public static readonly Geometry Link = Geometry.Parse("M10.59,13.41C11,13.8 11,14.44 10.59,14.83C10.2,15.22 9.56,15.22 9.17,14.83C7.22,12.88 7.22,9.71 9.17,7.76L12.71,4.22C14.66,2.27 17.83,2.27 19.78,4.22C21.73,6.17 21.73,9.34 19.78,11.29L18.29,12.78C18.3,11.96 18.17,11.14 17.89,10.36L18.36,9.88C19.54,8.71 19.54,6.81 18.36,5.64C17.19,4.46 15.29,4.46 14.12,5.64L10.59,9.17C9.41,10.34 9.41,12.24 10.59,13.41M13.41,9.17C13.8,8.78 14.44,8.78 14.83,9.17C16.78,11.12 16.78,14.29 14.83,16.24L11.29,19.78C9.34,21.73 6.17,21.73 4.22,19.78C2.27,17.83 2.27,14.66 4.22,12.71L5.71,11.22C5.7,12.04 5.83,12.86 6.11,13.65L5.64,14.12C4.46,15.29 4.46,17.19 5.64,18.36C6.81,19.54 8.71,19.54 9.88,18.36L13.41,14.83C14.59,13.66 14.59,11.76 13.41,10.59C13,10.2 13,9.56 13.41,9.17Z");
    public static readonly Geometry Quote = Geometry.Parse("M14,17H17L19,13V7H13V13H16M6,17H9L11,13V7H5V13H8L6,17Z");
    public static readonly Geometry Globe = Geometry.Parse("M16.36,14C16.44,13.34 16.5,12.68 16.5,12C16.5,11.32 16.44,10.66 16.36,10H19.74C19.9,10.64 20,11.31 20,12C20,12.69 19.9,13.36 19.74,14M14.59,19.56C15.19,18.45 15.65,17.25 15.97,16H18.92C17.96,17.65 16.43,18.93 14.59,19.56M14.34,14H9.66C9.56,13.34 9.5,12.68 9.5,12C9.5,11.32 9.56,10.65 9.66,10H14.34C14.43,10.65 14.5,11.32 14.5,12C14.5,12.68 14.43,13.34 14.34,14M12,19.96C11.17,18.76 10.5,17.43 10.09,16H13.91C13.5,17.43 12.83,18.76 12,19.96M8,8H5.08C6.03,6.34 7.57,5.06 9.4,4.44C8.8,5.55 8.35,6.75 8,8M5.08,16H8C8.35,17.25 8.8,18.45 9.4,19.56C7.57,18.93 6.03,17.65 5.08,16M4.26,14C4.1,13.36 4,12.69 4,12C4,11.31 4.1,10.64 4.26,10H7.64C7.56,10.66 7.5,11.32 7.5,12C7.5,12.68 7.56,13.34 7.64,14M12,4.03C12.83,5.23 13.5,6.57 13.91,8H10.09C10.5,6.57 11.17,5.23 12,4.03M18.92,8H15.97C15.65,6.75 15.19,5.55 14.59,4.44C16.43,5.07 17.96,6.34 18.92,8M12,2C6.47,2 2,6.5 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2Z");
    public static readonly Geometry CheckCircle = Geometry.Parse("M12 2C6.5 2 2 6.5 2 12S6.5 22 12 22 22 17.5 22 12 17.5 2 12 2M10 17L5 12L6.41 10.59L10 14.17L17.59 6.58L19 8L10 17Z");
    public static readonly Geometry Check = Geometry.Parse("M10 17L5 12L6.41 10.59L10 14.17L17.59 6.58L19 8L10 17Z");
    public static readonly Geometry Alert = Geometry.Parse("M13,14H11V10H13M13,18H11V16H13M1,21H23L12,2L1,21Z");
    public static readonly Geometry PlayCircle = Geometry.Parse("M10,16.5V7.5L16,12M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2Z");
    public static readonly Geometry Phone = Geometry.Parse("M17,19H7V5H17M17,1H7C5.89,1 5,1.89 5,3V21A2,2 0 0,0 7,23H17A2,2 0 0,0 19,21V3C19,1.89 18.1,1 17,1Z");
    public static readonly Geometry Laptop = Geometry.Parse("M4,6H20V16H4M20,18A2,2 0 0,0 22,16V6C22,4.89 21.1,4 20,4H4C2.89,4 2,4.89 2,6V16A2,2 0 0,0 4,18H0V20H24V18H20Z");
    public static readonly Geometry Monitor = Geometry.Parse("M21,16H3V4H21M21,2H3C1.89,2 1,2.89 1,4V16A2,2 0 0,0 3,18H10V20H8V22H16V20H14V18H21A2,2 0 0,0 23,16V4C23,2.89 22.1,2 21,2Z");

    public static readonly Geometry Folder = Geometry.Parse("M10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6H12L10,4Z");
    /// <summary>Three cards stacked (a bundle).</summary>
    public static readonly Geometry Stack = Geometry.Parse("M4,7H2V20A2,2 0 0,0 4,22H17V20H4V7M20,2H8A2,2 0 0,0 6,4V16A2,2 0 0,0 8,18H20A2,2 0 0,0 22,16V4A2,2 0 0,0 20,2M20,16H8V4H20V16Z");
    /// <summary>Arrows pointing in (Gather).</summary>
    public static readonly Geometry Gather = Geometry.Parse("M19.5,3.09L15,7.59V4H13V11H20V9H16.41L20.91,4.5L19.5,3.09M4,13V15H7.59L3.09,19.5L4.5,20.91L9,16.41V20H11V13H4Z");

    public static Geometry KindGlyph(string kind) => kind switch
    {
        "stack" => Stack,
        "more" => Folder,
        "image" => Image,
        "video" => Video,
        "link" => Link,
        "text" => Quote,
        _ => File,
    };

    public static Geometry DeviceGlyph(HIB.Core.Space.SpaceDeviceKind kind) => kind switch
    {
        HIB.Core.Space.SpaceDeviceKind.Phone => Phone,
        HIB.Core.Space.SpaceDeviceKind.Windows => Monitor,
        _ => Laptop,
    };
}
