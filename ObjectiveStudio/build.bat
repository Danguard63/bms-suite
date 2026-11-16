@echo off
rem Builds the Objective Studio (and the shared library in ..\TerrainTool\src\TerrainTool.Core) and writes everything to build.log
cd /d "%~dp0"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
echo ==== BUILD %date% %time% ==== > build.log
rem a Objective Studio still running (even without a window) locks ObjectiveStudio.exe: close it before building
tasklist /FI "IMAGENAME eq ObjectiveStudio.exe" 2>nul | find /I "ObjectiveStudio.exe" >nul
if %errorlevel%==0 (
  echo Objective Studio is running: closing it before the build...
  echo Objective Studio was running: closed before the build >> build.log
  taskkill /IM ObjectiveStudio.exe /F >nul 2>&1
  timeout /t 2 /nobreak >nul
)
if not exist "..\TerrainTool\src\TerrainTool.Core\TerrainTool.Core.csproj" (
  echo The shared library ..\TerrainTool\src\TerrainTool.Core is missing: the Objective Studio folder must be next to TerrainTool.
  echo Shared library missing >> build.log
  pause
  exit /b 1
)
dotnet --list-sdks >> build.log 2>&1
echo. >> build.log
echo ==== OBJECTIVE STUDIO ==== >> build.log
dotnet build src\ObjectiveStudio\ObjectiveStudio.csproj -c Release -nologo -clp:NoSummary >> build.log 2>&1
set ERR=%errorlevel%
echo BUILD EXIT %ERR% >> build.log
if %ERR%==0 (
  echo.
  echo Build succeeded. Start with run.bat
  echo Build succeeded >> build.log
) else (
  echo.
  echo Build FAILED: see build.log
)
pause
