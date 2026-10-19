@echo off
rem Build FastHenry (console) with MSVC 14.44 x64.
rem usage: build.bat <src dir> <out dir>
rem The object list is the Makefile's OBJS + MOBJS + NONUNIOBJS + sparse/ library, plus win_compat.c.
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
set SRC=%~1
set OUT=%~2
if not exist "%OUT%\obj" mkdir "%OUT%\obj"
set FILES=induct.c gmres.c savemat_mod.c readGeom.c joelself.c writefastcap.c SetupMulti.c calcp.c SetupComputePsi.c mulSetup.c BreakupSeg.c Precond.c addgroundplane.c findpaths.c fillM.c cx_ludecomp.c parse_command_line.c default_opts.c Prec_cost.c hole.c dist_betw_fils.c mutual.c newPrecond.c deg_mutual.c barnoldi.c regurgitate.c mulGlobal.c mulDo.c mulMulti.c mulLocal.c mulMats.c direct.c uglieralloc.c capsolve.c find_nonuni_path.c read_tree.c contact.c sparse\spAllocate.c sparse\spBuild.c sparse\spFactor.c sparse\spOutput.c sparse\spSolve.c sparse\spUtils.c sparse\spCompat.c sparse\spFortran.c win_compat.c
pushd "%SRC%"
set CMD=cl /nologo /O2 /fp:precise /W1 /D_CRT_SECURE_NO_WARNINGS /DNO_GETHOSTNAME /DWINTIMER /FIwin_compat.h /I. /Fo"%OUT%\obj\\" /Fe"%OUT%\fasthenry.exe" %FILES% /link /STACK:268435456
echo %CMD%
%CMD%
set RC=%ERRORLEVEL%
popd
cl 2>&1 | findstr /C:"Version"
exit /b %RC%
