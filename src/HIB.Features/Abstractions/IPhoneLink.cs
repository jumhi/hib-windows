using System.Text.Json;

namespace HIB.Features.Abstractions;

/// <summary>
/// The encrypted WebSocket link to the H.I.B phone app, as seen by the feature services.
/// The app adapts its session object to this interface; nothing in HIB.Features talks to
/// the socket directly.
/// </summary>
public interface IPhoneLink
{
    /// <summary>True while a phone has a live, authenticated (primary) session.
    /// This is the Mac's <c>WebSocketServer.hasPrimarySession</c>.</summary>
    bool IsConnected { get; }

    /// <summary>The connected phone's name from its <c>device</c> message, if any.</summary>
    string? DeviceName { get; }

    /// <summary>The raw 32-byte AES pairing key from the QR code (null before one exists).</summary>
    byte[]? PairingKey { get; }

    /// <summary>Sends <c>{"type": type, "data": data}</c> over the encrypted link.
    /// <paramref name="data"/> is serialized with System.Text.Json (a JsonNode, dictionary or
    /// anonymous object all work).</summary>
    Task SendAsync(string type, object data);

    /// <summary>Raised for every decrypted phone→desktop message: (type, data).</summary>
    event Action<string, JsonElement>? MessageReceived;

    /// <summary>Raised when <see cref="IsConnected"/> changes.</summary>
    event Action<bool>? ConnectionChanged;
}
