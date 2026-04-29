# Fix #2: Move `slimeClient.loop()` to Separate FreeRTOS Task

## Problem

After the first round of latency fixes (commit 008377d), the remaining bottleneck is that `usbHandler.loop()` (HID drain + UDP sends) and `slimeClient.loop()` (heartbeats, handshake retries, RX parsing of SlimeVR server responses) run sequentially in `main::loop()`. Fix #1 (time-sliced HID drain) mitigates this by limiting how many HID reports are processed per call, but the two loops still run mutually exclusively.

The fix does not fully eliminate the problem because:

```
  usbHandler.loop()      // drains up to 8 HID reports → sends UDP
  slimeClient.loop()     // iterates 16 trackers: handshakes + heartbeats + RX parse
  delay (implicit from WiFiManager/SerialManager loops + any blocking)
```

Between `slimeClient.loop()` calls, if the WiFi hardware is congested, LWIP buffer exhaustion can stall HID data processing.

## Proposed Solution

Move `slimeClient.loop()` to a dedicated FreeRTOS task of equal or higher priority than the main loop task, so it runs concurrently with HID processing rather than after it.

```
FreeRTOS Task: "slime_rx"    → slimeClient.loop()  (heartbeats, handshakes, RX)
FreeRTOS Task: "main"        → usbHandler.loop()    (HID drain, UDP sends)
```

## Thread-Safety Analysis

### Shared State in `SlimeUdpClient`

| State | Written by | Read by | Hazard |
|-------|-----------|---------|--------|
| `_trackers[i].packetId` | Both (`nextPacketId` from send calls + loop heartbeat) | Both | Increment not atomic on Xtensa for `long` (could produce missing/duplicated IDs) |
| `_trackers[i].handshakeOngoing` | RX parse sets `false`; timeout sets `true` | Handshake resend logic | Lost-update possible |
| `_trackers[i].isInitialized` | RX parse sets `true`; timeout sets `false` | `sendRotation`/`sendAcceleration` guard | Can send data after timeout races with RX |
| `_trackers[i].active`, `lastTimeoutTime` | `loop()`, `initializeTracker()` | `loop()` handshake logic | Non-atomic multi-byte writes |
| `_trackers[i].udp` (WiFiUDP socket) | Both tasks | Both tasks | `beginPacket()`/`endPacket()` are NOT reentrant across tasks on Arduino WiFiUDP |
| `_discoveryMode`, `_serverIp`, `_globalHandshakeSuccess` | RX parse | `sendHandshake`, `sendHeartbeat` | Lost-update, torn reads |

### The WiFiUDP Reentrancy Problem

This is the hardest issue. Arduino's `WiFiUDP` is not task-safe. If the HID task calls `vt.udp.beginPacket()` while the RX task is inside `vt.udp.parsePacket()` on the same socket, the internal LWIP socket state is corrupted. There are 16 per-tracker sockets — both tasks iterate all of them.

## Partitioning Options

### Option A: Work Partitioning by Responsibility
```
Task: "hid_out"   → owns sendRotation, sendAcceleration, sendBattery
                    (reads: isInitialized, packetId; writes: lastRotSendTime, lastAccelSendTime, lastBatterySendTime, packetId)
Task: "slime_rx"  → owns RX parse, handshakes, heartbeats, timeouts
                    (reads/writes: everything else)
```

Shared fields need to be clearly owned by one task. Need to add explicit handoff mechanisms:
- Packet ID: increment by "hid_out" only (heartbeats in RX task need separate sequence or skip IDs)
- `isInitialized`: written by "slime_rx" only; "hid_out" reads with a volatile/synchronized access (Xtensa single-byte loads are atomic, `bool` is 1 byte)
- `handshakeOngoing`: written by "slime_rx" only; no cross-task conflict
- UDP socket: "slime_rx" owns `parsePacket()`/`read()`; "hid_out" owns `beginPacket()`/`write()`/`endPacket()`. These are separate LWIP paths on the same socket, possibly safe if not overlapped — needs verification on ESP-IDF.

**Risk**: Medium. Socket path collision needs ESP-IDF-specific analysis.

### Option B: Full Mutex per VirtualTracker
Add `SemaphoreHandle_t` or `portMUX_TYPE` (spinlock) to each `VirtualTracker`. Both tasks take the lock before touching any tracker state.

**Risk**: High. Contention on busy trackers. Lock held during `udp.endPacket()` (blocking). Could reintroduce latency.

**Risk of deadlock**: Low (single lock per tracker, no nested locking) but the blocking-in-critical-section problem makes Option A preferable.

### Option C: FreeRTOS Message Buffer for One-Way Commands
Restructure so `usbHandler.loop()` posts rotation/accel data to a FreeRTOS Stream/Message Buffer. The slime task dequeues and sends. The slime task also does RX + heartbeats.

```
Task: "hid_out"   → processHidData() → post to stream_buffer (no UDP)
Task: "slime_io"  → dequeue from stream_buffer → send UDP  +  RX parse + heartbeats
```

**Upside**: Clear ownership. No locks. HID drain is pure CPU work (no blocking on WiFi).
**Downside**: Adds buffer copy latency (~microseconds). Stream buffer has fixed size (can overflow under load). More code restructuring.

**Risk**: Low-Medium. The most architecturally clean approach.

## Recommendation

**Option C** is the safest path. The tradeoffs are acceptable:
- ~50-100µs of added copy latency from the stream buffer (negligible vs. WiFi send time)
- Stream buffer overflow → oldest packets dropped (acceptable for real-time sensor data; newer data is better)
- Code restructuring touches `SlimeUdpClient` + `UsbHidHandler` + `main.cpp`

### Implementation Sketch

```cpp
// In SlimeUdpClient.h
class SlimeUdpClient {
    // ... existing ...
    StreamBufferHandle_t _rotationStream;   // carries trackerIndex + qx/qy/qz/qw
    StreamBufferHandle_t _accelStream;      // carries trackerIndex + ax/ay/az
    // Battery remains on slime task (infrequent)
    
    void begin(...) {
        _rotationStream = xStreamBufferCreate(8192, 1);  // ~256 rotation msgs
        _accelStream = xStreamBufferCreate(4096, 1);     // ~256 accel msgs
    }
    
    void postRotation(uint8_t idx, float qx, float qy, float qz, float qw);
    void postAcceleration(uint8_t idx, float ax, float ay, float az);
};

// In slime task:
void slimeTaskLoop() {
    while (true) {
        // Drain stream buffers → send UDP
        drainRotationStream();
        drainAccelStream();
        // Heartbeats, handshakes, RX parse
        trackHandshakes();
        sendHeartbeats();
        parseRxPackets();
        vTaskDelay(1);  // yield to WiFi stack
    }
}
```

The HID-side `processHidData()` calls `udpClient->postRotation(...)` and `udpClient->postAcceleration(...)` instead of `sendRotation()`/`sendAcceleration()`. The slime task drains the buffers and actually calls the UDP send methods.

### Items to Verify Before Implementation
- [ ] ESP-IDF `StreamBuffer` vs raw `MessageBuffer` — which has lower overhead for 20-byte fixed-size messages?
- [ ] Verify `WiFiUDP::parsePacket()` is non-blocking (it should be — it returns immediately with 0 if no data)
- [ ] Test with 12+ trackers on congested WiFi to confirm no LWIP buffer exhaustion
- [ ] Verify no regression in handshake timing (heartbeats must arrive within ~5s window)
