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
        internal static string PatchErrors = "";   // "Class: message; ..." - shown as patcherr= in status.txt

        /// <summary>
        /// Called once by the injector (on a background thread). Only patches; Unity objects are created on the main thread.
        /// Each patch class is applied on its own so one broken hook can't stop the rest.
        /// </summary>
        public static void Init(string dataFolder)
        {
            Cheats.ModFolder = dataFolder;
            if (patched) return;
            patched = true;
            HarmonyInst = new Harmony("pstrainer.trainer");
            var errors = new StringBuilder();
            foreach (Type t in AccessTools.GetTypesFromAssembly(typeof(Loader).Assembly))
            {
                if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                try { HarmonyInst.CreateClassProcessor(t).Patch(); }
                catch (Exception e)
                {
                    Exception inner = e;
                    while (inner.InnerException != null) inner = inner.InnerException;
                    errors.Append(t.Name).Append(": ").Append(inner.GetType().Name).Append(' ').Append(inner.Message).Append("; ");
                }
            }
            PatchErrors = errors.ToString().Replace('\n', ' ').Replace('\r', ' ');
            foreach (var _ in HarmonyInst.GetPatchedMethods()) PatchedCount++;
            if (PatchErrors.Length > 0)
                try { File.WriteAllText(Path.Combine(dataFolder, "patch_errors.txt"), errors.ToString()); } catch { }
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
            "no_aug", "no_rent", "clean_store", "perfect_health", "god_mode", "unlimited_scav",
            "highlight_wanted", "unlimited_perks", "highlight_fakes", "appraisal_helper"
        };

        // New-game perk screen limits (the game hard-codes 3 points / 4 perks).
        public static int PerkPoints() => On("unlimited_perks") ? 99 : 3;
        public static int PerkSlots() => On("unlimited_perks") ? 40 : 4;

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
                if (Cheats.InGame || lit.Count > 0) SafeRun(UpdateHighlights);
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

        // ---------------------------------------------------------------- item highlights (gold / red / blue)
        private Dictionary<GameItemElement, Color> lit = new Dictionary<GameItemElement, Color>();
        internal static int CountWanted, CountFake, CountKnown;
        internal static string FakeDetail = "";

        /// <summary>"item=trueValue, ..." for appraisal items on the counter (for testing / status display).</summary>
        private static string AppraisalSummary()
        {
            if (!Cheats.InGame) return "";
            try
            {
                EmporiumEntry e = EmporiumEntry.Instance;
                var parts = new List<string>();
                foreach (GraphNodeStorage root in new GraphNodeStorage[] { e.frontInvinvElement, e.invElement, e.backInvinvElementCounter })
                {
                    if (root == null) continue;
                    foreach (GameItemElement item in GraphUtils.FindAllChildrenType<GameItemElement>(root))
                        if (item.IsTag("APPRAISAL_ITEM") && parts.Count < 8)
                            parts.Add(item.identifier + "=" + AppraisalHelper.GetCorrectBaseValue(item));
                }
                return string.Join(", ", parts.ToArray());
            }
            catch { return ""; }
        }

        /// <summary>
        /// Works out which colour frame each item should have, stores it in Highlight.Colors (read by the frame patch),
        /// and refreshes only items whose frame changed.
        /// </summary>
        private void UpdateHighlights()
        {
            bool wantOn = Cheats.On("highlight_wanted") && Cheats.InGame;
            bool fakeOn = Cheats.On("highlight_fakes") && Cheats.InGame;

            StoreClient client = null;
            if (wantOn)
            {
                StoreClientInstance inst = PlayerStore.Instance.currentClientInstance;
                StoreClient c = inst != null ? inst.GetClientBlueprint() : null;
                if (c != null && c.IsClientBuying()) client = c;
            }
            Highlight.Client = client;

            var desired = new Dictionary<GameItemElement, Color>();
            int nWanted = 0, nFake = 0, nKnown = 0;
            var detail = new List<string>();
            if (client != null || fakeOn)
            {
                EmporiumEntry e = EmporiumEntry.Instance;
                GraphNodeStorage[] roots = { e.invElement, e.backInvinvElement, e.backInvinvElementCounter, e.frontInvinvElement,
                                             e.showcaseElement, e.hiddenElement, e.bazarLeftinvElement };
                var seen = new HashSet<GameItemElement>();
                foreach (GraphNodeStorage root in roots)
                {
                    if (root == null) continue;
                    foreach (GameItemElement item in GraphUtils.FindAllChildrenType<GameItemElement>(root))
                    {
                        if (!seen.Add(item)) continue;
                        // priority: red (unfound fake) > gold (wanted) > blue (known flaw)
                        Highlight.FakeState fs = fakeOn ? Highlight.GetFakeState(item) : Highlight.FakeState.None;
                        if (fs == Highlight.FakeState.Hidden) { desired[item] = Highlight.Red; nFake++; }
                        else if (client != null && Highlight.Wants(client, item)) { desired[item] = Highlight.Gold; nWanted++; }
                        else if (fs == Highlight.FakeState.Known) { desired[item] = Highlight.Blue; nKnown++; }
                        if (fs != Highlight.FakeState.None && detail.Count < 12) detail.Add(Highlight.Describe(item));
                    }
                }
            }
            CountWanted = nWanted; CountFake = nFake; CountKnown = nKnown;
            FakeDetail = string.Join(" | ", detail.ToArray());

            Highlight.Colors = desired;   // patch reads this
            foreach (var kv in lit)
            {
                Color c;
                if (!desired.TryGetValue(kv.Key, out c) || c != kv.Value) SafeRefresh(kv.Key);   // off or recoloured
            }
            foreach (var kv in desired)
                if (!lit.ContainsKey(kv.Key)) SafeRefresh(kv.Key);                                // newly on
            lit = desired;
        }

        private static void SafeRefresh(GameItemElement item)
        {
            try { item.RefreshPlayingFrame(); } catch { /* item was destroyed or moved out of view */ }
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
            sb.Append("version=8\n");
            sb.Append("patcherr=").Append(Loader.PatchErrors).Append('\n');
            sb.Append("perkpoints=").Append(Cheats.PerkPoints()).Append('\n');
            sb.Append("perkpatch=").Append(PerkTranspiler.Replaced).Append('\n');
            sb.Append("highlighted=").Append(lit.Count).Append('\n');
            sb.Append("hl_wanted=").Append(CountWanted).Append('\n');
            sb.Append("hl_fake=").Append(CountFake).Append('\n');
            sb.Append("hl_known=").Append(CountKnown).Append('\n');
            sb.Append("hl_detail=").Append(FakeDetail.Replace('\n', ' ')).Append('\n');
            sb.Append("appraisal=").Append(AppraisalSummary()).Append('\n');
            {
                StoreClient hc = Highlight.Client;
                sb.Append("customer=").Append(hc == null ? "-" : (hc.displayName ?? "?").Replace('\n', ' ') + " wants " + string.Join(",", hc.clientBuyingTagList ?? new List<string>())).Append('\n');
            }
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

    /// <summary>Highlight state shared by the runner and the frame-colour patch.</summary>
    internal static class Highlight
    {
        public static StoreClient Client;                                  // customer being matched (null = off)
        public static Dictionary<GameItemElement, Color> Colors = new Dictionary<GameItemElement, Color>();
        public static readonly Color Gold = new Color(1f, 0.78f, 0.12f, 1f);   // customer wants it
        public static readonly Color Red = new Color(1f, 0.22f, 0.22f, 1f);    // counterfeit / hidden flaw, not found yet
        public static readonly Color Blue = new Color(0.25f, 0.65f, 1f, 1f);   // flaw already discovered / exposed

        public enum FakeState { None, Hidden, Known }

        public static bool Wants(StoreClient client, GameItem item)
        {
            if (client == null || item == null) return false;
            try { return GeneralHelper.IsItemOwned(item) && client.IsClientBuyingThisItem(item); }
            catch { return false; }
        }

        /// <summary>
        /// A feature whose public claim differs from reality (IsFeatureMatch == false) is how the game models
        /// counterfeits and hidden defects (e.g. a fake stamp claims "genuine"). Hidden = not yet discovered/exposed.
        /// </summary>
        /// <summary>
        /// Claim differs from reality. Market-event features (price news, e.g. "water price up") never set
        /// isFeatureMatch, so they look like mismatches - they are not scams and are skipped.
        /// </summary>
        public static bool IsScam(ItemFeature f)
        {
            return f != null && f.featureType != ItemFeature.FeatureType.Event && !f.IsFeatureMatch();
        }

        /// <summary>"item: category/feature, ..." for every mismatching feature - written to status for checking.</summary>
        public static string Describe(GameItem item)
        {
            var sb = new StringBuilder(item.identifier ?? "?");
            try
            {
                foreach (ItemFeature f in item.itemFeatures)
                    if (IsScam(f))
                        sb.Append(" [").Append(f.GetCategory()).Append('/').Append(f.identifier)
                          .Append(f.IsExposed() || f.IsFeatureDiscovered() ? " known" : " hidden").Append(']');
            }
            catch { }
            return sb.ToString();
        }

        public static FakeState GetFakeState(GameItem item)
        {
            if (item == null || item.itemFeatures == null) return FakeState.None;
            bool any = false, allKnown = true;
            try
            {
                foreach (ItemFeature f in item.itemFeatures)
                {
                    if (!IsScam(f)) continue;
                    any = true;
                    if (!f.IsExposed() && !f.IsFeatureDiscovered()) allKnown = false;
                }
            }
            catch { return FakeState.None; }
            if (!any) return FakeState.None;
            return allKnown ? FakeState.Known : FakeState.Hidden;
        }
    }

    /// <summary>Reuses the game's own item frame (normally for a playing cassette) to outline highlighted items.</summary>
    [HarmonyPatch(typeof(CassetteHelper), nameof(CassetteHelper.GetPlayingFrameColor))]
    internal static class Patch_HighlightFrame
    {
        private static void Postfix(GameItem item, ref Color? __result)
        {
            if (__result.HasValue) return;
            GameItemElement el = item as GameItemElement;
            Color c;
            if (el != null && Highlight.Colors.TryGetValue(el, out c)) __result = c;
        }
    }

    /// <summary>
    /// The game hangs its frame on the un-rotated "middle" node (fine for square cassettes, wrong for rotated items).
    /// Move it onto the rotator so the frame turns with the sprite.
    /// </summary>
    [HarmonyPatch(typeof(GameItemElement), "UpdatePlayingFrameOverlay")]
    internal static class Patch_FrameFollowsRotation
    {
        private static void Postfix(GameItemElement __instance, TreeNodeRender[] ___playingFrameNodes, TreeNodeRender[] ___fakePlayingFrameNodes)
        {
            Reparent(___playingFrameNodes, __instance.rotator);
            Reparent(___fakePlayingFrameNodes, __instance.fakeRotator);
        }

        private static void Reparent(TreeNodeRender[] nodes, TreeNodeRender parent)
        {
            if (nodes == null || parent == null) return;
            foreach (TreeNodeRender n in nodes)
                if (n != null && n.rectTransform.parent != parent.rectTransform)
                {
                    n.SetParent(parent);
                    n.rectTransform.SetAsLastSibling();
                }
        }
    }

    /// <summary>
    /// Starting-perk screen: swap the hard-coded constants for trainer values.
    /// CanStart / RefreshCounters use the literal 3 (perk points), MaxPerkCount starts from the literal 4 (perk slots).
    /// </summary>
    internal static class PerkTranspiler
    {
        public static int Replaced;   // expect 3 (one per patched method); reported in status.txt

        public static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> code, int literal, string method)
        {
            var target = AccessTools.Method(typeof(Cheats), method);
            bool done = false;
            foreach (CodeInstruction ins in code)
            {
                if (!done && ins.opcode == System.Reflection.Emit.OpCodes.Ldc_I4_3 && literal == 3
                    || !done && ins.opcode == System.Reflection.Emit.OpCodes.Ldc_I4_4 && literal == 4)
                {
                    done = true;   // only the first occurrence - each method has exactly one
                    Replaced++;
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, target).MoveLabelsFrom(ins);
                    continue;
                }
                yield return ins;
            }
        }
    }

    [HarmonyPatch(typeof(NewGamePerksUI), "CanStart")]
    internal static class Patch_PerkCanStart
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code) => PerkTranspiler.Replace(code, 3, nameof(Cheats.PerkPoints));
    }

    [HarmonyPatch(typeof(NewGamePerksUI), "RefreshCounters")]
    internal static class Patch_PerkCounters
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code) => PerkTranspiler.Replace(code, 3, nameof(Cheats.PerkPoints));
    }

    [HarmonyPatch(typeof(NewGamePerksUI), "MaxPerkCount")]
    internal static class Patch_PerkSlots
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code) => PerkTranspiler.Replace(code, 4, nameof(Cheats.PerkSlots));
    }

    /// <summary>
    /// Appraisal Helper: adds the true value, the "Perfect" estimate range and a plus/minus breakdown
    /// to the tooltip of any item a customer brought for appraisal (hands, ribwiches, plasma cells).
    /// The value comes from the game's own formula; the breakdown mirrors it line by line.
    /// </summary>
    [HarmonyPatch(typeof(AppraisalHelper), nameof(AppraisalHelper.CreateAppraisalTooltip))]
    internal static class Patch_AppraisalTooltip
    {
        private const RenderHandler.ColorPalette Plus = RenderHandler.ColorPalette.LightGreen;
        private const RenderHandler.ColorPalette Minus = RenderHandler.ColorPalette.LightRed;
        private const RenderHandler.ColorPalette Info = RenderHandler.ColorPalette.LightOrange;

        private static void Postfix(RichTextBuilder builder, GameItem item)
        {
            if (!Cheats.On("appraisal_helper") || item == null || !item.IsTag("APPRAISAL_ITEM")) return;
            try
            {
                int value = AppraisalHelper.GetCorrectBaseValue(item);
                if (value <= 0) return;
                builder.AddLine("[Trainer] TRUE VALUE: " + value + " credits", true, Plus, bold: true);
                builder.AddLine("Perfect estimate: " + RNG.CalculatePercentage(value, 95) + " - " + RNG.CalculatePercentage(value, 105), true, Info);

                var lines = new List<KeyValuePair<int, string>>();
                int sum = Breakdown(item, lines);
                foreach (var l in lines)
                    builder.AddLine((l.Key > 0 ? "+" : l.Key < 0 ? "" : " ") + (l.Key != 0 ? l.Key.ToString() + "  " : "") + l.Value, true,
                        l.Key > 0 ? Plus : l.Key < 0 ? Minus : Info);
                if (lines.Count > 0 && sum != value)
                    builder.AddLine("(breakdown approximate - game value is what counts)", true, Info, italic: true);
            }
            catch { }
        }

        private static string S(GameItem i, string tag) { try { return i.GetTagReadonly(tag).GetString() ?? ""; } catch { return ""; } }
        private static int N(GameItem i, string tag) { try { return i.GetTagReadonly(tag).GetInt(); } catch { return 0; } }

        /// <summary>Fills plus/minus lines; returns the total those lines add up to (after the game's floor).</summary>
        private static int Breakdown(GameItem item, List<KeyValuePair<int, string>> o)
        {
            Action<int, string> add = (v, t) => o.Add(new KeyValuePair<int, string>(v, t));
            int total;

            if (item.IsTag("HAND_TAG"))
            {
                string mat = S(item, "HAND_MATERIAL"), itf = S(item, "HAND_INTERFACE"), cond = S(item, "HAND_CONDITION");
                string arm = S(item, "HAND_ADDON_ARMORED"), bla = S(item, "HAND_ADDON_BLADED");
                add(0, "Base 100");
                total = 100;
                if (mat == "GOLD_PLATED") { add(200, "Gold plated"); total += 200; }
                else if (mat == "CARBON_COMPOSITE") { add(50, "Carbon composite"); total += 50; }
                else add(0, "Material: " + Nice(mat));
                if (itf == "NEURAL") { add(120, "Neural interface"); total += 120; }
                else if (itf == "EMG_PLUS") { add(45, "EMG+ interface"); total += 45; }
                else if (itf == "UNSPECIFIED") { add(-60, "Unspecified interface"); total -= 60; }
                else add(0, "Interface: " + Nice(itf));
                if (cond == "WEAR") { add(-35, "Wear"); total -= 35; }
                else if (cond == "DAMAGE") { add(-85, "Damaged"); total -= 85; }
                if (arm == "ARMORED") { add(50, "Armored add-on"); total += 50; }
                if (bla == "BLADED") { add(80, "Bladed add-on"); total += 80; }
                if (AppHandHelper.IsTacticalSet(mat, itf, cond, arm, bla)) { add(100, "Tactical set bonus"); total += 100; }
                if (AppHandHelper.IsDeluxeSet(mat, itf, cond)) { add(200, "Deluxe set bonus"); total += 200; }
                return Math.Max(total, 50);
            }

            if (item.IsTag("RIBWICH_TAG"))
            {
                if (RibwichHelper.IsOpened(item)) { add(0, "OPENED - worth only 40"); return 40; }
                if (RibwichHelper.IsCounterfeit(item)) { add(0, "COUNTERFEIT - worth only 40"); return 40; }
                int month = N(item, "RIBWICH_PRODUCTION_MONTH_TAG"), year = N(item, "RIBWICH_PRODUCTION_YEAR_TAG");
                int fac = N(item, "RIBWICH_FACILITY_NUMBER_TAG");
                add(0, "Base 150 (genuine, sealed)");
                total = 150;
                if (N(item, "RIBWICH_MEAT_TAG") == 1) { add(800, "Double meat"); total += 800; }
                if (RibwichHelper.IsDeluxeEdition(item)) { add(300, "Deluxe edition"); total += 300; }
                if (RibwichHelper.IsFirstOrLastProductionMonth(month, year)) { add(50, "First/last production month"); total += 50; }
                if (RibwichHelper.IsFirstProduction(year)) { add(80, "First production year"); total += 80; }
                int fb = RibwichHelper.GetFacilityBonus(fac);
                if (fb != 0) { add(fb, "Facility #" + fac); total += fb; }
                return Math.Max(total, 40);
            }

            if (item.IsTag("PLASMA_CELL_TAG"))
            {
                string wear = S(item, "PLASMA_CASING_CONDITION_WEAR"), crack = S(item, "PLASMA_CASING_CONDITION_CRACK");
                string unc = S(item, "PLASMA_CASING_CONDITION_UNCOVERED"), reinf = S(item, "PLASMA_CASING_CONDITION_REINFORCED");
                string color = S(item, "PLASMA_COLOR"), seal = S(item, "PLASMA_SEAL");
                add(0, "Base 300");
                total = 300;
                if (wear == "WEAR") { add(-45, "Casing wear"); total -= 45; }
                else if (wear == "WEAR_HEAVY") { add(-90, "Heavy casing wear"); total -= 90; }
                if (crack == "CRACK") { add(-150, "Cracked casing"); total -= 150; }
                if (unc == "UNCOVERED") { add(-30, "Uncovered screw"); total -= 30; }
                if (reinf == "REINFORCED") { add(60, "Reinforced casing"); total += 60; }
                if (color == "POOR1" || color == "POOR2") { add(-150, "Poor plasma colour"); total -= 150; }
                else if (color == "HIGH_QUALITY") { add(150, "High-quality plasma colour"); total += 150; }
                bool basicColor = color == "STANDARD" || color == "POOR1" || color == "POOR2";
                if (seal == "LL" && basicColor) { add(60, "LL seal"); total += 60; }
                else if (seal == "ZHONGHUA" && wear == "NONE" && crack == "NONE" && unc == "NONE" && color == "STANDARD") { add(120, "Zhonghua seal (pristine, standard)"); total += 120; }
                else if (seal == "REPUBLIC" && crack != "CRACK" && color == "STANDARD") { add(120, "Republic seal (no crack, standard)"); total += 120; }
                else if (seal == "IGNITION_LAB" && reinf == "NONE" && wear == "NONE" && crack == "NONE" && unc == "NONE" && color == "HIGH_QUALITY")
                    { add(750, "Ignition Lab seal (pristine, high quality)"); total += 750; }
                else if (seal.Length > 0) add(0, "Seal: " + Nice(seal) + " (no bonus)");
                return Math.Max(total, 90);
            }
            return 0;
        }

        private static string Nice(string s) => string.IsNullOrEmpty(s) ? "none" : s.Replace('_', ' ').ToLowerInvariant();
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
