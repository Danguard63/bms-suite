BMS Objective Studio
====================

What is inside the objectives of a Falcon BMS 4.38 theater: airbases, layouts and their pieces, parking points,
Korean bases, bridges. It replaces the old Object Editor.

Start (run.bat) and choose:
  - a theater installed in BMS (Korea, Israel, add-ons...): Objective Studio only READS BMS. Your edits are kept in
    Theaters\<theater> until Build objects / Install into BMS (backup of every file replaced, Restore a backup);
  - a TerrainTool project (a theater being converted from an older BMS version): Run Objects from the old add-on,
    conversion tools (real runways, bridges, coastal moves), Install into BMS.

Window: 1 Edit objectives -> 2 Build objects -> 3 Install into BMS (the objects and the campaign files that follow
them, in one go). The blue "Next step" box says what to do now.

Other tools: Layouts from another theater... (installed theaters): bases and ground objective layouts of another theater
copied into this one with their pieces and 3D models, backup and Restore; then Replace base / Replace layout offer them.
The textures of 3D models are in the Model Editor (Textures...).

Build:  build.bat   (needs the .NET 8 SDK; the shared library is ..\TerrainTool\src\TerrainTool.Core)
Start:  run.bat     or  run.bat --project "<work folder or TerrainTool project>.ttproj"
