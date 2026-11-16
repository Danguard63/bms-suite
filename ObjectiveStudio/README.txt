BMS Objective Studio
====================

What is inside the objectives of a Falcon BMS 4.38 theater: airbases, layouts and their pieces, parking points,
Korean bases, bridges. It replaces the old Object Editor.

Start (run.bat) and choose:
  - a theater installed in BMS (Korea, Israel, add-ons...): Objective Studio only READS BMS. Your edits are kept in
    Theaters\<theater> until you build and install them (coming in the next version: Build objects / Install into BMS,
    with a backup of every file replaced);
  - a TerrainTool project (a theater being converted from an older BMS version): Run Objects from the old add-on,
    conversion tools (real runways, bridges, coastal moves), Install objects.

Window: 1 Edit objectives -> 2 Build objects -> 3 Install into BMS and update the campaign. The blue "Next step" box
says what to do now.

Build:  build.bat   (needs the .NET 8 SDK; the shared library is ..\TerrainTool\src\TerrainTool.Core)
Start:  run.bat     or  run.bat --project "<work folder or TerrainTool project>.ttproj"
