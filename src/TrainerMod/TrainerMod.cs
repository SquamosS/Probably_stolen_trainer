// Probably Stolen Trainer - payload.
// ProbablyStolenTrainer.exe injects this assembly into the running game's Mono runtime and
// calls PSTrainer.Loader.Init(dataFolder). Nothing is installed into the game folder.
// It talks to the exe through three small text files in the trainer's data folder:
//   commands.txt  (exe -> game)  one command per line, consumed here
//   status.txt    (game -> exe)  rewritten every second: values and toggle states
//   items.txt     (game -> exe)  list of every spawnable item id
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace PSTrainer
{
    public static class Loader
    {
        private static bool patched;
        internal static GameObject Runner;
        internal static Harmony HarmonyInst;
        internal static int PatchedCount;

        /// <summary>Called once by the injector (on a background thread). Only patches; Unity objects are created on the main thread.</summary>
        public static void Init(string dataFolder)
        {
            Cheats.ModFolder = dataFolder;
            if (patched) return;
            patched = true;
            HarmonyInst = new Harmony("pstrainer.trainer");
            HarmonyInst.PatchAll(typeof(Loader).Assembly);
            foreach (var _ in HarmonyInst.GetPatchedMethods()) PatchedCount++;
        }

        /// <summary>Runs on Unity's main thread (from the ModLoader.Update hook).</summary>
        internal static void EnsureRunner()
        {
            if (Runner != null) return;
            Runner = new GameObject("PSTrainerRunner");
            UnityEngine.Object.DontDestroyOnLoad(Runner);
            Runner.AddComponent<TrainerRunner>();
        }
    }

    /// <summary>Main-thread bootstrap: the game's ModLoader ticks every frame in every scene.</summary>
    [HarmonyPatch(typeof(ModLoader), "Update")]
    internal static class Patch_Bootstrap
    {
        private static void Postfix()
        {
            if (Loader.Runner == null) Loader.EnsureRunner();
        }
    }

    /// <summary>All cheat state. Toggles are read by the Harmony patches and the per-tick loop.</summary>
    public static class Cheats
    {
        public static string ModFolder;

        public static readonly string[] ToggleNames =
        {
            "freeze_cash", "always_bargain", "patient_clients", "no_heat", "no_inspections",
            "no_aug", "no_rent", "clean_store", "perfect_health", "god_mode", "unlimited_scav"
        };

        public static readonly Dictionary<string, bool> Toggles = new Dictionary<string, bool>();
        public static float Speed = 1f;
        public static int LockedCash = int.MinValue;
        public static string LastMessage = "Ready";

        static Cheats() { ResetToggles(); }

        public static void ResetToggles()
        {
            foreach (string n in ToggleNames) Toggles[n] = false;
            Speed = 1f;
            LockedCash = int.MinValue;
            if (Time.timeScale != 1f) Time.timeScale = 1f;
        }

        public static bool On(string name)
        {
            bool v;
            return Toggles.TryGetValue(name, out v) && v;
        }

        /// <summary>True only while a save is loaded and the shop scene is running.</summary>
        public static bool InGame => EmporiumEntry.Instance != null;
    }

    public class TrainerRunner : MonoBehaviour
    {
        private float nextPoll;
        private float nextStatus;
        private bool itemsWritten;

        private string CmdPath => Path.Combine(Cheats.ModFolder, "commands.txt");
        private string WorkPath => Path.Combine(Cheats.ModFolder, "commands.work");
        private string StatusPath => Path.Combine(Cheats.ModFolder, "status.txt");
        private string ItemsPath => Path.Combine(Cheats.ModFolder, "items.txt");

        private void Update()
        {
            float now = Time.unscaledTime;
            if (now >= nextPoll)
            {
                nextPoll = now + 0.2f;
                SafeRun(PollCommands);
                if (Cheats.InGame) SafeRun(ApplyContinuous);
            }
            if (now >= nextStatus)
            {
                nextStatus = now + 1f;
                SafeRun(WriteStatus);
                if (!itemsWritten && Cheats.InGame) SafeRun(WriteItems);
            }
        }

        private static void SafeRun(Action a)
        {
            try { a(); }
            catch (Exception e)
            {
                Cheats.LastMessage = "Error: " + e.Message;
                Debug.LogWarning("[PSTrainer] " + e);
            }
        }

        // ---------------------------------------------------------------- continuous cheats
        private void ApplyContinuous()
        {
            PlayerStore ps = PlayerStore.Instance;
            if (ps == null) return;

            if (Cheats.On("freeze_cash"))
            {
                if (Cheats.LockedCash == int.MinValue || ps.playerCash > Cheats.LockedCash) Cheats.LockedCash = ps.playerCash;
                if (ps.playerCash < Cheats.LockedCash) ps.playerCash = Cheats.LockedCash;
            }
            else Cheats.LockedCash = int.MinValue;

            if (Cheats.On("patient_clients"))
            {
                StoreClient c = ps.currentClientInstance != null ? ps.currentClientInstance.GetClientBlueprint() : null;
                if (c != null) c.clientImpatience = 0;
            }

            if (Cheats.On("no_heat") && ps.secData != null) ClearHeat(ps);
            if (Cheats.On("no_aug")) ps.augPresence = 0;

            if (Cheats.On("clean_store"))
            {
                ps.garbageLevel = 0;
                ps.isVandalized = false;
            }

            if (Cheats.On("perfect_health") && ps.healthData != null) Heal(ps);
            if (Cheats.On("unlimited_scav")) ps.scavengingAttempts = 0;

            if (Time.timeScale != Cheats.Speed && Cheats.Speed > 0f) Time.timeScale = Cheats.Speed;
        }

        private static void ClearHeat(PlayerStore ps)
        {
            SecData s = ps.secData;
            s.evidenceLevel = 0;
            if (s.crimeAmount != null)
                for (int i = 0; i < s.crimeAmount.Count; i++) s.crimeAmount[i] = 0;
        }

        private static void Heal(PlayerStore ps)
        {
            HealthData h = ps.healthData;
            h.woundState = 0;
            h.isWoundStable = true;
            h.isWoundedFresh = false;
            h.daysSinceSentinelInjection = 0;
            h.sentinelPool = 0;
        }

        // ---------------------------------------------------------------- commands
        private void PollCommands()
        {
            if (string.IsNullOrEmpty(Cheats.ModFolder) || !File.Exists(CmdPath)) return;
            try
            {
                if (File.Exists(WorkPath)) File.Delete(WorkPath);
                File.Move(CmdPath, WorkPath); // fails harmlessly while the exe is still writing
            }
            catch (IOException) { return; }

            string[] lines = File.ReadAllLines(WorkPath);
            File.Delete(WorkPath);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] p = line.Split('|');
                try { Cheats.LastMessage = Execute(p); }
                catch (Exception e) { Cheats.LastMessage = "Failed '" + p[0] + "': " + e.Message; }
            }
            WriteStatus();
        }

        private static int IntArg(string[] p, int i, int def = 0)
        {
            int v;
            return p.Length > i && int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        private string Execute(string[] p)
        {
            string cmd = p[0].ToLowerInvariant();

            if (cmd == "toggle")
            {
                string name = p.Length > 1 ? p[1] : "";
                if (!Cheats.Toggles.ContainsKey(name)) return "Unknown toggle " + name;
                Cheats.Toggles[name] = IntArg(p, 2) != 0;
                return name + (Cheats.Toggles[name] ? " ON" : " OFF");
            }
            if (cmd == "speed")
            {
                float f;
                if (p.Length > 1 && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                    Cheats.Speed = Mathf.Clamp(f, 0.25f, 10f);
                Time.timeScale = Cheats.Speed;
                return "Game speed x" + Cheats.Speed.ToString("0.##", CultureInfo.InvariantCulture);
            }

            if (!Cheats.InGame) return "Load a save first";
            PlayerStore ps = PlayerStore.Instance;

            switch (cmd)
            {
                case "addcash":
                {
                    int n = IntArg(p, 1);
                    ps.ModCash(n, true);
                    if (Cheats.On("freeze_cash")) Cheats.LockedCash = ps.playerCash;
                    return "Added " + n + " credits";
                }
                case "setcash":
                {
                    int n = Math.Max(0, IntArg(p, 1));
                    ps.playerCash = n;
                    Cheats.LockedCash = Cheats.On("freeze_cash") ? n : int.MinValue;
                    return "Credits set to " + n;
                }
                case "addfavor":
                {
                    int n = IntArg(p, 1);
                    ps.wildFavor += n;
                    return "Added " + n + " Wild favor";
                }
                case "setfavor":
                {
                    int n = Math.Max(0, IntArg(p, 1));
                    ps.wildFavor = n;
                    return "Wild favor set to " + n;
                }
                case "maxrep":
                    if (ps.storeReputations != null)
                        foreach (StoreReputation r in ps.storeReputations) r.ModReputation(400.0);
                    StoreReputation.UpdateReputation();
                    return "All faction reputation maxed";
                case "clearheat":
                    if (ps.secData != null) ClearHeat(ps);
                    return "Evidence and open charges cleared";
                case "clearaug":
                    ps.augPresence = 0;
                    return "Aug presence reset to 0";
                case "cleanstore":
                    ps.ClearTrash();
                    ps.garbageLevel = 0;
                    ps.isVandalized = false;
                    return "Store cleaned";
                case "heal":
                    if (ps.healthData != null) Heal(ps);
                    return "Wounds and Sentinel cleared";
                case "resetrent":
                    ps.dayUntilRent = 7;
                    ps.temporaryRent = 0;
                    return "Rent due date pushed to 7 days";
                case "dismiss":
                    DebugPanel.DismissNextClient();
                    return "Next customer dismissed";
                case "spawn":
                {
                    string id = p.Length > 1 ? p[1] : "";
                    int qty = Mathf.Clamp(IntArg(p, 2, 1), 1, 100);
                    // Unknown ids would make the game build a dummy item - refuse them up front.
                    if (!DirectoryMaster.GetIdentifierList<GameItem>().Contains(id)) return "Unknown item '" + id + "'";
                    int ok = 0;
                    for (int i = 0; i < qty; i++)
                    {
                        GameItem item = DirectoryMaster.Item(id);
                        if (item == null) break;
                        // TryAcceptAll returns how many units could NOT be placed (0 = fully placed).
                        if (GraphUtils.TryAcceptAll(EmporiumEntry.Instance.invElement, item) == 0) ok++;
                        else { item.Destroy(); break; }
                    }
                    return ok == qty ? "Spawned " + qty + "x " + id : "Spawned " + ok + "/" + qty + " " + id + " (inventory full)";
                }
                case "spend":
                {   // test helper: spends money through the game's own payment path
                    int n = Math.Max(0, IntArg(p, 1));
                    ps.PayMoney(n, false);
                    return "Game charged " + n + " credits";
                }
                case "kit":
                    return SpawnKit(p.Length > 1 ? p[1] : "");
                default:
                    return "Unknown command " + cmd;
            }
        }

        private static string SpawnKit(string kit)
        {
            switch (kit)
            {
                case "gun": DebugPanel.SpawnGunKit(); break;
                case "chem": DebugPanel.SpawnChemKit(); break;
                case "farming": DebugPanel.SpawnFarmingKit(); break;
                case "hydroponic": DebugPanel.SpawnHydroponicKit(); break;
                case "aquaculture": DebugPanel.SpawnAquacultureKit(); break;
                case "rat": DebugPanel.SpawnRatKit(); break;
                case "smuggler": DebugPanel.SpawnSmugglerKit(); break;
                case "water": DebugPanel.SpawnWaterTestingKit(); break;
                case "injectors": DebugPanel.SpawnInjectors(); break;
                case "plasma": DebugPanel.SpawnPlasma(); break;
                case "hypertech": DebugPanel.SpawnHypertechItems(); break;
                case "lottery": DebugPanel.SpawnLotteryKit(); break;
                case "brokenmachine": DebugPanel.SpawnBrokenMachineKit(); break;
                case "cassettes": DebugPanel.SpawnMusicCassettes(); break;
                case "debug": DebugPanel.SpawnDebugKit(); break;
                default: return "Unknown kit " + kit;
            }
            return "Spawned " + kit + " kit";
        }

        // ---------------------------------------------------------------- status / item list
        private void WriteStatus()
        {
            if (string.IsNullOrEmpty(Cheats.ModFolder)) return;
            StringBuilder sb = new StringBuilder();
            sb.Append("alive=").Append(DateTime.UtcNow.Ticks).Append('\n');
            sb.Append("ingame=").Append(Cheats.InGame ? 1 : 0).Append('\n');
            sb.Append("message=").Append(Cheats.LastMessage.Replace('\n', ' ')).Append('\n');
            sb.Append("speed=").Append(Cheats.Speed.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("patches=").Append(Loader.PatchedCount).Append('\n');
            sb.Append("version=2\n");
            foreach (KeyValuePair<string, bool> t in Cheats.Toggles)
                sb.Append("t.").Append(t.Key).Append('=').Append(t.Value ? 1 : 0).Append('\n');
            if (Cheats.InGame)
            {
                PlayerStore ps = PlayerStore.Instance;
                sb.Append("cash=").Append(ps.playerCash).Append('\n');
                sb.Append("favor=").Append(ps.wildFavor).Append('\n');
                sb.Append("day=").Append(StoreStation.Instance.dayCounter).Append('\n');
                sb.Append("rent=").Append(ps.GetRentValue()).Append('\n');
                sb.Append("rentdays=").Append(ps.dayUntilRent).Append('\n');
                sb.Append("aug=").Append(ps.augPresence).Append('\n');
                sb.Append("evidence=").Append(ps.secData != null ? ps.secData.evidenceLevel : 0).Append('\n');
                sb.Append("garbage=").Append(ps.garbageLevel).Append('\n');
            }
            string tmp = StatusPath + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(StatusPath)) File.Delete(StatusPath);
            File.Move(tmp, StatusPath);
        }

        private void WriteItems()
        {
            itemsWritten = true;
            StringBuilder sb = new StringBuilder();
            foreach (string dir in DirectoryMaster.GetDictionaryClassNameList<GameItem>())
            {
                string category = dir.Replace("ItemDirectory", "").Replace("Directory", "");
                foreach (string id in DirectoryMaster.GetIdentifierList<GameItem>(dir))
                    sb.Append(category).Append('|').Append(id).Append('\n');
            }
            File.WriteAllText(ItemsPath, sb.ToString());
        }
    }

    // ==================================================================== Harmony patches

    /// <summary>Bargains: huge faction bonus while the cheat is on, so markups/blackmail/discount demands land.</summary>
    [HarmonyPatch(typeof(BargainUIManager), "GetFactionRepBonus")]
    internal static class Patch_RepBonus
    {
        private static void Postfix(ref int __result) { if (Cheats.On("always_bargain")) __result += 500; }
    }

    [HarmonyPatch(typeof(BargainUIManager), "GetFactionRepBonusBlackmail")]
    internal static class Patch_RepBonusBlackmail
    {
        private static void Postfix(ref int __result) { if (Cheats.On("always_bargain")) __result += 500; }
    }

    [HarmonyPatch(typeof(BargainUIManager), nameof(BargainUIManager.OfferMarkup))]
    internal static class Patch_OfferMarkup
    {
        private static void Prefix(StoreClient ___trackedClient) { Patch_Helpers.Soften(___trackedClient); }
    }

    [HarmonyPatch(typeof(BargainUIManager), nameof(BargainUIManager.DemandBuyingDiscount))]
    internal static class Patch_DemandDiscount
    {
        private static void Prefix(StoreClient ___trackedClient) { Patch_Helpers.Soften(___trackedClient); }
    }

    [HarmonyPatch(typeof(BargainUIManager), nameof(BargainUIManager.Blackmail))]
    internal static class Patch_Blackmail
    {
        private static void Prefix(StoreClient ___trackedClient) { Patch_Helpers.Soften(___trackedClient); }
    }

    internal static class Patch_Helpers
    {
        public static void Soften(StoreClient c)
        {
            if (c == null || !Cheats.On("always_bargain")) return;
            c.alwaysRejectBadBargain = false;
            c.staysAfterFailedBargain = true;
        }
    }

    /// <summary>No inspections: skip the daily inspector scheduling.</summary>
    [HarmonyPatch(typeof(StoreClientManager), nameof(StoreClientManager.HandleInspectionClient))]
    internal static class Patch_NoInspections
    {
        private static bool Prefix() { return !Cheats.On("no_inspections"); }
    }

    /// <summary>No rent: landlord still visits, but paying costs nothing and rent never increases.</summary>
    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.OnRentPaid))]
    internal static class Patch_RentPaid
    {
        private static bool Prefix(PlayerStore __instance)
        {
            if (!Cheats.On("no_rent")) return true;
            __instance.dayUntilRent = 7;
            __instance.temporaryRent = 0;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.CanPayRent))]
    internal static class Patch_CanPayRent
    {
        private static void Postfix(ref bool __result) { if (Cheats.On("no_rent")) __result = true; }
    }

    /// <summary>God mode: the player character's health can't drop and it can't die (raids, combat, after-hours).</summary>
    [HarmonyPatch(typeof(GameCharacterItem), nameof(GameCharacterItem.SetStat))]
    internal static class Patch_SetStat
    {
        private static void Prefix(GameCharacterItem __instance, CombatEntity.EffectType type, ref int value)
        {
            if (type != CombatEntity.EffectType.health || !Cheats.On("god_mode")) return;
            if (!__instance.IsTag("player")) return;
            int max = __instance.GetStat(CombatEntity.EffectType.maxHealth);
            if (max > 0 && value < max) value = max;
        }
    }

    [HarmonyPatch(typeof(GameCharacterItem), nameof(GameCharacterItem.KillCurrentEntity))]
    internal static class Patch_Kill
    {
        private static bool Prefix(GameCharacterItem __instance)
        {
            return !(Cheats.On("god_mode") && __instance.IsTag("player"));
        }
    }

    /// <summary>God mode also keeps the player fully hydrated.</summary>
    [HarmonyPatch(typeof(Survival), nameof(Survival.UpdateSurvival))]
    internal static class Patch_Thirst
    {
        private static void Postfix(GameCharacterItem entity)
        {
            if (Cheats.On("god_mode") && entity != null && entity.IsTag("player"))
            {
                entity.currentThirst = 1000;
                Survival.CheckSurvivalEffect(entity);
            }
        }
    }
}
