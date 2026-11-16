BMS Art Editor
==============

Editor for the user interface art of a Falcon BMS 4.38 theater: team flags, main menu background, theater banner,
campaign pictures, small maps, squadron patches, medals and the texts of Theater.irc (country, team and campaign names).
It works on any installed theater, on the art of a TerrainTool project, on any Art folder, or on a new theater art.

How BMS uses the art:
  The .tdf of a theater has an "artdir" line. In that folder the theater keeps only the files it changes
  (e.g. _FHD\Main.idx/.rsc for the flags); everything else comes from the BMS art (Data\Art).
  The editor shows the art as BMS shows it: the theater's image if it has one, otherwise the BMS one ("BMS default").

Open...        - TerrainTool project: Output\Art\Art of the project (careful: the TerrainTool 'Theater art' job rebuilds it)
               - Theater installed in BMS: its artdir (Save writes into BMS, with a backup)
               - Art made with this editor: ArtEditor\Theaters\<name>\Art
               - Art folder...: any folder; New theater art...: starts from the BMS 4.38 art
Reference...   another art to take images from (another theater, an old 4.36/4.37 add-on such as E:\Add-On Mideast128\Art).
               It is only read.

Screens (visual editor): the BMS screens redrawn as the theater shows them (Full HD), with the menu on the left
  going to the other screens as in BMS. "Edit": click an element to see where it comes from, right-click to change
  its image or text. Colours: green = theater, orange = KTO image (changing it copies the archive into the theater's
  art), red = KTO text/layout (not editable), blue dashed = filled by BMS while it runs.
  Right-click → "Hide in this theater" removes a button or text from the theater's screens (e.g. a PvP art without
  TE Edit, TE Builder...); hidden elements keep a purple dashed frame in Edit mode and can be shown again.
  Fonts: right-click → "Change font..." (the window file is copied into the theater, the KTO is not touched).
  The KTO (Data\Art) is the master of every theater and is never changed by the editor.

For each image: where BMS shows it, the sizes it needs, the theater / BMS / reference previews and
  Replace with an image...   any PNG, JPG, BMP, GIF, TIFF, TGA or DDS: choose the part to use (the frame keeps the
                             proportions BMS needs); it is resized and converted for every file that has it (_FHD and Resource).
                             A flag needs one horizontal image: the editor makes the small, vertical and dark versions.
  Take from reference        the same image of the reference art (resized if needed)
  Reset to BMS 4.38          puts back the BMS image
  Export as PNG...           to edit it in another program
Squadron patches: "Add a new patch..." adds the image and its number (Patches\ImageIds.id): the squadrons of the
  campaign use that number (Campaign Editor).
Texts: edit the "Theater" column.

Save           writes the changed files into the art being edited. Every file it replaces is first copied into
               Backup\<date_time> (in this folder; "Backups" opens it).
Check          wrong sizes (e.g. images of an old add-on), missing images, patches without a number, missing texts.
Copy into a theater...  copies this art into the artdir of an installed theater (with a backup of what it replaces).
               Close BMS first; restart BMS to see the changes.

Build:  build.bat   (needs the .NET 8 SDK; the shared library is ..\TerrainTool\src\TerrainTool.Core)
Start:  run.bat
