BMS Sim Editor
==============

For a theater made with TerrainTool (a .ttproj project): everything about the theater's own Sim data.

  Open                 from TerrainTool (button "Sim Editor" at the top), or Recent projects / Browse project...
  Copy a BMS Sim file to SimOverrides...
                       copies files of BMS Data\Sim into the project's SimOverrides folder (same path), to be edited there.
                       Build theater (TerrainTool) makes NT\Sim = BMS Data\Sim + SimOverrides.
  Install theater Sim into BMS...
                       BMS closed: copies NT\Sim into the theater's folder and sets simdatadir in its .tdf (backup first).
  Database changes (DbEdits.txt)
                       one change per line: TABLE Num Field Value  # comment, or COPY TABLE From To (next free number).
                       Applied by Run Objects (Objective Studio) on top of BMS's database at every run.
  Check SAM systems & positions
                       air defence battalion types (launchers, missiles, radars, rate of fire) and, for every campaign and TE
                       of the project, whether each battalion fits in the point list BMS gives it on its objective.

BMS's own files are never changed.

Build:  build.bat   (needs the .NET 8 SDK; the shared library is ..\TerrainTool\src\TerrainTool.Core)
Start:  run.bat
