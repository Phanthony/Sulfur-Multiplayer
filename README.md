<div align="center">
<img width="675" height="520" alt="mp_photo_1" src="https://github.com/user-attachments/assets/1d44a974-344f-464c-a0e8-49107a1abf0d" />
<img width="1838" height="778" alt="mp_photo_2" src="https://github.com/user-attachments/assets/cea7a26c-0293-46f4-a761-3e354f2f7dfa" />
</div>

# SulfurMP Setup Guide

## Prerequisites

- **SULFUR** installed via Steam
- **.NET SDK** (for building the mod)

## BepInEx 5 Installation

1. Download **BepInEx 5.x (x64)** from [GitHub releases](https://github.com/BepInEx/BepInEx/releases)
2. Extract into the SULFUR game directory — creates `BepInEx/`, `winhttp.dll`, `doorstop_config.ini`
3. Run the game once to let BepInEx generate its folder structure

## Building the Mod

1. Copy `src/SulfurMP/GamePath.props.example` to `src/SulfurMP/GamePath.props`
2. Edit `GamePath.props` — set `<SulfurDir>` to your SULFUR install path
3. Build:

```bash
dotnet build src/SulfurMP/SulfurMP.csproj
```

Output auto-deploys to `<SulfurDir>\BepInEx\plugins\SulfurMP\SulfurMP.dll`.


## File Structure

After BepInEx is installed and the mod is built:

```
SULFUR/
├── winhttp.dll              (BepInEx doorstop)
├── doorstop_config.ini      (BepInEx config)
├── BepInEx/
│   ├── core/                (BepInEx runtime — auto-generated)
│   ├── plugins/
│   │   └── SulfurMP/
│   │       └── SulfurMP.dll (the mod — placed by build)
│   └── LogOutput.log        (debug log)
└── Sulfur_Data/
    └── Managed/             (game assemblies — referenced at build time)
```

## In-Game Usage

- **Pause menu** → "MULTIPLAYER" button to host, join, or browse lobbies
- **F9** for debug overlay (network stats, peer list, message log)
