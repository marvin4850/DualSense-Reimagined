# DualSense Reimagined – Full DualSense Integration

Source code of the Gothic 1 Remake mod **DualSense Reimagined** by Marvin2242: HD haptics, adaptive triggers, lightbar, touchpad gestures and microphone for the Sony DualSense controller.

## How it works

```
Gothic 1 Remake + ModKit script mod (Mod/)
   │  game events encoded into the game window title (no injection, no game files changed)
   ▼
DsBridge.exe (DsBridge/, .NET 8)
   │  USB: HD haptics as audio on the pad's haptic channels + HID output reports (triggers, motors, lightbar)
   │  Bluetooth: HID report 0x31/0x32
   ▼
DualSense
```

| Folder | What it is |
|---|---|
| `Mod/G1R_DualSenseBridge` | Official ModKit script mod (AngelScript). Reads game state and sends events through the window title. It is compiled by the game itself. |
| `DsBridge` | Companion program: reads events, plays haptic rules, drives the controller. It also reads the touchpad and microphone (only to detect a blow, nothing is recorded). |
| `Launcher` | `GothicDualSense.exe`: finds the game, syncs the mod, starts DsBridge hidden, then the game. Shows a window only on errors. |
| `haptics` | Effect rules (`packs/*.json`), recipes (`forge/`) and the synthesized HD sample bank (`bank/`). |
| `tools/release.ps1` | Builds the release archive uploaded to Nexus Mods. |

The executables do not inject code, patch memory or modify game files. Network access is not used.

## Build

Requirements: Windows 10/11 x64, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), Python 3.10+ (for the haptic bank conversion step, standard library only).

```powershell
git clone https://github.com/marvin4850/DualSense-Reimagined.git
cd DualSense-Reimagined
powershell -ExecutionPolicy Bypass -File tools\release.ps1
```

Result: `release\DualSense-Reimagined-v<version>.zip` and its `.sha256`. Steps the script performs:
1. `dotnet publish DsBridge` (self-contained, win-x64) → `GothicDualSense\bin\DsBridge.exe`
2. `dotnet publish Launcher` (single file, trimmed) → `GothicDualSense\GothicDualSense.exe`
3. `tools/release_haptics.py`: copies `haptics` and converts the WAV bank from float32 to 16-bit PCM
4. Copies the mod into `G1R\Mods\G1R_DualSenseBridge` and `GothicDualSense\mod`
5. Checks the packaged rules (`DsBridge.exe rules-check`) and creates the zip

To build only the programs: `dotnet build -c Release DsBridge` and `dotnet build -c Release Launcher`.

## Install (players)

Extract the release archive into the game folder and start the game with `GothicDualSense\GothicDualSense.exe`. Details are in `Launcher/README.txt`.

## License

MIT, see `LICENSE`. Third-party code (Jellybit's DualSense Haptic Designer, tslaceo's G1LockPickHelper, HidSharp, NAudio) keeps its own license, see `THIRD_PARTY_NOTICES.md`.
