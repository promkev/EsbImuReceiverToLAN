# AGENTS.md

## Repo overview

Multi-target relay: reads SlimeVR tracker data from USB HID dongles (VID 0x1209, PID 0x7690) and forwards it to a SlimeVR server over UDP.

Three targets:
- **`EsbImuReceiverToLAN/`** — .NET 10 console app (Windows/desktop)
- **`EsbImuReceiverToLanAndroid/`** — .NET MAUI Android app (phone + Meta Quest)
- **`EsbImuReceiverToLanESP32/`** — ESP32-S3 firmware via PlatformIO

The SlimeVR protocol parsing is in the **`SlimeImuProtocol/`** git submodule.

## Setup

```bash
git submodule update --init --recursive
```

## Building

### Desktop (.NET console)
```bash
dotnet build EsbImuReceiverToLAN.sln
```

### Android (phone)
```bash
dotnet build EsbImuReceiverToLAN.sln -c Release-Phone
```

### Android (Quest)
```bash
dotnet build EsbImuReceiverToLAN.sln -c Release-Quest
```

### ESP32-S3
```bash
cd EsbImuReceiverToLanESP32 && platformio run --target upload
```

## Solution configurations

| Configuration   | Purpose                  |
|-----------------|--------------------------|
| Debug           | Development              |
| Release         | Desktop release          |
| Release-Phone   | Android APK for phones   |
| Release-Quest   | Android APK for Quest    |

`Release-Phone` and `Release-Quest` differ only in the Android manifest used:
- Phone: `Platforms/Android/AndroidManifest.xml` (full permissions)
- Quest: `Platforms/Android/AndroidManifest.Quest.xml`

## ESP32-S3 quirks

- **USB mode:** CDC serial is deinitialized before USB Host takes over (order matters — see `main.cpp:78`).
- **WiFi provisioning** via serial commands or a captive portal.
- **Watchdog:** reboots the ESP if no HID data arrives for `HID_WATCHDOG_TIMEOUT_MS`.
- **Deferred USB init:** USB Host starts only after WiFi is connected and a SlimeVR handshake completes (or 10s timeout).
- **Loop order matters:** `usbHandler.loop()` must run before `slimeClient.loop()` — HID input is drained first to keep latency low.

## Key files

- `EsbImuReceiverToLAN/Program.cs` — desktop entry point, server discovery + config.txt persistence
- `EsbImuReceiverToLAN/TrackersHID.cs` — USB HID device enumeration, data read loop, packet parsing
- `EsbImuReceiverToLanESP32/src/main.cpp` — ESP32 setup/loop with deferred USB host init
- `EsbImuReceiverToLanESP32/src/UsbHidHandler.cpp` — ESP32 USB HID reading
- `EsbImuReceiverToLanESP32/src/SlimeUdpClient.cpp` — ESP32 UDP forwarding + SlimeVR handshake

## Testing

No test suite exists in this repository.
