# Builds the in-game mod and the trainer exe.
# Every cache / temp / output stays inside this project folder (nothing written to C:).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot          # ...\Trainer Maker
$work = $PSScriptRoot                             # ...\Trainer Maker\work

$env:DOTNET_CLI_HOME            = "$work\dotnet-home"
$env:NUGET_PACKAGES             = "$work\nuget\packages"
$env:NUGET_HTTP_CACHE_PATH      = "$work\nuget\http-cache"
$env:NUGET_PLUGINS_CACHE_PATH   = "$work\nuget\plugins-cache"
$env:NUGET_SCRATCH              = "$work\nuget\scratch"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO              = '1'
$env:TEMP = "$work\tmp"; $env:TMP = "$work\tmp"
New-Item -ItemType Directory -Force $env:TEMP, $env:DOTNET_CLI_HOME | Out-Null

# 0) cover art for the left panel: copied from your local Steam library cache (optional)
$cover = "$work\src\TrainerApp\Assets\cover.jpg"
if (-not (Test-Path $cover)) {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    $found = if ($steam) { Get-ChildItem "$steam\appcache\librarycache\4348910" -Recurse -Filter library_capsule.jpg -ErrorAction SilentlyContinue | Select-Object -First 1 }
    if ($found) { New-Item -ItemType Directory -Force (Split-Path $cover) | Out-Null; Copy-Item $found.FullName $cover }
    else { Write-Host "No cover art found in Steam cache - building without it." }
}

# 1) payload (injected into the game)
dotnet build "$work\src\TrainerMod\TrainerMod.csproj" -c Release -o "$work\out\mod" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Mod build failed" }

# 2) trainer exe (embeds the mod dll + manifest)
dotnet publish "$work\src\TrainerApp\TrainerApp.csproj" -c Release -o "$work\out\app" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Trainer build failed" }

Copy-Item "$work\out\app\ProbablyStolenTrainer.exe" "$root\ProbablyStolenTrainer.exe" -Force
New-Item -ItemType Directory -Force "$root\release" | Out-Null
Copy-Item "$work\out\app\ProbablyStolenTrainer.exe" "$root\release\ProbablyStolenTrainer.exe" -Force
Write-Host "Built: $root\ProbablyStolenTrainer.exe (and release\)"
