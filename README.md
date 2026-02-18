# SulfurMP - Multiplayer Mod for SULFUR

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
