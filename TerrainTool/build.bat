@echo off
rem Builds the tool and writes everything to build.log
cd /d "%~dp0"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
echo ==== BUILD %date% %time% ==== > build.log
rem TerrainTool still running (even without a window) locks TerrainTool.exe: close it before building
tasklist /FI "IMAGENAME eq TerrainTool.exe" 2>nul | find /I "TerrainTool.exe" >nul
if %errorlevel%==0 (
  echo TerrainTool is running: closing it before the build...
  echo TerrainTool was running: closed before the build >> build.log
  taskkill /IM TerrainTool.exe /F >nul 2>&1
  timeout /t 2 /nobreak >nul
)
dotnet --list-sdks >> build.log 2>&1
echo. >> build.log
echo ==== TEST CORE ==== >> build.log
dotnet run --project tests\CoreTest\CoreTest.csproj -c Release >> build.log 2>&1
echo TEST EXIT %errorlevel% >> build.log
echo. >> build.log
echo ==== APP WPF ==== >> build.log
dotnet build src\TerrainTool.App\TerrainTool.App.csproj -c Release -nologo -clp:NoSummary >> build.log 2>&1
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
