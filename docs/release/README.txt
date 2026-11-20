H.I.B for Windows - made by Haitham Hamid
=========================================

Install (recommended)
1. Run H.I.B-Setup.exe (from the same release) and click Install. It installs just for
   you, without administrator rights, into %LOCALAPPDATA%\Programs\H.I.B, adds H.I.B to
   the Start menu and lists it in Settings > Apps, where you can uninstall it like any app.
   Running a newer H.I.B-Setup.exe later updates it in place and keeps your settings and
   pairings.
2. Already using this unzipped copy? Open H.I.B > Settings > This PC > "Install H.I.B on
   this PC" does the same from here; afterwards you can delete this folder.

Or use this folder as a portable copy
1. Unzip this folder anywhere (for example Documents\H.I.B). Keep HIB.exe and the
   tools folder (if present) together.
2. Run HIB.exe. If Windows SmartScreen warns about an unknown publisher, choose
   "More info" > "Run anyway".

Then
3. When Windows Firewall asks, allow H.I.B on PRIVATE networks. (If you missed the
   prompt: open H.I.B > Pair > "Allow through firewall".)
4. On your phone, open H.I.B, tap scan and scan the QR code on the Pair page.
   The phone and the PC must be on the same Wi-Fi (or Tailscale).

Phone mirroring (optional)
Click Mirror. The "Set up mirroring" card walks you through it once - no terminal, nothing
to install, it ticks each step as your phone reports it:
  1. Developer options: "Show me on the phone", then tap Build number 7 times.
  2. Wireless debugging: "Open it on the phone" and switch it on (tap Allow if asked).
  3. Pair this PC: on the phone, Wireless debugging > Pair device with QR code, and scan
     the code on the PC - or "Use a pairing code instead" and type the 6 digits.
  4. "Mirroring is ready" - click Mirror.
Mirroring, apps in windows, phone audio and Phone Agent use the adb + scrcpy 5.0.1 in the
"tools" folder (official scrcpy release, Apache 2.0 licence - see tools\LICENSE.txt).
H.I.B always uses these; you never need platform-tools or PATH changes.

Tips
- Settings > "Start with Windows" keeps H.I.B in the tray after login.
- Closing the window keeps H.I.B running in the tray; use the tray menu > Quit to exit.
- Data lives in %APPDATA%\HIB. Uninstalling keeps it unless you tick
  "Also delete my settings and pairings".
