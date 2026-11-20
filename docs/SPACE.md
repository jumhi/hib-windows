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
  - `peerHello`, `ping`, `pong`
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
