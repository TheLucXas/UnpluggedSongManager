param(
    [string]$GameDir,
    [string]$QuestReferencesDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if (-not $GameDir) { $GameDir = $env:UNPLUGGED_GAME_DIR }
if (-not $GameDir) {
    $configFile = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.unplugged_studio_config.json'
    if (Test-Path $configFile) {
        try { $GameDir = (Get-Content $configFile -Raw | ConvertFrom-Json).game_path } catch { }
    }
}
if (-not $GameDir) {
    $candidateDirs = @()
    $programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
    if ($programFilesX86) { $candidateDirs += (Join-Path $programFilesX86 'Steam\steamapps\common\Unplugged') }
    foreach ($drive in [IO.DriveInfo]::GetDrives()) {
        $candidateDirs += (Join-Path $drive.RootDirectory.FullName 'SteamLibrary\steamapps\common\Unplugged')
        $candidateDirs += (Join-Path $drive.RootDirectory.FullName 'Steam\steamapps\common\Unplugged')
    }
    $GameDir = $candidateDirs | Where-Object { Test-Path (Join-Path $_ 'Unplugged_Data\Managed\Assembly-CSharp.dll') } | Select-Object -First 1
}

if (-not $GameDir) {
    throw 'Unplugged PC was not found. Pass -GameDir or set UNPLUGGED_GAME_DIR to your Unplugged installation folder.'
}
$GameDir = (Resolve-Path $GameDir).Path
foreach ($relativeFile in @(
    'Unplugged_Data\Managed\Assembly-CSharp.dll',
    'MelonLoader\MelonLoader.dll',
    'MelonLoader\0Harmony.dll'
)) {
    if (-not (Test-Path (Join-Path $GameDir $relativeFile))) {
        throw "Missing PC build reference: $relativeFile. Install Unplugged and MelonLoader in $GameDir."
    }
}

if (-not $QuestReferencesDir) { $QuestReferencesDir = Join-Path $repoRoot 'tools\quest' }
foreach ($relativeFile in @(
    'MelonNet\net8\MelonLoader.dll',
    'MelonNet\net8\Il2CppInterop.Runtime.dll',
    'Il2CppAssemblies\Assembly-CSharp.dll',
    'Il2CppAssemblies\Il2Cppmscorlib.dll'
)) {
    if (-not (Test-Path (Join-Path $QuestReferencesDir $relativeFile))) {
        throw "Missing Quest build reference: $relativeFile. Put the generated Quest interop assemblies and MelonLoader references in $QuestReferencesDir."
    }
}
$QuestReferencesDir = (Resolve-Path $QuestReferencesDir).Path

$env:UNPLUGGED_GAME_DIR = $GameDir
$projectFile = Join-Path $repoRoot 'src\USM\USM.csproj'
$outputDir = Join-Path $repoRoot 'dist\USM-CSharp'
& dotnet publish $projectFile -c Release -r win-x64 -o $outputDir /p:UseSharedCompilation=false /p:DebugType=none /p:DebugSymbols=false "/p:QuestReferencesDir=$QuestReferencesDir"
if ($LASTEXITCODE -ne 0) { throw "USM build failed with exit code $LASTEXITCODE." }

Write-Host "USM ready: $(Join-Path $outputDir 'USM.exe')"
