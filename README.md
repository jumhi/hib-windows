# H.I.B for Windows

Desktop companion for the H.I.B Android app (private AirSync fork). The phone connects to it exactly as it connects to the Mac app. Made by Haitham Hamid.

## Build, test, publish

```sh
export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec   # macOS dev box only
dotnet build HIB.sln
dotnet test tests/HIB.FakePhone
dotnet publish src/HIB/HIB.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out/win-x64
```

The app also runs on macOS/Linux for development. Windows-only bits (Start with Windows, firewall rules, native mDNS, clipboard change counter, cursor position) are guarded with `OperatingSystem.IsWindows()`.

## Test mode / environment variables

| Variable | Effect |
|---|---|
| `HIB_PORT` | WebSocket port (default 6996). Setting it also turns discovery off and skips the single-instance lock. |
| `HIB_NO_DISCOVERY=1` / `HIB_TEST=1` | No mDNS / UDP presence. |
| `HIB_DISCOVERY=1` | Force discovery on (even with `HIB_PORT`). |
| `HIB_DATA_DIR` | Data folder (default `%APPDATA%\HIB`: `hib-desktop.json` with key/name/id and settings, `AppIcons\`, `incoming\`, `flick-screens.json`). |
| `HIB_DOWNLOADS_DIR` | Where received files go (default `%USERPROFILE%\Downloads`). |

Driving a running app with the fake phone:

```sh
HIB_PORT=16996 dotnet run --project src/HIB
# Pair page → "Show code text", copy it:
HIB_QR='airsync://…' HIB_HOLD_SECONDS=30 dotnet test tests/HIB.FakePhone --filter Drive_running_app
```

`HIB_SCREENSHOT_DIR=/some/dir dotnet test tests/HIB.FakePhone --filter UiSnapshotTests` renders every page headlessly to PNG.

## Windows Firewall

The phone must reach the PC on **TCP 6996** (WebSocket) and should be able to send/receive **UDP 8889** (presence / wake-up) and **UDP 5353** (mDNS). On first launch Windows usually shows its own "allow access" prompt; tick **Private networks**.

If that prompt was dismissed, open **Pair → Allow through firewall**. H.I.B then runs, after a UAC prompt (and only when you click the button):

```
netsh advfirewall firewall delete rule name="H.I.B"
netsh advfirewall firewall add rule name="H.I.B" dir=in action=allow program="<path>\HIB.exe" enable=yes profile=private,domain
netsh advfirewall firewall add rule name="H.I.B" dir=in action=allow protocol=TCP localport=6996 enable=yes profile=private,domain
netsh advfirewall firewall add rule name="H.I.B" dir=in action=allow protocol=UDP localport=8889 enable=yes profile=private,domain
netsh advfirewall firewall add rule name="H.I.B" dir=in action=allow protocol=UDP localport=5353 enable=yes profile=private,domain
```

To remove them: `netsh advfirewall firewall delete rule name="H.I.B"` (elevated). If the Wi-Fi network is marked **Public**, either switch it to Private in Windows settings or add `public` to the profiles.

## adb / scrcpy (Phone page, mirroring, audio, Phone Agent)

`HIB.Features`' `AdbLocator` looks in `<exe dir>\tools` first, then PATH, the Android SDK, winget/scoop/choco installs.
The released zip bundles scrcpy 5.0.1 win64 (SHA-256 b12a2c4e…27a5, checked against the release's SHA256SUMS.txt). To bundle them in a local build, unzip the official scrcpy win64 release (it includes `adb.exe`) into `<repo>/tools/win-x64/`; a
`win-x64` publish copies that folder to `out/win-x64/tools/` next to `HIB.exe` (never into the single file).
Without it, the Phone page shows `winget install Genymobile.scrcpy` with a Copy button.
