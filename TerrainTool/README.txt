BMS NEW TERRAIN TOOL
====================

What it does (version 0.2)
  1 Theater & projection : Theater.txt parameters, center fitted to the airbases, TM projection built
                           the same way as Korea, Theater.txt written to the project Output folder.
  2 Airbases & check map : the 141 Mideast128 airbases with their real position (OurAirports) and their
                           old theater position; map with coastlines, borders, graticule and segment grid.
  3 Source data          : downloads the free datasets for the theater area (Copernicus DEM, OSM water,
                           coastlines and extracts, GEBCO, ESA WorldCover, Hansen) with resume.
  4 Processing           : replaces the QGIS work with GDAL: heightmap, watermap, coastline + coast
                           points, ocean depth, tree cover / forest maps, land cover, roads/bridges.
                           Output: <project>\Output\TB_Input (RAW files ready for Terrain Builder).
  5 NewTerrain check     : compares a NewTerrain folder with the Korea structure (read-only).

Rule: the tool NEVER writes into the BMS installation, Korea, Terrain Builder or the old theater.
Everything it produces goes to <project>\Output\NewTerrain.

Build
  build.bat   -> builds, runs the core library tests and writes build.log
  run.bat     -> starts the built tool

Layout
  src\TerrainTool.Core   library (projection, theater, airbases, NewTerrain check) - no external dependencies
  src\TerrainTool.Gis    processing steps (GDAL via the MaxRev.Gdal NuGet packages)
  src\TerrainTool.App    WPF user interface
  tests\CoreTest         projection tests against PROJ/pyproj reference values
  Data\airbases.csv      airbases: real Lat/Lon + LatOld/LonOld from the old theater
  Data\mappa_base.txt    Natural Earth 1:10m coastlines and borders (public domain)

Notes on the data
  - The coordinates in the qaf_mideast128_navaids PDF are the old theater's in-game positions: compared
    with reality they are shifted 0.2-1.1 deg east (e.g. Kuwait Intl ~0.7 deg). The new terrain is built
    from real data, so the center is fitted to the real positions.
  - Airbases marked "to verify" could not be matched reliably to OurAirports: enter the real Lat/Lon in
    tab 2 and save.
  - Real airbase coordinates: OurAirports (public domain), https://ourairports.com/data/
