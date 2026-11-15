# ZPL macro library: what is bound

OP-99's interpreter (`OpenSim.Optics/Scripting/Zpl/`) runs OpticStudio's ZPL macro language. This
page lists every keyword and function the OpticStudio 2025 R2 help names (its KEYWORDS page, its
ZPL Function Listing and its String Functions page, plus `$GETARG` from Running Macros from the
Command Line), whether this version binds it, and what the binding does or why it is not bound.
A macro that reaches a name marked "no" stops with an error naming the name and the line; the
macro window's **Check** lists them before a run. The language's rules are in
docs/Optics-Conventions.md §56; the user's view is in docs/Optics-User-Guide.md, "ZPL macros".

The table is generated from `ZplLibrary.TableMarkdown()`; `ZplConformanceTests` checks that this
page holds it unchanged and that every bound name has a conformance macro.

Counts: 218 keywords (75 bound, the language's own included), 156 numeric functions (95 bound),
32 string functions (24 bound).

| Name | Kind | Bound | What it does here, or why not |
|---|---|---|---|
| APMN | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| APMX | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| APTP | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| APXD | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| APYD | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| ATYP | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| AVAL | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| BEEP | keyword | no | Not bound: no sound is made by a library run. |
| BROWSE | keyword | no | Not bound: needs a file dialog. |
| CALLMACRO | keyword | yes | Runs a macro of the macro folder as a child; its screen output is copied into the caller's when it returns. The call buffers, lens, vectors and OPTRETURN values are shared. |
| CALLSETDBL | keyword | yes | Sets numeric call buffer entry index (0 to 50). |
| CALLSETSTR | keyword | yes | Sets string call buffer entry index (0 to 50). |
| COAT | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| COMPOSITEOFFAXISAPERTUREON | keyword | no | Not bound: its OpticStudio 2025 R2 help page repeats COMPOSITEON's title, syntax and discussion, so what it does is not given (it may be the Composite page's "Set Tilt/Decenter to follow Base surface aperture", which is not built). |
| COMPOSITEOFF | keyword | yes | Surface n is the base here (add-ons are not numbered as surfaces): its top add-on becomes surface n again (thickness 0, the medium of surface n − 1) and the base moves to n + 1; refused when the add-on is decentered or tilted. Every surface number held elsewhere follows. |
| COMPOSITEON | keyword | yes | Makes surface n (1 to the surface two before the image) a composite add-on of surface n + 1 (above the add-ons that surface has) and removes it from the surface list; refused for the stop, a non-zero thickness, a change of medium, a type the composite table does not list, or row settings an add-on cannot hold. Every surface number held elsewhere follows. |
| CLOSE | keyword | yes | Closes the file opened by OPEN. |
| CLOSEWINDOW | keyword | yes | Without an argument: sets quiet mode (the result says so). With a window number it fails: there are no numbered analysis windows. |
| COLOR | keyword | no | Not bound: macro graphics and image files are not built. |
| COMMAND | keyword | no | Not bound deliberately: it would run a shell command. |
| COMMENT | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| CONI | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| CONVERTFILEFORMAT | keyword | no | Not bound. |
| CONVERTIMAGETOGRID | keyword | no | Not bound: macro graphics and image files are not built. |
| COPYFILE | keyword | yes | Copies a file, overwriting the target. |
| CURV | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| DECLARE | keyword | yes | Language core (Conventions §56). |
| DEFAULTMERIT | keyword | no | Not bound: its arguments map onto the Optimization Wizard (OpticsScripting.WithDefaultMerit), but "any existing default merit function will be deleted" needs the help's rule for which rows are the old default, which it does not give. |
| DELETE | keyword | yes | Deletes surface n (not the object or image); fails when something still names it (a pickup, solve or row). |
| DELETECONFIG | keyword | no | Not bound: the multi-configuration editor's keywords are not bound (SETCONFIG is). |
| DELETEFILE | keyword | yes | Deletes a file (a name without a folder is in the macro folder); a file that does not exist is an error. |
| DELETEMCO | keyword | no | Not bound: the multi-configuration editor's keywords are not bound (SETCONFIG is). |
| DELETEMFO | keyword | yes | Deletes a row, or every row (ALL). |
| DELETEOBJECT | keyword | no | Not bound: the non-sequential model is not built. |
| DELETETOL | keyword | no | Not bound: the tolerance editor's keywords are not bound. |
| EDVA | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| END | keyword | yes | Language core (Conventions §56). |
| EXPORTBMP | keyword | no | Not bound: macro graphics and image files are not built. |
| EXPORTCAD | keyword | no | Not bound: the non-sequential model is not built. |
| EXPORTJPG | keyword | no | Not bound: macro graphics and image files are not built. |
| FINDFILE | keyword | yes | Puts the next file name (without its folder) matching the filter into the string variable, in ordinal order; the empty string when there are no more. A new filter starts again. |
| FLDX | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| FLDY | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| FWGT | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| FVDX | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| FVDY | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| FVCX | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| FVCY | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| FVAN | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| FOR | keyword | yes | Language core (Conventions §56). |
| NEXT | keyword | yes | Language core (Conventions §56). |
| FORMAT | keyword | yes | Language core (Conventions §56). |
| FTYP | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| GCRS | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| GDATE | keyword | no | Not bound: macro graphics and image files are not built. |
| GETDENCUSER1D | keyword | no | Not bound (GETTEXTFILE writes the analyses' text). |
| GETEXTRADATA | keyword | no | Not bound (GETTEXTFILE writes the analyses' text). |
| GETGLASSDATA | keyword | no | Not bound (GETTEXTFILE writes the analyses' text). |
| GETLSF | keyword | no | Not bound (GETTEXTFILE writes the analyses' text). |
| GETMTF | keyword | no | Not bound (GETTEXTFILE writes the analyses' text). |
| GETMTFUSER1D | keyword | no | Not bound (GETTEXTFILE writes the analyses' text). |
| GETNSCMTF | keyword | no | Not bound: the non-sequential model is not built. |
| GETPSF | keyword | no | Not bound (GETTEXTFILE writes the analyses' text). |
| GETSYSTEMDATA | keyword | no | Not bound (GETTEXTFILE writes the analyses' text). |
| GETTEXTFILE | keyword | yes | Writes an analysis's text (its default settings) to a file (UTF-8): the 3-letter code of the help's String Codes for the analyses this program has of that name (Conventions §56); no code is the single ray trace (Rtr), as the help says. A settings file is not read or written (fails). |
| GETVARDATA | keyword | yes | Puts the variables in VECn: [0] the count, then per variable the type code (1 curvature, 2 thickness, 3 conic, 4–6 model nd/Vd/ΔPgF, 7 TCE, 8 parameter, 10 multi-configuration), surface (operand), parameter (configuration), object (0) and value. |
| GETZERNIKE | keyword | no | Not bound (GETTEXTFILE writes the analyses' text). |
| GLAS | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| GLASSTEMPLATE | keyword | no | Not bound. |
| GLENSNAME | keyword | no | Not bound: macro graphics and image files are not built. |
| GLOBALTOLOCAL | keyword | no | Not bound. |
| GOSUB | keyword | yes | Language core (Conventions §56). |
| SUB | keyword | yes | Language core (Conventions §56). |
| RETURN | keyword | yes | Language core (Conventions §56). |
| GOTO | keyword | yes | Language core (Conventions §56). |
| GRAPHICS | keyword | no | Not bound: macro graphics and image files are not built. |
| GTEXT | keyword | no | Not bound: macro graphics and image files are not built. |
| GTEXTCENT | keyword | no | Not bound: macro graphics and image files are not built. |
| GTITLE | keyword | no | Not bound: macro graphics and image files are not built. |
| HAMMER | keyword | no | Not bound: the Hammer search (OP-58) runs by time, not by cycles. |
| IF | keyword | yes | Language core (Conventions §56). |
| THEN | keyword | yes | Language core (Conventions §56). |
| ELSE | keyword | yes | Language core (Conventions §56). |
| ENDIF | keyword | yes | Language core (Conventions §56). |
| IMA | keyword | no | Not bound: image analysis files are not built. |
| IMAGECOMBINE | keyword | no | Not bound: macro graphics and image files are not built. |
| IMAGEEXTRACT | keyword | no | Not bound: macro graphics and image files are not built. |
| IMASHOW | keyword | no | Not bound: image analysis files are not built. |
| IMASUM | keyword | no | Not bound: image analysis files are not built. |
| IMPORTEXTRADATA | keyword | no | Not bound. |
| INPUT | keyword | yes | Asks the host (the macro window) for a number or a string; fails when the run has no host (the command line). |
| INSERT | keyword | yes | Inserts a new Standard surface (thickness 0, air) at n (1 to the image surface); every surface number held elsewhere follows (pickups, solves, merit and multi-configuration rows). |
| INSERTCONFIG | keyword | no | Not bound: the multi-configuration editor's keywords are not bound (SETCONFIG is). |
| INSERTMCO | keyword | no | Not bound: the multi-configuration editor's keywords are not bound (SETCONFIG is). |
| INSERTMFO | keyword | yes | Inserts a BLNK row at row (1 to the row count + 1). |
| INSERTOBJECT | keyword | no | Not bound: the non-sequential model is not built. |
| INSERTTOL | keyword | no | Not bound: the tolerance editor's keywords are not bound. |
| LABEL | keyword | yes | Language core (Conventions §56). |
| LINE | keyword | no | Not bound: macro graphics and image files are not built. |
| LOADARCHIVE | keyword | no | Not bound (LOADLENS reads a .zar). |
| LOADCATALOG | keyword | no | Not bound: catalogs are loaded once per run. |
| LOADDETECTOR | keyword | no | Not bound: the non-sequential model is not built. |
| LOADLENS | keyword | yes | Replaces the lens with a .zmx or .zar (OP-94 reader), .osslens or .ossproj file; a name without a folder is in the lens folder. Appending (appendflag ≠ 0) and session files are not bound. |
| LOADMERIT | keyword | no | Not bound: OpticStudio's .MF file layout is not published. |
| LOADTOLERANCE | keyword | no | Not bound. |
| LOCALTOGLOBAL | keyword | no | Not bound. |
| LOCKWINDOW | keyword | no | Not bound: macros here do not drive analysis windows or their settings files. |
| MAKEFACETLIST | keyword | no | Not bound: the non-sequential model is not built. |
| MAKEFOLDER | keyword | yes | Creates a folder; an existing folder is left alone. |
| MODIFYSETTINGS | keyword | no | Not bound: macros here do not drive analysis windows or their settings files. |
| NSLT | keyword | no | Not bound: the non-sequential model is not built. |
| NSTR | keyword | no | Not bound: the non-sequential model is not built. |
| NSTR2 | keyword | no | Not bound: the non-sequential model is not built. |
| NUMFIELD | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| NUMWAVE | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| OPEN | keyword | yes | Opens a text file for READ; a name without a folder is taken in the macro folder. |
| OPENANALYSISWINDOW | keyword | no | Not bound: macros here do not drive analysis windows or their settings files. |
| OPTIMIZE | keyword | yes | Local optimization (LocalOptimizer): cycles 0 automatic or 1–99 fixed; algorithm 0 damped least squares, 1 orthogonal descent. |
| OPTRETURN | keyword | yes | Stores a value in data field 0 to 50 for the ZPLM operand. |
| OUTPUT | keyword | yes | OUTPUT SCREEN, or OUTPUT filename [, APPEND]; the file is written in Unicode (UTF-16) as the help says; a name without a folder goes to the macro folder. |
| PARM | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| PARAXIAL | keyword | yes | PARAXIAL OFF is accepted (real rays are always traced); PARAXIAL ON fails: paraxial RAYTRACE is not bound. |
| PAUSE | keyword | yes | PAUSE [message] asks the host to show the message and wait (fails without a host); PAUSE TIME, ms sleeps (at most 1,000,000 ms); PAUSE THREADS returns at once (no analysis windows compute during a run). |
| PIXEL | keyword | no | Not bound: macro graphics and image files are not built. |
| PLOT | keyword | no | Not bound: macro graphics and image files are not built. |
| PLOT2D | keyword | no | Not bound: macro graphics and image files are not built. |
| POLDEFINE | keyword | no | Not bound. |
| POLTRACE | keyword | no | Not bound. |
| POP | keyword | no | Not bound: physical optics propagation is not built. |
| PRINT | keyword | yes | Language core (Conventions §56). |
| PRINTFILE | keyword | no | Not bound: no printer is driven. |
| PRINTWINDOW | keyword | no | Not bound: no printer is driven. |
| PWAV | keyword | yes | Sets the primary wavelength number. |
| QUICKFOCUS | keyword | yes | Quick Focus (OP-60): mode 0 RMS spot radius, 1 spot x, 2 spot y, 3 wavefront; centroid 0 chief ray, 1 centroid. |
| QUICKSENSITIVITY | keyword | no | Not bound. |
| RADI | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| RANDOMIZE | keyword | yes | Seeds RAND and GAUS: the same seed repeats the series; 0 (or no seed) seeds from the clock. |
| RAYTRACE | keyword | yes | Traces a real ray (hx, hy, px, py [, wavelength]) through the current configuration (OpticsScripting.TraceRay); RAYE and RAYV as the help defines them. |
| RAYTRACEX | keyword | no | Not bound: launching a ray at an arbitrary surface is not built. |
| READ | keyword | yes | Reads one line into numeric variables, one space-delimited field each (period decimal separator). At the end of the file EOFF() becomes 1 and the variables are left as they were. |
| READ_LOCALE | keyword | no | Not bound: READ (period decimal separator) is. |
| READNEXT_LOCALE | keyword | no | Not bound: READNEXT (period decimal separator) is. |
| READNEXT | keyword | yes | Reads as many space-delimited numbers as variables listed, across line ends. At the end of the file EOFF() becomes 1 and the remaining variables are left as they were. |
| READSKIP | keyword | yes | Skips n characters, or to the end of the line when n is negative. |
| READSTRING | keyword | yes | Reads a whole line into a string variable (at most 359 characters, as the help says); at the end of the file EOFF() becomes 1 and the variable is set to the empty string. |
| RELEASE | keyword | yes | Language core (Conventions §56). |
| RELOADOBJECTS | keyword | no | Not bound: the non-sequential model is not built. |
| REM | keyword | yes | Language core (Conventions §56). |
| REMOVEVARIABLES | keyword | yes | Fixes every variable cell of the lens data and the multi-configuration table. |
| RESUMEUPDATES | keyword | yes | Accepted; counted (see SUSPENDUPDATES). |
| RENAMEFILE | keyword | yes | Renames a file; an existing target is an error. |
| REWIND | keyword | yes | Erases the last line printed to the screen; on output sent to a file it fails (not bound there). |
| SAVEARCHIVE | keyword | no | Not bound. |
| SAVEDETECTOR | keyword | no | Not bound: the non-sequential model is not built. |
| SAVELENS | keyword | yes | Saves the lens as .zmx (OP-94 writer) or .osslens; without a name, to the lens's own file. Session files are not bound. |
| SAVEMERIT | keyword | no | Not bound: OpticStudio's .MF file layout is not published. |
| SAVETOLERANCE | keyword | no | Not bound. |
| SAVEWINDOW | keyword | no | Not bound: macros here do not drive analysis windows or their settings files. |
| SCATTER | keyword | no | Not bound: surface scattering is not built. |
| SDIA | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| SETAIM | keyword | yes | Ray aiming off (0) or on (1, paraxial aiming: GETSYSTEMDATA's code 1 is "paraxial"). |
| SETAIMDATA | keyword | no | Not bound (SETAIM and SETSYSTEMPROPERTY 70–78 are). |
| SETAPODIZATION | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| SETCONFIG | keyword | yes | Makes configuration n current. |
| SETDETECTOR | keyword | no | Not bound: the non-sequential model is not built. |
| SETMCOPERAND | keyword | no | Not bound: the multi-configuration editor's keywords are not bound (SETCONFIG is). |
| SETNSCPARAMETER | keyword | no | Not bound: the non-sequential model is not built. |
| SETNSCPOSITION | keyword | no | Not bound: the non-sequential model is not built. |
| SETNSCPROPERTY | keyword | no | Not bound: the non-sequential model is not built. |
| SETOPERAND | keyword | yes | Sets a Merit Function Editor cell: column 1 type (ONUM number), 2 int1, 3 int2, 4–7 data1–4, 8 target, 9 weight, 10 comment, 11 type by name, 12–13 data5–6. |
| SETSTDD | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| SETSURFACEPROPERTY | keyword | yes | Sets a surface property by code or mnemonic; bound codes: 0 TYPE (the surface names this program builds), 1 COMM, 2 CURV, 3 THIC, 4 GLAS, 5 CONI, 6 SDIA, 7 TCE, 8 COAT, 10 PARM, 14 row color, 16 ignore, 23 APDX, 24 APDY, 27 CHZN, 28 MCSD, 60–65 tilt/decenter before, 66 APU, 70–75 after, 76–77 coordinate return, 90–92 draw flags. Not on a lens with a multi-configuration table. |
| SURP | keyword | yes | Shorthand for SETSURFACEPROPERTY (same codes). |
| SETSYSTEMPROPERTY | keyword | yes | Sets a system property by code; bound codes: 4 adjust index, 10 aperture type (0–3), 11 aperture value, 12 apodization type, 13 apodization factor, 14 telecentric, 16 title, 17 notes, 18 afocal, 23 glass catalogs, 24 temperature, 25 pressure, 30 lens units, 35 MTF units, 70 ray aiming (1 paraxial, 2 real), 71–73 pupil shift, 75 robust aiming, 76 scale shift by field, 77–78 pupil compress, 100 field type, 101 number of fields, 102–109 field data, 110 field normalization, 200 primary wavelength, 201 number of wavelengths, 202–203 wavelength and weight. |
| SYSP | keyword | yes | Shorthand for SETSYSTEMPROPERTY (same codes). |
| SETTEXTSIZE | keyword | no | Not bound: macro graphics and image files are not built. |
| SETTITLE | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| SETTOL | keyword | no | Not bound: the tolerance editor's keywords are not bound. |
| SETUNITS | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| SETVAR | keyword | yes | Makes a cell variable (status ≠ 0) or removes its variable flag (0): R, T, C, D (TCE) and Pn. |
| SETVECSIZE | keyword | yes | Sets the size of VEC1..4 (1 to 18,000,000; the data is cleared). |
| SETVIG | keyword | no | Not bound. |
| SHOWBITMAP | keyword | no | Not bound: macro graphics and image files are not built. |
| SHOWFILE | keyword | no | Not bound: no file viewer is opened. |
| SOLVEBEFORESTOP | keyword | yes | Accepted and does nothing: this program does not refuse macro solves before the stop. |
| SOLVERETURN | keyword | yes | Returns the value of a macro solve; only the last call counts. |
| SOLVETYPE | keyword | yes | Sets a solve: CF CV CP CZ, TF TV TP TZ, KF KP KZ, HF HP, SA SU SZ, PF_p PV_p PP_p Pickup arguments: surface, scale, offset, column (Integer Codes for Column Numbers); a macro solve's argument is the macro's file name. |
| STOPSURF | keyword | yes | Makes surface n the stop (not the object or image). |
| SURFTYPE | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| SUSPENDUPDATES | keyword | yes | Accepted; counted. Editors are not open during a run, so there is nothing to suspend. |
| TELECENTRIC | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| TESTPLATEFIT | keyword | no | Not bound. |
| THIC | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| TIMER | keyword | yes | Resets the clock ETIM() reads. |
| TOLERANCE | keyword | no | Not bound. |
| UNLOCKWINDOW | keyword | no | Not bound: macros here do not drive analysis windows or their settings files. |
| UPDATE | keyword | yes | Checks that the lens builds (its solves and pickups evaluate); ALL, EDITORS and MACROS do the same (no windows are open during a run). UPDATE n fails: there are no numbered windows. |
| VEC1 | keyword | yes | Sets element subscript (rounded down; 0 to the vector size) of VEC1. |
| VEC2 | keyword | yes | Sets element subscript (rounded down; 0 to the vector size) of VEC2. |
| VEC3 | keyword | yes | Sets element subscript (rounded down; 0 to the vector size) of VEC3. |
| VEC4 | keyword | yes | Sets element subscript (rounded down; 0 to the vector size) of VEC4. |
| WAVL | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| WWGT | keyword | no | Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function). |
| XDIFFIA | keyword | no | Not bound: image analysis files are not built. |
| ZBFCLR | keyword | no | Not bound: physical optics propagation is not built. |
| ZBFMULT | keyword | no | Not bound: physical optics propagation is not built. |
| ZBFPROPERTIES | keyword | no | Not bound: physical optics propagation is not built. |
| ZBFREAD | keyword | no | Not bound: physical optics propagation is not built. |
| ZBFRESAMPLE | keyword | no | Not bound: physical optics propagation is not built. |
| ZBFSHOW | keyword | no | Not bound: physical optics propagation is not built. |
| ZBFSUM | keyword | no | Not bound: physical optics propagation is not built. |
| ZBFTILT | keyword | no | Not bound: physical optics propagation is not built. |
| ZBFWRITE | keyword | no | Not bound: physical optics propagation is not built. |
| ZRDAPPEND | keyword | no | Not bound: the non-sequential model is not built. |
| ZRDFILTER | keyword | no | Not bound: the non-sequential model is not built. |
| ZRDPLAYBACK | keyword | no | Not bound: the non-sequential model is not built. |
| ZRDSAVERAYS | keyword | no | Not bound: the non-sequential model is not built. |
| ZRDSUM | keyword | no | Not bound: the non-sequential model is not built. |
| ABSO | numeric function | yes | Absolute value. |
| ACOS | numeric function | yes | Arc cosine in radians; \|x\| > 1 fails. |
| APMN | numeric function | yes | Aperture: circular minimum radius; rectangular or elliptical x half-width; spider arm width; 0 without an aperture. |
| APMX | numeric function | yes | Aperture: circular maximum radius; rectangular or elliptical y half-width; spider arm count; 0 without an aperture. |
| APOI | numeric function | no | Not bound. |
| APXD | numeric function | yes | Aperture x decenter (0 without an aperture). |
| APYD | numeric function | yes | Aperture y decenter (0 without an aperture). |
| APTP | numeric function | no | Not bound: the help does not list the aperture type codes. |
| ASIN | numeric function | yes | Arc sine in radians; \|x\| > 1 fails. |
| ASPR | numeric function | no | Not bound: macro graphics are not built. |
| ATAN | numeric function | yes | Arc tangent in radians. |
| ATYP | numeric function | yes | Aperture type: 0 entrance pupil diameter, 1 image space F/#, 2 object space NA, 3 float by stop size; another type fails (the help lists these four). |
| AVAL | numeric function | yes | Aperture value. |
| CALD | numeric function | yes | Numeric call buffer entry (0 to 50). |
| CHZN | numeric function | yes | Chip zone in lens units. |
| CONF | numeric function | yes | The current configuration number. |
| CONI | numeric function | yes | Conic constant. |
| COSI | numeric function | yes | Cosine of radians. |
| CURV | numeric function | yes | Curvature (1/lens unit; 0 for a plane). |
| EDGE | numeric function | no | Not bound. |
| EOFF | numeric function | yes | 1 when a read reached the end of the file opened by OPEN, else 0. |
| ETIM | numeric function | yes | Seconds since the last TIMER (or the start of the run). |
| EXPE | numeric function | yes | e to the power x. |
| EXPT | numeric function | yes | 10 to the power x. |
| FICL | numeric function | no | Not bound: fiber coupling is not built. |
| FLDX | numeric function | yes | Field x (degrees or lens units, by the field type). |
| FLDY | numeric function | yes | Field y. |
| FTAN | numeric function | yes | Field tangential angle (degrees). |
| FTYP | numeric function | yes | Field type: 0 angle, 1 object height, 2 paraxial image height, 3 real image height, 4 theodolite angle. |
| FVAN | numeric function | no | Obsolete in the help (FTAN is bound). |
| FVCX | numeric function | yes | Field vignetting compression x. |
| FVCY | numeric function | yes | Field vignetting compression y. |
| FVDX | numeric function | yes | Field vignetting decenter x. |
| FVDY | numeric function | yes | Field vignetting decenter y. |
| FWGT | numeric function | yes | Field weight. |
| GABB | numeric function | no | Not bound. |
| GAUS | numeric function | yes | Gaussian random number, zero mean, standard deviation x (Box–Muller on RAND's generator). |
| GETT | numeric function | no | Not bound: macros here open no analysis windows (GETTEXTFILE writes an analysis's text to a file instead). |
| GETL | numeric function | no | Not bound: macros here open no analysis windows (GETTEXTFILE writes an analysis's text to a file instead). |
| GIND | numeric function | no | Not bound. |
| GLCA | numeric function | no | Not bound: OpticStudio's global coordinate reference surface is not read (see RAGX). |
| GLCB | numeric function | no | Not bound: OpticStudio's global coordinate reference surface is not read (see RAGX). |
| GLCC | numeric function | no | Not bound: OpticStudio's global coordinate reference surface is not read (see RAGX). |
| GLCM | numeric function | no | Not bound: OpticStudio's global coordinate reference surface is not read (see RAGX). |
| GLCX | numeric function | no | Not bound: OpticStudio's global coordinate reference surface is not read (see RAGX). |
| GLCY | numeric function | no | Not bound: OpticStudio's global coordinate reference surface is not read (see RAGX). |
| GLCZ | numeric function | no | Not bound: OpticStudio's global coordinate reference surface is not read (see RAGX). |
| GNUM | numeric function | no | Not bound: glass numbers depend on OpticStudio's catalog order. |
| GPAR | numeric function | no | Not bound. |
| GRIN | numeric function | no | Not bound. |
| GTEM | numeric function | no | Not bound. |
| IMAE | numeric function | no | Not bound. |
| INDX | numeric function | yes | Index of the medium after the surface at the primary wavelength, relative to the system's air (as the trace uses it). |
| INTE | numeric function | yes | Largest integer not greater than x. |
| ISCS | numeric function | no | Not bound. |
| ISMS | numeric function | no | Not bound. |
| LOGE | numeric function | yes | Natural logarithm; x ≤ 0 fails. |
| LOGT | numeric function | yes | Base-10 logarithm; x ≤ 0 fails. |
| LOST | numeric function | no | Not bound: the non-sequential model is not built. |
| LVAL | numeric function | yes | Number of a string in the current culture's decimal separator; text that is not a number fails. |
| MAGN | numeric function | yes | √(x² + y²). |
| MAXF | numeric function | yes | The largest radial field (degrees or lens units). |
| MAXG | numeric function | no | Not bound. |
| MCON | numeric function | no | Not bound. |
| MCOP | numeric function | no | Not bound. |
| MCSD | numeric function | yes | Mechanical semi-diameter; an automatic one is the clear semi-diameter plus the chip zone. |
| MFCN | numeric function | yes | Evaluates the merit function over every configuration and returns its value; a row that cannot be computed fails, naming it. |
| NCON | numeric function | yes | The number of configurations. |
| NFLD | numeric function | yes | The number of fields. |
| NOBJ | numeric function | no | Not bound: the non-sequential model is not built. |
| NPAR | numeric function | no | Not bound: the non-sequential model is not built. |
| NPOS | numeric function | no | Not bound: the non-sequential model is not built. |
| NPRO | numeric function | no | Not bound: the non-sequential model is not built. |
| NSDC | numeric function | no | Not bound: the non-sequential model is not built. |
| NSDD | numeric function | no | Not bound: the non-sequential model is not built. |
| NSDE | numeric function | no | Not bound: the non-sequential model is not built. |
| NSDP | numeric function | no | Not bound: the non-sequential model is not built. |
| NSUR | numeric function | yes | The image surface's number (surfaces are 0 to NSUR(); the help's Example Macro 1 reads the image intercept as RAYX(NSUR())). |
| NWAV | numeric function | yes | The number of wavelengths. |
| OBJC | numeric function | no | Not bound: the non-sequential model is not built. |
| OCOD | numeric function | yes | The same number as ONUM, for OPEV and OPEW. |
| ONUM | numeric function | yes | This program's number of a merit operand code (its place in the operand registry, from 1), or 0 for an unknown code. The numbers are not OpticStudio's. |
| OPDC | numeric function | no | Not bound: the OPD of one RAYTRACE ray needs the chief ray and the exit pupil reference sphere the help does not define for a single ray. |
| OPER | numeric function | yes | Merit Function Editor data: column 1 type (number as ONUM; the code in $BUFFER()), 2 int1 (a BLNK's comment in $BUFFER()), 3 int2, 4–7 data1–4, 8 target, 9 weight, 10 value and 11 contribution (%) as of the last MFCN on the same lens, 12–13 data5–6. |
| OPEV | numeric function | yes | The value of operand code with int1, int2, data1–data4, evaluated alone on the current lens (configuration CONF()). |
| OPEW | numeric function | yes | As OPEV with data1–data6. |
| OPTH | numeric function | no | Not bound: the help's path origin (and the phase of diffractive surfaces) is not defined for this program's launch plane. |
| PARM | numeric function | yes | Parameter n of surface s (PARM(n, s)), in its column's unit. |
| PIXX | numeric function | no | Not bound: macro graphics are not built. |
| PIXY | numeric function | no | Not bound: macro graphics are not built. |
| PMOD | numeric function | yes | 0: paraxial mode is never on (PARAXIAL ON is not bound). |
| POWR | numeric function | yes | \|x\| to the power y. |
| PVEX | numeric function | yes | ZPLM's Data5 (0 outside an operand). |
| PVEY | numeric function | yes | ZPLM's Data6 (0 outside an operand). |
| PVHX | numeric function | yes | ZPLM's Data1 (0 outside an operand). |
| PVHY | numeric function | yes | ZPLM's Data2 (0 outside an operand). |
| PVPX | numeric function | yes | ZPLM's Data3 (0 outside an operand). |
| PVPY | numeric function | yes | ZPLM's Data4 (0 outside an operand). |
| PWAV | numeric function | yes | The primary wavelength number. |
| RADI | numeric function | yes | Radius in lens units; 0 for an infinite radius. |
| RAGL | numeric function | no | Not bound: global coordinates here are referred to surface 1 before its tilt (Conventions §13), and OpticStudio's global reference surface is not read. |
| RAGM | numeric function | no | Not bound: global coordinates here are referred to surface 1 before its tilt (Conventions §13), and OpticStudio's global reference surface is not read. |
| RAGN | numeric function | no | Not bound: global coordinates here are referred to surface 1 before its tilt (Conventions §13), and OpticStudio's global reference surface is not read. |
| RAGX | numeric function | no | Not bound: global coordinates here are referred to surface 1 before its tilt (Conventions §13), and OpticStudio's global reference surface is not read. |
| RAGY | numeric function | no | Not bound: global coordinates here are referred to surface 1 before its tilt (Conventions §13), and OpticStudio's global reference surface is not read. |
| RAGZ | numeric function | no | Not bound: global coordinates here are referred to surface 1 before its tilt (Conventions §13), and OpticStudio's global reference surface is not read. |
| RAND | numeric function | yes | Uniform random number from 0 to x (System.Random). |
| RANX | numeric function | yes | x direction cosine of the surface normal at the intercept, local; the normal's sense is +z at the vertex (Conventions §56). |
| RANY | numeric function | yes | y direction cosine of the surface normal (see RANX). |
| RANZ | numeric function | yes | z direction cosine of the surface normal (see RANX). |
| RAYE | numeric function | yes | RAYTRACE's error code: 0, +n missed surface n, −n total internal reflection at n, −9999 not launched. |
| RAYL | numeric function | yes | x direction cosine of the ray after the surface, local. |
| RAYM | numeric function | yes | y direction cosine after the surface, local. |
| RAYN | numeric function | yes | z direction cosine after the surface, local. |
| RAYO | numeric function | yes | Optical path length from the previous surface (path times index), lens units. |
| RAYT | numeric function | yes | Path length from the previous surface to the surface, lens units (may be negative). |
| RAYV | numeric function | yes | The surface at which the ray was vignetted, or 0. |
| RAYX | numeric function | yes | x of the ray's intercept with the surface, local coordinates, lens units. |
| RAYY | numeric function | yes | y of the intercept, local, lens units. |
| RAYZ | numeric function | yes | z of the intercept, local, lens units. |
| RELI | numeric function | no | Not bound. |
| SAGG | numeric function | no | Not bound. |
| SCOM | numeric function | yes | −1, 0 or +1 as A$ is less than, equal to or greater than B$ (ordinal comparison). |
| SDIA | numeric function | yes | Clear semi-diameter in lens units; an automatic one as the trace sizes it (SemiDiameterSizing). |
| SIGN | numeric function | yes | −1, 0 or +1. |
| SINE | numeric function | yes | Sine of radians. |
| SLEN | numeric function | yes | Number of characters of a string. |
| SOLM | numeric function | no | Not bound. |
| SOLV | numeric function | no | Not bound. |
| SOSO | numeric function | yes | Code 0: the surface of the macro solve being computed; fails outside a solve. Code 1 (the NSC object) is not bound. |
| SPRO | numeric function | yes | A surface property by integer code (as SETSURFACEPROPERTY's bound codes); string values go to $BUFFER() and 0 is returned. |
| SPRX | numeric function | yes | As SPRO with value2: code 10 gives parameter value2. |
| SRCN | numeric function | yes | The n-th surface whose comment matches A$ (backwards for negative n; the count for n = 0), or −1. |
| SQRT | numeric function | yes | Square root; x < 0 fails. |
| STDD | numeric function | no | Obsolete in the help. |
| SURC | numeric function | yes | The first surface whose comment matches A$ (case ignored), or −1. |
| SVAL | numeric function | yes | Number of a string, period decimal separator; text that is not a number fails. |
| SYPR | numeric function | yes | A system property by code (SETSYSTEMPROPERTY's bound codes that take no value2); strings go to $BUFFER() and 0 is returned. |
| TANG | numeric function | yes | Tangent of radians. |
| TMAS | numeric function | no | Not bound. |
| THIC | numeric function | yes | Thickness in lens units; an infinite thickness fails. |
| TOLV | numeric function | no | Not bound. |
| UNIT | numeric function | yes | Lens unit: 0 mm, 1 cm, 2 in, 3 m. |
| VEC1 | numeric function | yes | Element of VEC1. |
| VEC2 | numeric function | yes | Element of VEC2. |
| VEC3 | numeric function | yes | Element of VEC3. |
| VEC4 | numeric function | yes | Element of VEC4. |
| VERS | numeric function | no | Not bound: OpticStudio's version number has no meaning here. |
| WAVL | numeric function | yes | Wavelength in micrometers. |
| WINL | numeric function | no | Not bound: macros here open no analysis windows (GETTEXTFILE writes an analysis's text to a file instead). |
| WINN | numeric function | no | Not bound: macros here open no analysis windows (GETTEXTFILE writes an analysis's text to a file instead). |
| WWGT | numeric function | yes | Wavelength weight. |
| XMIN | numeric function | no | Not bound: macro graphics are not built. |
| XMAX | numeric function | no | Not bound: macro graphics are not built. |
| YMIN | numeric function | no | Not bound: macro graphics are not built. |
| YMAX | numeric function | no | Not bound: macro graphics are not built. |
| $BUFFER | string function | yes | The string buffer that OPER, SPRO, SYPR … fill. |
| $CALLSTR | string function | yes | String call buffer entry (0 to 50). |
| $COAT | string function | yes | The surface's coating name (empty when uncoated). |
| $COATINGPATH | string function | no | Not bound: the program has no coating file folder. |
| $COMMENT | string function | yes | The surface's comment. |
| $DATAPATH | string function | no | Not bound: the program has no single data folder. |
| $DATE | string function | yes | The date and time, yyyy-MM-dd HH:mm:ss (the help's format comes from OpticStudio's preferences). |
| $EXTENSIONPATH | string function | no | Not bound. |
| $FILENAME | string function | yes | The lens file's name without its folder (fails when the lens has no file). |
| $FILEPATH | string function | yes | The lens file's full path (fails when the lens has no file). |
| $GETSTRING | string function | yes | The n-th space-delimited word of A$; the empty string past the last. |
| $GETSTRINGC | string function | yes | The n-th comma-delimited part of A$; the empty string past the last. |
| $GLASS | string function | yes | The surface's glass name (empty for air; a constant index or model glass fails: it has no name). |
| $GLASSCATALOG | string function | yes | The i-th active glass catalog; all names separated by spaces for i < 1. |
| $GLASSPATH | string function | no | Not bound. |
| $LEFTSTRING | string function | yes | The leftmost n characters, padded with blanks on the right. |
| $LENSNAME | string function | yes | The lens title. |
| $MACROPATH | string function | yes | The macro folder (fails when none is set). |
| $NEWLINE | string function | yes | A newline. The help says "the tab character (\n)"; \n is the newline, which this returns. |
| $NOTE | string function | yes | Line n of the notes (a line ends at a newline or after 100 characters); empty past the end. |
| $OBJECTPATH | string function | no | Not bound: no non-sequential objects. |
| $PATHNAME | string function | yes | The folder of the lens file (fails when the lens has no file). |
| $PROGRAMPATH | string function | no | Not bound. |
| $QUOTE | string function | yes | The double quote character. |
| $RIGHTSTRING | string function | yes | The rightmost n characters, padded with blanks on the left (the help does not say which side). |
| $STR | string function | yes | The number formatted by the current FORMAT. |
| $TAB | string function | yes | The tab character. |
| $TEMPFILENAME | string function | yes | A new empty temporary file's full path (not deleted automatically; use DELETEFILE). |
| $TOLCOMMENT | string function | no | Not bound. |
| $TOLOPERAND | string function | no | Not bound. |
| $UNITS | string function | yes | MM, CM, IN or M. |
| $GETARG | string function | yes | A command-line argument by name (case ignored); "IsAutomated" is True from the command line, else False. An argument not given fails. |

## GETTEXTFILE analysis codes

GETTEXTFILE's type is a 3-character code of the help's String Codes page (case sensitive). These
codes run the analysis of this program with the same name, with its default settings; any other
code fails, naming it. No code at all is the single ray trace (`Rtr`), as the help says.

| Code | Analysis id |
|---|---|
| Ray | RayAberration |
| Opd | OpdFan |
| Pab | PupilAberration |
| Spt | StandardSpot |
| Stf | ThroughFocusSpot |
| Sff | FullFieldSpot |
| Sma | MatrixSpot |
| Mtf | FftMtf |
| Tfm | FftThroughFocusMtf |
| Smf | FftSurfaceMtf |
| Mth | FftMtfVsField |
| Fmm | FftMtfMap |
| Gmm | GeometricMtfMap |
| Hmf | HuygensMtf |
| Hsm | HuygensSurfaceMtf |
| Hmh | HuygensMtfVsField |
| Gtf | GeometricMtf |
| Tfg | GeometricThroughFocusMtf |
| Htf | HuygensThroughFocusMtf |
| Gvf | GeometricMtfVsField |
| Fps | FftPsf |
| Pcs | FftPsfCrossSection |
| Lsf | FftLineEdgeSpread |
| Hps | HuygensPsf |
| Hcs | HuygensPsfCrossSection |
| Wfm | WavefrontMap |
| Int | Interferogram |
| Ffa | FullFieldAberration |
| Foa | FoucaultAnalysis |
| Srs | SurfaceSag |
| Scv | SurfaceCurvature |
| Ssc | SurfaceSagCrossSection |
| Scc | SurfaceCurvatureCrossSection |
| Rms | RmsVsField |
| Rmw | RmsVsWavelength |
| Rmf | RmsVsFocus |
| Rfm | RmsFieldMap |
| Enc | DiffractionEnclosedEnergy |
| Gee | GeometricEnclosedEnergy |
| Lin | GeometricLineEdgeSpread |
| Xse | ExtendedSourceEnclosedEnergy |
| Rel | RelativeIllumination |
| Foo | FootprintDiagram |
| Fcd | FieldCurvatureDistortion |
| Grd | GridDistortion |
| Lon | LongitudinalAberration |
| Lat | LateralColor |
| Agm | AthermalGlassMap |
| Car | CardinalPoints |
| Cfs | ChromaticFocalShift |
| Dis | DispersionVsWavelength |
| Gbp | ParaxialGaussianBeam |
| Gcp | GlassCompare |
| Gft | GlassFit |
| Gho | GhostFocusGenerator |
| Gmp | GlassMap |
| Iht | IncidentAngleVsImageHeight |
| Pal | PowerFieldMap |
| Pha | PolarizationPhaseAberration |
| Pmp | PolarizationPupilMap |
| Pol | PolarizationRayTrace |
| Ppm | PowerPupilMap |
| Pre | PrescriptionData |
| Ptf | PolarizationTransmissionFan |
| Rtr | SingleRayTrace |
| Sag | SagTable |
| Sdi | SeidelDiagram |
| Sei | SeidelCoefficients |
| Ssg | SystemSummaryGraphic |
| Sur | SurfaceData |
| Sys | SystemData |
| Tra | PolarizationTransmission |
| Vig | VignettingPlot |
| Yni | YniContributions |
| Yyb | YYbarDrawing |
