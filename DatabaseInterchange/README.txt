BMS Database Interchange
========================

Copies things from one Falcon BMS 4.38 theater to another, with everything they need, renumbered for the destination.

Tab 1 – bases and ground objectives (objective layouts).
  From / To            the two theaters (theater.lst). The destination must have its own database (objectdir): BMS's shared
                       Terrdata\objects (Korea) is never written.
  Layouts (left)       the objective layouts of the source theater: select one or more (Ctrl / Shift + click).
  Make plan            nothing is written: shows what will be copied and with which numbers:
                         - the layout folder (OCD, pieces FED, runway / point lists PHD PDX, trees OTD) with a new OCD number,
                         - a new Class Table entry for the objective type,
                         - every kind of piece: reused if the destination has the same one (same name, same 3D model),
                           otherwise a new CT + FCD entry (and its radar),
                         - the 3D models the new pieces need and their numbered textures (KoreaObj): reused when the destination
                           has the same one, copied otherwise (a different model with the same number gets a new number).
  Install…             BMS closed. Writes the destination's tables and files; everything replaced is copied first to
                       Transfers\Backups\<time> with a manifest of what was added. FALCON4.* are removed (BMS builds them again).
  Restore…             undoes an installation: removes what it added, puts back what it replaced.

Then, to use an imported base: Objective Studio → open the destination theater → choose the objective → Replace layout →
the new layout (same name as in the source theater) → Save, Build objects, Install.

Tab 2 – aircraft, helicopters, ground vehicles, ships, weapons and unit types.
  Kind / search        filter the list of the source theater; "In destination" = the destination has a record with that name.
  Make plan            the CT entry and everything it uses, renumbered for the destination:
                         - vehicle (VCD): radar, weapon slots, hardpoint lists (WLD) and their weapons;
                         - aircraft: aircraft data (ACD) and its sensors (IR, radar, RWR, visual);
                         - weapon (WCD): radar, sim data (SWD), rocket pods (RKT) with their rockets;
                         - unit type (UCD): its vehicles, squadron stores (SSD, weapon numbers renumbered);
                         - 3D models (normal, damaged, destroyed) as for the layouts.
                       A record the destination already has the same is reused; the rest goes at the end of the tables.
                       New weapon numbers are added to the racks with the same name in the destination's BmsRack.dat.
                       Same name, other values: a window asks, record by record (or "Apply to all"):
                         Add as new  – the source's goes in with a new number, the destination's stays as it is;
                         Replace     – the destination's record gets the source's values (same number): everything
                                       that uses it in the destination changes too (a shared weapon list, radar…);
                         Keep        – the destination's record is used as it is.
                       "Same-name records…" changes the choices and makes the plan again.
  Copy the Sim files   also the Sim files they need: missile and bomb data (Misdata, Bombdata), radar data (Radar),
  they need            flight models of AI-only aircraft (Acdata). The same file is reused; a missing one is copied
                       and added at the end of the destination's list; one with the same name but other contents
                       is copied as <name>_<source> (or replaces it, with Replace). Flight models of the aircraft
                       flyable in BMS are never copied. A destination that uses BMS's Data\Sim first gets its own
                       Sim folder (a copy of BMS's) and 'simdatadir' in its .tdf.
  Install… / Restore…  as for the layouts (backup, manifest, FALCON4.* removed). Back up your BMS folder first.

Build:  build.bat   (needs the .NET 8 SDK; the shared library is ..\TerrainTool\src\TerrainTool.Core)
Start:  run.bat
