@echo off
rem Builds every program of the BMS Suite, one after the other (each one writes its own build.log)
cd /d "%~dp0"
for %%P in (TerrainTool ObjectiveStudio ArtEditor CampaignEditor ModelEditor DatabaseInterchange SimEditor BMSSuite) do call :one %%P
echo.
echo Done.
pause
goto :eof

:one
echo ==== %1
pushd %1
echo.| call build.bat >nul 2>&1
findstr /C:"Build succeeded" build.log >nul && (echo    OK) || (echo    FAILED - see %1\build.log)
popd
goto :eof
