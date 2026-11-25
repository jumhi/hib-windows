# H.I.B Desktop ↔ Android Protocol Spec (for the Windows port)

This spec describes how the H.I.B Mac app talks to the H.I.B Android app. It is written so a Windows desktop can stand in for the Mac **with no change to the Android app**. Everything here was read from source; it was not observed on the wire.

- Mac repo: `/Users/haithamhamid/Downloads/airsync-mac` (paths below are relative to `airsync-mac/airsync-mac/`, written `mac:`)
- Android repo: `/Users/haithamhamid/Downloads/airsync-android` (paths below are relative to `app/src/main/java/com/sameerasw/airsync/`, written `and:`)
- Android `applicationId = "com.sameerasw.airsync"`, `versionName = "4.1.0"` (`app/build.gradle.kts:15,18`)
- Android minimum desktop version: `MIN_MAC_APP_VERSION = "4.0.0"` (`app/build.gradle.kts:62`). The desktop must report `version >= 4.0.0` in `macInfo`, or the phone shows an "outdated" toast. The toast is the only effect.

"Mac" below means "the desktop". Where this doc says *Windows must…*, the phone depends on that behaviour.

---

## 0. TL;DR checklist for a compatible desktop

1. Run a plain **WebSocket server (no TLS)** on TCP **6996**, path **`/socket`**. Answer WS control-frame pings: OkHttp sends one every 5 s and drops the link if pongs stop.
2. Advertise over **mDNS `_airsync._tcp`** with TXT `id,name,port,ver=1,type=mac`. Also send **UDP JSON `presence`** broadcasts to **255.255.255.255:8889** and listen on UDP 8889.
3. Make one random **32-byte AES key** and keep it. Show the QR `airsync://<ip[,ip…]>:<port>?name=<pct-enc name>?plus=true?key=<std base64 key>`. The separators really are `?`, not `&`.
4. Every WS text frame in both directions is `base64( nonce(12) ‖ ciphertext ‖ tag(16) )` using **AES-256-GCM** with that key. Plaintext is a UTF-8 JSON envelope `{"type": "...", "data": {...}}`.
5. When `{"type":"device"}` arrives, reply with `{"type":"macInfo", ...}` and include `"isPlusSubscription": true`. Until `macInfo` arrives the phone treats the link as not authenticated, and it gives up after 10 s.
6. Every 4.5 s send `{"type":"ping","data":{}}` (encrypted). The phone answers `pong`. Drop the session after 20 s with no inbound frames.
7. Use the **same desktop name everywhere**: QR `name`, `macInfo.name`, mDNS/UDP `name`, wake-up `macName`. The phone keys its saved pairing and auto-reconnect on that name.

---

## 1. Transport

### 1.1 Roles, port, path

| Item | Value | Source |
|---|---|---|
| Desktop role | **WebSocket server**. The phone is always the client. | `mac:Core/WebSocket/WebSocketServer.swift:164-166` (`server["/socket"] = websocket(...)`) |
| Default port | **6996** | `mac:Constants/Constants.swift:12` `static let serverPort: UInt16 = 6996` |
| Configurable port | `UserDefaults "devicePort"` (string), default 6996 | `mac:Core/AppState.swift:153-154` |
| Path | **`/socket`** | `mac:WebSocketServer.swift:165`; phone builds `"ws://$formattedIp:$port/socket"` at `and:utils/WebSocketUtil.kt:236` |
| Scheme | `ws://` only. No TLS and no HTTP auth. | same |
| Binding | All IPv4 interfaces ("auto" mode), or one adapter chosen by the user | `WebSocketServer.swift:91-133` |
| Frames | **Text frames only** carry data. Binary frames only refresh the activity timer, and the Mac uses `writeBinary([])` as a force-close. | `WebSocketServer.swift:205-214`, `WebSocketServer+Ping.swift:125` |

**Phone client details** (`and:utils/WebSocketUtil.kt`):
- OkHttp settings: `connectTimeout(5 s)`, `readTimeout(0)`, **`pingInterval(5, SECONDS)`** (`:84-91`). The Windows server must answer RFC 6455 ping frames with pongs. Most libraries do this automatically.
- If the target is a comma-separated IP list, the phone tries every IP in parallel and keeps the first `onOpen`. There is a 15 s global timeout (`:143-236`).
- Before connecting, the phone only accepts private IPs: `192.168.*`, `10.*`, `100.*` (CGNAT/Tailscale), `172.16-31.*`, `127.0.0.1`, IPv6 `fe80:`/`fc`/`fd`/`::1`. The user setting "expand networking" turns this check off (`:547-591`).
- Handshake timeout: `HANDSHAKE_TIMEOUT_MS = 10_000L` (`:33`). After open, if no `macInfo` arrives within 10 s, the phone closes with code `4001 "Handshake timeout"` (`:279-310`).
- If decryption fails, the phone decides the key is wrong. It closes with `4003 "Pairing key mismatch"` and stops auto-reconnect until the user re-pairs (`:315-338`).
- A manual disconnect sends `{"type":"userDisconnected","data":{}}`, then closes with `1000 "Manual disconnection"` (`:732-760`).

### 1.2 Sessions on the Mac side

- Several sessions may be open at once, but only one is **primary**. Messages from a non-primary session are ignored, except `device` (`mac:Core/WebSocket/WebSocketServer+Handlers.swift:19-22`).
- The first session to connect becomes primary (`WebSocketServer.swift:225-227`).
- When a `device` message arrives from another session, the new session takes over only if its `data.targetIpAddress` is a private IP (`192.168.`, `10.`, `172.16-31.`). The old session is then force-closed (`Handlers.swift:126-141`, `WebSocketServer+Networking.swift:92-102`). Otherwise the new session is ignored.
- Everything the Mac sends goes to the primary session only (`WebSocketServer+Outgoing.swift:28-43`).
- When the primary session disconnects, the Mac marks the device disconnected, runs `adb disconnect`, and restarts its server after 1.5 s. It then sends a UDP presence burst (`WebSocketServer.swift:234-257, 304-329`). Windows doesn't need to restart; it only needs to be reachable again quickly.

### 1.3 Keepalive

| Constant | Value | Source |
|---|---|---|
| Mac app-level ping interval | **4.5 s** | `WebSocketServer.swift:22` `pingInterval: TimeInterval = 4.5` |
| Inactivity timeout | **20 s**, after which the primary session is dropped and the server restarts | `WebSocketServer.swift:24` `activityTimeout: TimeInterval = 20.0` |
| "Weak connection" UI | more than 12 s since the last inbound frame | `WebSocketServer+Ping.swift:63` |
| Ping payload | `{"type":"ping","data":{}}`, encrypted like any other message | `WebSocketServer+Ping.swift:131-141` |
| Phone reply | `{"type":"pong","data":{}}` | `and:utils/WebSocketMessageHandler.kt:491-499` |

Every binary frame refreshes `lastActivity`. So does every text frame that decrypts, even if its JSON then fails to decode (`WebSocketServer.swift:175-179, 205-208`). `pong` is **not** a `MessageType` enum case on the Mac, so its JSON decode fails and the Mac just logs it. That is harmless, because the activity time was already updated.

### 1.4 Discovery (desktop → phone and phone → desktop)

#### mDNS / Bonjour
- **Mac advertises** `NetService(domain: "", type: "_airsync._tcp", name: "AirSync-<name>", port: <port>)` (`mac:Core/Discovery/BonjourServiceAdvertiser.swift:51-53`). It republishes whenever the network path changes.
- TXT record, all values UTF-8 strings (`:56-62`):
  ```
  id   = <stable UUID, UserDefaults "device_stable_uuid">
  name = <desktop name>
  port = "6996"
  ver  = "1"
  type = "mac"
  ```
- **Phone** browses `"_airsync._tcp."` and accepts only services whose TXT `type == "mac"`. A missing `type` defaults to "mac". The phone uses TXT `id`, `name`, and `port`, falling back to the service name and port, plus the resolved host IP (`and:utils/discovery/MdnsDiscoveryBackend.kt:32, 257-283`).
- **Phone also advertises** `_airsync._tcp.` as `"AirSync-<deviceName>"` on port **8888** (its wake-up HTTP port). TXT: `id=<ANDROID_ID>, name, port="8888", ver="1", type="android"` (`MdnsDiscoveryBackend.kt:114-122`). The Mac browses the same type and lists those Android devices (`mac:Core/Discovery/BonjourServiceBrowser.swift:15, 54-64`).

#### UDP presence (port 8889)
- Port: `broadcastPort: NWEndpoint.Port = 8889` (`mac:Core/Discovery/UdpBroadcastDiscovery.swift:46`), `BROADCAST_PORT = 8889` (`and:utils/discovery/UdpDiscoveryBackend.kt:29`).
- **Mac → broadcast** `255.255.255.255:8889`, sent once from each local adapter address. Also unicast to previously connected phone IPs on the local subnet (`UdpBroadcastDiscovery.swift:153-189`). The payload is plain unencrypted JSON:
  ```json
  {"type":"presence","deviceType":"mac","id":"<device_stable_uuid>","name":"<desktop name>","ips":["192.168.1.20","10.0.0.5"],"port":6996}
  ```
- When it is sent: a burst of 3 sends 0.1 s apart at start, on wake, after a network change (debounced 2 s), and after a server restart. Otherwise every 10 s while no Wi-Fi phone is connected (`:142-151, 276-286`).
- **Phone → broadcast**, same port (`UdpDiscoveryBackend.kt:354-358`):
  ```json
  {"type":"presence","deviceType":"android","id":"<ANDROID_ID>","name":"<phone name>","ips":["192.168.1.33"]}
  ```
  Leaving: `{"type":"bye","deviceType":"android","id":"<ANDROID_ID>"}` (`:395-397`). When the phone hears a Mac presence while in PASSIVE mode, it broadcasts its own presence in reply (`:200-208`).
- The phone accepts `presence`/`bye` only when `deviceType == "mac"` (`:197-219`). It drops `100.*` IPs unless the phone also has a `100.*` address or expand networking is on (`:263-269`).
- The Mac accepts `deviceType == "android"`. Valid IPs are `100.*`, `192.168.*`, `172.16-31.*`, and `10.*` (only if the Mac also has a `10.*` address). Devices are pruned after 35 s (`UdpBroadcastDiscovery.swift:299-391`).

#### Phone auto-reconnect (what triggers the phone to connect to you)
`and:utils/WebSocketUtil.kt:937-1066`:
1. **Retry loop.** The phone retries the saved IP list and port of the last device. The device is matched **by name** (`deviceName == last.name`). Backoff starts at 2 s and grows ×1.5, capped at 10 s, or 30 s after 10 failures. The loop only runs on Wi-Fi, VPN, or Ethernet, or with expand networking on.
2. **Discovery-triggered.** When a discovered desktop (via mDNS or UDP) has `name == lastConnectedDevice.name`, the phone connects to that device's `ips` using the **saved** port and key.
3. Auto-reconnect is disabled after a manual disconnect, or when the user has auto-reconnect turned off.

⇒ **Windows must announce exactly the name the user paired with.**

#### Desktop-initiated "wake up / quick connect"
The Mac can ask a phone to connect to it (`mac:Core/QuickConnect/QuickConnectManager.swift`):
- Ports: `ANDROID_HTTP_WAKEUP_PORT = 8888`, `ANDROID_UDP_WAKEUP_PORT = 8889` (`:15-16`).
- Primary path: `POST http://<phoneIP>:8888/wakeup`, `Content-Type: application/json`, **plaintext** body (`:218-229, 282-290`):
  ```json
  {"type":"wakeUpRequest","data":{"macIP":"192.168.1.20","macPort":6996,"macName":"My PC","isPlus":true}}
  ```
  The phone answers `200 {"status":"success",...}`. It then looks up its stored key by **`macName`** (and IP), and connects with that key (`and:service/WakeupService.kt:30-31, 170-205`; `and:utils/WakeupHandler.kt`). A request for an unknown name with several saved desktops is ignored.
- Fallback if HTTP fails: the Mac sends UDP to `<phoneIP>:8889` with the payload `"AIRSYNC_WAKEUP:" + json` (`QuickConnectManager.swift:330-336`). **Note:** the phone's UDP handler parses the datagram as JSON and expects `type == "wakeUpRequest"` (`UdpDiscoveryBackend.kt:222-231`), so the prefixed form fails to parse. **Windows should send the bare JSON over UDP 8889**, which the phone does accept, in either nested or flat form.
- The key is never sent in a wake-up message.

### 1.5 Bluetooth LE fallback (optional)
The phone is a **GATT peripheral/server** and the Mac is the **central**. BLE is only used when Wi-Fi isn't connected. It is a separate, much smaller protocol: delimited strings, not the JSON envelope. Windows can skip it and still work. Details are in §8.

---

## 2. Pairing (QR code)

### 2.1 Key generation and storage
- `generateSymmetricKey()` creates `SymmetricKey(size: .bits256)`, i.e. **32 random bytes**, encoded as **standard Base64 with padding** (44 chars) (`mac:Core/Util/Crypto/CryptoUtil.swift:11-15`).
- The key is generated once and kept in `UserDefaults "encryptionKey"` as base64. A "reset" deletes it and makes a new one (`mac:WebSocketServer.swift:263-280`).
- The phone stores the base64 string as-is (`symmetricKey`) per desktop and decodes it with `Base64.decode(..., DEFAULT)` (`and:utils/CryptoUtil.kt:17-20`).

### 2.2 QR contents
`mac:Screens/ScannerView/ScannerView.swift:194-201`:
```swift
let encodedName = name?.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? "My Mac"
return "airsync://\(ip):\(port)?name=\(encodedName)?plus=\(AppState.shared.isPlus)?key=\(key)"
```
Inputs (`mac:Screens/ScannerView/QRConnectionManager.swift:24-50`):
- `ip` = `getLocalIPAddress(adapterName:)`. In auto mode this is **all non-loopback IPv4 adapter addresses joined with `,`**, e.g. `192.168.1.20,10.211.55.2` (`WebSocketServer+Networking.swift:38-55`).
- `port` = device port, default 6996.
- `name` = desktop name.
- `key` = raw base64 key. It is **not URL-encoded**, so `+ / =` appear literally.
- `plus` = `true`/`false` as lowercase Swift Bool text. A SELF_COMPILED build is always `true`.

Example:
```
airsync://192.168.1.20,10.211.55.2:6996?name=Haitham%E2%80%99s%20PC?plus=true?key=q3J0b2tl1Vf5qV8m9hQ0y2u7bQ0n8d0uYk3dV8b6a1E=
```

### 2.3 How the phone parses it
`and:presentation/ui/screens/AirSyncMainScreen.kt:497-531`:
- `host:port`: the phone tries `Uri.host`/`Uri.port`. A comma list breaks the URI host, so it falls back to `substringAfter("://").substringBefore("?")`, then splits on the **last** `:`.
- Query: takes everything after the first `?`, **splits on `?`**, then splits each part on the first `=`.
  - `name` → `URLDecoder.decode(it, "UTF-8")`. ⇒ Windows must percent-encode spaces as `%20`, not `+`, and encode a literal `+` as `%2B`. It must also encode `?`, `=`, `&`, and `#`. (Swift's `.urlQueryAllowed` does **not** encode `?`, `=`, or `&`. Do better than that.)
  - `plus` → `toBooleanStrictOrNull()`, so it must be exactly `true` or `false`.
  - `key` → used raw (no decoding). ⇒ **Do not percent-encode the key.**
- After a scan, the phone connects right away. On success it saves `{name, ip, port, isPlus, symmetricKey}` as the last device, plus a per-network map.
- The manifest also registers the `airsync` scheme as a deep link (`app/src/main/AndroidManifest.xml:115`).

### 2.4 Derived secrets (all from the same pairing key)
| Purpose | Derivation | Source |
|---|---|---|
| BLE auth token | `base64( SHA256( UTF8(<base64 key string>) )[0..16] )`. This hashes the **base64 text**, not the raw bytes. The phone sends it to the desktop in `device.bleAuthToken`. | `and:data/ble/BleTransportBridge.kt:21-30` |
| Find My relay topic and key | HKDF-SHA256 over the **raw 32 key bytes**. See §6.12. | `mac:Core/FindMy/FindMyRelay.swift:53-66` |

---

## 3. Encryption (WebSocket payloads)

| Property | Value |
|---|---|
| Algorithm | **AES-256-GCM** (CryptoKit `AES.GCM` / Java `"AES/GCM/NoPadding"`) |
| Key | the 32-byte pairing key, used directly with no KDF |
| Nonce | **12 random bytes** per message (`NONCE_SIZE_BYTES = 12`) |
| Tag | **16 bytes** (`TAG_SIZE_BITS = 128`) |
| AAD | none |
| Wire layout | `nonce(12) ‖ ciphertext ‖ tag(16)`, the CryptoKit `SealedBox.combined` layout, which equals Java `nonce + cipher.doFinal()` |
| Encoding | **standard Base64 with padding**. Phone encodes with `NO_WRAP`; Mac decodes strictly, so don't add line breaks. |
| Frame | a WebSocket **text** frame containing only that base64 string |
| Plaintext | UTF-8 JSON `{"type": "<string>", "data": <object>}` |

Sources: `mac:Core/Util/Crypto/CryptoUtil.swift:17-46`; `and:utils/CryptoUtil.kt:11-62`.

**Which messages are encrypted:** every WebSocket message in both directions, including `ping`/`pong`. The Mac always has a key, so it always encrypts (`WebSocketServer+Outgoing.swift:35-41`, `Ping.swift:137`). The phone encrypts whenever it holds a key, which it always does after a QR pairing (`WebSocketUtil.kt:608-610`).

**Not encrypted:** UDP presence/bye, mDNS TXT, HTTP/UDP wake-up, and BLE characteristic payloads. The Find My ntfy relay uses its own derived key (§6.12).

**Decrypt failures:**
- On the Mac, a frame that fails to decrypt is silently dropped (`WebSocketServer.swift:169-175`). It does **not** refresh the activity timer, because `lastActivity` is only set after a successful decrypt (`:177-179`). The socket stays open.
- On the phone, a decrypt failure ends the session and requires re-pairing (§1.1).

Reference implementation (C#):
```csharp
byte[] nonce = RandomNumberGenerator.GetBytes(12);
byte[] ct = new byte[pt.Length]; byte[] tag = new byte[16];
using var gcm = new AesGcm(key, 16); gcm.Encrypt(nonce, pt, ct, tag);
string frame = Convert.ToBase64String([..nonce, ..ct, ..tag]);
```

---

## 4. Handshake

```
Phone                                         Desktop
  | --- WS connect ws://ip:6996/socket ------> |   (becomes primary if none)
  |   (~1 s later: SyncManager.performInitialSync)
  | --- device (lite: no wallpaper) ---------> |   store device, try ADB, store bleAuthToken
  | <------------------------------ macInfo -- |   ← phone marks handshake complete
  |   (phone: start AirSyncService, periodic status sync, status now)
  |   (phone: compare savedAppPackages → appIcons)
  | --- status -----------------------------> |
  | --- appIcons ---------------------------> |
  | --- device (full: + wallpaper) [+1.5 s] -> |   desktop replies macInfo again
  | <------------------------------ macInfo -- |
  | <---- findRelayConfig / scenesList / status (Mac-side timers, ~2 s) |
  | <------------------------------ ping (every 4.5 s) / pong --------> |
```

### 4.1 `device` (phone → desktop)
Built in `and:utils/JsonUtil.kt:50-75`. The JSON is hand-built, so strings are not escaped. Sent from `and:utils/SyncManager.kt:211-221` (lite) and `:249-260` (full, after `delay(1500)` and only if the handshake is already done):
```json
{"type":"device","data":{
  "id":"a1b2c3d4e5f60789",            // Settings.Secure.ANDROID_ID
  "name":"Pixel 9",                    // user-set name, else Build model
  "ipAddress":"192.168.1.33",          // phone Wi-Fi IP (or "Unknown")
  "port":6996,                         // the port the phone has saved for this desktop
  "version":"4.1.0",                   // Android versionName
  "adbPorts":["37145","40211"],        // ports seen via mDNS _adb-tls-connect._tcp on the phone
  "wallpaper":"<base64 JPEG q85>",     // full variant only
  "bleAuthToken":"<base64 16 bytes>",  // see §2.4
  "targetIpAddress":"192.168.1.20"     // the desktop IP the phone actually reached
}}
```
What the Mac does (`mac:Handlers.swift:122-277`):
- Primary-session takeover rules (§1.2).
- **Requires `name`, `ipAddress`, and `port` (Int)**, otherwise it ignores the message and sends no `macInfo`.
- `version` defaults to `"2.0.0"`, `adbPorts` to `[]`, `id` to `""`.
- `targetIpAddress` → `activeMacIp`.
- `wallpaper` → cached, and written to `~/Library/Application Support/Wallpapers/<id>.jpg`.
- If ADB auto-connect is enabled and the build is Plus, the Mac runs the ADB flow (§7.1) against `ipAddress` using `adbPorts`.
- `bleAuthToken` → saved for BLE auth. If it changed, BLE scanning restarts.
- It **always replies `macInfo`** (`:275`).

> ⚠️ Phone bug to tolerate: `SyncManager.sendDeviceInfoNow()` (triggered by `refreshAdbPorts` or a rename) passes `WebSocketUtil.currentIpAddress` positionally into the `bleAuthToken` slot (`and:utils/SyncManager.kt:320-329`). That `device` message therefore carries `"bleAuthToken":"192.168.1.20"`. **Windows should ignore a `bleAuthToken` that doesn't decode to exactly 16 bytes.**

### 4.2 `macInfo` (desktop → phone)
`mac:Handlers.swift:1006-1047` and `mac:Model/MacInfo.swift`:
```json
{"type":"macInfo","data":{
  "name":"Haitham's PC",
  "modelIdentifier":"Mac15,3",
  "categoryType":"MacBook Pro",
  "exactDeviceName":"MacBook Pro (14-inch, M3, 2023)",
  "isPlusSubscription":true,
  "version":"4.1.0",
  "savedAppPackages":["com.whatsapp","org.telegram.messenger"],
  "model":"Mac15,3",          // duplicate of modelIdentifier
  "type":"MacBook Pro",       // duplicate of categoryType
  "isPlus":true               // duplicate of isPlusSubscription
}}
```
Phone handling (`and:utils/WebSocketMessageHandler.kt:605-779`; the handshake gate is in `WebSocketUtil.kt:342-391`):
- The **first decrypted frame whose `type == "macInfo"` completes the handshake.**
- `isPlusSubscription` (Bool) → saved as `isPlus` on the device. On the phone this gates the WebDAV file server (`and:service/AirSyncService.kt:162-172`) and the PLUS badge.
- `version` is checked against `4.0.0`.
- `model` and `type` (or `deviceType`) are stored and used for icons. `MacModelMapper` matches substrings such as `macbookair`, `macbookpro`, `macmini`, `imac`, `macstudio`, `macpro`, `macbookneo`, `iphone`, and `ipad` (`and:utils/MacModelMapper.kt:50-57`).
  ⚠️ If `type` or `categoryType` equals `iPhone`/`iPad`, the phone treats the host as iOS and **skips app-icon sync**. For Windows, use something like `"type":"Windows PC"` (generic icon), or `"MacBook Pro"` if a laptop icon is wanted.
- `savedAppPackages` is compared with the phone's launcher apps:
  - Empty list → the phone sends its full list with icons.
  - Lists differ → full list with icons.
  - Lists match → metadata only, with `icon:""`.

**How Plus / SELF_COMPILED is signalled:** only through `isPlusSubscription`/`isPlus` in `macInfo`, `plus=` in the QR, and `isPlus` in `wakeUpRequest`. In SELF_COMPILED builds the Mac forces `isPlus = true` at launch (`mac:Core/AppState.swift:198-201`; `Configs/SelfCompiled.xcconfig:1` sets `SWIFT_ACTIVE_COMPILATION_CONDITIONS = SELF_COMPILED`). There is no licence token on the wire. **Windows should always send `true`.**

---

## 5. Message envelope and decoding rules

- Envelope: `{"type": String, "data": Object}`. `data` is always a JSON object; `{}` when empty.
- The Mac decodes `type` into a closed Swift enum (`mac:Model/Message.swift:9-75`). **Unknown types fail decoding and are dropped.** Types the phone sends that the Mac drops this way: `pong`, `volumeControlResponse`, `toggleAppNotifResponse`, `toggleNowPlayingResponse`. Windows should ignore unknown types quietly.
- The Mac's `CodableValue` accepts `data` only as an object or a string (`mac:Core/WebSocket/AnyDecodable.swift:17-24`). Numbers in `data` decode as Int when integral, otherwise Double.
- The phone routes types in `and:utils/WebSocketMessageHandler.kt:73-122` and logs anything unknown.
- The phone sends with `WebSocketUtil.sendMessage()`. If the WS is down and BLE is authenticated, it falls back to BLE for a subset of types (`WebSocketUtil.kt:600-721`).
- On the Mac, file-transfer messages are handled on the socket thread; everything else is handled on the main thread (`WebSocketServer.swift:196-200`).

---

## 6. Message catalogue

Direction: **P→D** = phone to desktop, **D→P** = desktop to phone. Each "Mac does" note describes behaviour Windows should reproduce.

### 6.1 Connection and control

| Type | Dir | Payload | Notes |
|---|---|---|---|
| `device` | P→D | §4.1 | |
| `macInfo` | D→P | §4.2 | |
| `ping` | D→P | `{}` | every 4.5 s |
| `pong` | P→D | `{}` | |
| `userDisconnected` | P→D | `{}` | The phone disconnected on purpose. Mac: `disconnectDevice(manual: true)` (`Handlers.swift:908-912`) |
| `disconnectRequest` | D→P | `{}` | The desktop asks the phone to disconnect. The phone runs `WebSocketUtil.disconnect(manual=true)`, which sends `userDisconnected` and sets its "manually disconnected" flag so it won't auto-reconnect (`and:…/WebSocketMessageHandler.kt:501-508`; Mac `Outgoing.swift:147-149`, called from `AppState.swift:1238-1239`) |
| `refreshAdbPorts` | D→P | `{}` | The phone restarts ADB mDNS discovery, then after 2.5 s re-sends `device` (see the ⚠️ in §4.1) (`WebSocketMessageHandler.kt:967-976`). The Mac sends this when ADB auto-connect fails (`mac:Core/AppDelegate.swift:95-108`) |
| `wakeUpRequest` | D→P (HTTP/UDP, not WS) | §1.4 | |

### 6.2 Status / battery / now playing

**`status` P→D** (`and:utils/JsonUtil.kt:151-172`; sent periodically and on change by `SyncManager.checkAndSyncDeviceStatus`, `and:utils/SyncManager.kt:82-145`):
```json
{"type":"status","data":{
  "battery":{"level":82,"isCharging":false},
  "isPaired":true,
  "music":{"isPlaying":true,"title":"Song","artist":"Artist","volume":60,"isMuted":false,
           "albumArt":"<base64 jpeg/png>","albumArtLite":"<small base64>",
           "duration":215000,"position":42000,"positionTimestamp":1763600000000,
           "isBuffering":false,"likeStatus":"none"}}}
```
- The JSON is hand-built, so `title` and `artist` are **not escaped**. Parse leniently.
- `duration`, `position`, and `positionTimestamp` are in **milliseconds**.
- `likeStatus` is `"none" | "liked" | "disliked"` (free string).
- Mac (`Handlers.swift:370-465`): **requires** `battery.level`, `battery.isCharging`, `isPaired`, `music.isPlaying`, `title`, `artist`, `volume`, and `isMuted`, otherwise it drops the message. It converts ms to seconds. While playing and not buffering, it corrects position by `now - positionTimestamp` when that delta is within (-2 s, 5 s), then clamps to duration. A title change triggers track-change handling. It can also publish to the macOS Now Playing center (opt-in).

**`status` D→P** (`mac:Outgoing.swift:373-417`, driven by `mac:Core/Util/MacInfo/MacInfoSyncManager.swift`: a 60 s timer, now-playing changes, and only when the payload changed):
```json
{"type":"status","data":{
  "battery":{"level":-1,"isCharging":false},     // -1 = desktop without battery
  "isPaired":true,
  "music":{"isPlaying":true,"title":"…","artist":"…","volume":45,"isMuted":false,
           "likeStatus":"none","elapsedTime":12.5,"duration":201.0,
           "timestamp":"…","playbackRate":1.0,
           "albumArt":"<base64>","albumArtLite":"<80x80 jpeg q0.3 base64>"}}}
```
- `elapsedTime` and `duration` are in **seconds**; the phone multiplies by 1000.
- `music` is omitted when the Mac's "send now playing" setting is off.
- `albumArt` is re-sent each time, cached; `""` means no art.
- `volume` is the desktop's output volume, 0-100.
- The phone (`WebSocketMessageHandler.kt:514-599`) uses `name` if present and drives its Mac-media notification and widget. While the desktop is playing it pauses the phone's own media listener.

**`macVolume` D→P** `{"volume": 0-100}`. Sent when the desktop's system volume changes (`Outgoing.swift:291-293`, `mac:Core/Util/Remote/MacRemoteManager.swift:416`). The phone updates its remote UI.

**`modifierStatus` D→P**: a dict of modifier states. The helper exists (`Outgoing.swift:295`), but the Mac never calls it; the phone consumes it if it arrives.

### 6.3 Notifications

**`notification` P→D** (`and:utils/JsonUtil.kt:94-124`):
```json
{"type":"notification","data":{"id":"0|com.whatsapp|123|null|10123","title":"Ali","body":"hey","app":"WhatsApp",
 "package":"com.whatsapp","priority":"alerting",
 "actions":[{"name":"Reply","type":"reply"},{"name":"Mark as read","type":"button"}],
 "progress":40,"progressMax":100,"progressIndeterminate":false,"ongoing":false}}
```
- `priority` is `"alerting"`, `"silent"`, etc.
- `actions[].type` must be `"button"` or `"reply"` (`mac:Model/Notification.swift:11`).
- The progress and `ongoing` fields are optional.
- Mac (`Handlers.swift:279-318`): requires `id`, `title`, `body`, `app`, and `package`. It adds the notification to the list and posts a native notification unless the priority is silent. That path also feeds Login Codes (§6.13) and Rewind (§6.17).

**`notificationUpdate` P→D** `{"id":"…","dismissed":true,"action":"dismiss"}` (`JsonUtil.kt:130-138`). Mac: if `action == "dismiss"` (case-insensitive) or `dismissed == true`, it removes the notification and its native counterpart (`Handlers.swift:869-881`).

**`dismissNotification` D→P** `{"id":"…"}`. The user dismissed it on the desktop (`Outgoing.swift:220-222`). The phone cancels the notification and answers:
`dismissalResponse` P→D `{"id","success":Bool,"message"}`. The Mac only logs it (`Handlers.swift:892-898`).

**`notificationAction` D→P** `{"id":"…","name":"Reply","text":"on my way"}`. `text` is present **only** for inline replies; for a button it must be absent, not empty (`Outgoing.swift:224-228`; phone `WebSocketMessageHandler.kt:438-489`, which also accepts the legacy `action` key). The phone answers:
`notificationActionResponse` P→D `{"id","action":"<name>","success":Bool,"message"}`. The Mac only logs it.

**`toggleAppNotif` D→P** `{"package":"com.x","state":"true"|"false"}`. Note that `state` is a **string** (`Outgoing.swift:202-209`). The phone enables or disables forwarding for that app and answers `toggleAppNotifResponse` `{"package","success","newState":Bool,"message"}`, which the Mac drops.

**`toggleNowPlaying` D→P** `{"state":true}`. The phone supports it and answers `toggleNowPlayingResponse`; the Mac never sends it.

### 6.4 App list and icons

**`appIcons` P→D** (`and:utils/JsonUtil.kt:222-238`; triggered by `macInfo`):
```json
{"type":"appIcons","data":{
  "com.whatsapp":{"name":"WhatsApp","icon":"data:image/png;base64,iVBOR…","listening":true,"systemApp":false},
  "com.android.chrome":{"name":"Chrome","icon":"","listening":false,"systemApp":true}}}
```
- Icons are 128 px PNGs with a `data:` prefix (`and:utils/AppIconUtil.kt:18, 60-64`). `icon:""` means metadata only.
- Mac (`Handlers.swift:467-536`):
  - Strips everything up to `base64,` and writes `<appIconsDir>/<package>.png`.
  - Upserts `name`, `listening`, `systemApp`, and the icon path.
  - **Removes every package not in this message.** The message is the full list.
  - Persists the list; the next `macInfo.savedAppPackages` is built from it.

### 6.5 Clipboard

| Type | Dir | Payload |
|---|---|---|
| `clipboardUpdate` | both | `{"text":"…"}` |
| `clipboardImageUpdate` | both | `{"base64":"<image bytes b64>","mime":"image/png","name":"HIB-Clipboard.png"}` |

- **D→P text:** the Mac polls the pasteboard every **2.5 s** while a device is connected and sends when the text changed. Text is preferred over an image (`mac:Core/AppState.swift:1506-1544, 1576-1586`). The JSON is built with JSONSerialization.
- **D→P image:** sent only if there is no text and no file URL. PNG, **≤ 8 MiB** (`maxClipboardImageBytes = 8 * 1024 * 1024`, `AppState.swift:1557-1574`), named `"HIB-Clipboard.png"`.
- **P→D text** (`and:utils/JsonUtil.kt:243-250`): the Mac sets the pasteboard and records the text to Rewind (`Handlers.swift:914-919`).
- **P→D image:** the phone sends new screenshots (`and:utils/ScreenshotClipboardMonitor.kt:104-112`). The Mac applies the image only if clipboard sync is on. Defaults: mime `image/png`, name `HIB-Clipboard.png` (`Handlers.swift:921-932`).
- Loop avoidance: each side remembers the last value it received.
- The phone's READ_CLIPBOARD appop is granted over ADB (§7.4).

### 6.6 Phone media control (desktop controls the phone's player)

**`mediaControl` D→P** `{"action": A}` with `A ∈ playPause | play | pause | next | previous | stop | toggleLike | like | unlike | seekTo`. `seekTo` adds `"positionMs": Int` (`Outgoing.swift:232-267`). Note that the phone implements `play` and `pause` as `playPause`.

The phone answers `mediaControlResponse` P→D `{"action","success":Bool,"message"}`, which the Mac logs. The phone then re-sends `status` after 400 ms, 650 ms for seek, or 1200 ms for next/previous (`WebSocketMessageHandler.kt:282-378`).

**`volumeControl` D→P** (phone volume). The phone accepts (`WebSocketMessageHandler.kt:198-276`):
- `{"action":"setVolume","volume":0-100}`
- `{"action":"increaseVolume","increment":10}`
- `{"action":"decreaseVolume","decrement":10}`
- `{"action":"toggleMute"}`

It answers `volumeControlResponse` (the Mac drops it).

⚠️ The Mac's `volumeUp()`/`volumeDown()`/`toggleMute()` send `"volumeUp"`/`"volumeDown"`/`"mute"` (`Outgoing.swift:271-289`). The phone **rejects** these as unknown. **Windows should send `increaseVolume`/`decreaseVolume`/`toggleMute`/`setVolume`.**

### 6.7 Desktop media and remote control (phone controls the desktop)

**`macMediaControl` P→D** `{"action": "play"|"pause"|"playPause"|"next"|"previous"|"stop"}`. Sent from the phone's Mac-media notification (`and:service/MacMediaPlayerService.kt:452`). Mac (`Handlers.swift:840-858`): drives the system now-playing app, then replies:
`macMediaControlResponse` D→P `{"action":"…","success":true}`.

**`remoteControl` P→D** `{"action": A, …}` (`Handlers.swift:774-838`; phone UI in `and:presentation/ui/screens/RemoteControlScreen.kt`):

| action | extra fields | Mac does |
|---|---|---|
| `keypress` | `keycode: Int` (**macOS virtual keycodes**, e.g. Enter 36, Backspace 51, Esc 53, Space 49, ←123 →124 ↓125 ↑126; full map in `and:presentation/ui/components/KeyboardInputSheet.kt` `MacKeycodes`), `modifiers: [String]` | inject key. Modifier names: `shift`, `ctrl`/`control`, `option`/`alt`, `command`/`cmd`, `fn` (`mac:Core/Util/Remote/MacRemoteManager.swift:166-170`). **Windows must translate macOS keycodes to VK codes.** |
| `type` | `text`, `modifiers` | type the string |
| `arrow_up/down/left/right`, `enter`, `space`, `escape` | – | key |
| `vol_up`, `vol_down`, `vol_mute` | – | system volume |
| `vol_set` | `value: Int 0-100` | set volume |
| `media_play_pause`, `media_next`, `media_prev` | – | media keys |
| `mouse_move` | `dx`, `dy` (Double) | relative move |
| `mouse_click` | `button: "left"|"right"|"center"`, `isDown: Bool` | button down/up |
| `mouse_scroll` | `dx`, `dy` (Double) | scroll |
| `mouse_move_abs` | `x`, `y` (fractions 0…1 of the display), `display` (§7.1 id, `""` = main) | pointer to that point (SPACE.md §15.5). Windows: the display's desktop pixels → `SendInput` absolute virtual-desktop coordinates (any monitor, negative origins, physical pixels); unknown id = main |
| `key` | `keycode` (macOS virtual keycode), `isDown: Bool` | hold / let go of one key (SPACE.md §15.5). Windows: ⌘ → Ctrl and ⌃ → Win with ⌘-as-Ctrl (on by default), a held ⌘ + Tab sends **Alt**+Tab; every held key (and mouse button) is let go when the `/screen` session ends, the phone link drops, control is turned off, or 10 s pass with no `remoteControl` |
| `lock_screen`, `screensaver`, `brightness_up`, `brightness_down` | – | |

The phone's BLE fallback drops the `mouse_*` actions (`WebSocketUtil.kt:670-681`).

### 6.8 Calls

**`call_event` P→D**: the wire type string is literally `"call_event"` (`mac:Model/Message.swift:24`). Gson serialises the phone's `CallEvent` (`and:domain/model/CallEvent.kt:9-22`; sent at `and:service/CallStateCallback.kt:265-276`):
```json
{"type":"call_event","data":{"eventId":"uuid","deviceId":"…","timestamp":1763600000000,
 "direction":"incoming","state":"ringing","number":"+9665…","normalizedNumber":"+9665…",
 "contactName":"Mom","contactPhoto":"<base64 ≤256px>","simSlot":0}}
```
- `direction` ∈ `incoming|outgoing`.
- `state` ∈ `ringing|offhook|idle|missed`. The Mac also knows `accepted|rejected|ended` (`mac:Model/CallEvent.swift:10-22`).
- Mac (`Handlers.swift:320-359`) **requires `normalizedNumber` to be non-null**. Gson omits null fields, so events without one are dropped by the Mac. Windows should treat it as optional and fall back to `number`.

**`call_progress` P→D** `{"eventId":"…"}`. A heartbeat every **5 s** while ringing or off-hook (`CallStateCallback.kt:302-327`). Mac: `receivedCallProgress`, which keeps the call UI alive.

**`callControl` D→P** `{"action":"accept"|"decline"|"end"}` (`Outgoing.swift:441-456`). On the phone, `accept` answers the call and `end`/`decline` ends it (`WebSocketMessageHandler.kt:383-400`). A `callControlResponse` P→D `{"action","success","message"}` is understood by the Mac (logs only); the current phone build doesn't send it.

### 6.9 File transfer (WebSocket, base64 chunks)

Wire format (`mac:Core/Util/FileTransfer/FileTransferProtocol.swift`):
```json
{"type":"fileTransferInit","data":{"id":"<uuid>","name":"a.pdf","size":123456,"mime":"application/pdf",
                                    "chunkSize":65536,"isClipboard":false,"checksum":"<sha256 hex>"}}
{"type":"fileChunk","data":{"id":"<uuid>","index":0,"chunk":"<base64 of ≤chunkSize bytes>"}}
{"type":"fileChunkAck","data":{"id":"<uuid>","index":0}}
{"type":"fileTransferComplete","data":{"id":"<uuid>","name":"a.pdf","size":123456,"checksum":"<sha256 hex>"}}
{"type":"transferVerified","data":{"id":"<uuid>","verified":true}}
{"type":"fileTransferCancel","data":{"id":"<uuid>"}}
```
- Chunk `i` covers byte offset `i * chunkSize`.
- The checksum is the lowercase hex SHA-256 of the whole file. It is optional.
- The Mac's builders use string interpolation and don't escape `name`. Windows should escape properly.

**Phone → desktop (the only direction that works with the current phone).** This path is used by Flick (§6.14) and Phone Search fetch (§6.16). Sender: `and:utils/FlickSender.kt:131-212`.
- `CHUNK_SIZE = 64*1024`, `WINDOW = 12`, `ACK_TIMEOUT_MS = 2500`, `MAX_ATTEMPTS = 4` (`:39-42`).
- `init` carries an extra tag: `"flickId": id` for Flick, or `"searchFetchId": id` for a search fetch. No `checksum` is sent.
- The phone waits for `fileChunkAck` per index. When an ack times out it retransmits, giving up after 4 attempts and sending `fileTransferCancel`.
- `complete` carries `{id,name,size}` and no checksum.
- **Desktop receiver** (`mac:Handlers.swift:538-741`):
  1. On `init`: create a temp file `incoming_<id>_<name with / → _>`. Remember `chunkSize` (default 65536), `checksum`, and `flickId`.
  2. On each chunk: write the decoded bytes at `index * chunkSize`, then **always send `fileChunkAck {id,index}`**, even for unknown ids.
  3. On `complete`: if the on-disk size ≠ `size`, discard the file and fail. If a checksum was given, verify it (a mismatch is only logged). Then route the file:
     - a registered search fetch → `~/Downloads/H.I.B` (`PhoneFetchRegistry`, `mac:Core/PhoneSearch/PhoneSearchClient.swift:239+`)
     - `flickId` → the Flick destination folder (§6.14)
     - otherwise → `~/Downloads/<name>`, de-duplicated as `name(1).ext`, followed by a native "Received: name" notification.
  4. On `fileTransferCancel`: delete the partial file and fail the flick card or fetch.

**Desktop → phone.** The Mac's `sendFile()` (`mac:Outgoing.swift`) and Windows' `FileSender` (`win:Core/Phone/FileSender.cs`) send `init` with a SHA-256 `checksum` and `isClipboard`, then 64 KiB chunks with a sliding window of 8, retransmitting after 2000 ms without an ack, and finish with `complete`.
- **Phone receiver** (multi-desktop phones, `and:multidesk/PhoneFileReceiver.kt`): on `init` it opens a private cache file; each chunk is written at `index * chunkSize` on the socket thread and **always acked** (`fileChunkAck {id,index}`) to the computer that sent it, even for unknown ids. On `complete` it checks the size and the checksum (from `complete` or `init`), publishes the file to **`Download/H.I.B/<name>`** via MediaStore (Android renames on clash), answers **`transferVerified {id, verified}`**, and posts a "Received" notification with **Open**. Progress shows as an ongoing notification with Cancel; cancelling sends `fileTransferCancel {id, reason:"cancelledOnPhone"}`. A `fileTransferCancel` from the desktop discards the partial file. If that computer disconnects mid-transfer the partial file is dropped.
- Phones older than the multi-desktop build have no receiver: they log the unknown types and never ack, so the sender times out. Senders should treat "no ack within ~10 s" as "phone too old".
- Desktops should send `transferVerified` too after saving a received file (Windows does; the Mac does from this release). Senders wait up to 10 s for it; a `verified:false` means the file arrived damaged.
- `filePull` (`{"path":"…"}`) still has no phone handler.

### 6.10 Phone file browser

**`browseLs` D→P** `{"path":"/sdcard/DCIM","showHidden":false}` (`Outgoing.swift:211-213`).

**`browseData` P→D** (`and:utils/FileBrowserUtil.kt:40-80`):
```json
{"type":"browseData","data":{"path":"/storage/emulated/0/DCIM",
  "items":[{"name":"Camera","isDir":true,"size":0,"time":1763600000000}]}}
```
On error: `{"path","error":"…","items":[]}`. The Mac checks for `items` first, so the error is effectively never shown. Windows should check `error` first. Items are sorted directories first, then by name. `time` is epoch ms.

**WebDAV** (Plus + phone setting "file access"): the phone runs an **unauthenticated WebDAV server on port 9081** rooted at external storage (`and:utils/WebDavServer.kt:38-55`). The Mac mounts `http://<phoneIP>:9081/` with `/sbin/mount_webdav` (`mac:Core/Storage/WebDAVManager.swift:22-47`). On Windows, use the WebClient redirector (`net use`) or a WebDAV client library.

### 6.11 Ring My Phone and Play Phone Audio

**`ringPhone` D→P** `{"action":"start"|"stop"}` (`Outgoing.swift:329-331`). The phone rings at full volume, even on silent. It stops automatically after `MAX_RING_MS = 120_000L` (`and:utils/PhoneRinger.kt:36`).

**`ringState` P→D** `{"ringing":Bool}` (`PhoneRinger.kt:196-200`). The Mac updates its button (`Handlers.swift:51-53`). The Mac also has a 125 s safety timer (`mac:Core/PhoneControl/PhoneFeatures.swift`, `setRinging`).

How the Mac chooses a route (`PhoneFeatures.swift`, `PhoneRingController.start/stop`):
- WS when a device is connected.
- ADB broadcast when the WS has no primary session (§7.3).
- The Find My relay `findRing` when there is no WS and no ADB (§6.12).

**`phoneAudioControl` P→D** `{"action":"start"|"stop"|"toggle"}`. The default is `toggle` (`and:utils/PhoneAudioRemote.kt:16-24`). Mac: start or stop scrcpy audio-only mode (§7.6).

**`phoneAudioState` D→P** `{"active":Bool,"error":"…"?}`. Sent whenever the scrcpy audio state changes (`Outgoing.swift:343-347`).

### 6.12 Find My (nearby over WS, or over the internet via ntfy)

Message types (`mac:Core/FindMy/FindMyManager.swift`; `and:findmy/FindMyHandler.kt`):

| Type | Dir | Payload | Effect / reply |
|---|---|---|---|
| `findLocate` | D→P | `{}` | The phone answers `findLocation` |
| `findLocation` | P→D | `{"lat","lon","accuracyM","altitude","speed","bearing","timestamp"(ms),"provider","batteryPct","isCharging","networkName"}`, or `{"error":"…",…}` | Mac stores the last location (`PhoneLocationReport`, `FindMyManager.swift:19-45`). `lat`/`lon` are required to count as a location. |
| `findRing` | D→P | `{"action":"start"|"stop"}` | Rings the phone. Over the relay, the phone also publishes `findRingState` |
| `findRingState` | P→D | `{"ringing":Bool}` | |
| `findLock` | D→P | `{}` | The phone locks through its accessibility service and answers `findLockResult {"ok":Bool,"method":"accessibility"|"none"}`. If it fails, the Mac falls back to `adb shell input keyevent KEYCODE_SLEEP` (`FindMyManager.swift:234-256`) |
| `findMessage` | D→P | `{"text":"≤500 chars"}` | Shows the message on the phone and answers `findMessageResult {"ok":Bool}` |
| `findPing` | D→P (relay) | `{"id":"<32 hex>"}` | The phone answers `findPong {"id"}`, always over the relay |
| `findRelayConfig` | D→P (WS) | `{"enabled":true,"server":"https://ntfy.sh"}` | Sent about 2 s after each connect and when settings change (`FindMyManager.swift:111-117`) |
| `findRelayWake` | D→P (WS) | `{"seconds":90}` | The phone listens on the relay for 10–300 s even while connected (used by "test relay") |
| `findRingMac` | P→D | `{"action":"start"|"stop"}` | The desktop plays an alarm, then replies `findMacRingState {"ringing":Bool}` |
| `findLocateMac` | P→D | `{}` | The desktop replies `findMacLocation {"macName","timestamp","networkName"?, "lat","lon","accuracyM","altitude","provider":"corelocation"}`, or `{"provider":"none","error":"…"}` (`mac:Core/FindMy/MacFindMeServices.swift:192-226`) |
| `findPong` | either | `{"id"}` | Relay round-trip test |

Routing rules:
- **Desktop send** (`FindMyManager.swift:130-151`): use the WS if there is a primary session; otherwise use the relay if it is available.
- **Desktop reply** (`:312-320`): if the request came over the WS, reply over the WS. If it came over the relay, reply over the WS when connected (except `findPong`), otherwise over the relay.
- The phone uses the mirror-image rules (`FindMyHandler.kt:110-140`).

**Relay crypto and transport** (must match byte for byte):
- Server: `FindMySettings.defaultServer = "https://ntfy.sh"` (`mac:Core/FindMy/FindMyRelay.swift:17`). It is user-configurable and pushed to the phone in `findRelayConfig`. Trailing `/` is trimmed.
- HKDF info strings (verbatim): `topicInfo = "hib-findmy-topic-v1"`, `keyInfo = "hib-findmy-key-v1"` (`FindMyRelay.swift:53-54`; `and:findmy/FindMyCrypto.kt:29-30`).
- KDF: **HKDF-SHA256, IKM = raw 32-byte pairing key, salt = empty (≡ 32 zero bytes), L = 32**, i.e. a single expand block `HMAC(PRK, info ‖ 0x01)` (`FindMyRelay.swift:58-66`; `FindMyCrypto.kt:43-59`).
- Topic: `"hib-" + first 32 hex chars (lowercase) of HKDF(info="hib-findmy-topic-v1")`.
- Relay key: `HKDF(info="hib-findmy-key-v1")`, used as the AES-256-GCM key.
- Envelope plaintext (`FindMyRelay.swift:114-123`; `FindMyCrypto.kt:88-98`):
  ```json
  {"v":1,"type":"findRing","data":{"action":"start"},"ts":1763600000000,"nonce":"<16 random bytes hex>","from":"mac"}
  ```
  `from` is `"mac"` or `"phone"`. **Windows must send `"from":"mac"`**, because the phone ignores anything else (`and:findmy/FindMyRelayClient.kt:176-184`).
- Ciphertext: the same AES-GCM framing as §3, `base64(nonce12 ‖ ct ‖ tag16)`, using the relay key.
- Publish: `POST {server}/{topic}`, `Content-Type: text/plain`, body = that base64 (`FindMyRelay.swift:194-221`). The phone does the same (`FindMyRelayClient.kt:186-203`).
- Subscribe:
  - Mac: `GET {server}/{topic}/json?since=2m`, streaming, one JSON object per line, backoff 2→300 s (`FindMyRelay.swift:249-275`).
  - Phone: `/json?since=5m` while streaming, plus a 15-minute JobScheduler `?poll=1&since=5m` (`FindMyRelayClient.kt:140, 228`).
  - Only lines with `event == "message"` are used; the payload is the `message` field.
- Validation on receive:
  - `v == 1`
  - `|now - ts| ≤ 5 min` (`maxAgeMs = 5*60*1000`)
  - `nonce` non-empty
  - `from` is the other side
  - the nonce was not seen before (a persisted LRU of 200)
- The Mac saves the topic and key the first time a phone connects (`markPaired`), and treats the relay as unavailable after a key reset until the phone reconnects (`FindMyRelay.swift:68-90`).

### 6.13 Login codes (desktop-only logic)

Nothing extra goes over the wire. Every incoming `notification` is fed to `LoginCodeAutoCopier.handle` (`mac:Core/LoginCodes/LoginCodeAutoCopier.swift:56-80`). The rules:
- Skip `ongoing` notifications.
- Skip anything that arrives in the first **6 s** after connect (the phone re-sends its backlog).
- Detect the code with `OneTimeCodeDetector` (`mac:Core/LoginCodes/OneTimeCodeDetector.swift`). This is an English and Arabic keyword-proximity scorer:
  - digits 4–8 long, or 3-3 / 4-4 split codes
  - alphanumeric 6–8 after a keyword
  - rejection contexts for amounts, orders, cards, and similar
  - max keyword distance 90
  - The regexes are at `:227-253`. Port them verbatim.
- De-duplicate on `nid|code` for 15 min, and on the same code within 2 min.
- Copy the code to the clipboard **without syncing it back to the phone**.
- Show a HUD.
- Optionally restore the previous clipboard after `restoreDelay = 90` s.

Settings keys: `hib.loginCodes.autoCopy` and `hib.loginCodes.restoreClipboard`, both defaulting to true.

### 6.14 Flick to Mac

**`flickStart` P→D** (`and:utils/FlickSender.kt:70-99`). This is sent the moment a card leaves the top of the phone screen:
```json
{"type":"flickStart","data":{"id":"<uuid>","kind":"image","name":"IMG_1.jpg","mime":"image/jpeg",
 "size":2345678,"exitXRatio":0.42,"velocity":2100.0,"angle":-3.5,
 "thumbnail":"<base64 small image>","text":"<for link/text kinds, ≤100000 chars>"}}
```
- `kind` ∈ `image|video|file|link|text` (`mac:Core/Flick/FlickCardModel.swift:12-19`).
- For `link` and `text`, nothing is transferred: the desktop opens the link (if auto-open links is on) or copies the text when the card lands.
- For the other kinds, a phone→desktop file transfer follows (§6.9) with `fileTransferInit.data.flickId = id`.
- `fileTransferInit` may arrive **before** `flickStart`. If so, create the card from the init (`mac:Core/Flick/FlickManager.swift:33-83`).

**`flickLanded` D→P** `{"id":"<uuid>"}`. Sent when the card's landing animation ends. The phone plays a final haptic tick (`FlickManager.swift:197-205`; `Outgoing.swift:184-186`).

Destination folder: `hib.flick.destination` (default `desktop`; also `downloads` or `custom` via `hib.flick.customPath`). Other settings: `hib.flick.sound` and `hib.flick.autoOpenLinks` (`mac:Core/Flick/FlickSettings.swift:21-39`).

### 6.15 Touch ID on Phone (approval)

**`approvalRequest` D→P** `{"id":"<32 hex random>","title":"sudo","detail":"…","expiresInMs":30000}` (`mac:Core/PhoneApproval/PhoneApproval.swift:112-146`; `Outgoing.swift:321-323`).
- Mac limits: `timeout = 30` s, `maxConcurrent = 2`, `maxPerMinute = 8`.
- The phone clamps `expiresInMs` to 5–60 s, truncates the title to 120 chars and the detail to 400 (`and:approval/PhoneApprovalManager.kt:52-56`).

**`approvalResponse` P→D** `{"id","approved":Bool,"method":"biometric"|"deviceCredential"|"denied"|"timeout"}` (`PhoneApprovalManager.kt:86-118`). The Mac counts the request as approved **only if** `approved == true && method ∈ {biometric, deviceCredential}`. It matches ids in constant time, and each id is single-use (`PhoneApproval.swift:154-179`).

**`approvalCancel` D→P** `{"id"}`. Sent on desktop timeout or cancel.

The desktop-local source of requests on the Mac is a Unix socket for a PAM module: `~/Library/Application Support/H.I.B/approval.sock`, mode 0600. Request line: `{"v":1,"user","service":"sudo","tty","cmd","pid"}`. Reply line: `{"approved":Bool,"reason"}` (`mac:Core/PhoneApproval/PhoneApprovalServer.swift:5-9`). On Windows the equivalent would be a named pipe plus a credential-provider or UAC hook. That part is outside the phone protocol.

### 6.16 Search your phone

**`phoneSearch` D→P** `{"id":"<uuid>","query":"pdf last week","kinds":["files","photos","apps","contacts"],"limit":30}` (`Outgoing.swift:189-191`). The phone clamps `limit` to 1–100. A missing `kinds` means all four (`and:phonesearch/PhoneSearchHandler.kt:53-62`).

**`phoneSearchResults` P→D** `{"id","results":[…],"done":Bool}`. The phone sends one message per kind as each finishes (`done:false`), then a final `done:true` with empty results. The whole search is capped at `TIME_BUDGET_MS = 2000`. Thumbnails are capped at 12 per search and 20 KB each (`:45-48, 64-85, 355-364`).

Result row:
```json
{"kind":"photos","title":"IMG_1.jpg","subtitle":"DCIM/Camera","mime":"image/jpeg","path":"/storage/emulated/0/DCIM/Camera/IMG_1.jpg",
 "uri":"content://media/external/images/media/123","size":2345678,"date":1763600000000,"thumbnail":"<base64>"}
```
`apps` rows put the package in `subtitle` and `path`. `contacts` rows put the phone number in `subtitle`.

**`phoneFetchFile` D→P** `{"id":"hibfetch-<uuid>","path":"<absolute path or content:// uri>"}`. The phone streams the file back with the WS file transfer, using **the same id**, plus `"searchFetchId":id` in the init. If it can't read the file it sends `fileTransferCancel {id}` (`PhoneSearchHandler.kt:87-108`).

What the Mac does with a fetch:
- When ADB is connected and `path` starts with `/`, it first tries `adb -s <serial> pull <path> <dest>`.
- If the phone doesn't start the WS transfer within 20 s, the fetch fails.
- Downloads go to `~/Downloads/H.I.B` (`mac:Core/PhoneSearch/PhoneSearchClient.swift:140-237`).

### 6.17 Rewind (desktop-only)

No protocol messages. The desktop stores locally:
- phone notifications
- clipboard text and images in both directions (`direction:"toPhone"|"fromPhone"`)
- received files

Images are OCR'd on device (`mac:Core/Rewind/RewindRecorder.swift:69-124`). It feeds the desktop's "Search Phone & Memory" panel.

### 6.18 Scenes (automations)

| Type | Dir | Payload |
|---|---|---|
| `scenesList` | D→P | `{"scenes":[{"id":"…","name":"Focus","icon":"<SF Symbol name>"}]}`. Only enabled scenes with `showOnPhone`. Sent 2 s after connect (force) and whenever the list changes (`mac:Core/Scenes/SceneEngine.swift:185-190, 514-525`). |
| `runScene` | P→D | `{"id":"…"}` (`and:scenes/ScenesRemote.kt:75-76`) |
| `sceneRunResult` | D→P | `{"id","ok":Bool,"message":"…"}` (`SceneEngine.swift:441-453`) |
| `phoneEvent` | P→D | `{"event":"headset","connected":Bool,"name":"…"}`, or `{"event":"wifi","transport":"wifi"|"cellular"|"none","ssidAvailable":Bool,"ssid":"…"?}`. The Mac also accepts `{"event":"charging","charging",…}` and `{"event":"battery","level",…}` (`SceneEngine.swift:241-273`; `and:scenes/SceneEventMonitor.kt:132, 176-180`). Triggers only fire on a *change* after the first sample on each connection. |
| `phoneAction` | D→P | `{"action":A,"requestId":"<uuid>",…}`. `A` is one of: `dnd {"enabled":Bool}`, `ringer {"mode":"silent"|"vibrate"|"normal"}`, `volume {"stream":"media","percent":0-100}`, `openApp {"package"}`, `notify {"title","body"}` (`mac:Core/Scenes/SceneActionRunner.swift:27-44, 138-151`; `and:scenes/ScenePhoneActions.kt:25-41`) |
| `phoneActionResult` | P→D | `{"requestId","action","ok":Bool,"message"?,"error"?}`. The desktop times out after 10 s. |

Other scene triggers are desktop-side (`phoneConnected`/`phoneDisconnected`, battery from `status`, and so on).

### 6.19 Desk Deck (phone as a control deck for the desktop)

| Type | Dir | Payload |
|---|---|---|
| `deckOpen` | P→D | `{"open":Bool}`. While open, the desktop sends `deckState` every **2 s** (`mac:Core/DeskDeck/DeskDeckManager.swift:39-71`). |
| `deckState` | D→P | `{"cpu":0-100,"mem":0-100,"memPressure":"normal"|"warning"|"critical","uptime":<s>,"battery":<0-100 or -1>,"charging":Bool,"netUp":<KB/s>,"netDown":<KB/s>,"nextEvent":{"title","start"(ms),"end"(ms),"location"?}?}` |
| `deckAction` | P→D | `{"action":A,"arg":"…"?}`. `A` is one of `lock, screensaver, play_pause, next, previous, vol_up, vol_down, mute, brightness_up, brightness_down, screenshot, phone_audio, mic_mute, mission_control, show_desktop, open_app(arg=app name), shortcut(arg=Shortcut name), dnd(arg=Shortcut name)` (`DeskDeckManager.swift:243-305`) |
| `deckActionResult` | D→P | `{"action","ok":Bool,"error":"…"?}`. For unknown actions: `ok:false,"error":"Unknown action"` |

Phone parser: `and:deskdeck/DeskDeckRemote.kt:40-85`. Windows equivalents to use:
- `mission_control` → Win+Tab
- `show_desktop` → Win+D
- `screenshot` → Win+Shift+S or PrintScreen
- `shortcut` → run a script or task

### 6.20 Other D→P messages the phone understands

| Type | Payload | Phone does |
|---|---|---|
| `startQuickShare` | `{}` | Starts its Quick Share discovery, if enabled (`WebSocketMessageHandler.kt:997-1023`). The Mac sends this before a Quick Share send (`mac:Core/QuickShare/QuickShareManager.swift:112,160`). |
| `mirrorAttachment` | `{"path":"/sdcard/Download/HIB_Drops/<session>/<file>","fileName","mimeType","targetPackage"}` | The accessibility service attaches the file, which was already pushed over ADB, into the app being mirrored (`Outgoing.swift:161-181`; `mac:Core/Remote/MirrorDropShareCoordinator.swift:182-195`) |
| `screenFrame` / `screenShareState` | `{"base64","timestamp"}` / `{"active"}` | Used by an iOS host companion. Not sent by the Mac. |

### 6.21 Complete type index

**D→P (desktop sends):**
- `macInfo`, `ping`, `status`, `macVolume`
- `clipboardUpdate`, `clipboardImageUpdate`
- `mediaControl`, `volumeControl`, `callControl`
- `dismissNotification`, `notificationAction`, `toggleAppNotif`
- `disconnectRequest`, `refreshAdbPorts`, `startQuickShare`, `mirrorAttachment`, `browseLs`
- `fileTransferInit` (optional `relayTo`)/`fileChunk`/`fileTransferComplete` (multi-desktop phones receive or relay them), `fileChunkAck`, `fileTransferCancel`, `transferVerified`, `filePull` (no receiver)
- `desktopsRequest`
- `macMediaControlResponse`
- `ringPhone`, `phoneAudioState`
- `findRelayConfig`, `findRelayWake`, `findLocate`, `findRing`, `findLock`, `findMessage`, `findPing`, `findPong`, `findMacLocation`, `findMacRingState`
- `approvalRequest`, `approvalCancel`
- `flickLanded`
- `phoneSearch`, `phoneFetchFile`
- `scenesList`, `phoneAction`, `sceneRunResult`
- `deckState`, `deckActionResult`
- `modifierStatus` (unused)

**P→D (phone sends):**
- `device`, `pong`, `userDisconnected`, `status`
- `notification`, `notificationUpdate`, `notificationActionResponse`, `dismissalResponse`
- `mediaControlResponse`, `volumeControlResponse`\*, `toggleAppNotifResponse`\*, `toggleNowPlayingResponse`\*
- `appIcons`, `clipboardUpdate`, `clipboardImageUpdate`
- `call_event`, `call_progress`
- `macMediaControl`, `remoteControl`, `browseData`
- `fileTransferInit` (relayed ones carry `relayFrom`), `fileChunk`, `fileTransferComplete`, `fileTransferCancel`, `fileChunkAck` (for desktop→phone files), `transferVerified`
- `desktopsList`
- `ringState`, `phoneAudioControl`
- `findLocation`, `findLockResult`, `findMessageResult`, `findRingState`, `findRingMac`, `findLocateMac`, `findPong`
- `approvalResponse`
- `flickStart`
- `phoneSearchResults`
- `phoneEvent`, `phoneActionResult`, `runScene`
- `deckOpen`, `deckAction`

(\* the Mac drops these as unknown types)

### 6.22 Multi-desktop: several computers, one phone

A multi-desktop phone stays connected to **every** computer it is paired with at once (Mac and Windows), each over its own WebSocket with its own pairing key — nothing changes on the wire for a single computer. The phone keeps the old connection as the *primary* computer (the one its main screen is about) and opens one extra session per other paired computer, each with the primary's reconnect strategy (2 s ×1.5 backoff capped at 10 s / 30 s after 10 failures, only on Wi-Fi/VPN/Ethernet; immediate retry when mDNS/UDP sees that computer's name; wake-up requests reconnect just that computer).

**Pairing** never unpairs another computer: scanning a second computer's QR adds it to the phone's list (`and:multidesk/PairedDesktop.kt`). The first run migrates the single saved device into that list.

**What goes where (phone → desktops)** (`and:multidesk/DesktopRouting.kt`):
| Kind | Types | Goes to |
|---|---|---|
| Phone state | `notification`, `notificationUpdate`, `status`, `clipboardUpdate`, `clipboardImageUpdate`, `call_event`, `call_progress`, `ringState` | **every connected computer** |
| Reply sent while handling a request | anything | the computer that sent the request |
| Late reply | `pong`, `*Response`, `browseData`, `phoneSearchResults`, `approvalResponse`, `find*` results, `fileChunkAck`, `transferVerified`, `desktopsList` | the computer that most recently sent a request (30 s window) |
| Phone-initiated actions | `remoteControl`, `deckAction`, `runScene`, `macMediaControl`, … | the primary computer (or the most recently used other computer when the primary is offline) |
| Flick | `flickStart` + the file transfer | the computer chosen on the phone's Flick screen (chips appear when more than one computer is connected; the last choice is remembered) |

A `clipboardUpdate`/`clipboardImageUpdate` **from** one computer is also forwarded to the other connected computers (one clipboard across all of them). A non-primary computer's own `status`/`macVolume` are not shown on the phone. `disconnectRequest` from a non-primary computer turns that computer off on the phone until the user turns it back on or that computer sends a wake-up.

**`desktopsRequest` D→P** `{}` — "who else is connected to you?"

**`desktopsList` P→D** — the answer, and pushed unprompted to every connected computer ~0.8 s after any computer connects or drops:
```json
{"type":"desktopsList","data":{
  "phone":{"id":"phone","name":"Haitham's S26"},
  "desktops":[
    {"id":"Haitham’s MacBook","name":"Haitham’s MacBook","platform":"mac","type":"MacBook Pro","connected":true},
    {"id":"Office PC","name":"Office PC","platform":"windows","type":"Windows PC","connected":false}
  ]}}
```
- The recipient is never listed. `id` is the computer's name (the protocol's identity, §9.7). `platform` ∈ `mac|windows` (from `macInfo.type`/`model` containing "Windows"). Offline/turned-off computers are listed with `connected:false`.
- Desktops ask on every connect and listen for pushes. Old phones ignore `desktopsRequest`, so "no answer" means "phone only".

**Computer → computer files (relayed by the phone).** The sender uses the normal §6.9 sender with one extra field in `fileTransferInit`:
```json
{"type":"fileTransferInit","data":{"id":"<uuid>","name":"a.pdf","size":123,"mime":"application/pdf","chunkSize":65536,
  "isClipboard":false,"checksum":"<sha256>","relayTo":"Haitham’s MacBook"}}
```
`relayTo` absent or `"phone"` → the phone keeps the file (receiver above). Otherwise the phone (`and:multidesk/DesktopHub.kt`):
1. If the target isn't connected, answers `fileTransferCancel {id, reason:"targetOffline"}` at once.
2. Sends the target a synthetic `flickStart {id, kind:image|video|file (from mime), name, mime, size, exitXRatio:0.5, velocity:1800, angle:0, from:"<sender name>", fromPlatform}` so a card flies in, then the `fileTransferInit` with `relayTo` removed and `relayFrom`, `relayFromPlatform` and `flickId = id` added — an old desktop simply sees a Flick.
3. Forwards each `fileChunk` and the `fileTransferComplete` from the sender to the target unchanged, and each `fileChunkAck`/`transferVerified` from the target back to the sender. Nothing is stored on the phone: the sender's own ack window paces the whole path.
4. A `fileTransferCancel` from either side is forwarded to the other. If either computer drops, both get `fileTransferCancel {reason:"peerLost"|"targetLost"}`.
Cancel reasons a sender may see: `targetOffline`, `targetLost`, `peerLost`, `cancelledOnPhone`, `storage`, `badSize`.

**Receiving a relayed file** is a normal Flick receive (§6.14): the file goes to the Flick destination, the card/toast says "From <relayFrom>", and the receiver answers `transferVerified`.

---

## 7. ADB-side behaviour (desktop runs `adb` / `scrcpy` itself)

Binary search paths on the Mac (`mac:Core/Util/CLI/ADBConnector.swift:20-32`):
- adb: `~/Library/Android/sdk/platform-tools/adb`, `/opt/homebrew/bin/adb`, `/usr/local/bin/adb`
- scrcpy: `/opt/scrcpy/scrcpy`, `/opt/homebrew/bin/scrcpy`, `/usr/local/bin/scrcpy`

When launching scrcpy, the Mac sets `PATH=<adbDir>:$PATH` and `ADB=<adbPath>` (`mac:Core/PhoneControl/PhoneFeatures.swift`, `PhoneADBTarget.adbEnvironment`).

Device serial (`PhoneADBTarget.currentSerial`): the wired serial if wired ADB is enabled and selected; otherwise `<adbConnectedIP or device.ipAddress>:<adbPort>`.

### 7.1 Wireless ADB connect (automatic, on each `device` message, Plus only)
`ADBConnector.connectToADB` (`ADBConnector.swift:155-378`):
1. Optionally run `adb kill-server`, when the user initiated the connect or "always kill" is set.
2. Ports come from `device.adbPorts`. If there are none and "fallback to mDNS" is on, parse `adb mdns services`, taking lines containing the phone IP and the trailing `:port` (`:240-261`).
3. For each port, run `adb connect <ip>:<port>`. Success means the output contains `"connected to"`. Store `adbPort` and `adbConnectedIP`.
4. Failure outputs are mapped to hints (`"failed to authenticate"`, `"unauthorized"`, `"connection refused"`, `"no route to host"`, `"timed out"`; `:277-284`) and the user is offered "Pair with ADB QR".
5. Wired mode: `adb devices -l`. Prefer the serial mapped to `device.id`; a single attached device is auto-selected (`Handlers.swift:194-251`).
6. When the WS drops: `adb disconnect <ip>:<port>`.
7. Guardian (`mac:Core/AppDelegate.swift:95-108`): if ADB is enabled but not connected, send `refreshAdbPorts`, then retry `connectToADB(silent: true)` after 1.5 s.

### 7.2 Wireless debugging pairing (ADB QR)
`mac:Core/Util/CLI/ADBPairingManager.swift`:
- Random `serviceName = "adb-wireless-<6 digits>"`, `password = <8 digits>`.
- QR text: **`WIFI:T:ADB;S:<serviceName>;P:<password>;;`** (`:41-44`). This is Android's standard "Pair device with QR code" format.
- Browse `_adb-tls-pairing._tcp.` for a service whose name matches `serviceName`. Resolve it to IPv4 and port, then run `adb pair <ip>:<pairPort> <password>` (`:50, 196`).
- Browse `_adb-tls-connect._tcp.` and resolve it, then run `adb connect <ip>:<connectPort>` (`:54, 281`).
- On Windows, mDNS browsing works with `Windows.Networking.ServiceDiscovery.Dnssd` or Bonjour; `adb mdns services` also works.

### 7.3 Broadcast intents (ring when the WS is down)
`PhoneFeatures.swift` (`ringOverADB`):
```
adb -s <serial> shell am broadcast -a com.sameerasw.airsync.RING_PHONE      -n com.sameerasw.airsync/.service.RingCommandReceiver
adb -s <serial> shell am broadcast -a com.sameerasw.airsync.STOP_RING_PHONE -n com.sameerasw.airsync/.service.RingCommandReceiver
```
The receiver is exported but protected by `android.permission.DUMP`, which only the shell or adb holds (`app/src/main/AndroidManifest.xml:308-315`).

### 7.4 appops grant
Two seconds after every ADB connect (`mac:Core/PhoneControl/PhoneAppOpsGranter.swift`):
```
adb -s <serial> shell appops set com.sameerasw.airsync READ_CLIPBOARD allow
```
This lets the phone read the clipboard in the background.

### 7.5 Other one-shot ADB commands
- Lock phone fallback: `adb -s <serial> shell input keyevent KEYCODE_SLEEP` (`FindMyManager.swift:234-256`)
- Phone screenshot: `adb -s <serial> exec-out screencap -p` (`ADBConnector.swift:657-700`)
- Pull: `adb -s <serial> pull <remote> <local>` (file browser and search fetch)
- Push: `adb -s <serial> push <local> <remote>` (`ADBConnector.swift:715-760`)
- Mirror drag-drop: push to `/sdcard/Download/HIB_Drops/<session>/…`, then send `mirrorAttachment` (§6.20)
- Phone Agent (`mac:Core/PhoneAgent/PhoneAgentADB.swift`, an LLM driving the phone; desktop-only):
  - `exec-out screencap -p`
  - `shell uiautomator dump /sdcard/hib_ui.xml >/dev/null 2>&1 && cat /sdcard/hib_ui.xml`
  - `shell input tap X Y`
  - `shell input swipe X1 Y1 X2 Y2 MS`
  - `shell input keyevent N`
  - `shell input text <quoted>`
  - `shell monkey -p <pkg> -c android.intent.category.LAUNCHER 1`

### 7.6 scrcpy invocations
Mirroring and apps (`ADBConnector.startScrcpy`, `ADBConnector.swift:452-560`). Base arguments:
```
--window-title=<title> · <device> --video-bit-rate=<N>M --video-codec=h265 --no-power-on
(--serial=<wired serial> | --tcpip=<ip>:<port>)
[--window-x=X --window-y=Y] [--always-on-top]
camera:  --video-source=camera --camera-facing=<front|back> --no-control --no-audio
normal:  [--stay-awake] [--turn-screen-off] [--no-audio] [--keyboard=uhid]
desktop mode: --new-display=/<dpi> -x
plain mirror: --max-size=<resolution>
single app (pinned apps / "Mac Apps"): --new-display=/<dpi> --start-app=<pkg> --no-vd-system-decorations -x [--no-vd-destroy-content]
```
"Mac Apps" (`mac:Core/MacApps/AndroidMacAppsManager.swift`) creates launcher bundles in `~/Applications/H.I.B Apps/` that open `hib://open-app?package=<pkg>&name=<label>`, which in turn calls the single-app scrcpy. On Windows, use `.lnk` shortcuts plus a `hib:` URL protocol handler.

Play phone audio on the desktop (`PhoneAudioBridge.start`, `PhoneFeatures.swift`):
```
scrcpy -s <serial> --no-video --no-control --no-window --audio-source=playback --audio-codec=opus --audio-buffer=80 [--audio-dup]
```
The desktop sends `phoneAudioState` when the process becomes active (its output contains "Device:" or "audio", or it is still running after 2.5 s) and when it exits. On a non-zero exit, `error` is set to the last `ERROR` line.

---

## 8. Bluetooth LE fallback (optional for Windows)

UUID base `-7461-4694-8146-2162624a682c` (`mac:Core/BLE/BLEConstants.swift:5-50`; `and:data/ble/BleConstants.kt`).

| Service | UUID |
|---|---|
| System | `a1520001-7461-4694-8146-2162624a682c` |
| Notifications | `a1520002-…` |
| Media | `a1520003-…` |
| Clipboard | `a1520004-…` |
| Proximity beacon (non-connectable advert, low power, while Wi-Fi is up) | `a15200f0-…` |

| Characteristic | UUID suffix | Direction / format |
|---|---|---|
| ProtocolVersion | `a1520101` | `1` |
| AuthToken | `a1520102` | central writes the UTF-8 `bleAuthToken` (§2.4) |
| AuthResult | `a1520103` | notify: `0x01` = success, `0x00` = fail |
| BatteryLevel | `a1520104` | notify: 1 byte = phone battery % |
| MacBattery | `a1520105` | central writes `"<level>|<1/0>"` |
| SystemState | `a1520106` | |
| MacControl | `a1520107` | phone notifies `"media|<action>"`, `"volume|<action>"`, `"remote|<action>[|value]"`, `"remote|manual_disconnect"` |
| DeviceName | `a1520108` | notify: phone name |
| NotificationData | `a1520201` | notify: `pkg␟app␟title␟text` |
| NotificationAction | `a1520202` | central writes `id|name|text` |
| NotificationDismiss | `a1520203` | central writes the id |
| NotificationDismissNotify | `a1520204` | notify: id |
| MediaState | `a1520301` | notify: `playing(1/0)␟title␟artist␟volume␟muted(1/0)␟likeStatus` |
| MediaControl | `a1520302` | central writes `playPause`/`next`/`volUp`/`setVolume|N`/`callAccept`/`callDecline`/`callEnd`/`toggleNotif|pkg|state`/`manual_disconnect`, … |
| MacMediaState | `a1520303` | central writes `playing␟title␟artist␟volume␟muted␟likeStatus[␟albumArtLite]` |
| ClipboardDataNotify | `a1520401` | notify: text |
| ClipboardDataWrite | `a1520402` | central writes text |

- `␟` is U+001F (`delimiter = "\u{001F}"`).
- Chunking (`mac:Core/BLE/BLEChunkUtil.swift`): each packet is `[index: UInt16 BE][total: UInt16 BE] + payload`. Payload size = MTU − 4. Reassembly happens per characteristic.
- Flow: the central scans for the System service. When `bleAuthToken` is known and Wi-Fi isn't connected, it connects (10 s timeout), discovers all 4 services, subscribes to every notify characteristic, and writes AuthToken. After AuthResult `0x01` it treats the link as connected (`mac:Core/BLE/BLECentralManager.swift:275-526`).

**Walk-away lock** (`mac:Core/Proximity/ProximityMonitor.swift`; desktop-only):
- RSSI comes from the connected link, or from a passive scan for the System or beacon service UUIDs.
- Smoothing: mean of the last 5 samples within 12 s.
- Away thresholds: near **−65**, normal **−75**, far **−85** dBm. Hysteresis **+7** dB.
- Lock delays: 10/20/30/60 s. Skipped while the user was active in the last 60 s.
- Settings keys: `hib.walkAway.*`.

---

## 9. Known mismatches and gotchas (handle these in Windows)

1. **Volume action names.** Send `increaseVolume`, `decreaseVolume`, `toggleMute`, or `setVolume` (§6.6), not the Mac's `volumeUp`, `volumeDown`, or `mute`.
2. **Desktop→phone file transfer needs a multi-desktop phone** (§6.9, §6.22); older phones never ack. `filePull` still has no phone handler.
3. **`bleAuthToken` can be an IP string** after `refreshAdbPorts` (§4.1). Validate it before storing.
4. **`call_event` without `normalizedNumber`.** Don't require the field (§6.8).
5. **The phone hand-builds some JSON** (`device`, `status`, `appIcons`, `dismissalResponse`, `mediaControlResponse`, …) without escaping. Use a tolerant parser, or accept that some messages with quotes in titles may be malformed.
6. **Unknown types.** Ignore them silently. Examples are `pong`, `*Response` variants, and future types.
7. **Identity is the desktop name.** Keep it stable and identical across QR, `macInfo`, mDNS, UDP, and wake-up. Renaming the desktop breaks the phone's auto-reconnect and wake-up key lookup until the user re-scans.
8. **The QR key is not URL-encoded; the name is.** The query separator is `?`.
9. **The UDP wake-up must be bare JSON.** The Mac's `AIRSYNC_WAKEUP:` prefix is not understood by the phone.
10. **Answer WebSocket control pings.** OkHttp pings every 5 s.
11. **`type`/`categoryType` in `macInfo` must not be "iPhone"/"iPad"**, or the phone stops syncing app icons.
12. **`browseData` errors include `items:[]`.** Check `error` first.
13. **Units.** `status` from the phone uses milliseconds; `status` from the desktop uses seconds (`elapsedTime`, `duration`).
14. **Remote keypresses are macOS keycodes.** Map them to Windows virtual keys.
