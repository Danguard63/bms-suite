BMS Campaign Editor
===================

Editor for Falcon BMS campaigns (.cam) and Tactical Engagement missions (.tac), for any installed theater
(Korea, Israel, Korea TvT, Mideast ...) or for a TerrainTool project (.ttproj).

Saving:
  - BMS theater:         Save writes into the file you opened, also in the theater's Campaign folder in BMS
                         (you are asked once per session: back up the Campaign folder first).
  - TerrainTool project: the project's CampaignEdits folder (+ copy in Output\Install); into the add-on with
                         "Install into BMS..." in the editor.
  - Save As...:          any name and folder (inside BMS only Campaign folders).
  Every file that Save / Save As replaces is first copied into Backup\<date_time> (in this folder):
  the "Backups..." button lists them and restores them.

Build:  build.bat   (needs the .NET 8 SDK; the shared library is ..\TerrainTool\src\TerrainTool.Core)
Start:  run.bat
        run.bat --project "C:\TerrainTool\Projects\Mideast\Mideast.ttproj"
        run.bat --project <file.ttproj> --open <file.cam or .tac>

Start window: on the left the TerrainTool projects ("Add TerrainTool project...") and the theaters of the
BMS Data folder; on the right their campaign and TE files. Double-click a file to open it.
