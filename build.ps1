# Builds this game's trainer: payload (injected into the game) + trainer exe.
#   Standalone:            powershell -File build.ps1
#   From the workspace:    ..\..\build.ps1 ProbablyStolen   (shares caches, collects the exe in Trainers\)
# Nothing is written outside this folder unless TRAINER_CACHE / TRAINER_COLLECT point somewhere else.
$ErrorActionPreference = 'Stop'
$game  = $PSScriptRoot
$name  = 'ProbablyStolenTrainer'                 # exe name
$appId = '4348910'                               # Steam app id (for cover art)
$build = "$game\.build"
$cache = if ($env:TRAINER_CACHE) { $env:TRAINER_CACHE } else { "$build\cache" }

$env:DOTNET_CLI_HOME            = "$cache\dotnet-home"
$env:NUGET_PACKAGES             = "$cache\nuget\packages"
$env:NUGET_HTTP_CACHE_PATH      = "$cache\nuget\http-cache"
$env:NUGET_PLUGINS_CACHE_PATH   = "$cache\nuget\plugins-cache"
$env:NUGET_SCRATCH              = "$cache\nuget\scratch"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO              = '1'
$env:TEMP = "$build\tmp"; $env:TMP = "$build\tmp"
New-Item -ItemType Directory -Force $env:TEMP, $env:DOTNET_CLI_HOME | Out-Null

# 0) cover art from the local Steam library cache (optional, never committed)
$cover = "$game\src\TrainerApp\Assets\cover.jpg"
if (-not (Test-Path $cover)) {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    $found = if ($steam) { Get-ChildItem "$steam\appcache\librarycache\$appId" -Recurse -Filter library_capsule.jpg -ErrorAction SilentlyContinue | Select-Object -First 1 }
    if ($found) { New-Item -ItemType Directory -Force (Split-Path $cover) | Out-Null; Copy-Item $found.FullName $cover }
    else { Write-Host "No cover art in Steam cache - building without it." }
}

# 1) payload
dotnet build "$game\src\TrainerMod\TrainerMod.csproj" -c Release -o "$build\out\mod" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Payload build failed" }

# 2) trainer exe (embeds the payload + cover)
dotnet publish "$game\src\TrainerApp\TrainerApp.csproj" -c Release -o "$build\out\app" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Trainer build failed" }
dotnet build-server shutdown *> $null   # don't leave compiler servers locking files

New-Item -ItemType Directory -Force "$game\release" | Out-Null
Copy-Item "$build\out\app\$name.exe" "$game\release\$name.exe" -Force
if ($env:TRAINER_COLLECT) {
    New-Item -ItemType Directory -Force $env:TRAINER_COLLECT | Out-Null
    Copy-Item "$build\out\app\$name.exe" "$env:TRAINER_COLLECT\$name.exe" -Force
}
Write-Host "Built: $game\release\$name.exe"
