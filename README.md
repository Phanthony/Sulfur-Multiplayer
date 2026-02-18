# SulfurMP - Multiplayer Mod for SULFUR
<div align="center">
<img width="675" height="520" alt="mp_photo_1" src="https://github.com/user-attachments/assets/1d44a974-344f-464c-a0e8-49107a1abf0d" />
<img width="1838" height="778" alt="mp_photo_2" src="https://github.com/user-attachments/assets/cea7a26c-0293-46f4-a761-3e354f2f7dfa" />
</div>

# SulfurMP Setup Guide

## Installation

1. Download the latest release from [GitHub Releases](https://github.com/Phanthony/Sulfur-Multiplayer/releases/)
2. Extract the zip and drag the `BepInEx` folder into your SULFUR game directory
   - Default location: `C:\Program Files (x86)\Steam\steamapps\common\SULFUR\`
3. Launch the game

Your SULFUR directory should look like this:

```
SULFUR/
├── winhttp.dll
├── doorstop_config.ini
├── BepInEx/
│   ├── core/
│   ├── plugins/
│   │   └── SulfurMP/
│   │       └── SulfurMP.dll
│   └── ...
└── Sulfur_Data/
```

## In-Game Usage

- **Pause menu** → "MULTIPLAYER" button to host, join, or browse lobbies
- **F9** for debug overlay (network stats, peer list, message log)

---

## Building from Source

For developers who want to build the mod themselves.

### Prerequisites

- **SULFUR** installed via Steam
- **.NET SDK**

### Steps

1. Copy `src/SulfurMP/GamePath.props.example` to `src/SulfurMP/GamePath.props`
2. Edit `GamePath.props` — set `<SulfurDir>` to your SULFUR install path
3. Build:

```bash
dotnet build src/SulfurMP/SulfurMP.csproj
```

Output auto-deploys to `<SulfurDir>\BepInEx\plugins\SulfurMP\SulfurMP.dll`.
