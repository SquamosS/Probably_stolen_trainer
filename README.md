# SquamosS Trainer — Probably Stolen

A standalone trainer for the single-player game **Probably Stolen**.
It runs next to the game, attaches to the running process, and gives you 25 cheats on numpad hotkeys.
Nothing is installed into the game folder.

![Probably Stolen](https://img.shields.io/badge/game-Probably%20Stolen-orange) ![Platform](https://img.shields.io/badge/platform-Windows%20x64-blue)

## Download & use

1. Download [`release/ProbablyStolenTrainer.exe`](release/ProbablyStolenTrainer.exe).
2. Start the game, then start the trainer (either order). It attaches by itself a few seconds after the game starts.
3. Load a save. The status badge turns green: **Active — cheats running**.
4. Use the hotkeys (Num Lock must be **on**) or click any row.

Requires the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (x64).

> Back up your saves first: `%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\`
> Some antivirus tools flag trainers because they attach to another process. That is expected for this kind of tool.

## Cheats

### Toggles

| Hotkey | Cheat |
|---|---|
| Num 1 | Freeze Money (can go up, never down) |
| Num 2 | Bargains Always Succeed (markup, blackmail, discount demands) |
| Num 3 | Customers Never Impatient |
| Num 4 | No Heat / Evidence |
| Num 5 | No Inspections |
| Num 6 | No Aug Presence |
| Num 7 | Free Rent (landlord still visits, costs 0, no increase) |
| Num 8 | Clean Store (no trash / vandalism) |
| Num 9 | Perfect Health (no wounds / Sentinel) |
| Num 0 | God Mode + No Thirst (raids, after-hours) |
| Num . | Unlimited Scavenging |
| Num + | Game Speed (x0.5 – x5) |

### Instant actions

| Hotkey | Action |
|---|---|
| Ctrl+Num 1 | Edit Money |
| Ctrl+Num 2 | Add Money |
| Ctrl+Num 3 | Edit Wild Favor |
| Ctrl+Num 4 | Max All Faction Reputation |
| Ctrl+Num 5 | Clear Heat Now |
| Ctrl+Num 6 | Reset Aug Presence |
| Ctrl+Num 7 | Reset Rent Timer (7 days) |
| Ctrl+Num 8 | Clean Store Now |
| Ctrl+Num 9 | Heal Wounds |
| Ctrl+Num 0 | Dismiss Next Customer |

Also included: a searchable **item spawner** for every item in the game, 15 **item kits**, and a switch for the game's built-in **developer tools** (F8 console / F9 debug panel, after a game restart).

`Ctrl+Shift+Home` turns all hotkeys off and on.

## How it works

- `work/src/TrainerApp` — the Windows app (WinForms, .NET 9). It finds the game, loads the payload into the game's Mono runtime, and sends commands through small text files in `TrainerData/` next to the exe.
- `work/src/TrainerMod` — the payload that runs inside the game. It uses Harmony patches and the game's own functions.

## Build from source

Requirements: .NET 9 SDK and the game installed (the payload compiles against the game's own DLLs).

```powershell
powershell -ExecutionPolicy Bypass -File work\build.ps1
```

If the game isn't in `D:\SteamLibrary\steamapps\common\Probably Stolen`, pass your path:
`dotnet build work\src\TrainerMod -p:GameManaged="<game>\Probably Stolen_Data\Managed"`.

All build caches stay inside the project folder.

## Disclaimer

For personal, offline single-player use only. Not affiliated with Questing Goose Studio or tinyBuild.
**Probably Stolen** and its artwork belong to their owners.

— made by **SquamosS**
