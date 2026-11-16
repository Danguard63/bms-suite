# Builds the release package of the BMS Suite:
#   dist\BMS Suite\BMSSuite.exe                 the main menu (one file)
#   dist\BMS Suite\Programs\<program>\...       one folder per program
#   dist\BMS Suite\Data\NaturalEarth\...        map data shared by the programs
#   dist\BMSSuite-<version>.zip                 the same, zipped
# Needs the .NET 8 SDK. Usage (from the repository folder):
#   powershell -ExecutionPolicy Bypass -File tools\make-release.ps1 -Version 0.1.0
param(
    [string]$Version = "0.1.0",
    [string]$Out = "dist"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

$pkg = Join-Path $Out "BMS Suite"
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force -Path (Join-Path $pkg "Programs") | Out-Null

# the shared map data must be in the repository (Data\NaturalEarth)
if (-not (Test-Path "Data\NaturalEarth\ne_10m_land.geojson")) { throw "Data\NaturalEarth is missing" }

$common = @("-c", "Release", "-r", "win-x64", "--self-contained", "false", "-p:Version=$Version", "-p:DebugType=None", "-p:DebugSymbols=false", "-nologo")

# programs: folder in Programs, project file
$programs = @(
    @{ Folder = "TerrainTool";         Project = "TerrainTool\src\TerrainTool.App\TerrainTool.App.csproj" },
    @{ Folder = "ObjectiveStudio";     Project = "ObjectiveStudio\src\ObjectiveStudio\ObjectiveStudio.csproj" },
    @{ Folder = "ArtEditor";           Project = "ArtEditor\src\ArtEditor\ArtEditor.csproj" },
    @{ Folder = "CampaignEditor";      Project = "CampaignEditor\src\CampaignEditor\CampaignEditor.csproj" },
    @{ Folder = "ModelEditor";         Project = "ModelEditor\src\ModelEditor\ModelEditor.csproj" },
    @{ Folder = "DatabaseInterchange"; Project = "DatabaseInterchange\src\DatabaseInterchange\DatabaseInterchange.csproj" }
)

foreach ($p in $programs) {
    $dest = Join-Path $pkg ("Programs\" + $p.Folder)
    Write-Host "==== $($p.Folder)"
    dotnet publish $p.Project @common -o $dest
    if ($LASTEXITCODE -ne 0) { throw "publish of $($p.Folder) failed" }
    # data files a program reads from its own folder
    $data = Join-Path $p.Folder "Data"
    if ($p.Folder -ne "TerrainTool" -and (Test-Path $data)) { Copy-Item -Recurse -Force $data (Join-Path $dest "Data") }
    if (Test-Path (Join-Path $p.Folder "README.txt")) { Copy-Item -Force (Join-Path $p.Folder "README.txt") $dest }
}

Write-Host "==== BMSSuite (main menu)"
dotnet publish "BMSSuite\src\BMSSuite\BMSSuite.csproj" @common -o $pkg -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "publish of BMSSuite failed" }

Copy-Item -Recurse -Force "Data\NaturalEarth" (Join-Path $pkg "Data\NaturalEarth")
Copy-Item -Force "tools\README-release.txt" (Join-Path $pkg "README.txt")
Copy-Item -Force "CREDITS.md" (Join-Path $pkg "CREDITS.md")
if (Test-Path "LICENSE") { Copy-Item -Force "LICENSE" (Join-Path $pkg "LICENSE.txt") }

$zip = Join-Path $Out "BMSSuite-$Version.zip"
Compress-Archive -Path $pkg -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Package: $zip"
