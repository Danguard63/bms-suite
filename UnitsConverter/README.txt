BMS 4.37 Units Converter
========================

Brings aircraft, helicopters, ground vehicles, ships and weapons of an old BMS database (BMS 4.37 or older) into a theater
of a BMS 4.38 installation. Everything is done here, from start to end; a backup is made before every installation.

1  Old database        a BMS 4.37 installation (one entry per theater), an add-on folder (with Terrdata\Objects) or just
                       an objects folder with Falcon4_CT.xml.
2  BMS 4.38 theater    the BMS 4.38 installation and the theater to convert into. It must have its own database
                       (objectdir): BMS's own Terrdata\objects (Korea) is never written.
3  Find what can be converted
                       lists what the old database has and the theater has not (by name). Tick what to convert.
                         Source model     BMS 4.38 has a different 3D model with the same number: tick to bring the old one
                                          (it gets a free number; nothing of BMS is replaced).
                         Name in the TE   aircraft and helicopters: the name in the Tactical Engagement.
                         TE picture and icon like   the aircraft whose picture and icon the TE list shows.
4  Check the conversion (nothing written): the plan, step by step:
                         1 3D models        the old database's own models and textures, added with a free number when
                                            BMS has another one with theirs (only added, never replaced);
                         2 Hardpoint lists  compared by their weapons: an old add-on often changed BMS's lists keeping the
                                            names (its lists are added when they differ);
                         3 Weapons          BMS's ones with the same name; the others are added with their models;
                         4 Aircraft         free class numbers (Specific), first year of service (4.38 needs one), the
                                            flight model chosen in 'Flies like';
                         5 Squadron and flight types of an aircraft come along;
                         6 Tactical Engagement: a line in the theater's TePlanes.lst and, when the theater has its own
                                            interface folder (artdir), the name in its Art\Main\textids.id and units.irc.
   Convert and install…  BMS closed. Backup first (Backups\<theater>\<time>), then the database, the models, the TE list
                         and the names. FALCON4.* of the theater are removed (BMS builds them again).
   Restore…              puts the theater back as it was before a conversion (newest first).

What is converted is listed in the theater's objects folder (UnitsConverter.csv, UnitsConverter_picks.csv).

Flight models: column 'Flies like'. An AI-only aircraft keeps its own (the old database's flight model, copied into
the theater's Sim folder and added at the end of its AcTypes.lst): the suggested choice. The flight models of aircraft
flyable in BMS are never copied nor changed, as the BMS developers ask: for them the list offers BMS 4.38 flight models
(racks on every hardpoint used, the same pylons left and right, wheels on the ground first). If weapons or wheels look
wrong in BMS: Restore, pick another one, convert again.
3D models and textures of an add-on belong to their authors: ask them before you share a theater with them.
