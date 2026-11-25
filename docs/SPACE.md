# H.I.B Space: phone, Mac and PC as one set of devices

This document is the shared design for the phone (Android), the Mac app and the Windows app.
All three must follow it exactly so they work together. It extends `docs/PROTOCOL.md`
(section numbers like §6.9 refer to that file).

## 0. Why

The owner's complaints, verbatim in spirit:
- "Connected, but connected poorly": sharing a file from one place to another doesn't work or the app
  "doesn't understand" where it should go.
- Flick doesn't know where to go. It might be meant for the phone, the Mac or the Windows PC.
- Play: "If I want to play on the Mac or on Windows, it should know."
- Never write "Flick to Mac". It is just **Flick**. It might be going to Windows.
- A flick that lands on Windows doesn't pop up the way it does on the Mac.
- The UI must make all of this obvious.

## 1. Ideas (what the user sees)

1. **Every device knows every device.** The phone introduces the computers to each other. Each app
   shows the same list: the phone and every computer, with live status. Each entry has a name, an icon
   (phone, laptop for Mac, desktop monitor for Windows) and an **In use** badge on the computer being
   used right now.
2. **Computers talk directly.** Once the phone has introduced them, the Mac and the PC open a direct,
   encrypted link over the local network ("peer link", §3.3). Files between computers go **direct**,
   which is fast and works even when the phone is away. If there's no direct link, they go **through
   the phone** (the existing §6.22 relay).
3. **The app knows which computer you are using.** Each computer reports keyboard/mouse activity.
   When nothing else is said, Flick, Play and Send go to the computer you are using now.
4. **One target rule everywhere.** Each step is used only when the one before it doesn't decide:
   1. an explicit pick by the user
   2. **aim** (phone only: the computer the phone points at)
   3. the computer **in use**
   4. the last one used
   5. the primary computer
5. **Flick goes where the phone points (§7).** No buttons to switch device.
   - **On the phone:** point the phone at a screen (the Mac's, the PC's, any of their monitors) and
     throw. That screen glows while you point at it, and the card lands on it. A one-time "Point setup"
     runs by itself the first time: each screen lights up in turn and you point at it for a second.
   - Not pointing at any screen → the target rule (in use, then last used, then primary).
   - Anything can be flicked: photos, videos, any file, links, text, the clipboard.
   - **On a computer:** dragging a file brings up a shelf of device bubbles at the top of the screen.
     Dropping (or throwing) the file on a bubble sends it there.
   - **Arrival:** the same flying card, on the Mac and on Windows. On the phone it arrives as a rich
     notification with a preview.
6. **Play on…:** phone audio plays on exactly one computer, by default the one in use. Starting it on
   one computer stops it on the others.
7. **Words:** "Flick", "Send to…", "Play on…", "Devices", "In use", "Direct", "Through phone".
   - Never "Flick to Mac" or "Play on Mac" on the phone.
   - Use the real device name where you can ("Play on Haitham's MacBook"), otherwise "computer".

## 2. Identity

- A computer's id is its desktop name, the same as in §6.22 (`macInfo.name`). The phone's id is `"phone"`.
- Platforms are `mac`, `windows` and `android`. Device icons:
  - phone: smartphone
  - mac: laptop
  - windows: desktop monitor
- Comparing ids (for tie-breaks) is lexicographic over UTF-8 bytes.

## 3. Wire protocol additions

All JSON is the normal envelope `{"type": "...", "data": {...}}`, encrypted as in §3.

### 3.1 `desktopActivity` D→P (and peer→peer, as `peerActivity`)

```json
{"type":"desktopActivity","data":{"inUse":true,"idleSeconds":3}}
```
- `inUse` means there was user keyboard or mouse input in the last **90 s** and the screen is not locked
  or asleep (if that can be detected).
- `idleSeconds` is the time since the last input.
  - Mac: `CGEventSource.secondsSinceLastEventType(.hidSystemState, eventType: CGEventType(rawValue: ~0)!)`.
  - Windows: `GetLastInputInfo`.
  - Other systems: always send `inUse:false, idleSeconds:-1`.
- When to send:
  - right after the handshake
  - whenever `inUse` flips (check every 2 s)
  - every 30 s as a heartbeat
- The phone stores `inUse` and `lastActiveAt = now - idleSeconds` for each computer.
- Old phones ignore it.

### 3.2 `desktopsList` P→D (extended §6.22)

```json
{"type":"desktopsList","data":{
  "you":"Office PC",
  "phone":{"id":"phone","name":"Haitham's S26","model":"SM-S948B","battery":80,"charging":false},
  "space":{"key":"<base64 of 32 random bytes>","v":1},
  "inUse":"Haitham’s MacBook",
  "playingOn":null,
  "desktops":[
    {"id":"Haitham’s MacBook","name":"Haitham’s MacBook","platform":"mac","type":"MacBook Pro",
     "connected":true,"inUse":true,"idleSeconds":4,"ips":["10.107.0.216"],"port":6996}
  ]}}
```
Every field from §6.22 stays the same. The new fields are:

- `you`: the recipient's own id as the phone sees it. As before, the recipient never appears in `desktops`.
- `space.key`: the **space key**. The phone makes it once: 32 bytes from SecureRandom, stored in its
  preferences, the same for every computer. It changes only if the user resets H.I.B on the phone.
  - It is only ever sent inside the encrypted per-computer channel.
  - Computers store it as a secret: Keychain on the Mac, DPAPI on Windows. Never log it.
- `inUse`: the id of the computer the phone considers in use, or `null`. This is the connected computer
  with `inUse:true` and the newest `lastActiveAt`. It may be the recipient itself.
- `playingOn`: the id of the computer playing the phone's audio, or `null`.
- In each desktop entry:
  - `inUse` and `idleSeconds` come from that computer's last `desktopActivity`.
  - `ips` lists the address the phone's session to that computer uses first, then any other IPs the
    phone knows for it (from its QR code or discovery).
  - `port` is that computer's WebSocket port.
- When it is sent:
  - on `desktopsRequest`
  - pushed to every connected computer ~0.8 s after any computer connects or drops
  - pushed whenever `inUse` or `playingOn` changes (debounced 1 s)

### 3.3 Peer link (computer ↔ computer, direct)

- **Endpoint:** `ws://<ip>:<port>/peer` on each computer's **existing** WebSocket server, which gains a
  second path. The `/socket` behaviour for phones does not change at all.
- **Encryption:** AES-256-GCM with the raw 32-byte **space key**. Framing is the same as with the phone:
  text frames carrying `base64(nonce12 ‖ ciphertext ‖ tag16)`.
- **Hello:** the client sends `peerHello` first, and the server answers with its own `peerHello`:
  ```json
  {"type":"peerHello","data":{"id":"Office PC","name":"Office PC","platform":"windows","type":"Windows PC",
    "port":6996,"ips":["10.107.0.40"],"v":1}}
  ```
  If the server can't decrypt a first frame within 5 s, it closes with code 4003.
  A computer that has no space key yet refuses `/peer` connections.
- **Dialling:**
  - Each computer keeps a list of **known peers**: everything the phone listed in `desktopsList`, plus
    every `peerHello` received.
  - The list is saved with the space key: `{id, name, platform, type, ips, port, lastSeen}`.
  - For each known peer with no live link, the computer dials all of its `ips` in parallel and keeps the
    first that opens.
  - Retries back off: 5 s, 10 s, 20 s, up to 60 s. Any `desktopsList` or `peerHello` resets the backoff.
  - Remembered peers are dialled at app start, so computers link up **without the phone**.
- **Duplicates:** keep at most one link per pair of computers. If a second one opens, both sides keep the
  link whose **client** id sorts lower and close the other with code 4009 ("duplicate"). Both sides
  compute this the same way.
- **Keepalive:** `{"type":"ping"}` every 10 s, answered with `pong`. Drop the link after 30 s with no frames.
- **Messages on the link:**
  - `peerHello`, `ping`, `pong`, `peerDiag` / `peerDiagLog` (below)
  - `peerActivity` (same data as `desktopActivity`, §3.1): send it on link-up, on change and every 30 s
  - **Files**, using the §6.9 sender and receiver exactly, with the peer link as the socket:
    1. The sender first sends `flickStart`:
       `{id, kind: image|video|file (from the mime type), name, mime, size, thumbnail?, from: <sender name>,
       fromPlatform, exitXRatio: 0.5, velocity: 1800, angle: 0}`
    2. Then `fileTransferInit` with `flickId = id`, `relayFrom = <sender name>` and `relayFromPlatform`.
    3. Then the chunks, acks, `fileTransferComplete` and `transferVerified` (or `fileTransferCancel`).
    4. The receiver handles it exactly like a relayed Flick (§6.22, "Receiving a relayed file"): a card
       flies in saying "From <name>", and the file goes to the Flick destination.
  - **Text or a link:** `flickStart {id, kind: "text"|"link", text, from, fromPlatform}`. The receiver
    shows the card. A link opens if auto-open is on; text is copied.
  - **Not sent on the link:** `clipboardUpdate`. The phone already shares the clipboard with every
    computer, so sending it here would cause loops.
  - **Diagnostics (read another computer's log):**
    - Request: `{"type":"peerDiag","data":{}}`
    - Reply: `{"type":"peerDiagLog","data":{"version":"4.1.0","text":"<the last 256 KB of the log>"}}`
    - Answered only on an authenticated link, after a valid `peerHello`. Before the hello it is ignored
      like every other message.
    - `text` is UTF-8, starts at a line boundary, and is scrubbed of the pairing key, the space key, API
      keys and anything key- or token-shaped.
    - On Windows the log is `%LOCALAPPDATA%\H.I.B\logs\hib.log` (or `$HIB_DATA_DIR/logs/hib.log`): at most
      2 MB plus one rotation (`hib.log.1`). It holds the ADB console, scrcpy command lines and output, and
      the app trace.
- **Sending a file from one computer to another:**
  1. Use the direct link if it is up.
  2. Otherwise relay through the phone (§6.22 `relayTo`).
  3. Otherwise report "<name> isn't reachable".
- The UI shows the route as **Direct** or **Through phone**.

### 3.4 Flick to the phone (D→P)

- **Files:** the computer first sends `flickStart` D→P:
  `{id, kind, name, mime, size, thumbnail?, from: own name, fromPlatform}`.
  - `thumbnail` is a base64 JPEG of at most 64 KB, for images (and video posters if easy).
  - Then it does the normal phone-receive transfer (§6.22, without `relayTo`) with `fileTransferInit.flickId = id`.
- **Text or a link:** `flickStart {id, kind: "text"|"link", text, from, fromPlatform}`.
- **What the phone does:**
  - While a file arrives, it shows a progress notification.
  - When the file finishes, it shows a heads-up notification titled **"Flick from <from>"**:
    - For images, a BigPicture preview.
    - Actions **Open** and **Share**; tapping the notification opens the file.
  - For text: it copies the text and notifies "Copied from <from>".
  - For a link: it notifies with an **Open** action.
- Old phones ignore `flickStart` D→P, and the file still arrives.

### 3.5 Play on… (extends §6.11)

- Phone audio plays on **at most one computer**.
- **Phone "Play on X":** send `phoneAudioControl {action:"start"}` to X and `{action:"stop"}` to every
  other connected computer. Set `playingOn = X`.
- **Computer "Play here"** (a button on the computer):
  1. The computer starts its own scrcpy audio, as today.
  2. It sends `phoneAudioState {active:true}`.
  3. When the phone gets `phoneAudioState active:true` from X, it sends `phoneAudioControl {action:"stop"}`
     to every other computer and sets `playingOn = X`.
  4. `active:false` from the computer playing sets `playingOn = null`.
- The phone's own Play control defaults to the target rule (§1.4): the computer in use.
- Computers show "Playing on <name>" when `playingOn` is another computer, and offer "Play here".

### 3.6 Flick from the phone

- The phone sends `flickStart` and the transfer to the chosen computer only.
- `flickStart` gains `screen` (§7.4): the id of the screen to land on. It still carries
  `heading`/`elevation` for computers that don't know `screen`.

## 4. The phone app (Android)

### Flick screen
- **Title:** "Flick". The subtitle names where it goes now: "→ <computer> · <screen>" (screen part only
  when that computer has more than one screen).
- The target is **where the phone points** (§7). There is no tap-to-pick and no drag-direction
  picking: the device bubbles across the top are indicators only (the target one glows).
- Opening Flick runs **Point setup** (§7.5) first when it is needed.
- The in-app Flick card offers **Photos & videos**, **Files** (any type) and **Clipboard**.
- The share sheet accepts anything (already `*/*`).

### Devices card
- The "Your computers" card becomes **"Your devices"**: each computer with its state
  (Connected, In use, Off, Offline) and Add computer.

### Receiving
- Flicks from computers arrive as in §3.4.

### Play on…
- Available in the app: the media card, or wherever "Play on Mac" used to be.
- A Quick Settings tile **"Play on computer"**:
  - Tap plays on the target-rule computer, or stops if already playing.
  - Long-press opens a chooser.
- Show "Playing on <name>".

### Words
- Remove every user-visible "Mac" that really means "the computer": strings, Kotlin literals and
  notifications. Use the device's name or "computer".
- "Flick to Mac" becomes "Flick".

### Space key
- Generate it and send it as in §3.2.

## 5. Computers (Mac and Windows), shared UX

### Devices tab
A tab in the main window's top tab pill, between Apps and Settings. It shows a card for:
- the phone
- each other computer
- this computer, small and last, labelled "This Mac" / "This PC"

Each card shows:
- an icon, the name and the model
- status: **Connected / In use / Offline**
- the route: **Direct / Through phone**
- battery, for the phone
- "Playing" if it is playing phone audio

Each card is a **drop target**: dropping files on it sends them there. It also has these buttons:
- **Send files…**
- **Send clipboard**: a text flick, per §3.3 or §3.4
- for the phone: **Ring**
- for this computer: **Play phone audio here** / **Stop**

### Flick shelf
- **Mac:** when the user starts dragging files anywhere, a glass shelf of device bubbles slides down
  from the top-centre of the screen under the pointer.
  - Detect this with a global `.leftMouseDragged` monitor plus a change in `NSPasteboard(name: .drag)`'s
    `changeCount` that includes file URLs.
  - Dropping on a bubble sends the files.
  - The shelf hides when the drag ends.
- **Windows:** a slim always-on-top glass pill at the top-centre of the primary screen, about 120×10 px
  and translucent.
  - When a drag enters it, it grows into the full shelf of bubbles. Dropping on a bubble sends.
  - It also hides itself while a full-screen app is in front.
  - Also add Explorer **Send to** entries: `.lnk` shortcuts in `%APPDATA%\Microsoft\Windows\SendTo`
    named "H.I.B – <device name>", which run `HIB.exe --send-to "<id>" <files…>`.
    - A running H.I.B receives the request through a named pipe; otherwise H.I.B starts and then sends.
    - Keep the shortcuts in sync with the device list.

### Incoming Flick card
- The same visual and motion as the Mac's (`airsync-mac/Core/Flick/FlickOverlay.swift`,
  `FlickCardContent.swift`, `FlickManager.swift`): the card flies in from the top edge, lands with a
  spring, shows the preview, the name and "From <device>", can be clicked to open and dragged out, and
  then settles.
- Windows must look and behave the same as the Mac.

### Defaults
- "Send to…" with no explicit pick uses the target rule. This computer is never a target of itself.

## 6. Windows look (match the Mac app)

The Windows app must look like the Mac H.I.B app, not like a generic Fluent app. The reference screenshot
of the Mac main window is `docs/design/mac-main.png`. The theme tokens come from
`airsync-mac/airsync-mac/Components/Theme/HIBTheme.swift`:

| Token | Value |
|---|---|
| ink | #090C21 |
| midnight | #111533 |
| panel | #131838 |
| cyan (accent) | #20D9FF |
| lime (connected) | #BAFF38 |
| lavender | #7A6BFF |

- **Window background:** a gradient from top-left to bottom-right: ink → midnight → #0B1A2E.
- **Ambient light:** two big blurred circles, cyan at 18% opacity top-right and lavender at 14%
  bottom-left, plus a faint 34 px grid of cyan lines at 2.5% opacity.
- **Panels:** fill is a gradient from panel at 86% to midnight at 62%, with a 0.8 px stroke gradient
  (cyan 30% → white 8% → lavender 18%), corner radius 20 and a soft shadow.
- **Small caps labels:** cyan with wide letter spacing (for example "LINKED GALAXY").
- **Layout:**
  - **Left column (~300 px):**
    - the linked-phone card: a "LINKED GALAXY" label, the phone name and a lime status dot
    - a tall rounded **phone mockup** filled with the phone's wallpaper (`PhoneDeviceInfo.Wallpaper`) or a
      lavender gradient, containing:
      - a status icon pill at the top
      - a big clock
      - recent app icons
      - a row of quick actions: send file, files, do not disturb, clipboard
      - a pill with battery, volume and music
    - at the bottom, a **Mirror ▾** and a **Desktop ▾** split button
  - **Right side:** a floating glass **tab pill** at the top centre with icon-only tabs: Notifications,
    Apps, Devices, Settings. Content goes below it.
  - When no phone is paired, the left column and tabs give way to a centred pairing (QR) view in the same style.
- **Settings:** a Mac-style settings screen with its own list of sections on the left. Everything that used
  to be a top-level sidebar page lives here: Clipboard, Files, Phone, Webcam & Calls, Search phone,
  Find My, Phone Agent, Flick, Pair, About and so on.

## 7. Point & Flick: screens across every computer

The phone keeps one map of every screen of every computer and where it is (the direction the phone
points to reach it). Pointing picks the computer **and** the screen. The computers light up the screen
being pointed at, so the user sees the target before throwing.

### 7.1 Screen ids
- **Mac:** `FlickScreenPicker.key(for:)` (vendor-model-serial-name).
- **Windows:** the Avalonia `Screen.DisplayName` (e.g. `\\.\DISPLAY1`); if empty, `"x,y,wxh"` of its bounds.
- The phone treats a computer that never sent `screensInfo` (older version) as one screen with id `""`
  ("let the computer choose").

### 7.2 `screensInfo` D→P
```json
{"type":"screensInfo","data":{"screens":[
  {"id":"…","name":"Built-in Retina Display","primary":true,"x":0,"y":0,"w":1512,"h":982}
]}}
```
- Sent right after the computer's `macInfo` on every new phone connection, whenever the displays
  change, and in reply to `screensRequest`.
- `x,y,w,h`: the screen's bounds in the computer's desktop coordinates (points/DIPs, y down on both).
  The phone only uses them to order screens left → right and to name them.

### 7.3 `screensRequest` P→D
`{"type":"screensRequest","data":{}}`. The computer replies with `screensInfo`. The phone sends it to
every connected computer when the Flick screen opens.

### 7.4 `flickAim` P→D and `flickStart.screen`
```json
{"type":"flickAim","data":{"screen":"<id>"}}   // light this screen
{"type":"flickAim","data":{"screen":null}}     // light nothing here
```
- While the phone's Flick screen is open, the phone sends `flickAim{screen:id}` to the computer that
  owns the aimed screen and `flickAim{screen:null}` to any computer it lit before. It re-sends the
  current one every 1.5 s, and sends `null` to all when Flick closes.
- **Aim light (computer):** on that screen, a soft cyan (#20D9FF) glow rising from the bottom edge
  (about 14% of the screen height, fading upward; cards rise from the bottom) and a small pill at the
  bottom centre: "Flick lands here". Click-through, above everything, on every Space/desktop, no focus
  steal. Fades in 150 ms, out 250 ms. Hides by itself 4 s after the last `flickAim` naming it.
  An unknown id lights the main screen.
- `flickStart` gains `"screen":"<id>"`. When that screen exists the card lands on it, overriding the
  computer's own aim/teaching. Otherwise the computer's own rule applies.

### 7.5 `aimSetup` P→D (Point setup)
```json
{"type":"aimSetup","data":{"screen":"<id>","step":1,"total":3,"progress":0.4}}
{"type":"aimSetup","data":{"screen":null}}   // hide
```
- **Point target (computer):** on that screen, a full-screen dim (ink #090C21 at 55%) with a large
  pulsing cyan ring in the centre, "Point your phone here" and "Screen <step> of <total>" under it.
  `progress` (0…1) fills the ring; at 1 the ring turns lime (#BAFF38) with a check, then fades.
  Click-through. Hides on `screen:null` or 6 s after the last update.
- **Phone:**
  - Runs it automatically when Flick opens, there are 2+ screens across the connected computers, and
    any of them has no saved aim. "Point setup again" in Flick settings re-runs it for all.
  - Order: computer in use first, then the others; each computer's screens left → right.
  - For each screen it sends `aimSetup` (to that screen's computer; `null` to the previous computer
    when it moves on) and shows "Point at the glowing screen" with the computer and screen name.
    For an id `""` (older computer) it says "Point at <name>'s screen" instead.
  - Hold-to-capture: while the aim moves less than 3° over the last 0.5 s, progress grows to 1 in
    0.9 s. Moving resets it. At 1: save the aim, haptic, next screen. "Skip" ends setup (target rule).
  - Sends `aimSetup{screen:null}` to every computer when setup ends.

### 7.6 Phone storage and matching
- `hib_flick_screens_v1`: JSON list of `{computer, screen, name, h, e}` (`computer` = desktop name).
  Re-running setup replaces the entries of the screens it captured. The old per-computer aims
  (`FlickAimStore`) are no longer used.
- Live aim: the nearest saved screen of a **connected** computer within 35° (great-circle angle).
  None within 35° → target rule, nothing lit.
- With only one screen in total, there is no setup and no aim: everything goes to that computer.

## 8. ADB setup without a terminal (mirroring, Play here, apps)

Mirroring needs the computer paired with the phone's Wireless debugging once. Finding the phone's
pairing service by mDNS fails on many networks (different subnets, multicast blocked), which forced
the user into PowerShell. The phone can always see its **own** services, so it tells the computer.

### 8.1 `adbSetupState` P→D
```json
{"type":"adbSetupState","data":{"developerOptions":true,"wirelessDebugging":false,
  "connectPort":42789,"pairingPort":null,"pairingName":null}}
```
- `developerOptions` = `Settings.Global.DEVELOPMENT_SETTINGS_ENABLED`, `wirelessDebugging` =
  `Settings.Global "adb_wifi_enabled"`. `connectPort`: the phone's `_adb-tls-connect._tcp` port (NSD on
  the phone). `pairingPort`/`pairingName`: the phone's `_adb-tls-pairing._tcp` service while a pairing
  screen (QR scan or pairing code) is open on the phone, else null.
- Sent after the handshake, whenever any field changes (ContentObserver on the two settings; NSD
  callbacks), and in reply to `adbSetupRequest`. While a computer's setup is open (§8.2 `watch`), the
  phone keeps NSD discovery of both service types running and reports changes within ~1 s.

### 8.2 `adbSetupRequest` D→P
```json
{"type":"adbSetupRequest","data":{"open":"developer"|"wireless"|null,"watch":true}}
```
- `watch:true` starts the fast reporting of §8.1 for 5 min (renewed by each request); `watch:false` stops it.
- `open:"developer"`: help the user turn on Developer options — open About phone → Software
  information (Samsung) / About phone (others) and show "Tap Build number 7 times".
- `open:"wireless"`: open Developer options with Wireless debugging highlighted
  (`:settings:fragment_args_key` = `toggle_adb_wireless`).
- If the app can't start an activity from the background, it posts a high-priority notification
  ("Tap to open Wireless debugging") that opens it.

### 8.3 Pairing on the computer (Windows and Mac)
1. **Set up mirroring** card (instead of any silent failure or terminal instructions), driven by
   `adbSetupState`:
   - Developer options off → "Turn on Developer options" + **Show me on the phone** (`open:"developer"`).
   - Wireless debugging off → "Turn on Wireless debugging" + **Open it on the phone** (`open:"wireless"`),
     and "Allow on this network" when the phone asks.
   - Not paired → a large pairing QR (`WIFI:T:ADB;S:<name>;P:<password>;;`) with "On your phone:
     Wireless debugging → Pair device with QR code". When `adbSetupState.pairingName` equals `<name>`,
     run `adb pair <phoneIp>:<pairingPort> <password>` at once — no mDNS.
   - **Use a pairing code instead**: "Wireless debugging → Pair device with pairing code"; the port comes
     from `pairingPort`, so the user only types the 6 digits.
   - Paired → `adb connect <phoneIp>:<connectPort>` → "Mirroring is ready".
2. `adb mdns services` stays only as a fallback when the phone doesn't send `adbSetupState` (old app).
3. Always the bundled adb; never ask the user to install platform-tools or edit PATH.
4. The Mirror / Desktop / app buttons open this card whenever ADB isn't ready, never a dead end.

## 9. Shared mouse & keyboard ("Roam")

One mouse and keyboard move across the Mac and the PC: push the cursor off the edge that faces the
other computer and it carries on there, typing included. Both computers' own mice/keyboards keep
working; whichever you touch drives. Modelled on Deskflow's mechanisms (reference source:
deskflow-master, GPL-2.0 — learn the mechanism, write our own code; don't transliterate its files).
Everything travels over the existing authenticated **peer link** (§3.3) — no new port, no new trust.

### 9.1 Roles
- **Controller:** the computer whose local physical input pushed the cursor across its edge. It
  captures its own mouse/keyboard (swallowed locally, cursor hidden and parked), tracks a virtual
  cursor on the target and sends events.
- **Target:** injects what it receives. If the target sees local **physical** input (not injected —
  Mac: our event-source marker; Windows: `LLMHF_INJECTED`/`LLKHF_INJECTED` or our `dwExtraInfo`
  marker) while controlled, it sends `ctlRelease` and the controller returns the cursor home.
- Either computer can be the controller at different times (symmetric).
- **Safety:** peer link lost or no pong within 2 s while controlling → release at once (cursor back
  home at the edge it left from, local input un-swallowed). Releasing never needs the network.

### 9.2 Layout (where the other computer is)
- P→D `screenLayout` (phone → every computer; after each handshake and whenever the Point & Flick map
  changes): `{"screens":[{"computer":"<desktop name>","screen":"<id>","h":214.0,"e":8.5}]}`.
- Each computer computes, for every other linked computer B, its side relative to itself A from the
  mean aim of each computer's screens (unit-vector mean): `dh` = signed heading difference B − A in
  (−180, 180], `de` = eB − eA. If |dh| ≥ |de|: B is **right** when dh > 0, else **left**. Otherwise B
  is **above** when de > 0, else **below**. Both computers must agree (same function).
- No aims for both → the Devices tab asks once: "Where is <name>? ← Left / Right →" (default right),
  synced to the other computer in `ctlHello`. A manual choice overrides aims.
- The shared edge is the outer edge of each computer's whole desktop (bounding box of its screens) on
  that side. Crossing at position p along the controller's edge enters the target at the same
  proportional position along its opposite edge, then clamps into a real screen.
- **Learns when a computer moves (left/right only):** with Roam ready, if the local physical cursor is
  pushed against a left or right outer edge that has **no** neighbour 3 times within 2.5 s (a push =
  cursor at the edge plus ≥ 30 px more motion toward it, separated by moving away ≥ 20 px), and exactly
  one linked computer B currently sits on the opposite side, B moves to this side: store it as the
  pair's `manualSide`, send `ctlHello` (the other computer applies the mirrored side), and show an
  edge pill "PC-INTERN is on this side now · Undo" for 5 s. A new Point setup (`screenLayout` change)
  clears learned/manual sides. Top/bottom edges never learn (the menu bar and taskbar get bumped).
- Switch rules (from Deskflow's Server::isSwitchOkay): no switch within 24 px of a corner (keeps hot
  corners), no switch while a mouse button is held unless a file drag is in progress (§9.5).

### 9.3 Peer messages (both directions, over the peer link)
All coordinates are the **target's** desktop coordinates (points/DIPs, y down — as in `screensInfo`).
```json
{"type":"ctlHello","data":{"v":1,"enabled":true,"platform":"mac|windows",
  "screens":[{"id":"…","x":0,"y":0,"w":1512,"h":982}],"manualSide":null|"left"|"right"|"above"|"below",
  "cmdAsCtrl":true}}
{"type":"ctlEnter","data":{"x":0,"y":420,"clipboard":{"text":"…"}|null}}
{"type":"ctlMove","data":{"x":12.5,"y":431}}                // absolute; coalesced to ≤ 1 per 8 ms
{"type":"ctlButton","data":{"b":"left|right|middle|back|forward","down":true}}
{"type":"ctlWheel","data":{"dx":0,"dy":-120}}               // 120 = one notch; dy > 0 scrolls up/away
{"type":"ctlKey","data":{"usage":4,"down":true}}            // USB HID keyboard-page usage, AFTER mapping
{"type":"ctlLeave","data":{"clipboard":{"text":"…"}|null}}  // controller → target: cursor went home
{"type":"ctlRelease","data":{}}                             // target → controller: local input here
{"type":"ctlDragBegin","data":{"id":"…","count":2,"name":"report.pdf"}}
{"type":"ctlDragCancel","data":{"id":"…"}}
```
- `ctlHello` on every peer-link hello and whenever its fields change. `enabled:false` on either side
  disables Roam for that pair.
- Keys are sent as **physical** keys (HID usage) so the target's own keyboard layout/input language
  applies. On release-to-home the controller sends key-up for every key it still holds down.
- **Modifier mapping** is done by the controller (it knows the target platform). Default `cmdAsCtrl`
  (Mac user typing on the PC): Mac ⌘ → Ctrl, Mac Control → Windows key, Option ↔ Alt; PC → Mac is the
  inverse (Ctrl → ⌘, Win → Control, Alt → Option). Off = plain (⌘ ↔ Win, Control ↔ Ctrl).
- Media keys (volume, play/pause, next/previous, mute, brightness where possible) map to the target's.
- Order is preserved (one TCP connection; `TCP_NODELAY` on peer sockets). Never coalesce keys/buttons.
- Settled details (both apps):
  - `manualSide` is the sender's view ("where the other computer is, from me"); the receiver mirrors it.
    If both set different sides before linking, the lower peer id (UTF-8 bytes) wins.
  - `cmdAsCtrl` is one shared setting for the pair; either side changing it sends `ctlHello`.
  - Media keys: mute / volume up / volume down = keyboard usages 0x7F / 0x80 / 0x81; play-pause, next,
    previous, stop = consumer usages encoded as 0x000C00CD / 0x000C00B5 / 0x000C00B6 / 0x000C00B7 in
    `usage`. Brightness is dropped when the target is Windows.
  - `ctlWheel`: dy > 0 scrolls up/away, dx > 0 scrolls right.
  - A target answers `ctlEnter` with `ctlRelease` when Roam is off there or it's busy controlling.
  - A computer that can't capture or inject (e.g. no Accessibility grant) sends `enabled:false`.
  - During a drag ghost the target never injects the left button; the drop is `ctlButton` up, then
    the controller's `flickStart` with `at`.

### 9.4 Clipboard on crossing
- `ctlEnter` and `ctlLeave` carry the sender's clipboard text if it changed since the last crossing
  (≤ 1 MB). The receiver sets it so copy on one computer → paste on the other works instantly, with no
  round-trip through the phone. The normal phone clipboard sync stays as is.

### 9.5 Dragging files across the edge
- Controller side: a drag with file items in progress (Mac: the drag pasteboard has file URLs; Windows:
  an invisible OLE drop-target window under the cursor at the edge, as Deskflow does) crosses the
  edge → the controller parks its own drag on an invisible **drop catcher** window of H.I.B at the
  park point (so the local drop lands nowhere else) and sends `ctlDragBegin`.
- Target side: shows a small ghost card ("report.pdf" / "2 files") following the cursor.
- Button up on the target → the controller sends the files with the existing file transfer
  (`flickStart` + §6.9, `relayFrom` = controller) plus `"at":{"x":…,"y":…}` = the drop point. The
  target turns the ghost into the usual **Flick card at that point** (saved in Downloads; click to
  open, drag the card into any app). Escape or the cursor going home → `ctlDragCancel`.

### 9.6 What the user sees
- **Devices tab:** "Mouse & keyboard" row per linked computer: switch (default on), side (from Point
  setup or the ← / → picker), "⌘ works as Ctrl on the PC" switch. Status: "Move the cursor off the
  right edge to go to PC-INTERN".
- **Edge glow:** when the cursor is within 40 px of the shared edge (and Roam is ready), a thin cyan
  (#20D9FF) glow appears along that edge with a small pill "PC-INTERN →". Fades out when moving away.
- **In use follows the cursor:** the computer the cursor is on reports `desktopActivity inUse:true`
  immediately (the other false), so Flick / Play / Send default to it.
- **Mac permission:** the Mac needs Accessibility (and Input Monitoring for keys). Capture/injection
  run in a small separate helper, `H.I.B Input`, installed to
  `~/Library/Application Support/H.I.B/H.I.B Input.app` and replaced **only when its own version
  changes**, so the permission survives every H.I.B update (H.I.B itself is re-signed ad hoc on each
  install, which would void the grant). The main app talks to it over a 0600 Unix socket in that
  folder. Settings shows "Allow H.I.B Input" with a button that opens the right Privacy pane, and a
  live tick once granted.
- **Windows:** works without admin; it can't drive elevated (admin) windows or the lock screen — say
  so in one line in the Devices row's info.

## 10. Room: arrange your desk

Every app that is part of the space (the phone and each computer) appears in one **Room** view, with
each computer's real displays drawn to their physical shape and size — a 49" Odyssey OLED G9 is a long
32:9 bar, a MacBook is a laptop. The user drags computers into place (left, right, above, below,
offset) and that arrangement is what Roam's cursor follows, edge segment by edge segment.

### 10.1 Richer screen info
`screensInfo` (§7.2) and `ctlHello.screens` (§9.3) items gain:
`"name":"Odyssey OLED G9"` (friendly monitor name — Mac: `NSScreen.localizedName`; Windows:
`QueryDisplayConfig` target `monitorFriendlyDeviceName`), `"mmW":1196,"mmH":337` (physical size; Mac:
`CGDisplayScreenSize`; Windows: EDID from the monitor's registry key; 0 = unknown), `"builtIn":true|false`,
`"pxW":5120,"pxH":1440` (native pixels), `"scale":1.0`. Unknown mm → assume 0.25 mm per point/DIP.

### 10.2 Room coordinates and `roomLayout` (peer link, both ways)
- Room space is millimetres, x right, y down. Each computer is one **group**: its screens keep the OS
  arrangement among themselves (positions = OS desktop coordinates × that computer's primary screen
  mm-per-DIP; sizes = each screen's own mm). Groups are placed by an offset (gx, gy).
```json
{"type":"roomLayout","data":{"v":1,"rev":1763712000000,"by":"<computer id>",
  "groups":[{"computer":"<desktop name>","x":0,"y":0},{"computer":"PC-INTERN","x":1210,"y":-40}]}}
```
- Sent on every peer hello and whenever the user moves something. Highest `rev` (ms timestamp) wins;
  equal `rev` → lower `by` (UTF-8 bytes) wins. Both computers store and render the same room.
- **No layout yet** → auto-place with `rev:0` (any edit wins): left → right / above / below from the
  §9.2 side function (Point setup aims or manualSide), else the Mac first and others to its right;
  bottoms aligned (screens sit on one desk line).
- **Learning** (§9.2 dead-edge pushes) now moves the other computer's group so it touches the pushed
  edge (centred on it) and bumps `rev`.

### 10.3 Portals (how Roam uses the room)
- For screens a (computer A) and b (computer B ≠ A): a **portal** exists where a's edge and b's
  opposite edge are within 30 mm of each other and overlap along the edge (right↔left, left↔right,
  bottom↔top, top↔bottom). Crossing a's edge at room position p (mm along the edge) enters b at the same
  p, clamped into the overlap, then converted to b's desktop coordinates. Corners: § 9.2 rule.
- With a room layout, portals replace the §9.2 whole-desktop edges: 3 or 4 computers, above/below and
  partial overlaps all work. Without one, §9.2 stays as is.
- Settled details (both apps, so both compute identical portals):
  - A group's offset (x, y) is the room position of that computer's desktop origin (0, 0). The primary
    screen is the one whose origin is (0, 0), else the first listed; mm per point/DIP = its `mmW` / its
    width (0.25 when unknown); each other screen sits at its OS position × that factor, at its own size.
  - Points convert desktop ↔ room linearly inside each screen's own two rectangles.
  - The cursor crosses only inside a portal's overlap (the clamp only guards the ends) and enters 1
    point/DIP inside the other screen's edge. Several portals at one point: the smallest |gap|, then the
    longest overlap, then the lower computer id (UTF-8 bytes), then the lower screen index.
  - Offsets and mm are whole numbers on the wire (rounded half away from zero). An edit's
    `rev` = max(now ms, current rev + 1).
  - The same author's same-`rev` layout with other groups replaces the older one (an auto layout re-sent
    after another computer joined); a computer never adopts an older copy of its own layout.
  - Portals are used only with computers that sent `roomLayout` on the current link; with others, §9.2.
  - With nothing stored, each computer stands by its own auto layout (rev 0) and sends it; computers the
    stored layout doesn't place are auto-placed around this computer (left/right on its desk line,
    above/below centred on it). Nothing known at all: the Mac first, the others to its right, bottoms on
    y = 0.
  - Implausible sizes are sent as 0 at the source: each axis must be 0.05–1.5 mm per point/DIP and the two
    within 1.5× of each other (an aspect-only EDID, a TV claiming metres → 0).
  - Learning puts the other group touching this computer's whole group on the pushed side, centred.

### 10.4 Room view (Mac and Windows; same look)
- Opens from the Devices tab ("Arrange") and **by itself the first time a new computer joins** ("Arrange
  your desk"). Title "Your desk", subtitle "Drag your computers to where they really are".
- A front view of the desk: a soft desk line at the bottom; each display a rounded rectangle at true
  proportions (glass panel, cyan edge when hovered, the display's name and resolution inside, e.g.
  "Odyssey OLED G9 · 5120×1440"); a MacBook drawn as a laptop (screen + thin base); external monitors
  with a small stand. Each computer's screens move together; a label under each group with the
  computer's name and a "This Mac" / "This PC" badge.
- Phones/tablets in the space appear as small device chips at the side (with battery), not draggable.
- Dragging snaps a group's edges to another group's edges within 40 mm and shows the touching segment
  as a glowing cyan line (that's where the mouse will cross). A group touching nothing shows "Not
  connected to anything" in amber. Esc / Cancel restores; changes apply live (send `roomLayout`).
- Scales to fit; works with 2, 3 or 4 computers.

## 11. Point to act

Whatever the phone points at receives what the phone does — not only Flick.

### 11.1 Phone
- One shared **point target** used by: Desk Deck, the Remote (keys/arrows/text), Scenes, the computer
  media controls, and the Play on… default. While any of those screens is open, aim tracking runs;
  target = the computer owning the aimed screen (nearest within 35°, §7.6), else the target rule.
- Those screens show a chip "→ <computer>" that updates live (light haptic tick on change), and the
  phone sends `flickAim{screen, label}` so the pointed screen glows with that screen's label.
- Every action from them goes to the target computer only. Desk Deck: when the target changes, send
  `deckOpen{open:false}` to the old computer and `deckOpen{open:true}` to the new one; the deck shows
  the target's live state (CPU, memory, battery, network…).

### 11.2 `flickAim.label`
- `flickAim` gains optional `"label":"Desk Deck"` (phone-provided, short). The aim-light pill shows it
  instead of "Flick lands here" (which stays the default when absent).

### 11.3 Windows gains the actions it lacked (match the Mac's payloads exactly)
- `deckOpen` / `deckState` / `deckAction` / `deckActionResult` (Mac: Core/DeskDeck/DeskDeckManager.swift):
  lock, screensaver, play_pause, next, previous, vol_up, vol_down, mute, screenshot, phone_audio,
  mic_mute, mission_control (→ Task View), show_desktop, open_app; brightness where the display allows
  it; anything unsupported answers `deckActionResult{ok:false,error:"Not available on Windows"}`.
- `remoteControl` (Mac: WebSocketServer+Handlers.swift, MacRemoteManager) — keys, arrows, text → SendInput.
- `macMediaControl` (play/pause, next, previous, volume) → media keys / system volume.
- Windows details (settled): `deckState` carries the Mac's fields except `nextEvent` (no calendar);
  `memPressure` is "warning" from 85 % memory in use and "critical" from 95 %; `screenshot` = Win+PrintScreen
  (saved like ⌘⇧3); `screensaver` starts the screensaver when one is set, else turns the displays off;
  `mic_mute` toggles the default microphone's mute (console and communications); `brightness_*` steps DDC/CI
  monitors 10 %, else a laptop panel through WMI, else `ok:false` "This PC's display doesn't allow brightness
  control"; `vol_*` are the volume keys (Windows' own flyout) and `macVolume` follows 0.5 s later like the
  Mac; in `remoteControl` the phone's `cmd`/`command` and `ctrl`/`control` modifiers (and the ⌘/Control
  keycodes) are Ctrl, `option`/`alt` is Alt; macOS keycodes are translated to physical keys, so the PC's own
  layout applies; `macMediaControl` play/pause/playPause all toggle (Windows has one play/pause key) and
  answer `macMediaControlResponse`.

## 12. Roam, round 2 (what real use showed)

### 12.1 The cursor must be visible on a computer with no mouse of its own (Windows)
Windows hides the pointer when no mouse is attached (`GetSystemMetrics(SM_MOUSEPRESENT) == 0`), so a PC driven
from the Mac showed no cursor. While a Windows computer is **controlled** and has no mouse, turn on Mouse Keys
(`SPI_SETMOUSEKEYS` with `MKF_AVAILABLE | MKF_MOUSEKEYSON | MKF_REPLACENUMBERS`, as Deskflow's
MSWindowsComputer does) and restore the user's previous Mouse Keys setting on release, on exit and — if H.I.B
crashed while it was on — at the next start. Re-check `SM_MOUSEPRESENT` on `WM_DEVICECHANGE`.

### 12.2 Keys must reach the target, or say why
- Target (Windows): before injecting, check the foreground window's process. If it is elevated (admin) and
  H.I.B isn't, Windows (UIPI) silently drops our input: show the pill "The app in front runs as administrator
  — H.I.B can't type into it" once per app, and log it. `SendInput` returning 0 → log `GetLastError()`.
- Both sides log the key path without contents: per 10 s, counts of keys captured / sent / received / injected /
  unmapped, plus every unmapped key code. Never log characters.
- **Get a computer's log from the Mac:** the Devices row for a linked PC gets "Get log" (also in the menu-bar
  menu ▸ Diagnostics): send `peerDiag` (§3.3) over the existing peer link, save `peerDiagLog.text` to
  `~/Library/Logs/H.I.B/<computer name>.log` and reveal it in Finder. Windows offers the same for the Mac
  (the Mac answers `peerDiag` with the tail of its own log, `~/Library/Logs/H.I.B/H.I.B.log`, scrubbed — the Mac
  app writes that rolling log, ≤ 2 MB, Roam/peer/ADB lines only).

### 12.3 Smoother
- Controller: send `ctlMove` immediately when nothing is in flight; while a frame is still being written, keep
  only the newest position and send it the moment the socket is free. No fixed 8 ms timer.
- Target: apply input on a dedicated high-priority input thread — never the UI thread, never through a
  dispatcher queue, no per-move logging or allocation.
- Both: `TCP_NODELAY` (have), WebSocket compression off for the peer link, frames small.
- Show the live link latency (peer ping RTT, median of the last 20) in the Devices row: "Latency 3 ms". Above
  25 ms show the tip "Wi-Fi is adding delay — 5 GHz or a cable to one computer helps".
- Scrolling: accumulate fractional wheel deltas and send them as they come (Windows accepts sub-notch deltas;
  the Mac posts pixel scroll events), so trackpad scrolling is smooth, not stepped.

### 12.4 Games and full-screen apps (game mode)
The controller keeps a virtual cursor for the target; a game that locks the real cursor at the centre let that
virtual cursor drift to the edge and the mouse fell back to the Mac mid-game.
- Each computer watches its own foreground app and reports its state to every linked computer:
  `{"type":"ctlTargetState","data":{"mode":"normal|fullscreen|locked","app":"VALORANT-Win64-Shipping.exe"}}`
  (on change). Windows: `fullscreen` = the foreground window covers its whole monitor; `locked` = fullscreen
  and the cursor is hidden (`GetCursorInfo` flags 0) or clipped (`GetClipCursor` smaller than that monitor).
  Mac: `fullscreen` = the frontmost app has a full-screen window on that display; `locked` = also the cursor is
  hidden/disassociated. Exclude H.I.B's own windows.
- Controlling a `locked` target: send **relative** moves `{"type":"ctlRel","data":{"dx":3,"dy":-1}}` (the
  target injects them without MOUSEEVENTF_ABSOLUTE so games see raw motion), the virtual cursor is frozen and
  the mouse **never** leaves by an edge. Leave with the lock hotkey (§12.5) or when the game shows its cursor
  again (state goes back to `fullscreen`/`normal`).
- `fullscreen` (on either side, also when the PC is driving itself while a game runs on it): edges are closed
  except the **gate** — the top 15 % (at least 60 px) of each shared edge — and crossing there needs a double
  push (two pushes within 500 ms, Deskflow's switch double-tap). First push shows a small pill at the gate:
  "Game running — push again here to leave".
- A pill on entering game mode on the controlling side: "Game mode · the mouse stays on <name> · ⌃⌥⌘L to leave"
  (Windows keyboard: "Scroll Lock or Ctrl+Alt+Shift+L"). Settings: "Game mode: automatic (default) / off".

### 12.5 Deskflow features we were missing
- **Lock to this computer:** ⌃⌥⌘L on a Mac keyboard; Scroll Lock or Ctrl+Alt+Shift+L on a PC keyboard (hotkeys are
  read on the controller's own keyboard). Toggles; a pill shows "Locked to <name>".
- **Jump hotkeys:** ⌃⌥⌘← / → / ↑ / ↓ (Mac) and Ctrl+Alt+Shift+arrows (PC) jump the cursor to the computer on that
  side (centre of its main screen).
- **Lock together** (default on): locking one computer's screen locks the other linked computers
  (`{"type":"ctlScreenLock","data":{}}`); unlocking does not unlock others.
- **Keep the target awake:** while controlled, the target holds an idle-sleep/display-sleep assertion.
- **Pointer speed per computer** (Devices row slider, 0.5×–2×, default 1×) applied by the controller to deltas
  sent to that target; **scroll speed** and **natural scrolling** per target likewise.
- **Clipboard images** on crossing (PNG ≤ 8 MB) in addition to text (§9.4): `clipboard:{"text":…}` or
  `{"png":"<base64>"}`.
- **Key repeat:** forward the controller's auto-repeat key-downs; the target injects each (Windows doesn't
  auto-repeat injected keys on its own).

### 12.6 Settled (both apps)
- **Gate:** measured on the screen the cursor is on — the top 15 % of a left/right edge, the left-most 15 % of a
  top/bottom edge, at least 60 px, never longer than the edge; a neighbour (portal or §9.2 edge) is still needed there.
- **Double push:** a push = a crossing attempt inside the gate. The second must come ≤ 500 ms after the last event of
  the first, after moving back ≥ 3 px from the edge. Pushes outside the gate never count (they may show the pill).
- **`ctlRel`:** `dx`/`dy` whole counts, screen axes (dy > 0 = down), pointer speed already applied, fractions
  carried by the controller; receivers accept floats. While a frame is in flight relative motion is summed, never
  dropped; absolute `ctlMove` keeps only the newest.
- **`ctlTargetState`:** sent on every peer link-up (even `normal`) and on change; unknown modes read as `normal`.
  Game mode off on a computer = it reports `normal` and ignores the other's state.
- **Game mode exits:** the lock hotkey in game mode sends the cursor home to where it left (with `ctlLeave`). When
  the target goes back from `locked`, the controller puts its virtual cursor at the centre of that screen.
- **`ctlScreenLock`:** sent to linked computers with Roam on for the pair when the sender's "Lock together" is on;
  obeyed only when the receiver's own setting is on and its screen isn't locked yet; a lock caused by
  `ctlScreenLock` doesn't send one back (5 s quiet).
- **Clipboard:** one of `{"text":…}` or `{"png":"<base64>"}`; text wins when the clipboard has both; PNG ≤ 8 000 000
  bytes before base64, else nothing is carried; receivers ignore data that isn't a PNG.
- **Key repeat:** a repeat is just `ctlKey` down again with the same usage (no extra field).
- **Jump:** lands at the centre of the main screen (origin 0,0, else the first listed). With a room, "on that side"
  compares group centres (nearest first); without one, the §9.2 side. The hotkey's own key (L / arrow) never
  reaches the target; modifiers already sent are released as usual.
- **Latency:** each side times its own pings (pings carry no id; each pong matches the oldest outstanding ping).
  The Mac pings each linked Roam computer every 3 s (0.5 s while controlling).
- **Mac log:** `~/Library/Logs/H.I.B/H.I.B.log` keeps console lines starting `[roam`, `[room`, `[peer`, `[space`,
  `[adb…`, `[scrcpy…`, `[diag`, `[hib`, each prefixed `YYYY-MM-DD HH:MM:SS.mmm `; past 2 MB the newest 1 MB stays.
  "Get log" saves to `<name>.log` with `/`, `:` and `\` in the name replaced by `-`.

## 13. ADB that stays connected (Mac and Windows)
Whenever the phone link is up and this computer has ever had ADB working with this phone (or the phone's
`adbSetupState` says Wireless debugging is on), keep ADB connected without any button:
- try at link-up, whenever `adbSetupState.connectPort` changes, right after a network change, and every 10 s
  while disconnected (backoff to 60 s; reset on any of the triggers);
- use the phone's reported `connectPort` first, then remembered ports;
- if connecting fails with "No route to host" / refused while the phone answers on the app link, restart the
  adb server **from H.I.B** once (so it runs under H.I.B's own Local Network permission on macOS), then retry;
- show the state quietly ("ADB · connected" / "reconnecting…") and never toast on retries. The
  "Connect ADB" button stays as a manual nudge only.

## 14. Flick bundles (many at once)
- The phone throws every selected item in **one** throw. Each `flickStart` carries
  `"bundle":{"id":"…","index":0,"count":5}` (same `id`, `screen`, `at`, `exitXRatio`, `velocity` for all).
- **Computer:** a bundle lands as one stacked card (the first item on top, the next 2–3 peeking out with small
  rotations, a count badge "5"), with the usual landing spring. Click the stack → it **fans out** into a pattern
  centred on where it landed, with staggered springs:
  2 = side by side · 3 = two on top, one centred below · 4 = 2×2 · 5 = dice-five (2 top, 1 middle, 2 bottom) ·
  6 = 3×2 · 7 = 2-3-2 · 8 = 3-2-3 · 9 = 3×3 · 10+ = a grid with a "+N" card that opens the folder.
  Fanned cards work like single cards (click opens, drag out). A small "Gather" control (or Esc, or clicking
  empty space) stacks them again; dragging the **stack** drags all files at once.
- **Layout details (Windows, for the Mac to match):** one unit = a card plus a 22 pt gap; dice-five nests the middle
  card half a row between the corners; 10+ is the 3×3 grid where the 9th place is "+N" (N = count − 8); the fan
  moves to stay on screen and shrinks (never below 45 %) when it can't fit; a soft ink/cyan halo behind the cards is
  the "empty space" that gathers on click; the Gather pill also offers Show in folder.
- Files of a bundle are saved together under Downloads (as today) and the card shows progress for the whole
  bundle until all arrived.
- **Computer → phone:** a bundle arrives as one notification "5 photos from <name>" with a preview strip; tapping
  opens a viewer with all of them (Open / Share / Save all).

## 15. Phone Remote: see, drive and unlock (phase 1)
Design and reasons: `docs/superpowers/specs/2026-11-24-phone-remote-design.md`. This section is the contract.

### 15.1 Main link (`/socket`, encrypted as today)
```json
P→D {"type":"remoteHelloRequest","data":{}}
D→P {"type":"remoteHello","data":{"v":1,"id":"<computer id, §2>","platform":"mac|windows",
      "screen":true,"control":true,"unlock":"ready|needsSetup|unsupported|managed","locked":false,
      "displays":[{"id":"<§7.1 id>","name":"Odyssey G9","w":5120,"h":1440,"main":true}],
      "addrs":["100.101.102.103","fd7a:115c:a1e0::1"]}}
D→P {"type":"remoteLockState","data":{"locked":true}}           // on change, to every connected phone
D→P {"type":"remoteFocus","data":{"text":true,"secure":false}}   // on change (debounce 150 ms) while a /screen session is open
P→D {"type":"unlockRequest","data":{}}
D→P {"type":"unlockChallenge","data":{"nonce":"<base64, 32 random bytes>"}}   // valid 30 s, one use
P→D {"type":"unlockApprove","data":{"nonce":"…","sig":"<base64 DER ECDSA P-256/SHA-256>"}}
D→P {"type":"unlockResult","data":{"ok":false,"error":null|"notSetUp|wrongPassword|notLocked|failed|expired|badSignature|busy"}}
D→P {"type":"unlockEnrollRequest","data":{"name":"<computer name>"}}  // computer's setup asks the phone for its key
P→D {"type":"unlockEnroll","data":{"phoneId":"<stable random id of this phone install>","publicKey":"<base64 X.509 SPKI DER, P-256>"}}
D→P {"type":"unlockEnrollResult","data":{"ok":true}}
```
- `remoteHello` is sent on request and whenever any field changes. `control:false` / `screen:false` when the
  user turned "Allow viewing and control" off. `addrs` = this computer's Tailscale addresses: interface
  addresses inside `100.64.0.0/10` or `fd7a:115c:a1e0::/48` (empty if none). The phone remembers them per
  computer id and dials them when LAN addresses fail (start them 1 s after the LAN attempts; first open wins).
- **Signature:** `sig` = ECDSA-SHA256 (DER) over the UTF-8 bytes of `"hib-unlock-v1|" + id + "|" + nonce`
  (`id` = `remoteHello.id`, `nonce` = the base64 string exactly as sent). Android signs through
  `BiometricPrompt.CryptoObject(Signature("SHA256withECDSA"))` with a Keystore key created with
  `setUserAuthenticationRequired(true)` + auth-per-use (`BIOMETRIC_STRONG | DEVICE_CREDENTIAL`),
  `setInvalidatedByBiometricEnrollment(true)`, StrongBox if available; one key per computer id.
- **Enrolment:** accepted only while the computer's own "Set up Unlock from phone" flow is open (≤ 2 min after it
  sent `unlockEnrollRequest`), from the phone link it sent the request on. One enrolled key per computer
  (re-enrolling replaces it). The phone shows "Allow unlocking <name> with your fingerprint?" then the prompt.
- **Rate limits:** one `unlockRequest` per 5 s (else `busy`); after 3 `wrongPassword` results, unlock turns off on
  that computer (`unlock:"needsSetup"`) until set up again there.
- Windows in phase 1 reports `unlock:"managed"` when domain- or Entra-joined, else `"unsupported"`.

### 15.2 Input
Unchanged `remoteControl` (PROTOCOL.md): `mouse_move{dx,dy}`, `mouse_click{button,isDown}`, `mouse_scroll{dx,dy}`,
`keypress{keycode,modifiers}`, `type{text}`. Drag = `mouse_click left isDown:true`, moves, `isDown:false`.
On the Mac these are executed by the `H.I.B Input` helper (so they survive app updates).

### 15.3 Screen stream (`ws://<ip>:<port>/screen`)
- Opened by the phone only while the Remote picture is visible; one session per phone per computer.
- **Text frames** are encrypted exactly like `/socket` (base64 of `nonce12‖ct‖tag16`, pairing key).
  **Binary frames** are the same AES-256-GCM output **raw** (no base64): `nonce12‖ct‖tag16`.
- The first frame must be a text `screenOpen` that decrypts with the pairing key within 5 s, else close 4003.
```json
P→D {"type":"screenOpen","data":{"display":"<id or \"\" = main>","maxW":2400,"maxH":1080,"fps":60,
      "codec":"h264|jpeg","view":{"x":0,"y":0,"w":1,"h":1}}}
D→P {"type":"screenOpened","data":{"display":"<id>","w":1920,"h":540,"codec":"h264"}}   // w,h = encoded size
P→D {"type":"screenView","data":{"x":0.2,"y":0.1,"w":0.5,"h":0.5}}   // visible region, fractions of the display
P→D {"type":"screenDisplay","data":{"display":"<id>"}}                // → new screenOpened + keyframe
P→D {"type":"screenKeyframe","data":{}}
P→D {"type":"screenPing","data":{"t":<phone ms>}}  → D→P {"type":"screenPong","data":{"t":<same>}}  // every 1 s
P→D {"type":"screenStats","data":{"fps":58,"rttMs":23,"decodeMs":4}}  // every 2 s
D→P {"type":"screenPaused","data":{"reason":"locked|macPermission|stopped"}}
D→P {"type":"screenResumed","data":{}}                                // followed by config + keyframe
```
- **Binary plaintext** = 22-byte little-endian header, then payload:
  `u8 kind` (1 = config: H.264 SPS+PPS Annex-B, 2 = keyframe, 3 = frame, 4 = JPEG image) ·
  `u32 seq` · `u64 ptsMicros` · `f32 cursorX` · `f32 cursorY` (fractions of the **encoded view**, may be outside
  0…1 when the cursor is off the view) · `u8 cursorVisible` (0/1).
  H.264 payloads are Annex-B access units (start codes), Baseline/Main, no B-frames; a config frame (kind 1)
  precedes **every** keyframe (so a phone that reset its decoder after `screenKeyframe` recovers). Keyframe every 2 s and on `screenKeyframe`.
- **Cursor:** never drawn into the video; the phone draws it from the header.
- **Adaptive:** start 8 Mbps / 60 fps on a LAN route, 3 Mbps / 30 fps when the phone is on a Tailscale address
  (it says so with `"route":"tailscale"` in `screenOpen`). The server never queues more than 2 frames: when the
  socket is behind, it drops non-key frames (newest wins) and steps the bitrate down (×0.7, floor 1 Mbps), then
  fps (60 → 30 → 15); it steps back up after 5 s without drops.
- **Close codes:** 4003 bad key · 4010 screen capture not permitted (Mac: Screen Recording for H.I.B Input) ·
  4012 viewing turned off or Stop pressed (refused for 60 s after Stop) · 4013 another phone already viewing.
- **Locked:** no video while locked; `screenPaused{reason:"locked"}`, then `screenResumed` after unlock.
- **JPEG fallback** (`codec:"jpeg"`, when the phone's decoder can't do H.264): kind 4, ≤ 10 fps, quality 0.6.

### 15.4 On the computer
- While any `/screen` session is open: pill "● Controlled from <phone name>" with **Stop** (top-centre of the main
  display, click-through except the button), and a keep-awake assertion (display + system).
- "Stay reachable when locked" (default on once unlock is set up): prevent idle **system** sleep while on mains
  power (display may sleep).
- Settings (same words on both): **Phone remote** — Allow viewing and control · Unlock from phone (Set up / Change
  password / Turn off; Windows: explains managed / not yet) · Stay reachable when locked · (Mac) Screen
  Recording for H.I.B Input with live tick and Open button.
- Log: `[remote] opened by <phone> display <name> <w>x<h> <codec>`, `[remote] closed (<reason>)`,
  `[remote] unlock ok|failed <error>` — never the password.

## 16. Clipboard that just works (all three apps)
Root causes found 2026-11-25: phone copies only synced while H.I.B was on screen (Android denies background
reads: logcat "Denying clipboard access to com.sameerasw.airsync"); late/stale clips overwrote newer ones (no
ordering); one copy echoed several times (Roam-applied and BLE-applied clips skipped the sync dedupe; the Mac sent
every clip over WebSocket **and** BLE); Windows lost copies made around a reconnect, never re-sent text it once
received, and dropped a copy for good when a clipboard read failed; nothing was re-sent to a computer whose link was
coming back.

### 16.1 Every clip has an identity
`clipboardUpdate` (phone link, both directions), the Roam `clipboard` object (§9.4/§12.6) and BLE clips gain:
```json
{"text":"…" | "png":"<base64>", "id":"<16 random bytes, base64url>", "ts":1764072000123, "origin":"<device id>"}
```
- `ts` = the origin's wall-clock ms when the user copied (when its watcher first saw the change). `origin` = §2 id
  (`"phone"` for the phone). Relays (the phone) forward `id`, `ts`, `origin` **unchanged** and never send a clip back
  to its `origin` or to the link it came from.
- Missing fields (older versions): `id` = sha256 of the content (base64url, first 16 bytes), `ts` = receive time,
  `origin` = the sending link's device.

### 16.2 Rules on every device
- **Seen ids:** keep the last 64 ids applied or sent. A clip whose id was seen is ignored (kills echo loops).
- **Newest wins:** keep `localTs` = ts of what is on this device's clipboard now (its own copy time, or the applied
  clip's `ts`). Apply an incoming clip only if `ts > localTs - 1500` (1.5 s clock-skew allowance) — a late, older
  clip never overwrites a newer copy.
- **Applying is not copying:** after applying, the device records the applied content's sha256 and change marker
  (Mac `changeCount`, Windows sequence number, Android the clip description timestamp); its watcher must not send
  that change out again. Every path that writes the clipboard (phone link, Roam crossing, BLE, Flick text copy) goes
  through the one sync component so this always holds.
- **Copying the same text again is a new clip:** a user change to the clipboard always gets a new id, even if the
  text equals an earlier one.
- **Catch-up on link-up:** when a link becomes ready (phone↔computer, either side), each side sends its current clip
  (with its original `id`/`ts`/`origin`) if it is a real clip (not empty) and younger than 10 minutes; the other side
  decides with the rules above. No silent "baseline" that swallows a copy.
- **Reads that fail are retried:** if reading the clipboard fails (Windows OpenClipboard contention, busy UI thread),
  retry 5× over ~1 s without marking the change as handled.
- **One transport per clip:** the Mac sends over BLE only when the WebSocket link is down.
- **Limits:** text ≤ 1 MB; PNG ≤ 8 000 000 bytes before base64; larger → not carried (log one line).
- **Settings:** "Clipboard sync" off on a device means it neither sends nor applies (both directions, all paths
  except an explicit user action such as "Send clipboard").

### 16.3 Phone: copies made while H.I.B is in the background
- Detection: H.I.B reads its own "Denying clipboard access to com.sameerasw.airsync" log line (needs `READ_LOGS`,
  granted over ADB by the computer's existing app-ops granter: `pm grant com.sameerasw.airsync
  android.permission.READ_LOGS` and `appops set com.sameerasw.airsync SYSTEM_ALERT_WINDOW allow`). On that line (or
  on a `OnPrimaryClipChangedListener` callback that can't read), it briefly takes input focus with a 1×1 focusable
  overlay window, reads the clip, sends it, removes the overlay (≤ 300 ms, invisible).
- When `READ_LOGS` isn't granted yet: reading happens whenever H.I.B comes to the front, from the notification's
  "Send clipboard" action, the Quick Settings tile and the share sheet; Settings → Clipboard shows "Copies made in
  other apps sync once ADB has set this up" with the state.
- Android 13+ may ask "Allow H.I.B to access all device logs?" — the setup text tells the user to choose Allow.
- Image copies on the phone (clip with an image `content://` URI) are sent as `png` (re-encoded, ≤ 8 MB).

### 16.4 Diagnostics
Each app logs one line per clip event: `[clip] copied|sent|applied|ignored(seen|older|off|size) id=<first 6 chars>
from=<origin> via=<phone|roam|ble>` — never the content.
