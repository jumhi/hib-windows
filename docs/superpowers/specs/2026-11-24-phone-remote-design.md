# Phone Remote: see, drive and unlock your computers from the phone

Status: design, 2026-11-24. Covers the Android app, the Mac app (+ its `H.I.B Input` helper) and the Windows app.
When built, the wire parts move into `docs/SPACE.md` as §15.

## 1. Intent and why

Haitham wants to control his computers (MacBook and the Windows PC "PC-INTERN") from his phone when he is
**not at the desk but on the same network**. He wants to **see the screen**, use the phone **as a touchpad**,
and have **the phone's keyboard pop up when he taps a text box**. His computers are **usually locked** when he
is away, so the phone must also **unlock them** (fingerprint on the phone). "Basically a remote control."

The AI agent idea (text/talk to an agent that drives the computers) is **parked**. The remote is built so an
agent can later use the same screen feed and input path, but nothing agent-related is in scope.

Everything stays in the H.I.B apps and open source where the licences allow (Android and Windows repos are
public; the Mac app must stay private because of the upstream AirSync licence).

## 2. Goals, non-goals, anti-goals

**Goals**
1. A **Remote** screen on the phone: pick a computer (and a display), see it live, smoothly (target 30 fps,
   60 when the network allows; under ~150 ms glass-to-glass on home Wi-Fi).
2. Touchpad control over the live picture (relative cursor, like a laptop trackpad).
3. Automatic keyboard: tapping/clicking into a text field on the computer opens the phone keyboard; leaving it
   closes it. A ⌨ button forces it. A key strip adds Esc, Tab, ⌘/Ctrl, Alt/Option, arrows.
4. **Unlock from phone** on both computers with the phone's fingerprint (or phone PIN), and **Lock** when done.
5. While viewed/controlled, the computer stays awake and shows a small visible notice.
6. Works with the G9 (5120×1440) usefully: pinch-zoom, view follows the cursor, text stays crisp when zoomed.
7. **Works from home too** (added 2026-11-24): over a private Tailscale network (§6.8), the same remote,
   picture and unlock work when the phone is on another network.
8. A locked computer **stays awake and reachable while on mains power**, so "away and locked" works.

**Non-goals (this round)**
- The AI agent; voice control.
- Our own internet relay server (Tailscale/Headscale carries the traffic instead).
- Waking a computer that is fully asleep / off Wi-Fi; Wake-on-LAN. (Goal 8 keeps it from sleeping instead.)
- Signing in after a restart (Mac FileVault pre-boot screen, first Windows sign-in after boot).
- Computer audio to the phone; file drag from the phone into the remote view.
- Viewing the lock screen itself (see §6.4: neither OS lets an app capture it).

**Anti-goals (would make it a failure even if it "works")**
- Anything that lets something other than **the paired phone + a fresh biometric/PIN on that phone** unlock a
  computer (replayed messages, a copied pairing key, a modified phone app, another device on the network,
  another user account on the PC). Honest boundary: code already running as the signed-in user on that
  computer is trusted (it could read that user's stored secrets anyway).
- The login password ever leaving the computer it belongs to, being stored on the phone, logged, or printed.
- Remote control that the person sitting at the computer can't notice.
- A Mac permission (Screen Recording / Accessibility) that has to be re-granted after every H.I.B update.
- Laggy/jerky video that makes the touchpad feel broken (better lower resolution than stutter).

## 3. Constraints and facts the design rests on

- **Link:** the phone is a WebSocket client of each computer at `ws://<ip>:6996/socket` (Mac: Swifter;
  Windows: own `LinkServer`; Android: OkHttp, one `DesktopSession` per computer). Every frame today is a **text**
  frame `base64(nonce12‖ct‖tag16)`, AES-256-GCM with that computer's pairing key. **No binary frames exist yet**
  (both servers currently ignore them). Windows caps messages at 96 MB.
- **Mac input from the phone today breaks after every update:** `MacRemoteManager` posts CGEvents from the
  main app, which loses Accessibility on each ad-hoc re-sign. This feature moves it into the helper (§6.3).
- **macOS 15+ Screen Recording nag:** apps capturing without the system picker get a recurring "keep allowing?"
  prompt; avoiding it needs a restricted Apple entitlement that ad-hoc signing can't have. Accepted as a known
  limit (§6.4).
- **Existing phone trackpad:** `RemoteControlScreen.kt` (pager page 1) already sends `remoteControl`
  (`mouse_move{dx,dy}` relative, `mouse_click{button,isDown}`, `mouse_scroll`, `keypress`, `type{text}`,
  `lock_screen`, media/volume) through `PointTarget.send`, with 10 ms batching. Both computers handle these
  (Mac `MacRemoteManager` posts CGEvents from the main app; Windows `DeskDeck.cs` → `WindowsDesktopControl`
  with Unicode `Text()`).
- **Existing frame display on the phone:** `screenFrame` JPEG messages (from the separate iOS host project) are
  decoded with `BitmapFactory` and shown by `IPhoneMirrorOverlay`. No MediaCodec anywhere yet.
- **No screen capture or video encoding exists on Mac or Windows today.** No Screen Recording permission
  handling. No focused-element (AX / UI Automation) code anywhere.
- **Mac signing:** the main app is re-signed ad hoc on every install, which voids its TCC grants. Only the
  `H.I.B Input` helper (copied to `~/Library/Application Support/H.I.B/` and replaced only when **its** version
  changes) keeps grants. Its socket `input.sock` is 0600, same-uid only, newline JSON. Bumping its version forces
  one Accessibility re-grant (the user accepts one for this feature).
- **Lock state:** Mac knows via `com.apple.screenIsLocked/Unlocked` and `CGSSessionScreenIsLocked`; Windows via
  WTS session notifications and `OpenInputDesktop` failure. Lock commands exist on both.
- **Phone biometrics:** `ApprovalActivity` already uses `BiometricPrompt(BIOMETRIC_STRONG | DEVICE_CREDENTIAL)`.
- **Secrets:** Mac Keychain wrappers exist (`PhoneAgentKeychain` pattern); Windows DPAPI (`ApiKeyStore`).
- Never read Keychain via the `security` CLI; never print keys or passwords; never test by injecting real input
  on the user's Mac.

## 4. Approaches considered

| | Approach | Trade-off |
|---|---|---|
| **A (picked)** | Build the remote **inside H.I.B**: computers stream their screen over a new path on the existing server; reuse `remoteControl` for input; add unlock on both computers. | Most work, but one app, works for his friend too, built on what exists, and the future agent can reuse it. |
| B | H.I.B as viewer/chat, **OpenClaw** as the brain/controller on each computer. | Was about the agent; parked with it. Extra install per computer, past security issues. |
| C | Glue: **RustDesk** for screen + OpenClaw on WhatsApp. | Fastest, but three apps, not H.I.B; no unlock. |

Haitham picked "inside H.I.B" and parked the agent.

**Video sub-choice (builder considered, my call):** H.264 hardware encode → MediaCodec decode on the phone,
over **binary** WebSocket frames. The existing JPEG `screenFrame` path (~7 fps, heavy on Wi-Fi) stays only as the
fallback when the phone's decoder fails to configure.

## 5. What the user sees (phone)

```
┌───────────────────────────────┐
│ ◀  [Mac] [PC-INTERN ●]   ▾ G9 ⋯│  computer chips (live dot) · display picker · menu
│ ┌───────────────────────────┐ │
│ │                           │ │  live screen; pinch = zoom the view,
│ │      live picture    ↖    │ │  view follows the cursor when zoomed
│ │                           │ │  whole picture is the touchpad
│ └───────────────────────────┘ │
│ 23 ms · 60 fps            🔒   │  quiet latency readout · Lock button
│ [⌨][Esc][Tab][⌘][⌥][←][↑][↓][→]│  key strip (labels match the target: Ctrl/Alt on PC)
└───────────────────────────────┘
```

- **Entry points:** the existing Remote page (pager page 1) gains the live picture above/behind the trackpad
  (the current trackpad-only layout stays as "Picture off" in ⋯ for slow networks); each computer card on Home
  gets a **Remote** quick action that opens it for that computer. The Remote always uses the computer chosen on
  its own chips (explicit pick), never the "pointed at" computer.
- **Orientation:** portrait and landscape; landscape gives the picture the full width (good for the G9).
- **Touchpad gestures (over the picture):**
  | Gesture | Does |
  |---|---|
  | one finger slide | move cursor (relative, with acceleration; existing ×1.8 feel as default) |
  | tap | left click (at the cursor, not at the finger) |
  | two-finger tap | right click |
  | two-finger slide | scroll (natural-scrolling setting respected) |
  | double-tap and hold, then slide | drag (left button held until lift) |
  | pinch | zoom the **view** (1× fit … 6×), never sends anything to the computer |
  | three-finger slide | pan the zoomed view by hand (turns off follow-cursor until next pinch-out) |
- **Keyboard:** opens by itself when the computer reports a text field got focus (§6.5), closes when focus
  leaves one. Typing sends text as it is typed (commit-by-commit, not a "Send" box); backspace/enter work;
  password fields still open it but the phone keyboard is put in no-suggestions/no-learning mode. ⌨ forces it.
- **Locked computer:** the picture area becomes a card: "PC-INTERN is locked" + big **Unlock** button. Tap →
  phone fingerprint/PIN prompt → "Unlocking…" → live picture appears. Failure: "Couldn't unlock — check the
  password in H.I.B on PC-INTERN" (no detail about why beyond that).
- **Unreachable:** "Can't reach PC-INTERN. It may be asleep." with the last time seen. When away from home and
  Tailscale is off on the phone: "Turn on Tailscale to reach your computers from here" with an Open button.
- **Route chip:** next to the latency readout, "Home Wi-Fi" or "Tailscale" (and "Tailscale · relayed" when
  traffic goes through a relay, with the picture lowered automatically).
- **Mac screen-recording nag:** if the Mac stops the picture because macOS is asking "keep allowing?", the
  picture area says "Your Mac is asking to keep allowing screen recording. Control still works; the picture comes
  back after someone clicks Allow." Touchpad and keyboard keep working.
- **Unlock not set up:** the locked card says "Set up Unlock from phone on PC-INTERN first" and explains where.
- **Lock button** 🔒 locks the computer and (if this phone unlocked it in this session) leaves the Remote.

## 6. How it works

### 6.1 Messages (main `/socket` link, encrypted as today)
```json
{"type":"remoteHello","data":{"v":1,"screen":true,"unlock":"ready|needsSetup|unsupported","locked":false,
  "displays":[{"id":"…","name":"Odyssey G9","w":5120,"h":1440,"main":true}]}}          // computer → phone, on request & on change
{"type":"remoteHelloRequest","data":{}}                                                // phone → computer
{"type":"remoteFocus","data":{"text":true,"secure":false}}                             // computer → phone while a session is open
{"type":"remoteLockState","data":{"locked":true}}                                      // computer → phone on change while a session is open
{"type":"unlockChallenge","data":{"nonce":"<base64 32 bytes>"}}                       // computer → phone, answer to unlockRequest
{"type":"unlockRequest","data":{}}                                                     // phone → computer
{"type":"unlockApprove","data":{"nonce":"<same nonce>","sig":"<base64 ECDSA P-256 signature>"}} // phone → computer, after BiometricPrompt
{"type":"unlockEnroll","data":{"phoneId":"…","publicKey":"<base64 X.509 SubjectPublicKeyInfo, P-256>"}} // phone → computer, at setup
{"type":"unlockEnrollResult","data":{"ok":true}}                                       // computer → phone
{"type":"unlockResult","data":{"ok":true,"error":null|"notSetUp|wrongPassword|notLocked|failed|expired"}}
```
- Input reuses `remoteControl` unchanged (relative `mouse_move`, `mouse_click`, `mouse_scroll`, `keypress`,
  `type`). Drag needs no new action: `mouse_click left isDown:true`, moves, then `isDown:false`.
- `keypress` keycodes stay macOS virtual keycodes with `modifiers` (Windows maps them as today; ⌘ = Ctrl on PC).

### 6.2 The screen stream (`/screen` path, same port)
- The phone opens `ws://<ip>:6996/screen` **only while the Remote picture is visible** for that computer, and
  closes it when leaving, backgrounding, or switching computer.
- **First frame (text, encrypted like the main link):**
  `{"type":"screenOpen","data":{"display":"<id>","maxW":2400,"maxH":1080,"fps":60,"codec":"h264",
   "view":{"x":0,"y":0,"w":1,"h":1}}}` — `view` is the visible region as fractions of the display.
  The server replies `{"type":"screenOpened","data":{"w":…,"h":…,"codec":"h264"}}` or closes with 4003 (bad key)
  / 4010 (screen capture not permitted — the phone shows the computer-side fix) / 4011 (locked).
- **Video:** **binary** frames. Each frame = AES-256-GCM (pairing key) of
  `header ‖ payload`, sent raw (no base64): `nonce12 ‖ ct ‖ tag16`.
  Plain header (little-endian): `u8 kind (1=config SPS/PPS, 2=keyframe, 3=frame) · u32 seq · u64 ptsMicros ·
  f32 cursorX · f32 cursorY (display fraction, NaN = hidden) · u8 cursorVisible`, then the H.264 Annex-B access unit.
- **Control on the same socket (text):** `screenView{view}` when the user pinches/pans (server re-crops and keeps
  encoding at the phone's size, so zoomed text is sharp), `screenDisplay{display}`, `screenKeyframe{}` (decoder
  lost sync), `screenStats{fps, rtt}` every 2 s from the phone for adaptive bitrate.
- **Adaptive quality:** start 8 Mbps / 60 fps; drop bitrate, then fps (30), then size if the server's send queue
  holds more than ~2 frames; never queue more than 2 frames — drop the oldest non-key frames (latest wins).
- **Cursor:** drawn by the phone from the header (so it moves at frame rate and is visible even when the OS
  hides it), not burned into the video.
- **Fallback:** if MediaCodec can't configure H.264, the phone asks `codec:"jpeg"` and the server sends
  `kind 4 = JPEG` frames at up to 10 fps through the same framing.

### 6.3 Capture and encode per computer
- **Mac input moves to the helper:** all `remoteControl` mouse/keyboard actions from the phone are carried out
  by the `H.I.B Input` helper (new helper messages for relative move, click, scroll, key with modifiers, and
  Unicode `text`), not by `MacRemoteManager` in the main app, so phone control keeps working after updates.
- **Mac:** capture with **ScreenCaptureKit** and encode with **VideoToolbox** (hardware H.264, real-time,
  no B-frames, keyframe every 2 s or on request) **inside the `H.I.B Input` helper**, so the Screen Recording
  grant survives H.I.B updates (same reason the helper exists). The helper streams encoded access units to the
  app over a second 0600 Unix socket `screen.sock` (same-uid check as `input.sock`); the app wraps and sends
  them to the phone. Helper version is bumped once for this feature (one re-grant of Accessibility, plus a
  first-time Screen Recording grant). H.I.B Settings shows "Screen Recording for H.I.B Input" with a live tick
  and an Open button, like the Accessibility row.
- **Windows:** **Desktop Duplication** (DXGI) capture + **Media Foundation** hardware H.264 encoder, in-process
  in the H.I.B app (no admin, no extra permission). Library choice (raw P/Invoke vs a permissive wrapper such as
  Vortice) is the builder's; must be MIT/BSD/Apache compatible with MPL 2.0 and listed in THIRD_PARTY_NOTICES.
  Software fallback if no hardware encoder.
- **Displays:** `displays` matches the ids already used by `screensInfo` (§7.1 of SPACE.md) so the Room and the
  remote agree on names.

### 6.4 Locked screens
- Neither OS lets a normal app capture the lock screen (Mac shield window; Windows secure desktop). So while
  locked the server sends no video, `remoteLockState{locked:true}` goes to the phone, and the phone shows the
  locked card. When it unlocks, the stream resumes on its own (new keyframe).
- **Mac Screen Recording nag:** when capture stops because macOS wants the "keep allowing?" answer, the helper
  reports it, the server sends `screenPaused{reason:"macPermission"}` on `/screen`, and the phone shows the
  message from §5. Input keeps working.

### 6.4b Staying reachable while locked
- Setting "Stay reachable when locked" (default **on** once Unlock from phone is set up, otherwise off):
  - only while on mains power (MacBook on battery: normal sleep, to protect the battery);
  - Mac: `IOPMAssertionCreateWithName(kIOPMAssertionTypePreventUserIdleSystemSleep)` (the display may still
    sleep); MacBook lid closed still follows macOS rules (sleeps unless clamshell with external display).
  - Windows: `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` (display may sleep); note in the row
    that "Wi-Fi power saving can still drop the connection" when the adapter is wireless.
  - Shown in Settings as "Stays awake while locked and plugged in, so your phone can reach it."

### 6.5 Focus → keyboard
- While a `/screen` session is open, each computer watches keyboard focus and sends `remoteFocus` on change
  (debounced 150 ms):
  - **Mac:** in the **helper** (it has Accessibility; the main app doesn't keep it): AX focused element; text if
    role is `AXTextField`, `AXTextArea`, `AXComboBox`, `AXSearchField`, or the element is editable
    (`AXValue` settable / `AXInsertionPointLineNumber` present); `secure` for `AXSecureTextField`.
    Reported to the app over `input.sock` as a new helper message.
  - **Windows:** UI Automation focus-changed event; text if control type Edit/Document/ComboBox(editable) or it
    supports a writable ValuePattern/TextPattern; `secure` if `IsPassword`.
  - Apps with no accessibility info (some games, Electron edge cases) never auto-open; the ⌨ button covers them.

### 6.6 Unlock from phone
**Flow:** phone `unlockRequest` → computer replies `unlockChallenge{nonce}` (fresh 32 random bytes, valid 30 s,
one use, only when the computer is locked and unlock is set up) → phone shows BiometricPrompt
(`BIOMETRIC_STRONG | DEVICE_CREDENTIAL`, title "Unlock PC-INTERN") → `unlockApprove{nonce}` → computer unlocks →
`unlockResult`. The nonce defeats replay of a captured encrypted frame (GCM alone doesn't).

**The fingerprint is proven to the computer, not just checked on the phone:** at setup the phone creates an
Android Keystore EC P-256 key per computer with `setUserAuthenticationRequired(true)` (auth-per-use,
`BIOMETRIC_STRONG | DEVICE_CREDENTIAL`, `setInvalidatedByBiometricEnrollment(true)`, StrongBox when available) and
sends the public key in `unlockEnroll`. The computer accepts it only after the person at the computer confirms
"Allow <phone> to unlock this computer?" in H.I.B (done during "Set up Unlock from phone", so the user is
there). `sig` = ECDSA-SHA256 over UTF-8 `"hib-unlock-v1|" + computerId + "|" + nonce`, produced through a
`BiometricPrompt.CryptoObject(Signature)`; the computer verifies with the enrolled key. A copied pairing key or
a modified app can't produce it. If the phone's fingerprints change, the key is invalidated and the phone says
"Set up unlock again on <computer>".

Only one unlock
attempt per 5 s; after 3 failed password attempts, unlock-from-phone turns itself off on that computer until
re-enabled there.

**Mac (like the open-source BLEUnlock):**
- Setup in H.I.B Settings → "Unlock from phone": the user types the Mac login password once. The app passes it
  over `input.sock` to the **helper**, which stores it as a generic password in the **login keychain**, created
  by the helper (so its access list names the helper binary), and verifies it first (e.g. via `ODRecord verifyPassword` or equivalent) so a typo is caught at setup.
- Unlock: the app tells the helper `unlock{}`; the helper wakes the display (user-activity assertion), makes
  the password field active (a key press), types the password with CGEvents, presses Return, then confirms the
  unlock via `com.apple.screenIsUnlocked` within 5 s → `unlockResult`.
- The password never travels to the app's memory except during setup, and never to the phone.
- The login keychain normally stays unlocked while the screen is locked. If the helper can't read the item
  (keychain locked by a "lock when sleeping" setting, or the helper binary changed after a helper update), the
  result is `notSetUp` and H.I.B on the Mac shows "Enter your password again to keep Unlock from phone working".
  Helper updates are rare (only when its code changes), so this is acceptable.

**Windows (credential provider, like Proxlock / Remote Fingerprint Unlock):**
- A small **H.I.B sign-in add-on**: a native COM credential provider DLL loaded by LogonUI. Installed by the
  "Set up Unlock from phone" button in H.I.B (one UAC prompt) into an **admin-only folder**
  (`%ProgramFiles%\H.I.B\SignIn\`, never the per-user install folder, which the user can write) plus its HKLM
  registration. "Turn off" in H.I.B and the H.I.B uninstaller remove both (one UAC prompt each).
- **Managed PCs:** before offering setup, H.I.B checks whether the PC is domain/Entra-joined or has policy that
  blocks third-party credential providers or requires Windows Hello only. If so, setup is not offered and
  Settings says "Your organisation manages this PC, so Unlock from phone isn't available here." (Viewing and
  control still work.)
- **The lock-screen curtain:** the builder must verify when LogonUI instantiates providers (on lock vs. after
  the curtain is dismissed). If the pipe exists only after the curtain, the provider must still be reachable
  on the first lock-screen state (e.g. the provider is loaded with the curtain and auto-submits, which is what
  existing phone-unlock products achieve). This is a first-spike item.
- The user enters their Windows password once in H.I.B (for a Microsoft account: the account password, not the
  PIN); H.I.B verifies it (`LogonUser`) and stores it with DPAPI (CurrentUser) like `ApiKeyStore`.
- Unlock: the H.I.B app (still running in the locked session) connects to the provider's **named pipe**, whose
  ACL allows only SYSTEM and the signed-in user's SID; it authenticates with a per-install secret (32 random
  bytes written at setup to a file readable only by SYSTEM and that user) using an HMAC over a provider-issued
  nonce, then sends username/domain/password. The provider builds a `KERB_INTERACTIVE_UNLOCK_LOGON` and
  auto-submits (`CredentialsChanged` → `GetSerialization`). Without a valid HMAC the provider ignores the pipe
  (unlike hodor, where any app can unlock). The pipe ACL and HMAC keep out other user accounts and
  lower-integrity processes; same-user code is inside the trust boundary (see anti-goals).
- The phone's signed approval (above) is verified by the H.I.B app **before** it talks to the pipe.
- The provider only ever appears as one extra tile ("H.I.B") on the lock screen and does nothing on its own.

### 6.7 On the computer while controlled
- Notice pill near the top-centre of the main display: "● Controlled from <phone name>" with a **Stop** button
  (Stop closes the stream and refuses new `/screen` opens for 60 s). Shown as long as a `/screen` session is open.
- Keep-awake assertion while a session is open (reuse Roam's `RoamAwake` / `SetThreadExecutionState`).
- Log lines `[remote] opened by <phone> display <name>`, `[remote] unlock ok|fail <reason>` (never the password).
- If Roam is currently controlling this computer from the other computer, the phone still works (input just
  mixes, like two mice); not special-cased.

### 6.8 From home: Tailscale
- **Why Tailscale:** open-source clients, free personal plan, end-to-end WireGuard, works through university/home
  NAT without opening ports; **Headscale** (open-source control server) is a drop-in for people who want no
  company in the middle. H.I.B does not bundle or control Tailscale; the user installs it on the phone and each
  computer and signs in to the same tailnet (Settings links to it with a three-step explainer).
- **Addresses:** each computer adds its Tailscale addresses (100.64.0.0/10 and fd7a:115c:a1e0::/48, plus the
  MagicDNS name when known) to the addresses it already reports to the phone (`macInfo`/device info and
  `desktopsList` `ips`). The phone remembers them per computer.
- **Dialling:** the phone tries LAN addresses first (as today), then the Tailscale ones in parallel after 1 s;
  first to open wins. Discovery broadcasts don't cross Tailscale, so remembered addresses are what make it work.
  The peer link between computers may use Tailscale addresses the same way (low priority).
- **Encryption stays H.I.B's own** (AES-GCM pairing key) on top of WireGuard.
- **Quality:** on a Tailscale route the stream starts at 3 Mbps / 30 fps and adapts (§6.2). If the phone can tell
  the route is relayed (DERP: Tailscale's `tailscale status` isn't available on Android, so use RTT > 80 ms as
  the signal), it caps at 2 Mbps and shows "relayed".
- **Away from home and Tailscale off:** the phone sees no route and shows the hint from §5.
- **Work/university computers:** installing Tailscale may be blocked or against IT rules; Settings' explainer
  says so plainly.

### 6.9 Settings
- **Computer (Mac and Windows, same words):** "Phone remote" section: Allow viewing and control (default on),
  Unlock from phone (setup / change password / turn off), Screen Recording status (Mac), Sign-in add-on status
  (Windows).
- **Phone:** Remote ⋯ menu: Picture on/off, Quality (Auto / Sharper / Smoother), Natural scrolling,
  Pointer speed.

## 7. Parts decided by Haitham
- Remote control first; AI agent later.
- Screen visible on the phone; touchpad (not direct touch); keyboard pops up on text fields.
- Computers are usually locked when he's away → unlock from the phone is required.
- Built inside H.I.B, open source where allowed.
- It must also work from home (another network) → Tailscale.

## 8. My calls (Haitham can overrule)
- H.264 over binary WebSocket frames on a new `/screen` path; JPEG only as fallback.
- Mac capture, focus watching and unlock live in the `H.I.B Input` helper (one re-grant, then stable).
- Unlock needs biometric **or** phone PIN (BiometricPrompt with device credential), matching Touch-ID-on-phone.
- Cursor drawn by the phone; view follows cursor when zoomed; server crops to the zoomed region.
- Locked computers show a card, not a picture (the OS won't allow capturing the lock screen).
- A visible "Controlled from phone" pill with Stop on the computer.
- Unlock is proven with a fingerprint-bound Keystore signature, enrolled with confirmation at the computer.
- Locked computers on mains power stay awake by default once unlock is set up.
- Mac phone input moves into the helper; Mac password lives in the login keychain, owned by the helper.
- Windows sign-in add-on in an admin-only folder; not offered on organisation-managed PCs.
- The macOS screen-recording nag is accepted as a known limit (picture pauses, control continues).
- Tailscale (or Headscale), installed by the user, is the from-home path; no H.I.B relay server.

## 8b. PC-INTERN is organisation-managed (answered 2026-11-24)
- Haitham's PC-INTERN is an IT-managed work/university PC. Consequences:
  - **No sign-in add-on on PC-INTERN.** Security tools on managed PCs commonly treat a third-party credential
    provider as credential theft, and policy usually blocks it. H.I.B detects a managed PC and never offers it.
  - **Tailscale on PC-INTERN only if IT allows it.** H.I.B never tunnels into a managed network on its own
    (no subnet routing through the Mac, no relay). Settings' explainer says to check with IT.
  - Viewing and control on PC-INTERN work on the same network as today, unlocked.
- **Phasing (my call):**
  - **Phase 1:** the Remote on both computers (picture, touchpad, auto keyboard, notice pill, keep-awake),
    Mac phone input moved to the helper, Unlock from phone on the **Mac**, Tailscale address support,
    managed-PC detection on Windows.
  - **Phase 2:** the Windows sign-in add-on (useful on personal PCs such as his friend's; needs the MinGW-w64
    download approved first).

## 9. Left to the builder
- Windows capture/encode library choice (within licence limits); exact bitrate ladder.
- Toolchain for the Windows credential provider DLL (native C/C++; cross-compiling from the Mac needs a
  MinGW-w64 toolchain — **installing it needs Haitham's go-ahead** as a download).
- Animations, exact layout of the locked card and key strip (must follow the H.I.B look and words).
- Test strategy: logic (framing, crypto, nonce/expiry, adaptive ladder, gesture → message mapping, focus rules)
  covered by unit tests on all three; real capture/unlock verified on the real machines by Haitham.
