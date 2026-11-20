using System.Globalization;
using System.Text.Json;

namespace HIB.Features.Adb;

/// <summary>
/// What the phone reports about its own ADB setup (docs/SPACE.md §8.1 <c>adbSetupState</c>): the two settings,
/// the Wireless debugging port (<c>_adb-tls-connect._tcp</c>) and, while a pairing screen is open on the phone,
/// its pairing service (<c>_adb-tls-pairing._tcp</c>). The phone can always see its own services, so this
/// works across subnets and on networks that block mDNS.
/// </summary>
public sealed record AdbSetupState(bool DeveloperOptions, bool WirelessDebugging, int? ConnectPort, int? PairingPort, string? PairingName)
{
    /// <summary>Parses the message's <c>data</c>. Tolerant: "true"/1 for booleans, "42789" for ports; 0 → no port.</summary>
    public static AdbSetupState? Parse(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object) return null;
        var name = Str(d, "pairingName");
        return new AdbSetupState(Bool(d, "developerOptions") ?? false, Bool(d, "wirelessDebugging") ?? false,
            Port(d, "connectPort"), Port(d, "pairingPort"), string.IsNullOrWhiteSpace(name) ? null : name.Trim());
    }

    private static bool TryGet(JsonElement d, string name, out JsonElement v)
    {
        foreach (var p in d.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { v = p.Value; return true; }
        v = default;
        return false;
    }

    private static string? Str(JsonElement d, string name)
        => TryGet(d, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement d, string name)
    {
        if (!TryGet(d, name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n != 0 : null,
            JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() switch { "true" or "1" or "yes" => true, "false" or "0" or "no" => false, _ => null },
            _ => null,
        };
    }

    private static int? Port(JsonElement d, string name)
    {
        if (!TryGet(d, name, out var v)) return null;
        long n;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out n)) { }
        else if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString()?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) { }
        else return null;
        return n is > 0 and <= 65535 ? (int)n : null;
    }
}

/// <summary>What the "Set up mirroring" card shows (docs/SPACE.md §8.3).</summary>
public enum AdbSetupStep
{
    /// <summary>The phone isn't connected to H.I.B: nothing to ask it.</summary>
    Phone,
    /// <summary>Waiting for the phone's first <c>adbSetupState</c>.</summary>
    Checking,
    DeveloperOptions,
    WirelessDebugging,
    /// <summary>Pair this computer (QR, or a pairing code).</summary>
    Pair,
    /// <summary>Pairing or connecting right now.</summary>
    Connecting,
    /// <summary>ADB is connected: "Mirroring is ready".</summary>
    Ready,
}

/// <summary>The card's decision table, kept apart from the UI so it can be tested.</summary>
public static class AdbSetupFlow
{
    /// <param name="phoneConnected">The phone is linked to H.I.B.</param>
    /// <param name="adbConnected">ADB talks to the phone.</param>
    /// <param name="state">The phone's last <c>adbSetupState</c>, or null.</param>
    /// <param name="oldPhoneApp">The phone never sent <c>adbSetupState</c> (an older app): go straight to the QR, found by mDNS.</param>
    /// <param name="busy">An <c>adb pair</c> or <c>adb connect</c> is running (not just waiting for a scan).</param>
    /// <param name="needsPairing">A connect to the phone's port was refused: this computer isn't paired yet.</param>
    public static AdbSetupStep StepFor(bool phoneConnected, bool adbConnected, AdbSetupState? state, bool oldPhoneApp, bool busy, bool needsPairing)
    {
        if (adbConnected) return AdbSetupStep.Ready;
        if (!phoneConnected) return AdbSetupStep.Phone;
        if (busy) return AdbSetupStep.Connecting;
        if (state is null) return oldPhoneApp ? AdbSetupStep.Pair : AdbSetupStep.Checking;
        if (!state.DeveloperOptions) return AdbSetupStep.DeveloperOptions;
        if (!state.WirelessDebugging) return AdbSetupStep.WirelessDebugging;
        // The phone has a pairing screen open, or a connect said "not paired": show the QR / code.
        if (needsPairing || state.PairingPort is not null) return AdbSetupStep.Pair;
        return AdbSetupStep.Connecting;
    }

    /// <summary>Step 1 done (Developer options on).</summary>
    public static bool DeveloperDone(AdbSetupState? s, bool adbConnected) => adbConnected || s?.DeveloperOptions == true;
    /// <summary>Step 2 done (Wireless debugging on).</summary>
    public static bool WirelessDone(AdbSetupState? s, bool adbConnected) => adbConnected || s is { DeveloperOptions: true, WirelessDebugging: true };

    /// <summary>adb's answer means "this computer isn't paired with the phone's Wireless debugging".</summary>
    public static bool MeansNotPaired(string? output)
    {
        if (string.IsNullOrEmpty(output)) return false;
        var o = output.ToLowerInvariant();
        return o.Contains("failed to authenticate") || o.Contains("unauthorized") || o.Contains("connection refused")
               || o.Contains("not paired") || o.Contains("authentication");
    }

    /// <summary>
    /// Ports for <c>adb connect phoneIp:port</c>: the phone's own <c>connectPort</c> when it reports one (the truth,
    /// fresher than the device message's <c>adbPorts</c>); none while it says Wireless debugging is off; the
    /// <c>adbPorts</c> otherwise (an older phone app).
    /// </summary>
    public static IReadOnlyList<string> PortsToTry(AdbSetupState? state, IReadOnlyList<string> adbPorts)
    {
        if (state is null) return adbPorts;
        if (!state.WirelessDebugging) return [];
        return state.ConnectPort is int p ? [p.ToString(CultureInfo.InvariantCulture)] : adbPorts;
    }

    /// <summary>The QR's name came back from the phone (NSD may add " (2)" on a clash).</summary>
    public static bool SameServiceName(string? reported, string ours)
        => !string.IsNullOrEmpty(reported) && !string.IsNullOrEmpty(ours)
           && (reported.Contains(ours, StringComparison.Ordinal) || ours.Contains(reported, StringComparison.Ordinal));
}
