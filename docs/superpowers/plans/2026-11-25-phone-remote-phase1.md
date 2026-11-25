# Phone Remote (phase 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The phone shows a computer's live screen, drives it as a touchpad with an auto-popping keyboard, unlocks the Mac with a fingerprint-signed approval, and reaches computers over Tailscale.

**Architecture:** Computers serve a new `/screen` WebSocket path (H.264 in encrypted binary frames) next to `/socket`; input reuses `remoteControl`; unlock is a nonce challenge answered with a Keystore signature. On the Mac, capture, encode, focus watching, input and unlock run in the `H.I.B Input` helper (stable TCC grants); the app relays.

**Tech Stack:** Android (Kotlin, Compose, OkHttp 5, MediaCodec, AndroidX Biometric, Keystore) · Mac (Swift, Swifter, ScreenCaptureKit, VideoToolbox, AX, CGEvent, Security) · Windows (.NET 11, Avalonia, DXGI Desktop Duplication, Media Foundation, UI Automation, SendInput).

**Spec:** `hib-windows/docs/superpowers/specs/2026-11-24-phone-remote-design.md`; wire contract `hib-windows/docs/SPACE.md` §15 (authoritative for every name, field, value and close code).

## Global Constraints
- Wire names, fields, header layout (22 bytes LE), close codes 4003/4010/4012/4013, rates and limits exactly as SPACE.md §15.
- Encryption: AES-256-GCM pairing key; text frames base64(`nonce12‖ct‖tag16`); binary frames raw `nonce12‖ct‖tag16`.
- Signature string `"hib-unlock-v1|" + id + "|" + nonce`; ECDSA P-256 SHA-256 DER; public key X.509 SPKI DER base64.
- Never log, print or send a password, pairing key or private key; log lines per §15.4 only.
- Same words on Mac and Windows; device-neutral wording; H.I.B look (navy glass, cyan #20D9FF accents).
- Mac: helper version 1.0 (1) → **1.1 (2)** exactly once in this work; never run taps / post real events / capture the user's screen during tests.
- Windows deps must be MIT/BSD/Apache (MPL-compatible) and added to THIRD_PARTY_NOTICES.md.
- Phase 1 excludes the Windows credential provider; Windows reports `unlock:"managed"` or `"unsupported"`.
- Git: Mac branch `hib`, Android `hib`, Windows `main`; work in a worktree branch, merge back; push only to `private`.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus
1. Phone backgrounded / screen off mid-stream → `/screen` closes, computer's pill and keep-awake end within 5 s (server-side: socket closed or no `screenPing` for 5 s ends the session). Test on each computer: session ends on 5 s ping silence.
2. Display unplugged or resolution change while streaming → new `screenOpened` + config + keyframe, no crash. Test: capture source reports new size → encoder reconfigures and emits config before keyframe.
3. Replayed or late `unlockApprove` (same nonce twice, after 30 s, wrong id in signed string) → `expired`/`badSignature`, nothing typed. Test on Mac logic.
4. Computer locks while streaming, then unlocks → `screenPaused{locked}` then `screenResumed` + keyframe; phone shows locked card then picture again. Test: lock-state transitions drive the session state machine.
5. Slow Wi-Fi / Tailscale relay → server never holds more than 2 frames; bitrate steps ×0.7 to 1 Mbps floor, fps 60→30→15; steps up after 5 s clean. Test the ladder as pure logic on both computers.

---

### Task 1 (Mac): Helper 1.1 — input, text, focus, unlock, capture
**Files:** Modify `HIBInput/InputHelper.swift`, `HIBInput/HelperSocket.swift`; Create `HIBInput/HelperScreen.swift` (SCStream + VTCompressionSession), `HIBInput/HelperFocus.swift` (AX focus watcher), `HIBInput/HelperUnlock.swift` (login-keychain item owned by the helper, verify, wake + type + Return, confirm via `com.apple.screenIsUnlocked` ≤ 5 s); bump HIBInput `MARKETING_VERSION 1.1`, `CURRENT_PROJECT_VERSION 2` in `AirSync.xcodeproj/project.pbxproj`.
**Interfaces (input.sock JSON, newline-delimited; new):**
- app→helper: `rmove{dx,dy}`, `text{s}`, `focusWatch{on:Bool}`, `screenStart{display,maxW,maxH,fps,bitrate,view{x,y,w,h}}`, `screenUpdate{bitrate?,fps?,view?,display?}`, `screenKeyframe`, `screenStop`, `unlockSet{p}`, `unlockClear`, `unlockHas`, `unlock{}`.
- helper→app: `focus{text,secure}`, `screenErr{reason:"permission"|"locked"|"failed"}`, `unlockSetResult{ok,error?}`, `unlockHasResult{has}`, `unlockResult{ok,error?}`; `status` gains `screen:Bool` (CGPreflightScreenCaptureAccess).
- Video: second 0600 same-uid socket `screen.sock` in the same folder; each record = `u32 LE length` + the §15.3 22-byte header (seq/pts/cursor filled by the helper) + payload.
- [ ] Pure-logic tests first (tools/tests runner pattern): AX role → `text/secure` mapping; record framing round-trip; unlock state (has/needs, 3 wrong → cleared).
- [ ] Implement; build Release with `CODE_SIGNING_ALLOWED=NO`; tests pass; commit.

### Task 2 (Mac): App side — `/screen`, remote messages, unlock, settings, pill
**Files:** Modify `airsync-mac/Core/WebSocket/WebSocketServer.swift` (route `/screen`, binary send), `WebSocketServer+Handlers.swift` (`remoteHelloRequest`, `unlockRequest/Approve/Enroll`; `remoteControl` mouse/keyboard → helper via `RoamInput`), `Core/Roam/RoamInput.swift` (new helper messages); Create `Core/Remote/Phone/RemoteSessionLogic.swift` (pure: session state machine, adaptive ladder, ping timeout, close codes), `RemoteSession.swift`, `RemoteUnlockLogic.swift` (pure: nonce issue/expiry/one-use, signed-string build, P256 DER verify, rate limit), `RemoteUnlock.swift`, `RemoteTailscale.swift` (addrs in 100.64/10, fd7a:115c:a1e0::/48), `RemotePill.swift`, `Screens/Settings/PhoneRemoteSettingsView.swift`.
**Interfaces:** consumes Task 1 messages; produces §15 wire behaviour.
- [ ] Tests (tools/tests/run_space_tests.sh new block): nonce one-use/30 s expiry; signature verify with a fixed test vector (generate P-256 key in test, sign, verify; tampered id → badSignature); ladder steps (×0.7, floor 1 Mbps, 60→30→15, up after 5 s); ping-silence 5 s ends session; locked→paused→resumed; Tailscale address filter.
- [ ] Implement; settings row and pill per §15.4; build; all Mac suites pass; commit.

### Task 3 (Android): Stream client + decoder + Remote UI
**Files:** Create `remote/ScreenStreamClient.kt` (OkHttp `/screen`, crypto, framing, pings, stats), `remote/ScreenFrame.kt` (pure header parse), `remote/H264Decoder.kt` (MediaCodec → Surface; JPEG fallback), `remote/RemoteViewState.kt` (pure: zoom/pan/follow-cursor math, view fractions), `remote/RemoteGestures.kt` (pure gesture → `remoteControl` mapping: tap, two-finger tap, two-finger scroll, double-tap-hold drag, pinch never sends); Modify `presentation/ui/screens/RemoteControlScreen.kt` (computer chips, display picker, picture, key strip, auto keyboard from `remoteFocus`, locked/unreachable/Tailscale cards, route chip, latency), `multidesk/ComputersCard.kt` (Remote quick action), `utils/WebSocketMessageHandler.kt` / `multidesk/DesktopSession.kt` (route `remoteHello`, `remoteLockState`, `remoteFocus`, unlock replies; remember `addrs`; dial Tailscale addrs 1 s after LAN).
- [ ] Unit tests: header parse (all 4 kinds, NaN cursor), crypto round-trip binary vs text, view math (zoom 1–6×, follow keeps cursor inside 20 % margin), gesture mapping table, addr fallback ordering.
- [ ] Implement; `assembleDebug`; tests pass; commit.

### Task 4 (Android): Unlock with Keystore signature
**Files:** Create `remote/RemoteUnlockKeys.kt` (per-computer EC P-256 Keystore key, alias `hib_unlock_<sha256(id)>`), `remote/RemoteUnlockFlow.kt` (request → challenge → BiometricPrompt CryptoObject → approve), enrol consent UI (`unlockEnrollRequest` → dialog → key → `unlockEnroll`); invalidated key → "Set up unlock again on <name>".
- [ ] Tests: signed-string builder exact bytes; SPKI encoding base64; state machine (challenge expiry, busy, error texts).
- [ ] Implement; tests pass; commit.

### Task 5 (Windows): `/screen` server, capture/encode, focus, pill, settings
**Files:** Modify `src/HIB/Core/Link/LinkServer.cs` (`/screen` path, binary send); Create `src/HIB/Core/Remote/RemoteSessionLogic.cs` (same pure logic as Mac Task 2), `RemoteSession.cs`, `Windows/DesktopDuplicationCapture.cs`, `Windows/MfH264Encoder.cs` (hardware MFT, software fallback, JPEG fallback), `Windows/UiaFocusWatcher.cs`, `RemoteHello.cs` (displays from §7.1 ids, `unlock` managed/unsupported via `NetGetJoinInformation` + `dsregcmd`-equivalent API, Tailscale addrs), `Views/Remote/RemotePill.cs`, settings section in the existing Settings page; input via existing `DeskDeck`/`WindowsDesktopControl`.
- [ ] Tests (tests project): ladder, ping timeout, close codes, framing/crypto round-trip, Tailscale filter, focus mapping table, managed detection mapping.
- [ ] Implement; build; all tests pass; `tools/publish-release.sh --test` builds; commit.

### Task 6: Merge, install, release
- [ ] Merge each worktree branch; Mac: build, install to /Applications/H.I.B.app (ad-hoc sign); Android: `assembleDebug`, `adb install -r -d` via mDNS serial; Windows: bump version 1.7.0, publish, `gh release create v1.7.0`; Android public release 1.7.0 signed. Push to `private` only.
