BMS Model Editor
================

3D models of Falcon BMS 4.38 (Data\Terrdata\objects\Models\<number>), their textures, and the theater's database.

  List (left)          aircraft, helicopters, ground vehicles, ships and weapons from the BMS database
                       (Falcon4_CT / VCD / WCD), or every model folder by number. Search by name or model number.
  3D view              LOD and livery, DOF sliders with their names (gear, flaps, canopy, ...), switches
                       on / off, weapons mounted on the slots, slot markers, front / side / top views,
                       Screenshot... (PNG at twice the window size, default folder Screenshots).
  Parent.dat           text editor with "Check data" (LOD files, Dofs / Switches numbers, slots, liveries,
                       textures, radius) and "Suggest values" (Dofs, Switches and radius computed from the model).
                       Save writes into BMS: BMS must be closed, the old file is copied to Backup first.
  Info                 who uses the model (CT entries), files, every LOD (triangles, DOFs, switches, slots),
                       materials and textures.
  Open folder...       opens a model folder outside BMS, for example your own model exported with the BMS Blender plugin (Parent.dat, Model_N.bml,
                       Materials.mtl, textures): 3D view, DOFs, switches, slots, Check data, Info. You can also drag
                       the folder onto the window. Nothing is copied into BMS; shared textures (e.g. "1909/PilotUS_ALB.dds")
                       and numbered textures are looked for in BMS too. Save in the Parent.dat tab writes in that folder.
  Check all models...  checks every model folder and lists the problems; double-click opens the model;
                       Export... writes a tab separated text file.
  Textures...          textures of aircraft, helicopters, vehicles and ships with their liveries: replace a texture (the old
                       one is kept: Backups in that window), 3D preview. Also: run.bat --textures <model number>.
  Database (tab)       the theater's Falcon4_*.xml as the BMS Editor shows them; changes kept in the work folder until
                       Install into BMS (backup, Restore). On a vehicle: Duplicate... makes a copy under another name
                       (e.g. "F-16CM-50 Red" for the other side of a PvP theater): same 3D model, flight model (only
                       referred to), sensors and weapons; its own class number, squadron and flight types, TE line and
                       name. Installed at once with a backup; "Remove a copy..." takes it away.
  Backups...           every file changed by the Model Editor, with Restore.

Nothing is written into BMS except by "Save" (Parent.dat tab), Textures (Replace), Database (Install into BMS, Duplicate). Before changing files inside BMS make a backup of
your BMS Data folder.

Data\DOF.xml and Data\switch.xml (names of DOFs and switches) come from the official BMS Blender exporter
(bms-blender-plugin, Copyright (c) 2023 Benchmark Sims, MIT license: Data\LICENSE-bms-blender-plugin.txt).
How DOFs, switches and slots work in a BML file was also taken from that exporter.

Build:  build.bat   (needs the .NET 8 SDK; the shared library is ..\TerrainTool\src\TerrainTool.Core)
Start:  run.bat     or  run.bat --model 1502   or  run.bat --data "D:\Falcon BMS 4.38\Data" --model 724
