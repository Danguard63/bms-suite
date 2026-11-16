BMS Database Interchange
========================

Copies things from one Falcon BMS 4.38 theater to another, with everything they need, renumbered for the destination.

Now: bases and ground objectives (objective layouts).
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

Coming next: aircraft, vehicles and weapons (CT, VCD, slots, hardpoint lists, weapons, sensors, 3D models).

Build:  build.bat   (needs the .NET 8 SDK; the shared library is ..\TerrainTool\src\TerrainTool.Core)
Start:  run.bat
