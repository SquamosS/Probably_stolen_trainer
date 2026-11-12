# Live test: sends commands to the injected trainer and checks the values the game reports back.
param([string]$ExeFolder)   # folder the trainer exe was started from (default: workspace Trainers\, else release\)
$proc = "Probably Stolen"
if (-not $ExeFolder) {
    $ws = Join-Path $PSScriptRoot "..\..\Trainers"
    $ExeFolder = if (Test-Path (Join-Path $ws "TrainerData\$proc")) { $ws } else { Join-Path $PSScriptRoot "release" }
}
$d = Join-Path $ExeFolder "TrainerData\$proc"
if (-not (Test-Path "$d\status.txt")) { throw "No status file in $d - is the trainer attached?" }
function St { $h=@{}; foreach($l in Get-Content "$d\status.txt"){ $p=$l.Split('=',2); if($p.Count -eq 2){ $h[$p[0]]=$p[1] } }; $h }
function Send($c, $wait=900) { Add-Content "$d\commands.txt" $c; Start-Sleep -Milliseconds $wait; St }
$results = @()
function Check($name, $ok, $detail) { $script:results += [pscustomobject]@{ Test=$name; Result=$(if($ok){'PASS'}else{'FAIL'}); Detail=$detail } }

$s0 = St; $cash0=[int]$s0.cash; $favor0=[int]$s0.favor
Check "Payload version 2 loaded" ($s0.version -eq '2') "version=$($s0.version)"
Check "Game hooks attached (expect 12)" ($s0.patches -eq '12') "patches=$($s0.patches)"
"Start: cash=$cash0 favor=$favor0 aug=$($s0.aug) evidence=$($s0.evidence) garbage=$($s0.garbage) rentdays=$($s0.rentdays)"

$s = Send "addcash|1000";              Check "Add Money"   ([int]$s.cash -eq $cash0+1000) "$cash0 -> $($s.cash) | $($s.message)"
$s = Send "setcash|$cash0";            Check "Edit Money"  ([int]$s.cash -eq $cash0) "-> $($s.cash) | $($s.message)"
$s = Send "toggle|freeze_cash|1"
$s = Send "spend|5000" 1200;           Check "Freeze Money (game charges 5000)" ([int]$s.cash -eq $cash0) "after charge: $($s.cash) | $($s.message)"
$s = Send "toggle|freeze_cash|0"
$s = Send "setcash|$cash0"
$s = Send "setfavor|999";              Check "Edit Wild Favor" ([int]$s.favor -eq 999) "-> $($s.favor) | $($s.message)"
$s = Send "setfavor|$favor0";          Check "Restore Wild Favor" ([int]$s.favor -eq $favor0) "-> $($s.favor)"
$s = Send "clearaug";                  Check "Reset Aug Presence" ([int]$s.aug -eq 0) "$($s0.aug) -> $($s.aug) | $($s.message)"
$s = Send "toggle|no_aug|1";           Check "No Aug toggle" ($s.'t.no_aug' -eq '1' -and [int]$s.aug -eq 0) "aug=$($s.aug)"
$s = Send "toggle|no_aug|0"
$s = Send "clearheat";                 Check "Clear Heat" ([int]$s.evidence -eq 0) "evidence=$($s.evidence) | $($s.message)"
$s = Send "cleanstore";                Check "Clean Store Now" ([int]$s.garbage -eq 0 -and $s.message -eq 'Store cleaned') "garbage=$($s.garbage) | $($s.message)"
$s = Send "heal";                      Check "Heal Wounds" ($s.message -like 'Wounds*') "$($s.message)"
$s = Send "resetrent";                 Check "Reset Rent Timer" ([int]$s.rentdays -eq 7) "rentdays=$($s.rentdays)"
$s = Send "speed|2";                   Check "Game Speed x2" ($s.speed -eq '2') "speed=$($s.speed) | $($s.message)"
$s = Send "speed|1";                   Check "Game Speed back x1" ($s.speed -eq '1') "speed=$($s.speed)"
foreach($t in 'always_bargain','patient_clients','no_heat','no_inspections','no_rent','clean_store','perfect_health','god_mode','unlimited_scav') {
  $s = Send "toggle|$t|1" 500; $on = $s."t.$t"; $s = Send "toggle|$t|0" 500
  Check "Toggle switches: $t" ($on -eq '1' -and $s."t.$t" -eq '0') "on=$on off=$($s."t.$t")"
}
$item = (Get-Content "$d\items.txt" | Where-Object { $_ -match '\|(water|bottle)' } | Select-Object -First 1).Split('|')[1]
$s = Send "spawn|$item|1";             Check "Spawn Item ($item)" ($s.message -eq "Spawned 1x $item") "$($s.message)"
$s = Send "spawn|not_a_real_item|1";   Check "Spawn rejects bad id" ($s.message -like 'Unknown item*') "$($s.message)"
$s = Send "kit|cassettes" 1200;        Check "Spawn Kit (cassettes)" ($s.message -eq 'Spawned cassettes kit') "$($s.message)"
$s = St
"End: cash=$($s.cash) favor=$($s.favor)"
$results | Format-Table -AutoSize | Out-String -Width 200
