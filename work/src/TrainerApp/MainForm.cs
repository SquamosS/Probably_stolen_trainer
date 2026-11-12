using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PSTrainerApp;

internal sealed class MainForm : Form
{
    private const string ProcessName = "Probably Stolen";

    private readonly string dataDir = Path.Combine(AppContext.BaseDirectory, "TrainerData");
    private string CmdFile => Path.Combine(dataDir, "commands.txt");
    private string StatusFile => Path.Combine(dataDir, "status.txt");
    private string ItemsFile => Path.Combine(dataDir, "items.txt");
    private string InjectedFile => Path.Combine(dataDir, "injected.txt");

    private readonly List<OptionRow> rows = new();
    private readonly Dictionary<string, OptionRow> toggles = new();
    private readonly System.Windows.Forms.Timer tick = new() { Interval = 40 };
    private readonly HashSet<Keys> keysDown = new();
    private bool hotkeysEnabled = true;

    private StatusPill pill;
    private Label lblPid, lblMessage;
    private readonly Dictionary<string, StatTile> tiles = new();
    private SwitchLabel devSwitch;
    private FieldBox search;
    private DarkList lstItems;
    private DarkCombo cmbKit;
    private Stepper qty;
    private List<string> allItems = new();
    private DateTime itemsStamp;

    private Process game;
    private bool injecting;
    private bool pushedState;
    private int statusFailCount;
    private DateTime nextProcessCheck, nextStatusRead;

    private const int Pad = 16, Gap = 12, Top = 78, BodyH = 548;

    public MainForm()
    {
        Text = Theme.Brand + " - Probably Stolen";
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        ClientSize = new Size(1300, 686);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        Directory.CreateDirectory(dataDir);
        Icon = MakeIcon();

        BuildHeader();
        int x = Pad;
        BuildGameCard(ref x);
        BuildToggleCard(ref x);
        BuildActionCard(ref x);
        BuildItemCard(ref x);
        BuildFooter();

        tick.Tick += (_, _) => OnTick();
        tick.Start();
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; /* CS_DROPSHADOW */ return cp; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2, dark = 1; // Win11 rounded corners + dark frame
        try { DwmSetWindowAttribute(Handle, 33, ref round, 4); DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
    }

    private static Stream Res(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp)) DrawBadge(g, new Rectangle(0, 0, 64, 64), 26f);
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static void DrawBadge(Graphics g, Rectangle r, float fontSize)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var path = Theme.Round(r, r.Width * 0.28f);
        using var br = new LinearGradientBrush(r, Theme.Accent, Theme.Accent2, 45f);
        g.FillPath(br, path);
        using var f = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var tb = new SolidBrush(Color.FromArgb(24, 14, 10));
        g.DrawString("SQ", f, tb, new RectangleF(r.X, r.Y + 1, r.Width, r.Height), sf);
    }

    // ================================================================== layout
    private void BuildHeader()
    {
        var badge = new BadgeBox { Location = new Point(Pad, 16), Size = new Size(44, 44) };
        var title = new Label { Text = Theme.Brand, Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true, Location = new Point(70, 12), BackColor = Color.Transparent };
        var sub = new Label { Text = "Probably Stolen  •  v1.0+  •  25 cheats  •  made by SquamosS", Font = Theme.Sub, ForeColor = Theme.Dim, AutoSize = true, Location = new Point(73, 42), BackColor = Color.Transparent };
        Controls.AddRange(new Control[] { badge, title, sub });

        var close = new WindowGlyph("✕", true) { Location = new Point(ClientSize.Width - 50, 14) };
        close.Click += (_, _) => Close();
        var min = new WindowGlyph("—", false) { Location = new Point(ClientSize.Width - 90, 14) };
        min.Click += (_, _) => WindowState = FormWindowState.Minimized;
        Controls.Add(close); Controls.Add(min);

        pill = new StatusPill();
        pill.Set("Waiting for game", Theme.Dim);
        Controls.Add(pill);
        PlacePill();

        foreach (Control c in new Control[] { this, title, sub, badge })
            c.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); } };
    }

    private void PlacePill() => pill.Location = new Point(ClientSize.Width - 104 - pill.Width, 22);

    private void BuildGameCard(ref int x)
    {
        var card = new Card { Location = new Point(x, Top), Size = new Size(222, BodyH) };
        Controls.Add(card);
        x += card.Width + Gap;

        var cover = new CoverArt { Location = new Point(14, 14), Size = new Size(194, 210) };
        using (var s = Res("cover.jpg")) if (s != null) cover.Image = Image.FromStream(s);
        card.Controls.Add(cover);

        card.Controls.Add(new Label { Text = "PROCESS", Font = Theme.Small, ForeColor = Theme.Faint, AutoSize = true, Location = new Point(14, 236), BackColor = Theme.Card });
        card.Controls.Add(new Label { Text = "Probably Stolen.exe", Font = Theme.Bold, ForeColor = Theme.Text, AutoSize = true, Location = new Point(14, 252), BackColor = Theme.Card });
        lblPid = new Label { Text = "PID —", Font = Theme.Small, ForeColor = Theme.Dim, AutoSize = true, Location = new Point(14, 272), BackColor = Theme.Card };
        card.Controls.Add(lblPid);

        string[,] stats = { { "cash", "Money" }, { "favor", "Wild favor" }, { "day", "Day" }, { "rent", "Rent due" }, { "aug", "Aug" }, { "evidence", "Evidence" } };
        for (int i = 0; i < stats.GetLength(0); i++)
        {
            var t = new StatTile(stats[i, 1]) { Location = new Point(14 + (i % 2) * 99, 298 + (i / 2) * 52), Size = new Size(95, 46) };
            tiles[stats[i, 0]] = t;
            card.Controls.Add(t);
        }

        devSwitch = new SwitchLabel("Game dev tools", "F8 console • F9 debug panel") { Location = new Point(14, 462), Width = 196 };
        devSwitch.Toggled += SetDevTools;
        card.Controls.Add(devSwitch);
        card.Controls.Add(new Label { Text = "takes effect on next game start", Font = Theme.Small, ForeColor = Theme.Faint, AutoSize = true, Location = new Point(58, 504), BackColor = Theme.Card });
    }

    private void BuildToggleCard(ref int x)
    {
        var card = new Card("Toggles", "  Num keys") { Location = new Point(x, Top), Size = new Size(372, BodyH) };
        Controls.Add(card);
        x += card.Width + Gap;
        int y = 48;
        AddToggle(card, "freeze_cash", "Freeze Money", "Num 1", Keys.NumPad1, ref y);
        AddToggle(card, "always_bargain", "Bargains Always Succeed", "Num 2", Keys.NumPad2, ref y);
        AddToggle(card, "patient_clients", "Customers Never Impatient", "Num 3", Keys.NumPad3, ref y);
        AddToggle(card, "no_heat", "No Heat / Evidence", "Num 4", Keys.NumPad4, ref y);
        AddToggle(card, "no_inspections", "No Inspections", "Num 5", Keys.NumPad5, ref y);
        AddToggle(card, "no_aug", "No Aug Presence", "Num 6", Keys.NumPad6, ref y);
        AddToggle(card, "no_rent", "Free Rent", "Num 7", Keys.NumPad7, ref y);
        AddToggle(card, "clean_store", "Clean Store", "Num 8", Keys.NumPad8, ref y);
        AddToggle(card, "perfect_health", "Perfect Health", "Num 9", Keys.NumPad9, ref y);
        AddToggle(card, "god_mode", "God Mode + No Thirst", "Num 0", Keys.NumPad0, ref y);
        AddToggle(card, "unlimited_scav", "Unlimited Scavenging", "Num .", Keys.Decimal, ref y);
        var speed = AddRow(card, new OptionRow(OptKind.Toggle, "speed", "Game Speed", "Num +", Keys.Add, false, EditKind.Stepper, 2.0)
            { Min = 0.5, Max = 5, Step = 0.5 }, ref y);
        speed.ValueChanged += r => { if (r.On) Send("speed|" + r.Value.ToString(CultureInfo.InvariantCulture)); };
    }

    private void BuildActionCard(ref int x)
    {
        var card = new Card("Instant Actions", "  Ctrl + Num") { Location = new Point(x, Top), Size = new Size(372, BodyH) };
        Controls.Add(card);
        x += card.Width + Gap;
        int y = 48;
        AddAction(card, "setcash", "Edit Money", "Ctrl+Num 1", Keys.NumPad1, ref y, EditKind.Number, 99999);
        AddAction(card, "addcash", "Add Money", "Ctrl+Num 2", Keys.NumPad2, ref y, EditKind.Number, 10000);
        AddAction(card, "setfavor", "Edit Wild Favor", "Ctrl+Num 3", Keys.NumPad3, ref y, EditKind.Number, 500);
        AddAction(card, "maxrep", "Max All Reputation", "Ctrl+Num 4", Keys.NumPad4, ref y);
        AddAction(card, "clearheat", "Clear Heat Now", "Ctrl+Num 5", Keys.NumPad5, ref y);
        AddAction(card, "clearaug", "Reset Aug Presence", "Ctrl+Num 6", Keys.NumPad6, ref y);
        AddAction(card, "resetrent", "Reset Rent Timer", "Ctrl+Num 7", Keys.NumPad7, ref y);
        AddAction(card, "cleanstore", "Clean Store Now", "Ctrl+Num 8", Keys.NumPad8, ref y);
        AddAction(card, "heal", "Heal Wounds", "Ctrl+Num 9", Keys.NumPad9, ref y);
        AddAction(card, "dismiss", "Dismiss Next Customer", "Ctrl+Num 0", Keys.NumPad0, ref y);
    }

    private OptionRow AddRow(Card card, OptionRow r, ref int y)
    {
        r.Location = new Point(8, y);
        r.Width = card.Width - 16;
        card.Controls.Add(r);
        rows.Add(r);
        r.Activated += Activate;
        y += 40;
        return r;
    }

    private void AddToggle(Card card, string id, string label, string hk, Keys key, ref int y)
        => toggles[id] = AddRow(card, new OptionRow(OptKind.Toggle, id, label, hk, key, false), ref y);

    private void AddAction(Card card, string cmd, string label, string hk, Keys key, ref int y, EditKind edit = EditKind.None, double value = 0)
        => AddRow(card, new OptionRow(OptKind.Action, cmd, label, hk, key, true, edit, value), ref y);

    private void BuildItemCard(ref int x)
    {
        int w = ClientSize.Width - Pad - x;
        var card = new Card("Item Spawner", "  516+ items") { Location = new Point(x, Top), Size = new Size(w, BodyH) };
        Controls.Add(card);
        int iw = w - 28;

        search = new FieldBox("Search items…") { Location = new Point(14, 48), Width = iw };
        search.Box.TextChanged += (_, _) => FilterItems();
        card.Controls.Add(search);

        var listFrame = new Card { Location = new Point(14, 88), Size = new Size(iw, 280), BackColor = Theme.Field };
        lstItems = new DarkList { Location = new Point(4, 6), Size = new Size(iw - 8, 268) };
        lstItems.Items.Add("Load a save first");
        lstItems.DoubleClick += (_, _) => SpawnSelected();
        listFrame.Controls.Add(lstItems);
        card.Controls.Add(listFrame);

        qty = new Stepper { Location = new Point(14, 378), Width = 96 };
        card.Controls.Add(qty);
        var spawn = new ModernButton("Spawn Item") { Location = new Point(118, 378), Width = iw - 104 };
        spawn.Click += (_, _) => SpawnSelected();
        card.Controls.Add(spawn);

        card.Controls.Add(new Label { Text = "ITEM KITS", Font = Theme.Small, ForeColor = Theme.Faint, AutoSize = true, Location = new Point(14, 428), BackColor = Theme.Card });
        cmbKit = new DarkCombo { Location = new Point(14, 448), Width = iw };
        cmbKit.Items.AddRange(new object[] { "Gun printing kit|gun", "Chemistry kit|chem", "Farming kit|farming", "Hydroponic kit|hydroponic", "Aquaculture kit|aquaculture", "Rat kit|rat", "Smuggler kit|smuggler", "Water testing kit|water", "Injectors|injectors", "Plasma|plasma", "Hypertech items|hypertech", "Lottery kit|lottery", "Broken machine kit|brokenmachine", "Music cassettes|cassettes", "Debug kit|debug" }
            .Select(s => (object)new KitItem(((string)s).Split('|')[0], ((string)s).Split('|')[1])).ToArray());
        cmbKit.SelectedIndex = 0;
        card.Controls.Add(cmbKit);
        var kit = new ModernButton("Spawn Kit", primary: false) { Location = new Point(14, 486), Width = iw };
        kit.Click += (_, _) => Send("kit|" + ((KitItem)cmbKit.SelectedItem).Id);
        card.Controls.Add(kit);
    }

    private sealed record KitItem(string Name, string Id) { public override string ToString() => Name; }

    private void BuildFooter()
    {
        int y = Top + BodyH + 12;
        Controls.Add(new Label
        {
            Text = "Ctrl+Shift+Home  disable / enable hotkeys     •     Num Lock must be ON     •     Toggles survive save loads",
            Location = new Point(Pad, y), AutoSize = true, ForeColor = Theme.Faint, Font = Theme.Small, BackColor = Color.Transparent
        });
        lblMessage = new Label
        {
            Text = "", Location = new Point(Pad, y + 22), Size = new Size(ClientSize.Width - 2 * Pad, 22),
            ForeColor = Theme.Accent, Font = Theme.Bold, BackColor = Color.Transparent
        };
        Controls.Add(lblMessage);
        var credit = new Label { Text = Theme.Brand + "  ©  SquamosS", AutoSize = true, ForeColor = Theme.Faint, Font = Theme.Small, BackColor = Color.Transparent };
        Controls.Add(credit);
        credit.Location = new Point(ClientSize.Width - Pad - credit.PreferredWidth, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        using (var glow = new LinearGradientBrush(new Rectangle(0, 0, ClientSize.Width, 3), Theme.Accent, Theme.Accent2, 0f))
            g.FillRectangle(glow, 0, 0, ClientSize.Width, 2);
        using var pen = new Pen(Theme.Line);
        g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    // ================================================================== behaviour
    private void Activate(OptionRow r)
    {
        if (r.Kind == OptKind.Toggle)
        {
            r.On = !r.On;
            if (r.Id == "speed") Send("speed|" + (r.On ? r.Value : 1.0).ToString(CultureInfo.InvariantCulture));
            else Send("toggle|" + r.Id + "|" + (r.On ? 1 : 0));
            Beep(r.On);
        }
        else
        {
            r.Flash();
            string cmd = r.Edit == EditKind.Number ? r.Id + "|" + ((long)r.Value).ToString(CultureInfo.InvariantCulture) : r.Id;
            Send(cmd);
            Beep(true);
        }
    }

    private static void Beep(bool up)
    {
        try { (up ? System.Media.SystemSounds.Asterisk : System.Media.SystemSounds.Hand).Play(); } catch { }
    }

    private void Send(string line)
    {
        if (!IsAttached()) { lblMessage.Text = "Game not attached yet — start Probably Stolen first."; return; }
        for (int i = 0; i < 10; i++)
        {
            try { File.AppendAllText(CmdFile, line + "\n"); return; }
            catch (IOException) { Thread.Sleep(15); }
        }
        lblMessage.Text = "Couldn't send command (file busy) — try again.";
    }

    private bool IsAttached() => game != null && !game.HasExited && File.Exists(InjectedFile) && ReadInjectedPid() == game.Id;

    private int ReadInjectedPid()
    {
        try { return int.Parse(File.ReadAllText(InjectedFile).Split('|')[0], CultureInfo.InvariantCulture); } catch { return -1; }
    }

    private void OnTick()
    {
        PollHotkeys();
        DateTime now = DateTime.UtcNow;
        if (now >= nextProcessCheck) { nextProcessCheck = now.AddSeconds(1); CheckProcess(); }
        if (now >= nextStatusRead) { nextStatusRead = now.AddMilliseconds(500); ReadStatus(); }
    }

    private void CheckProcess()
    {
        if (game != null && game.HasExited) { game.Dispose(); game = null; pushedState = false; }
        if (game == null)
        {
            game = Process.GetProcessesByName(ProcessName).FirstOrDefault();
            if (game == null)
            {
                lblPid.Text = "PID —";
                SetState("Waiting for game", Theme.Dim);
                ClearTiles();
                return;
            }
            try { devSwitch.On = File.Exists(Path.Combine(Path.GetDirectoryName(game.MainModule!.FileName)!, "dev.txt")); } catch { }
        }
        lblPid.Text = "PID " + game.Id;

        if (!IsAttached() && !injecting)
        {
            if ((DateTime.Now - game.StartTime).TotalSeconds < 8) { SetState("Game starting…", Theme.Dim); return; }
            Inject();
        }
    }

    private void Inject()
    {
        injecting = true;
        SetState("Attaching…", Theme.Warn);
        var target = game;
        Task.Run(() =>
        {
            try
            {
                byte[] payload;
                using (var s = Res("PSTrainerMod.dll")!) { payload = new byte[s.Length]; s.ReadExactly(payload); }
                foreach (var f in new[] { CmdFile, StatusFile }) if (File.Exists(f)) File.Delete(f);
                using var inj = new MonoInjector(target);
                inj.InjectAndRun(payload, "PSTrainer", "Loader", "Init", dataDir);
                File.WriteAllText(InjectedFile, target.Id + "|" + target.StartTime.Ticks);
                BeginInvoke(() => { SetState("Attached", Theme.On); lblMessage.Text = "Trainer attached to the game."; });
            }
            catch (Exception ex)
            {
                BeginInvoke(() => { SetState("Attach failed", Theme.Bad); lblMessage.Text = ex.Message; });
                Thread.Sleep(4000);
            }
            finally { injecting = false; }
        });
    }

    private void ReadStatus()
    {
        if (!IsAttached()) return;
        Dictionary<string, string> st;
        try
        {
            if (!File.Exists(StatusFile)) { if (++statusFailCount > 12) SetState("Attached — no reply yet", Theme.Warn); return; }
            st = File.ReadAllLines(StatusFile).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        }
        catch (IOException) { return; }
        statusFailCount = 0;

        bool alive = st.TryGetValue("alive", out var a) && long.TryParse(a, out long ticks) && (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds < 5;
        bool inGame = st.GetValueOrDefault("ingame") == "1";
        if (!alive) { SetState("Game paused / not responding", Theme.Warn); return; }
        SetState(inGame ? "Active — cheats running" : "Attached — load a save", inGame ? Theme.On : Theme.Warn);

        if (!pushedState)
        {
            pushedState = true;
            var speed = rows.First(r => r.Id == "speed");
            bool gameHasState = st.Any(kv => kv.Key.StartsWith("t.", StringComparison.Ordinal) && kv.Value == "1");
            if (gameHasState)
            {   // trainer was reopened while the game kept running: adopt the game's state
                foreach (var t in toggles.Values) t.On = st.GetValueOrDefault("t." + t.Id) == "1";
                if (double.TryParse(st.GetValueOrDefault("speed"), NumberStyles.Float, CultureInfo.InvariantCulture, out double sp) && sp != 1.0)
                { speed.On = true; speed.Value = sp; speed.Invalidate(); }
            }
            else
            {   // fresh game session: push whatever is switched on in the trainer
                foreach (var t in toggles.Values) if (t.On) Send("toggle|" + t.Id + "|1");
                if (speed.On) Send("speed|" + speed.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (st.TryGetValue("message", out var msg) && msg.Length > 0) lblMessage.Text = msg;
        if (inGame)
        {
            tiles["cash"].Value = Num(st.GetValueOrDefault("cash"));
            tiles["favor"].Value = Num(st.GetValueOrDefault("favor"));
            tiles["day"].Value = st.GetValueOrDefault("day") ?? "—";
            tiles["rent"].Value = Num(st.GetValueOrDefault("rent")) + " / " + st.GetValueOrDefault("rentdays") + "d";
            tiles["aug"].Value = st.GetValueOrDefault("aug") ?? "—";
            tiles["evidence"].Value = st.GetValueOrDefault("evidence") ?? "—";
        }
        else ClearTiles();

        LoadItemsIfChanged();
    }

    private static string Num(string s) => long.TryParse(s, out long v) ? v.ToString("N0", CultureInfo.InvariantCulture) : "—";

    private void ClearTiles() { foreach (var t in tiles.Values) t.Value = "—"; }

    private void SetState(string text, Color c) { pill.Set(text, c); PlacePill(); }

    // ------------------------------------------------------------------ items
    private void LoadItemsIfChanged()
    {
        if (!File.Exists(ItemsFile)) return;
        DateTime stamp = File.GetLastWriteTimeUtc(ItemsFile);
        if (stamp == itemsStamp) return;
        itemsStamp = stamp;
        try
        {
            allItems = File.ReadAllLines(ItemsFile).Where(l => l.Contains('|'))
                .Select(l => { var p = l.Split('|'); return p[1] + "   [" + p[0] + "]"; })
                .Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            FilterItems();
        }
        catch (IOException) { itemsStamp = default; }
    }

    private void FilterItems()
    {
        if (allItems.Count == 0) return;
        string q = search.Box.Text.Trim();
        lstItems.BeginUpdate();
        lstItems.Items.Clear();
        foreach (var s in allItems)
            if (q.Length == 0 || s.Contains(q, StringComparison.OrdinalIgnoreCase)) lstItems.Items.Add(s);
        lstItems.EndUpdate();
    }

    private void SpawnSelected()
    {
        if (lstItems.SelectedItem is not string s || !s.Contains("   [")) { lblMessage.Text = "Pick an item from the list first."; return; }
        string id = s.Substring(0, s.IndexOf("   [", StringComparison.Ordinal));
        Send("spawn|" + id + "|" + qty.Value);
    }

    // ------------------------------------------------------------------ hotkeys
    private void PollHotkeys()
    {
        bool ctrl = Down(Keys.ControlKey), shift = Down(Keys.ShiftKey);
        if (Edge(Keys.Home, ctrl && shift && Down(Keys.Home)))
        {
            hotkeysEnabled = !hotkeysEnabled;
            lblMessage.Text = hotkeysEnabled ? "Hotkeys enabled" : "Hotkeys disabled";
            Beep(hotkeysEnabled);
        }
        foreach (Keys k in rows.Select(r => r.Key).Distinct())
        {
            if (!Edge(k, Down(k)) || !hotkeysEnabled) continue;
            var row = rows.FirstOrDefault(r => r.Key == k && r.Ctrl == ctrl);
            if (row != null && !(shift && ctrl)) Activate(row);
        }
    }

    private bool Edge(Keys k, bool isDown)
    {
        if (isDown) return keysDown.Add(k);
        keysDown.Remove(k);
        return false;
    }

    private static bool Down(Keys k) => (GetAsyncKeyState((int)k) & 0x8000) != 0;

    // ------------------------------------------------------------------ dev tools marker
    private void SetDevTools(bool on)
    {
        if (game == null || game.HasExited) { devSwitch.On = !on; lblMessage.Text = "Start the game once so the trainer can find its folder."; return; }
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(game.MainModule!.FileName)!, "dev.txt");
            if (on && !File.Exists(path)) File.WriteAllText(path, "");
            if (!on && File.Exists(path)) File.Delete(path);
            lblMessage.Text = on ? "Game dev tools on — restart the game, then F9 = debug panel, F8 = console." : "Game dev tools off (after restart).";
        }
        catch (Exception ex) { lblMessage.Text = ex.Message; }
    }

    [DllImport("user32")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32")] private static extern bool ReleaseCapture();
    [DllImport("user32")] private static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);
    [DllImport("dwmapi")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    // ------------------------------------------------------------------ small header widgets
    private sealed class BadgeBox : Control
    {
        public BadgeBox() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); }
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.Clear(Theme.Bg); DrawBadge(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), 17f); }
    }

    private sealed class WindowGlyph : Control
    {
        private bool hover;
        private readonly bool danger;
        public WindowGlyph(string glyph, bool danger)
        {
            Text = glyph; this.danger = danger; Size = new Size(34, 30); Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            if (hover) Theme.Fill(g, new RectangleF(0, 0, Width - 1, Height - 1), 8, danger ? Color.FromArgb(200, 60, 70) : Theme.CardHi);
            TextRenderer.DrawText(g, Text, Theme.Bold, ClientRectangle, hover ? Color.White : Theme.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private sealed class CoverArt : Control
    {
        public Image Image { get; set; }
        public CoverArt() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Theme.Card);
            using var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), 10);
            if (Image != null)
            {
                g.SetClip(path);
                float scale = Math.Max((float)Width / Image.Width, (float)Height / Image.Height);
                float w = Image.Width * scale, h = Image.Height * scale;
                g.DrawImage(Image, (Width - w) / 2, (Height - h) / 2 - h * 0.08f, w, h);
                using (var fade = new LinearGradientBrush(new Rectangle(0, Height / 2, Width, Height / 2 + 1), Color.Transparent, Color.FromArgb(200, Theme.Card), 90f))
                    g.FillRectangle(fade, 0, Height / 2, Width, Height / 2 + 1);
                g.ResetClip();
            }
            using var pen = new Pen(Theme.Line);
            g.DrawPath(pen, path);
        }
    }
}
