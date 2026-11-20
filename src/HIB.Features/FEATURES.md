# HIB.Features

The ADB, mirroring, Find My, Phone Agent and other "power features" of **H.I.B for Windows**,
ported from the Mac app (by Haitham Hamid). This is a plain `net11.0` class library with no UI
dependency. It builds and its tests run on macOS too. Windows-only calls (DPAPI) are guarded with
`OperatingSystem.IsWindows()`.

Every service is a plain C# class. State is exposed through `INotifyPropertyChanged` properties
and .NET events, so Avalonia view models can bind to it directly.

## 1. Wiring it into the app

```csharp
using HIB.Features;
using HIB.Features.Abstractions;

// Once at startup:
HibFeatures.UiDispatcher = a => Avalonia.Threading.Dispatcher.UIThread.Post(a); // marshal PropertyChanged/events
HibFeatures.Log = s => Debug.WriteLine(s);                                        // never receives keys/secrets
var settings = new JsonFileSettingsStore();          // %APPDATA%\H.I.B\settings.json (same keys as the Mac)
IPhoneLink link = new SessionPhoneLink(session);     // adapter over the app's WebSocket session, see §2

var adb       = new AdbService(AdbLocator.Shared, settings);
var pairing   = new AdbPairingService(adb);
var appOps    = new AppOpsGranter(adb);      appOps.Start();
var mirror    = new MirrorService(adb, settings);
var audio     = new PhoneAudioBridge(adb, link, settings);
var findMy    = new FindMyService(link, settings, adb); findMy.Start();
var ring      = new RingService(link, adb, findMy);
var agent     = new PhoneAgentController(adb, link, settings);   // ApiKeyStore defaults to %APPDATA%\H.I.B\keys
var search    = new PhoneSearchClient(link, adb);
var scenes    = new ScenesBridge(link);
var codes     = new LoginCodeAutoCopier(settings, new WindowsLoginCodeClipboard(), link);
```

Each service subscribes to `link.MessageReceived` and handles only its own message types. The
app's session code does not need to route messages to them.

App responsibilities that stay in the app:
- On every `device` message (Plus), call `adb.ConnectAsync(device.ipAddress, device.adbPorts)`.
- When the WebSocket drops, call `adb.DisconnectAsync()`.
- Optionally run a guardian: if ADB is enabled but not connected and `!adb.UserRequestedDisconnect`,
  send `refreshAdbPorts`, then after 1.5 s call `ConnectAsync(..., silent: true)` (PROTOCOL §7.1).
- Forward phone→PC file transfers whose id is tracked by `PhoneFetchRegistry` (§5).
- Show UI for `adb.ConnectionFailed` (offer "Set up mirroring"), `mirror.MirrorExited`
  (`NeedsPairing`), `codes.CodeCopied` (HUD), and `agent.AttentionRequested`.

## 2. `IPhoneLink` (Abstractions/IPhoneLink.cs)

```csharp
public interface IPhoneLink {
    bool IsConnected { get; }            // = Mac's hasPrimarySession
    string? DeviceName { get; }          // from the phone's `device` message
    byte[]? PairingKey { get; }          // raw 32-byte AES key from the QR
    Task SendAsync(string type, object data);              // {"type","data"} over the encrypted WS
    event Action<string, JsonElement>? MessageReceived;    // every decrypted P→D message
    event Action<bool>? ConnectionChanged;
}
```

`data` passed to `SendAsync` is always a `System.Text.Json.Nodes.JsonObject`. Serialize it with
`JsonSerializer.Serialize(data)` or `((JsonNode)data).ToJsonString()`.

`ISettingsStore` (Get/Set/Contains/Remove) is the UserDefaults equivalent. There are two
implementations: `JsonFileSettingsStore` (atomic JSON file) and `InMemorySettingsStore` (tests).

## 3. ADB

### AdbLocator
`AdbPath` and `ScrcpyPath` are found in this order, then cached. Call `Refresh()` to search again.
1. `ExtraDirectories`, then `BundledToolsDirectory` (default `<app dir>\tools`, also `tools\adb\`, `tools\scrcpy\`).
2. `PATH`.
3. Well-known install locations:
   - Windows: `%ANDROID_HOME%`, `%ANDROID_SDK_ROOT%`, `%LOCALAPPDATA%\Android\Sdk\platform-tools`,
     winget (`%LOCALAPPDATA%\Microsoft\WinGet\Packages\Genymobile.scrcpy*\scrcpy-win64-*`, which
     also ships an `adb.exe`), scoop shims, Chocolatey, `Program Files\scrcpy`, `C:\platform-tools`.
   - macOS: `~/Library/Android/sdk/platform-tools`, `/opt/homebrew/bin`, `/usr/local/bin`.
   - Linux: the usual system paths.

`AdbLocator.AdbEnvironment(adbPath, scrcpyPath)` returns `PATH=<adbDir>;…` and `ADB=<adbPath>`. Use it
when launching scrcpy so scrcpy uses the same adb server. On Windows scrcpy runs `%ADB%` unquoted, so a
path with a space is replaced by its 8.3 short name (or, without one, `ADB` is left unset and scrcpy
uses the adb.exe in its own folder).

### AdbService
Bindable properties: `IsConnected`, `IsConnecting`, `ConnectedIp`, `Port`, `Mode`, `ConsoleLog`,
`LastResult`.

Preferences: `WiredAdbEnabled`, `SelectedWiredSerial`, `FallbackToMdns` (default on),
`AlwaysKillAdbBeforeConnect`.

`CurrentSerial` returns the wired serial when wired mode is on and a serial is selected.
Otherwise it returns `ip:port` while connected, or null.

| Method | What it does |
|---|---|
| `ConnectAsync(ip, ports, killServer, silent)` | Tries `adb connect ip:port` for each reported port. A port succeeds when the output contains "connected to". With no ports and `FallbackToMdns` on, it reads ports from `adb mdns services` lines that contain the IP. Failures map to the Mac's hints. For a non-silent connect it raises `ConnectionFailed`. Concurrent calls are ignored. |
| `DisconnectAsync(userInitiated)` | Runs `adb disconnect ip:port`. |
| `GetWiredDevicesAsync()` / `ResolveWiredSerialAsync()` | Parses `adb devices -l`. Skips `ip:port` and mDNS serials. |
| `MdnsServicesAsync()` | Parses `adb mdns services`. |
| `RunAsync(args)` / `RunOnDeviceAsync(args)` / `ShellAsync(...)` | Raw adb access. No console window; arguments are quoted correctly. |
| `ScreenshotAsync()` | Runs `exec-out screencap -p`. Returns null unless the result is a real PNG. |
| `PullAsync` / `PushAsync` | Runs `adb pull` / `adb push`. |
| `LockPhoneAsync()` | Runs `input keyevent KEYCODE_SLEEP` (the Find My lock fallback). |
| `SendRingBroadcastAsync(start)` | Sends `am broadcast -a com.sameerasw.airsync.(STOP_)RING_PHONE -n …/.service.RingCommandReceiver`. |

### AdbPairingService and AdbSetupSession ("Set up mirroring", docs/SPACE.md §8)
The phone reports its own ADB setup in `adbSetupState` (Developer options, Wireless debugging,
`connectPort`, and `pairingPort`/`pairingName` while a pairing screen is open). `PhoneHub` parses it
per link (`AdbSetup`, `AdbSetupChanged`); `FeatureHost` passes it to `AdbPairingService.UpdateSetupState`
and sets `PhoneIp`. `PhoneHub.RequestAdbSetupAsync(open, watch)` sends `adbSetupRequest`.

- **QR:** `StartPairingAsync()` creates `adb-wireless-<6 digits>` + an 8-digit password and exposes
  `QrText = WIFI:T:ADB;S:<name>;P:<pw>;;` at once. When `adbSetupState.pairingName` matches the name it
  runs `adb pair <phoneIp>:<pairingPort> <pw>` immediately (no mDNS), then `adb connect <phoneIp>:<connectPort>`.
- **Pairing code:** `PairWithCodeAsync("123456")` takes the port from `pairingPort`; the user only types
  the 6 digits.
- `adb mdns services` is only used when the phone never sent `adbSetupState` (older phone app).
- `AdbSetupFlow.StepFor(...)` decides the card's step; `AdbSetupFlow.PortsToTry` makes every connect use
  `connectPort` over stale `adbPorts` (and no mDNS while the phone says Wireless debugging is off).
- `AdbSetupSession` drives the card: `watch` while open, connect as soon as Wireless debugging is on,
  the QR when that says "not paired", pair the moment the phone reports our QR, connect after pairing.
- Retries `adb pair`/`adb connect` up to 3 times with `kill-server` on protocol or daemon faults. The
  pairing code is never written to the ADB console or the log.

### AppOpsGranter
`Start()`: 2 s after every ADB connect, runs
`adb shell appops set com.sameerasw.airsync READ_CLIPBOARD allow`.

## 4. Mirroring and audio

### MirrorService (scrcpy windows)
`StartMirrorAsync(name)`, `StartDesktopAsync(name)`, `StartAppAsync(name, package, label)` (one
app in its own window: `--new-display=/<dpi> --start-app=<pkg> --no-vd-system-decorations -x`),
and `StartCameraAsync(name, facing)`.

Arguments are identical to the Mac's `startScrcpy`:
- Common: title, `--video-bit-rate`, `--video-codec=h265`, `--no-power-on`.
- Target: `--serial=` (wired) or `--tcpip=ip:port`.
- Optional: position, on-top, stay-awake, screen-off, no-audio, `--keyboard=uhid`, `--max-size`.

`MirrorSettings` uses the Mac's preference keys (`scrcpyBitrate` = 4, `scrcpyResolution` = 1200,
`scrcpyDesktopDpi` = "192", and so on).

`MirrorExited` reports the exit code, the output, `NeedsPairing`, and — when `IsProblem` — a `Title`
and the real `Reason` (scrcpy's last specific `ERROR:` line, see `ScrcpyOutput.ExtractError`).
`MirrorWindowOpened` fires when the window is on screen (scrcpy 5 logs `INFO: Texture: WxH` with the
first frame; on Windows a visible window handle also counts). `MirrorSlowToOpen` fires once when scrcpy
still runs 12 s after start without a window (`MirrorWatchdog`). On Windows H.I.B calls
`AllowSetForegroundWindow(ASFW_ANY)` before starting scrcpy and brings its window to the front (and
onto a monitor) when it appears; a saved `--window-x/y` is clamped to a monitor that exists.
`Sessions` lists the running windows; stop them with `Stop(session)` or `StopAll()`.

Everything (ADB console, scrcpy command lines and output, `HibFeatures.Trace`) also goes to
`HibLog` — `%LOCALAPPDATA%\H.I.B\logs\hib.log`, ≤ 2 MB with one rotation, scrubbed by `LogScrubber`.

scrcpy mirrors through ADB, so Android never shows its "share your screen" prompt.
`MirrorService.BuildArguments(...)` is public, so the ADB console can show the exact command.

### PhoneAudioBridge (Play Phone Audio on this PC)
Runs `scrcpy -s <serial> --no-video --no-control --no-window --audio-source=playback --audio-codec=opus --audio-buffer=80 [--audio-dup]`.
- `KeepPlayingOnPhone` adds `--audio-dup`.
- `IsActive` turns on when the output mentions "Device:" or "audio", or when the process is still
  running after 2.5 s.
- `phoneAudioState {active, error?}` is sent to the phone whenever the state changes.
- The phone's `phoneAudioControl {action: start|stop|toggle}` is handled automatically.
- The bridge stops when ADB drops.
- On a crash, `LastError` is the last `ERROR` line from scrcpy.

## 5. Phone features over the link

### RingService
`Start()`, `Stop()` and `Toggle()` choose a route in this order:
1. The link (`ringPhone {action}`).
2. The ADB broadcast, when the link is down.
3. The Find My relay (`findRing`), when there is no link and no ADB.

`IsRinging` follows `ringState` and `findRingState` from the phone and has a 125 s safety timeout.

One deliberate fix: when nothing is reachable, the Mac sets `lastError` but still flips the button
to ringing. This port does not flip it.

### FindMyService and FindMyRelay (§6.12 of PROTOCOL.md)
- **Keys:** `FindMyRelayKeys.Derive(pairingKey)` uses HKDF-SHA256 with the pairing key as IKM, an
  empty salt, and L=32.
  - Topic: `hib-` followed by the first 32 hex characters of HKDF(info `hib-findmy-topic-v1`).
  - Relay key: HKDF(info `hib-findmy-key-v1`).
  - `MarkPaired` runs 2 s after the phone connects. `Current` returns null after a pairing-key
    reset until the phone reconnects.
- **Envelope:** `FindMyCodec.Seal/Open` use AES-256-GCM, written as `base64(nonce12‖ct‖tag16)`.
  - Plaintext: `{"v":1,type,data,ts,nonce(16B hex),from:"mac"}`.
  - `Open` checks that `v == 1`, `type` is present, `nonce` is non-empty, and `|now-ts| ≤ 5 min`.
  - The relay then accepts only `from == "phone"` and drops repeats through a persisted LRU of
    200 nonces.
- **Relay:**
  - `PublishAsync` sends `POST {server}/{topic}` with `text/plain`.
  - `EnsureSubscribed()` streams `GET …/json?since=2m` with a 90 s idle timeout and 2→300 s backoff.
  - The server comes from `FindMySettings` (`hibFindMyRelayServer`, default `https://ntfy.sh`).
- **Service:**
  - Actions: `Locate()`, `Lock()` (falls back to ADB `KEYCODE_SLEEP`), `ShowMessage(text≤500)`,
    `RingViaRelay(start)`, `TestRelay()` (ping/pong round trip).
  - `findRelayConfig` is pushed on connect.
  - `LastLocation` is persisted.
  - Routing follows the Mac: use the link when connected, otherwise the relay. Replies go back the
    way the request came.
  - Find-my-PC: handle `DesktopRingRequested(bool)` (play an alarm, then call
    `ReportDesktopRingState`). Optionally set `DesktopLocationProvider` (for example with
    `Windows.Devices.Geolocation`); without it, the PC answers `provider:"none"`.

### PhoneSearchClient
`Search(query, handler, kinds, limit)` sends `phoneSearch`. The handler receives result batches
from `phoneSearchResults`. Searches give up after 6 s.

`FetchAsync(result)` downloads into `~/Downloads/H.I.B`:
- For absolute paths, it uses `adb pull` when ADB is connected.
- Otherwise it sends `phoneFetchFile {id:"hibfetch-…", path}`. The phone streams the file back
  through the normal WebSocket file transfer, using the same id.
- The app's transfer handler must call `PhoneFetchRegistry.IsTracked(id)`, then `ReportProgress`
  for each chunk, then `Complete(id, tempPath)` (we move the file) or `Fail(id)`.
- A fetch fails if the transfer doesn't start within 20 s.

### ScenesBridge (§6.18, protocol side only)
- `SetDoNotDisturbAsync`, `SetRingerAsync`, `SetMediaVolumeAsync`, `OpenAppAsync` and `NotifyAsync`
  send `phoneAction`. Each awaits the matching `phoneActionResult` (10 s timeout) and throws
  `SceneActionException` on failure.
- `PublishScenesAsync` sends `scenesList`.
- `RunSceneRequested` fires on `runScene`. Answer it with `ReportSceneResultAsync`.
- `PhoneEventReceived` fires on headset, wifi, charging and battery events.

The scene engine (triggers, editor, PC-side actions) is app logic and is not in this library.

### LoginCodeAutoCopier and OneTimeCodeDetector (§6.13)
`OneTimeCodeDetector.Detect(title, body)` is a verbatim port of the English and Arabic regex
scorer. All 14 of the Swift self-check cases pass.

`LoginCodeAutoCopier` listens to `notification` messages. It skips `ongoing` notifications and the
6 s backlog after connect. It de-duplicates on `nid|code` for 15 min, and on the same code within
2 min. It copies through the app's `ILoginCodeClipboard`, then restores the previous clipboard
after 90 s if it is unchanged.

Settings: `hib.loginCodes.autoCopy` and `hib.loginCodes.restoreClipboard`, both default true.

On Windows, implement `ILoginCodeClipboard` as follows:
- `ChangeCount` → `GetClipboardSequenceNumber`.
- `WriteCode` sets the text without syncing it to the phone, plus the
  `ExcludeClipboardContentFromMonitorProcessing` and `CanIncludeInClipboardHistory=0` formats.

## 6. Phone Agent (PhoneAgent/)

An LLM operates the phone over ADB, one verified step at a time. Gemini is the default provider;
Claude is optional.

### PhoneAgentController
- Bindable: `Prompt`, `CanRun`, `IsRunning`, `Status`, `Steps` (an `ObservableCollection<PhoneAgentStep>`,
  each with `Kind`, `Title` and a JPEG `Thumbnail`), `Usage`, `ActionCount`, `PendingQuestion`,
  `PendingConfirmation`, `Provider`, `HasApiKey`.
- Controls: `Run()`, `Stop()`, `AnswerQuestion(text|null)`, `ResolveConfirmation(bool)`, `RefreshKeyState()`.
- Hooks:
  - `AttentionRequested(critical)`: flash the taskbar.
  - `AppNamesProvider`: package → label, taken from `appIcons`.
  - `SendClipboardToPhone`: defaults to `clipboardUpdate {text}` over the link. It is used to paste
    non-ASCII text, followed by `KEYCODE_PASTE`.
- The loop, tools (`tap`, `long_press`, `swipe`, `type_text`, `key`, `open_app`, `list_apps`,
  `wait`, `screenshot`, `ask_user`, `request_confirmation`, `done`), the step limit, nudges and the
  system prompt are ported verbatim.
- **Safety rules:**
  - The model must call `request_confirmation` before purchases (always), sending, deleting,
    calls, and hard-to-undo settings changes.
  - The model must never type passwords, PINs or one-time codes.
  - Password fields never expose their text in the UI tree.
  - Instructions that appear on the phone screen are ignored.
- The prompt says "PC" where the Mac version says "Mac".

### PhoneAgentAdb
- `CaptureScreenAsync`: runs `exec-out screencap -p`, then uses SkiaSharp to make a JPEG
  (q 0.7, longest side ≤ 1280 px) and a 220 px thumbnail, and records `Scale` (device px per
  screenshot px).
- UI tree: `uiautomator dump /sdcard/hib_ui.xml && cat`, compacted to at most 150 lines in
  screenshot coordinates.
- Input:
  - `input tap`, `input swipe` (also used for long-press), `input keyevent`.
  - `input text '…'` for ASCII: spaces become `%s` and the argument is single-quoted.
  - `monkey -p <pkg> -c android.intent.category.LAUNCHER 1` to open an app.
  - `pm list packages -3` for the app list.

### GeminiClient, GeminiQuota and GeminiConversation (default provider)
Raw HTTPS to `v1beta/models/{model}:generateContent`. The key goes only in the `x-goog-api-key`
header. This is an exact port of `PhoneAgentGeminiClient.swift`:
- **Candidates:** the active model, then the chosen model, then the fallbacks: 3-flash-preview,
  3.1-flash-lite, 3.7-flash, 3.5-flash, flash-latest, 4-hydrogen. Duplicates are removed.
  Exhausted models are skipped unless every model is exhausted.
- **Hedging:** the first request starts on the first candidate. After every 12 s of silence, and
  whenever one fails, the next model is asked too. The first success wins and becomes the sticky
  `ActiveModel`. The whole race runs at most twice, with 8 s between passes.
- **Per-model handling:**
  - 40 s timeout per model.
  - 400 "API key not valid", 401, or 403 mentioning a key → invalid-key error.
  - 429 with a retry delay over 120 s or "per day" → `MarkExhausted(model, retry ?? 12h)`, then
    the next model.
  - Other 429s are retried in place (at most 2 times, each wait ≤ 30 s).
  - 404 and 5xx → next model. Other 400s → bad request.
  - Network errors are retried twice with 2 s and 4 s backoff.
- **Thought signatures:** once the conversation has moved between models, `thoughtSignature`
  values are replaced with `skip_thought_signature_validator`.
- **Quota memory:** `GeminiQuota` persists `hib.phoneAgent.geminiExhaustedUntil` = {model:
  unix-seconds}. Durations are clamped to 60 s–24 h. When nothing is left,
  `AvailableCount(preferred) == 0` produces the "free requests used up" message.
- **Conversation:**
  - Requests use `systemInstruction`, `contents`, `tools.functionDeclarations` (OpenAPI subset,
    `required` sorted), `toolConfig.functionCallingConfig.mode = ANY`, and
    `generationConfig.thinkingConfig.thinkingLevel`. If the model rejects `thinkingConfig`, it is
    dropped.
  - Tool results go back as `functionResponse` with the same id. The screenshot goes in
    `functionResponse.parts[].inlineData` (with `displayName` `screen-N.jpg`, referenced by `$ref`).
  - Model turns are appended exactly as received.

### ClaudeClient and ClaudeConversation (optional)
Messages API: model `claude-opus-5-5`, `anthropic-version 2026-10-21`, server-side fallbacks beta
(dropped if the API rejects it), strict tools, prompt caching, and `output_config.effort`. Retries
follow the Mac's rules.

### PhoneAgentSettings and ApiKeyStore
Settings use the same keys as the Mac (`hib.phoneAgent.*`; provider default gemini, model
`gemini-3.8-flash`, max steps 40 in the range 5–150, confirmations on).

`ApiKeyStore` keeps one file per provider in `%APPDATA%\H.I.B\keys\`. On Windows the file is a
DPAPI blob (`ProtectedData`, CurrentUser scope, with entropy). On other platforms it is a plain
file with mode 0600. Keys are never logged.

## 7. Tests (`src/HIB.Features.Tests`, xunit)

Run with `dotnet test src/HIB.Features.Tests`. What the tests cover:
- **Find My:** the topic and key match values computed with the Mac's Swift/CryptoKit code for two
  fixed pairing keys. A CryptoKit-sealed envelope opens. `SealRaw` with the same nonce is
  byte-identical to `SealedBox.combined`. Also covered: round trip, age, tamper, version and nonce
  checks; the replay LRU; relay line filtering; publish URL and body; key reset.
- **Gemini:** the request JSON shape, the tool-result and nudge shapes, finish-reason mapping,
  signature scrubbing, `RetryInfo` parsing, candidates and quota memory (clamping, expiry,
  persistence, `AvailableCount`). Fallback, hedging, in-place rate-limit retry, invalid key,
  "all exhausted" and the thinkingConfig retry run against a scripted fake HTTP handler, with no
  network.
- **Other parsers and builders:** adb output parsers, scrcpy and audio arguments, UI-tree
  compaction (password hidden), `input text` quoting, screenshot downscaling, and the OTP
  self-check table.
- **Behaviour:** login-code de-duplication and restore, the API key store (0600 / DPAPI), and the
  Scenes, PhoneSearch, FindMy and Ring link protocols.
- **Live phone test (opt-in):** `HIB_LIVE_ADB=1 [HIB_ADB_SERIAL=…] dotnet test --filter LiveAdbReadOnlyTests`.
  It runs only screencap and uiautomator dump on the connected phone.
