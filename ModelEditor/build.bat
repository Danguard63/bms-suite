@echo off
rem Builds the Model Editor (and the shared library in ..\TerrainTool\src\TerrainTool.Core) and writes everything to build.log
cd /d "%~dp0"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
echo ==== BUILD %date% %time% ==== > build.log
rem a Model Editor still running (even without a window) locks ModelEditor.exe: close it before building
tasklist /FI "IMAGENAME eq ModelEditor.exe" 2>nul | find /I "ModelEditor.exe" >nul
if %errorlevel%==0 (
  echo Model Editor is running: closing it before the build...
  echo Model Editor was running: closed before the build >> build.log
  taskkill /IM ModelEditor.exe /F >nul 2>&1
  timeout /t 2 /nobreak >nul
)
if not exist "..\TerrainTool\src\TerrainTool.Core\TerrainTool.Core.csproj" (
  echo The shared library ..\TerrainTool\src\TerrainTool.Core is missing: the Model Editor folder must be next to TerrainTool.
  echo Shared library missing >> build.log
  pause
  exit /b 1
)
dotnet --list-sdks >> build.log 2>&1
echo. >> build.log
echo ==== MODEL EDITOR ==== >> build.log
dotnet build src\ModelEditor\ModelEditor.csproj -c Release -nologo -clp:NoSummary >> build.log 2>&1
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
