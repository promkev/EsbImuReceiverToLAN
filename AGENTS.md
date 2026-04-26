# AGENTS.md — ESb IMU Receiver to LAN

## Repository Layout

- `EsbImuReceiverToLAN/` — Windows console app (.NET 10)
- `EsbImuReceiverToLanAndroid/` — .NET MAUI Android app (also builds for Quest)
- `EsbImuReceiverToLanESP32/` — Arduino/ESP32-S3 firmware (PlatformIO)
- `SlimeImuProtocol/` — **git submodule** (`https://github.com/Sebane1/SlimeImuProtocol.git`)
  - **Must run `git submodule update --init` after clone or the C# projects will fail to compile.**

## Build System Quick Reference

| Target | Toolchain | Key File |
|--------|-----------|----------|
| Windows desktop | `dotnet build` or VS 2022+ | `EsbImuReceiverToLAN/EsbImuReceiverToLAN.csproj` |
| Android / Quest | `dotnet build` or VS with Android workload | `EsbImuReceiverToLanAndroid/EsbImuReceiverToLanAndroid.csproj` |
| ESP32-S3 | PlatformIO CLI or VS Code + PlatformIO extension | `EsbImuReceiverToLanESP32/platformio.ini` |

### .NET Solution configurations
The `.sln` defines custom configurations beyond Debug/Release:
- `Release-Phone` — builds phone APK using `AndroidManifest.xml`
- `Release-Quest` — builds Quest APK using `AndroidManifest.Quest.xml`
Both release configs reference an absolute keystore path (`I:\Visual Studio\...\smolslimetolan.keystore`).

## ESP32 Firmware Notes

- **Framework:** Arduino (not ESP-IDF directly), board `esp32-s3-devkitc-1`.
- **USB mode:** Native USB is used for CDC serial logging **until** WiFi connects and SlimeVR handshake completes. Then `SerialManager::deinit()` is called and the same USB hardware switches to USB Host mode to read the HID tracker dongle.
- **Critical side-effect:** Once USB Host starts, there is **no serial output** (including crash info). Use LED or UDP logs for post-handshake debugging.
- **HID watchdog:** If no HID report arrives for `HID_WATCHDOG_TIMEOUT_MS`, the firmware calls `ESP.restart()`.
- **External lib:** Depends on `https://github.com/esp32beans/ESP32_USB_Host_HID.git` via `lib_deps` in `platformio.ini`.
- **WiFi provisioning:** If no saved credentials exist, the device starts a captive portal AP. SSID/password are saved to NVS via `WiFiConfig`.

## Android / Quest Notes

- **Entrypoint:** `MainActivity.cs` wires USB attach/detach intents to `TrackerListenerService`.
- **Foreground service:** The service must be started as `StartForegroundService` on Android O+.
- **Manifest split:** Quest uses `AndroidManifest.Quest.xml` (selected via conditional `AndroidManifest` property in the csproj); phone uses the default manifest.
- **USB host permission:** `device_filter.xml` restricts auto-launch to the correct VID/PID.

## Windows Desktop Notes

- **HID library:** Uses `HidSharp` (NuGet 2.1.0).
- **Auto-discovery:** On first run it broadcasts UDP to discover the SlimeVR server IP, then persists the address to `config.txt` next to the executable.
- **Runtime dependency:** Requires the HID USB dongle to be plugged in at startup; no graceful hot-plug is implemented in the current code.

## Cross-Platform Shared Protocol

All three targets consume `SlimeImuProtocol` for the SlimeVR UDP packet format and tracker state logic. Do **not** inline protocol constants locally; changes belong in the submodule so they stay in sync across targets.
