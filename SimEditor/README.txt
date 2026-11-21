BMS Sim Editor
==============

Everything about a theater's own Sim data, for any add-on theater installed in BMS or for a TerrainTool project (.ttproj).

  Open                 the theater chosen in the BMS Suite main menu, or: Theater installed in BMS / Recent / TerrainTool project...
  Sim files            the Sim files (missiles, radars, vehicles, bombs, flight models of AI-only aircraft) as tables of values;
                       the first save makes the theater's copy in the work folder (SimOverrides). Flight models of aircraft
                       flyable in BMS and AcTypes.lst are not listed: they stay BMS's, as the BMS developers ask.
  Copy a BMS Sim file to SimOverrides...
                       copies whole files into SimOverrides (same path), to be edited there.
  Database changes (DbEdits.txt)
                       one change per line: TABLE Num Field Value  # comment, or COPY TABLE From To (next free number).
  Install into BMS...  BMS closed, backup first. Installed theater: the changed Sim files into its own Sim folder (made the
                       first time as a copy of BMS Data\Sim, simdatadir set in its .tdf) and DbEdits.txt into its database.
                       TerrainTool project: the theater's Sim made again (BMS Data\Sim + SimOverrides) and installed; when
                       DbEdits.txt changed, the objects are built again with it (as Run Objects) and installed with the
                       campaign files.
  Restore...           (installed theater) puts the theater back as before an install.
  Check SAM systems & positions
                       air defence battalion types (launchers, missiles, radars, rate of fire) and, for every campaign and TE
                       of the theater, whether each battalion fits in the point list BMS gives it on its objective.

BMS's own Data\Sim and BMS's own theaters are never changed.

Build:  build.bat   (needs the .NET 8 SDK; the shared library is ..\TerrainTool\src\TerrainTool.Core)
Start:  run.bat
