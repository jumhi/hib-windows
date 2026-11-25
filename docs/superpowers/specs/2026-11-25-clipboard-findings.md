# Clipboard sync: root causes (2026-11-25)

Evidence-based (code read + the user's phone logcat). The fix contract is SPACE.md §16.

**Current flow.** Phone → computers: `ClipboardSyncManager.startSync` (AirSyncService.kt:244-249), listener
ClipboardUtil.kt:64-82, sends `clipboardUpdate{text}` (JsonUtil.kt:243) to every computer via DesktopRouting
BROADCAST_TYPES (DesktopHub.kt:405-408); images only for screenshots (ScreenshotClipboardMonitor). Computer → phone:
Mac polls `changeCount` every 2.5 s (AppState.swift:1510-1543); Windows polls `GetClipboardSequenceNumber` every
2.5 s (ClipboardSync.cs:53,104); both send to the phone only. The phone relays a computer's clip to the other
computers (DesktopHub.kt:481-484). Computer ↔ computer directly only on Roam crossing (ctlEnter/ctlLeave). No ordering.

1. **Phone copies only sync while H.I.B is in front.** CONFIRMED on the device: logcat shows 17× "ClipboardService:
   Denying clipboard access to com.sameerasw.airsync, application is not in focus". The only automatic path is
   `OnPrimaryClipChangedListener`; the READ_CLIPBOARD app-op granted over ADB (PhoneAppOpsGranter.swift:38,
   AppOpsGranter.cs:50) doesn't bypass the focus check. No IME / READ_LOGS / overlay workaround exists.
2. **Stale clip overwrites a newer one.** Roam carries clips instantly, the phone path lags ≤ 2.5 s + relay; e.g. copy
   X on Mac, cross to PC, copy Y on PC → Mac's poll sends X → phone relays X → PC applies X unconditionally
   (ClipboardSync.cs:146-157); Mac `AppState.updateClipboardFromAndroid` (:1587-1594) also always overwrites.
3. **Echoes.** Roam-applied clips skip the sync dedupe (Mac RoamEngine.swift:1840-1843 only updates
   RoamClipboardGate; Windows RoamEngine.cs:793-800 calls cb.SetTextAsync directly) → both computers send the same
   text to the phone. Mac sends every clip over WebSocket AND BLE (AppState.swift:1581-1584) → phone sets clipboard
   twice; its dedupe clears `lastReceivedText` on the first callback (ClipboardSyncManager.kt:57-61,165-167), so the
   second is sent back to every computer.
4. **Windows loses copies around a reconnect:** ConnectionChanged sets `_baselined=false` (ClipboardSync.cs:56), no
   polling while disconnected (:93), first poll after reconnect records and never sends (:116-119).
5. **Windows never re-sends text it once received:** `if (text == _lastReceivedText ...) return;` (:120), never cleared.
6. **Windows drops a copy when a read fails:** `_lastChangeCount = count` before the read (:111);
   `AvaloniaClipboardBridge.GetTextAsync` turns exceptions into null (UiHelpers.cs:67-72); never retried.
7. **No catch-up for a reconnecting computer:** DesktopHub send/relay (:405-408, :483) give up when
   `DesktopSession.send` returns false (:89-94).
8. **Roam marks a clip carried before delivery** (RoamClipboardGate.changed RoamLogic.swift:691-696; Windows
   `_lastCrossText` RoamEngine.cs:784-785) — lost if the peer link is dead (it drops every ~2 min: "no frames from
   PC-INTERN for 30 s") or the receiver answers ctlRelease (RoamEngine.swift:1580-1589). Windows gives the crossing only
   250 ms to read the clipboard (RoamEngine.cs:29,776).
9. Smaller: Mac BLE receive writes the pasteboard without the dedupe (BLETransportBridge.swift:156-165); Mac text
   receive ignores `isClipboardSyncEnabled` (Handlers.swift:969-973); phone doesn't relay BLE-received clips
   (BleGattServer.kt:452); phone never sends copied images; Mac sets `lastClipboardValue` before a send that may fail
   (:1538); an ~8 MiB PNG relayed through the phone nears OkHttp's 16 MiB queue cap.

Separate, still open: the Mac↔PC peer link goes silent for 30 s about every 2 minutes and is dropped/re-dialled
(Mac log, 15 drops on 2026-11-24). Cause unknown; needs both sides' keepalive code checked.
