BMS Suite
=========

The main menu of the BMS Suite: editing tools for Falcon BMS 4.38.

  run.bat         starts the menu (build it first with build.bat)

What it shows
  - Falcon BMS 4.38 found (Data folder from the registry, or chosen by clicking the path), theaters of theater.lst
  - Default theater: each program is started with it (--theater "<name>" --bms-data "<Data folder>")
  - Continue: the last program opened, on the same theater
  - One tile per program: TerrainTool, Objective Studio, Art Editor, Campaign Editor, Model Editor, Database Interchange
    ("not built yet" = run build.bat in that program's folder; "running" = it is open)
  - Backups: every backup made by the programs, newest first (restore from the program that made it)

The menu writes nothing into BMS. Its settings are in ..\Settings\suite.json.
Programs are looked for in ..\Programs (release package) or in each program's folder (development build).
