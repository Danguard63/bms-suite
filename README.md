# BMS Suite

[![Support on Ko-fi](https://img.shields.io/badge/Support%20on-Ko--fi-F2A900?logo=kofi&logoColor=white)](https://ko-fi.com/danguard63)

Editing tools for **Falcon BMS 4.38**: theaters, objectives, campaigns, user interface art, 3D models and databases.
One main menu (`BMSSuite.exe`) starts every program on the theater you are working on.

| Program | What it does |
|---|---|
| **TerrainTool** | Builds the New Terrain of a theater from free real-world data (elevation, water, land cover, roads), the input for Terrain Builder. |
| **Objective Studio** | Objectives of a theater: airbases and layouts, pieces, parking, bridges, coastal moves; builds and installs the objects with a backup. |
| **Art Editor** | User interface art: flags, menu backgrounds, banners, campaign pictures, small maps, patches, fonts, sounds, texts. |
| **Campaign Editor** | Campaigns (.cam) and Tactical Engagements (.tac): units, squadrons, objectives, priorities, stations, theater cells. |
| **Model Editor** | 3D models: DirectX view with PBR materials, DOFs, switches, slots, Parent.dat checks. |
| **Database Interchange** | Copies bases and objective layouts from one theater to another, renumbered for the destination (aircraft and vehicles next). |

## Download and install

1. Open **Releases** (right side of this page) and download `BMSSuite-<version>.zip`.
2. Unzip it into a folder you can write to, for example `C:\BMS Suite` (not inside `Program Files`, not inside BMS).
3. Start `BMSSuite.exe`.

Needs Windows 10/11 x64 and the **.NET 8 Desktop Runtime** (free, from Microsoft:
https://dotnet.microsoft.com/download/dotnet/8.0 → ".NET Desktop Runtime" x64). Windows asks to install it the first time if it is missing.

## Safety

- The programs read Falcon BMS. Before a program writes into BMS it copies every file it replaces into its own backup folder,
  and each program has a **Restore**. The main menu lists every backup.
- Close BMS before installing anything into it. **Back up your BMS `Data` folder** before you start editing.
- The Korea art (`Data\Art`) and the shared objects (`Data\Terrdata\objects`) of BMS are never changed.

## Build from source

Needs the .NET 8 SDK on Windows.

- One program: run `build.bat` in its folder, then `run.bat`.
- The whole package: `powershell -ExecutionPolicy Bypass -File tools\make-release.ps1 -Version 0.1.0` → `dist\BMSSuite-0.1.0.zip`.
- GitHub Actions builds the package on every push to `main` (run → Artifacts); publishing a release `vX.Y.Z` on GitHub builds the zip and attaches it.

The shared library is `TerrainTool\src\TerrainTool.Core`; every program references it.

The BMS Suite is free. If it helps you, you can buy me a coffee on [Ko-fi](https://ko-fi.com/danguard63) – thank you!

Falcon BMS is a product of Benchmark Sims. This project is not affiliated with Benchmark Sims. Third-party data and code: see [CREDITS.md](CREDITS.md).
