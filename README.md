# H.I.B

**Your Android phone and your computer, working as one.** Notifications, calls, clipboard, files,
music, phone mirroring and **Flick** — point your phone at a screen and throw a photo, file or link
onto it. Made by Haitham Hamid.

This repository is the **Windows app**. The phone app lives at
[jumhi/hib-android](https://github.com/jumhi/hib-android).

## Get started (5 minutes)

You need: a Windows 10/11 PC and an Android phone (Android 11+), on the same Wi-Fi.

1. **On the phone:** download the latest `H.I.B.apk` from
   [hib-android releases](https://github.com/jumhi/hib-android/releases/latest) and open it.
   - Allow your browser to install apps when Android asks.
   - Samsung: if it says the app is blocked, turn off **Settings › Security and privacy › Auto Blocker**
     for a moment, install, then turn it back on.
2. **On the PC:** download `H.I.B-Setup.exe` from
   [the latest release](https://github.com/jumhi/hib-windows/releases/latest) and run it.
   - SmartScreen may say "Windows protected your PC" (the app isn't signed by a company):
     click **More info › Run anyway**.
   - Click **Install**. H.I.B goes to the Start menu and to **Settings › Apps**, where you can
     uninstall it like any app. No administrator rights needed.
   - When Windows Firewall asks, allow **Private networks**.
3. **Pair:** open H.I.B on the phone, tap **Scan**, and scan the QR code H.I.B shows on the PC.
4. **Mirroring (optional):** click **Mirror** on the PC. A **Set up mirroring** card walks you through
   it, ticking each step as your phone reports it — nothing to install, no terminal:
   1. **Developer options:** click **Show me on the phone**, then tap **Build number** 7 times.
   2. **Wireless debugging:** click **Open it on the phone** and switch it on (tap **Allow** if the phone
      asks about the network).
   3. **Pair this PC:** on the phone, **Wireless debugging › Pair device with QR code**, and scan the code
      on the PC. Or choose **Use a pairing code instead** and type the 6 digits the phone shows.
   4. **Mirroring is ready** — click **Mirror**. You only do this once per PC.

## What you get

- Phone notifications on the PC, with replies and actions
- Calls, clipboard both ways, media controls, battery
- **Flick:** point your phone at the PC (or any of its screens) and throw anything onto it;
  flick files back to the phone or to your other computers
- **Devices:** send files and text between the phone and every computer
- **One mouse and keyboard for your computers:** push the cursor off the edge toward your other computer
  and keep working there, typing included; drag files across the edge
- Phone screen mirroring, phone apps in their own windows, phone audio on the PC (scrcpy)
- Find My phone, ring the phone, Phone Agent

## Credits and licence

- Works with the H.I.B Android app, a modified build of
  [AirSync](https://github.com/sameerasw/airsync-android) by Sameera Sandakelum (sameerasw)
  (MPL 2.0 with additional terms). H.I.B is not affiliated with or endorsed by AirSync.
- Bundles [scrcpy](https://github.com/Genymobile/scrcpy) and adb — see
  [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
- This Windows app: Mozilla Public License 2.0 ([LICENSE](LICENSE)).

---

# For developers

## Build, test, publish

```sh
export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec   # macOS dev box only
dotnet build HIB.sln
dotnet test tests/HIB.FakePhone
dotnet publish src/HIB/HIB.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out/win-x64
```

Release assets (portable zip + installer) in one go, into `dist/` (nothing is uploaded):

```sh
tools/publish-release.sh          # add --test to run the tests first
# dist/HIB-Windows.zip   H.I.B\HIB.exe + tools\ + README.txt (portable, unzip and run)
# dist/H.I.B-Setup.exe   the same app with tools\ embedded (-p:HibSetup=true); run it to install
# dist/SHA256SUMS.txt
```

## Install / uninstall (per user, no admin)

`H.I.B-Setup.exe` (any exe name containing "setup", or `HIB.exe --install`) opens a small Setup window instead of the app:
it closes a running H.I.B (a `{"quit":true}` on the `--send-to` pipe, then ends older copies), copies `HIB.exe` and the
tools to `%LOCALAPPDATA%\Programs\H.I.B\`, adds `H.I.B.lnk` to the Start menu (and optionally the desktop), re-points
Start with Windows and the Explorer "Send to" shortcuts at the installed exe and registers
`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\H.I.B` so it shows in Settings › Apps. A newer Setup updates in
place; `%APPDATA%\HIB` (settings, pairings) is never touched. An unzipped copy shows "Install H.I.B on this PC" in
Settings › This PC.

`HIB.exe --uninstall` asks first ("Also delete my settings and pairings" is off by default), then removes the Run entry,
shortcuts, Send to entries and the uninstall key; the folder goes once the process has exited (a hidden `cmd` retrying
`rmdir`). `--uninstall --quiet` / `--install --quiet` skip the window. The logic is in `src/HIB/Core/Install`.

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
# Pair screen → Advanced → "Show code text", copy it:
HIB_QR='airsync://…' HIB_HOLD_SECONDS=30 dotnet test tests/HIB.FakePhone --filter Drive_running_app
```

`HIB_SCREENSHOT_DIR=/some/dir dotnet test tests/HIB.FakePhone --filter "UiSnapshotTests|SetupWindowTests"` renders every page (and the Setup window) headlessly to PNG; `HIB_INSTALL_ROW=portable|installed` shows the Settings install/uninstall row on any OS.
`HIB_DESIGN_DIR=/some/dir dotnet test tests/HIB.FakePhone --filter DesignShotsTests` renders every screen at 2× with a fake phone (the `docs/design/windows-*.png` images).

## Windows Firewall

The phone must reach the PC on **TCP 6996** (WebSocket) and should be able to send/receive **UDP 8889** (presence / wake-up) and **UDP 5353** (mDNS). On first launch Windows usually shows its own "allow access" prompt; tick **Private networks**.

If that prompt was dismissed, open **Settings › Pair a phone › Advanced → Allow through firewall**. H.I.B then runs, after a UAC prompt (and only when you click the button):

```
netsh advfirewall firewall delete rule name="H.I.B"
netsh advfirewall firewall add rule name="H.I.B" dir=in action=allow program="<path>\HIB.exe" enable=yes profile=private,domain
netsh advfirewall firewall add rule name="H.I.B" dir=in action=allow protocol=TCP localport=6996 enable=yes profile=private,domain
netsh advfirewall firewall add rule name="H.I.B" dir=in action=allow protocol=UDP localport=8889 enable=yes profile=private,domain
netsh advfirewall firewall add rule name="H.I.B" dir=in action=allow protocol=UDP localport=5353 enable=yes profile=private,domain
```

To remove them: `netsh advfirewall firewall delete rule name="H.I.B"` (elevated). If the Wi-Fi network is marked **Public**, either switch it to Private in Windows settings or add `public` to the profiles.

## adb / scrcpy (Settings › Mirroring & ADB, audio, Phone Agent)

`HIB.Features`' `AdbLocator` looks in `<exe dir>\tools` first, then PATH, the Android SDK, winget/scoop/choco installs.
The released zip and H.I.B-Setup.exe bundle scrcpy 5.0.1 win64 (SHA-256 b12a2c4e…27a5, checked against the release's SHA256SUMS.txt). To bundle them in a local build, unzip the official scrcpy win64 release (it includes `adb.exe`) into `<repo>/tools/win-x64/`; a
`win-x64` publish copies that folder to `out/win-x64/tools/` next to `HIB.exe` (never into the single file); the Setup build
(`-p:HibSetup=true`, its own `obj/setup`, `bin/setup`) embeds it as the `HIB.Tools.zip` resource instead and unpacks it to
`%LOCALAPPDATA%\Programs\H.I.B\tools\`. Override the folder with `-p:HibToolsDir=…`.
Without it, Settings › Mirroring & ADB shows `winget install Genymobile.scrcpy` with a Copy button.
