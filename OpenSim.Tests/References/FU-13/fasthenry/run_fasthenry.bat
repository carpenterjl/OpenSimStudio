@echo off
rem ---------------------------------------------------------------------------------------------
rem Run the FastHenry 3.0.1 built here (MSVC 19.44 x64) on one deck.
rem   run_fasthenry.bat <deck.inp> [suffix] [solver]
rem     suffix : appended to the output names (Zc<suffix>.mat); default none
rem     solver : gmres (default) = -s iterative -m direct -a off -t 1e-7 -c 1000
rem                                GMRES on the full dense mesh matrix, deck discretisation as written;
rem                                matched dense LU to all 6 printed digits on the pour decks
rem              lu            = -s ludecomp -a off      (dense LU, exact; ~1 min at 4.6k filaments)
rem              multi         = -s iterative -m multi -o 3 -t 1e-6 -c 1000
rem                                multipole: needs automatic refinement (-a on); ~0.15-0.2 %% off the
rem                                dense product on the pour decks, at any order tried (3, 5, 7)
rem FastHenry writes Zc<suffix>.mat into the CURRENT directory, so this script runs it in the
rem deck's folder. Log: <deck>.log next to the deck. Read Zc with scripts\fh.py (parse_zc).
rem Memory with -m direct / -s ludecomp: 8 B x filaments^2 (L) + 16 B x meshes^2 (mesh matrix).
rem ---------------------------------------------------------------------------------------------
setlocal
set EXE=%~dp0fasthenry.exe
set DECK=%~f1
set SUF=%~2
set SOLV=%~3
if "%SOLV%"=="" set SOLV=gmres
if /I "%SOLV%"=="gmres" set OPTS=-s iterative -m direct -a off -t 1e-7 -c 1000
if /I "%SOLV%"=="lu" set OPTS=-s ludecomp -a off
if /I "%SOLV%"=="multi" set OPTS=-s iterative -m multi -o 3 -t 1e-6 -c 1000
if "%SUF%"=="" (set SOPT=) else (set SOPT=-S %SUF%)
pushd "%~dp1"
"%EXE%" "%~nx1" %OPTS% %SOPT% > "%~n1%SUF%.log" 2>&1
set RC=%ERRORLEVEL%
popd
exit /b %RC%
