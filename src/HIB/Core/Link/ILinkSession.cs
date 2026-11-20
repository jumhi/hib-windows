// H.I.B for Windows — public link contract used by feature modules (ADB, mirroring, ring, Find My, Phone Agent).
// Made by Haitham Hamid.
//
// This file intentionally depends only on the BCL so it can be moved into a shared contracts
// assembly unchanged if HIB.Features needs to reference it without referencing the app.
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HIB.Core.Link;

/// <summary>
/// The live link to the paired phone. One instance per app; it outlives individual
/// WebSocket connections (it stays valid while the phone is disconnected).
/// </summary>
/// <remarks>
/// Events are raised on background (socket) threads. UI code must marshal to the UI thread.
/// </remarks>
public interface ILinkSession
{
    /// <summary>True once the primary phone connection has completed the device → macInfo handshake.</summary>
    bool IsConnected { get; }

    /// <summary>The phone currently (or most recently) connected; null if no phone has connected this run.</summary>
    PhoneDeviceInfo? Device { get; }

    /// <summary>The stable desktop name announced everywhere (QR, macInfo, mDNS, UDP, wake-up).</summary>
    string DesktopName { get; }

    /// <summary>The raw 32-byte AES-256-GCM pairing key (Find My derives its relay key from this). Returns a copy.</summary>
    byte[] GetPairingKey();

    /// <summary>The WebSocket server port (default 6996).</summary>
    int Port { get; }

    /// <summary>
    /// Raised for every decrypted message from the primary phone session, after the link layer
    /// handled it (device/ping/pong are handled internally but still raised, except pong).
    /// </summary>
    event EventHandler<LinkMessageEventArgs>? MessageReceived;

    /// <summary>Raised when <see cref="IsConnected"/> changes.</summary>
    event EventHandler<bool>? ConnectionChanged;

    /// <summary>Raised when <see cref="Device"/> changes (new device message, battery update…).</summary>
    event EventHandler<PhoneDeviceInfo>? DeviceUpdated;

    /// <summary>
    /// Encrypts and sends <c>{"type": type, "data": data}</c> to the primary phone session.
    /// <paramref name="data"/> may be an anonymous object, a dictionary, a JsonElement/JsonNode, or null ({}).
    /// Null-valued properties are omitted. Returns false when no phone is connected or the send failed.
    /// </summary>
    Task<bool> SendAsync(string type, object? data = null, CancellationToken cancellationToken = default);
}

/// <summary>A message received from the phone.</summary>
public sealed class LinkMessageEventArgs : EventArgs
{
    public LinkMessageEventArgs(string type, JsonElement data)
    {
        Type = type;
        Data = data;
    }

    /// <summary>The envelope "type", e.g. "notification".</summary>
    public string Type { get; }

    /// <summary>The envelope "data" object (a cloned element, safe to keep).</summary>
    public JsonElement Data { get; }
}

/// <summary>What the desktop knows about the connected phone.</summary>
public sealed record PhoneDeviceInfo
{
    /// <summary>Settings.Secure.ANDROID_ID (may be empty on old builds).</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>The phone's own Wi-Fi IP as it reported it.</summary>
    public string IpAddress { get; init; } = "";
    /// <summary>The remote IP of the WebSocket connection as seen by the desktop.</summary>
    public string RemoteAddress { get; init; } = "";
    public int Port { get; init; }
    /// <summary>Android app versionName (default "2.0.0" when missing).</summary>
    public string Version { get; init; } = "2.0.0";
    /// <summary>Wireless-debugging ports seen by the phone (for "adb connect ip:port").</summary>
    public IReadOnlyList<string> AdbPorts { get; init; } = Array.Empty<string>();
    /// <summary>The desktop IP the phone actually reached.</summary>
    public string? TargetIpAddress { get; init; }
    /// <summary>Validated BLE auth token (base64 of 16 bytes) or null.</summary>
    public string? BleAuthToken { get; init; }
    /// <summary>Wallpaper JPEG bytes, if the full device message was received.</summary>
    public byte[]? Wallpaper { get; init; }
    /// <summary>Battery percent, or -1 if unknown.</summary>
    public int BatteryLevel { get; init; } = -1;
    public bool IsCharging { get; init; }
    public DateTimeOffset ConnectedAt { get; init; } = DateTimeOffset.Now;
}
