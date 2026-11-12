using System.Drawing.Drawing2D;
using System.Globalization;

namespace PSTrainerApp;

internal static class Theme
{
    public const string Brand = "SquamosS Trainer";

    public static readonly Color Bg = Color.FromArgb(13, 15, 20);
    public static readonly Color Card = Color.FromArgb(22, 25, 33);
    public static readonly Color CardHi = Color.FromArgb(30, 34, 45);
    public static readonly Color Field = Color.FromArgb(16, 18, 25);
    public static readonly Color Line = Color.FromArgb(40, 45, 60);
    public static readonly Color Text = Color.FromArgb(236, 239, 245);
    public static readonly Color Dim = Color.FromArgb(138, 146, 166);
    public static readonly Color Faint = Color.FromArgb(88, 95, 115);
    public static readonly Color Accent = Color.FromArgb(255, 170, 60);
    public static readonly Color Accent2 = Color.FromArgb(255, 96, 120);
    public static readonly Color On = Color.FromArgb(52, 211, 153);
    public static readonly Color Warn = Color.FromArgb(251, 191, 36);
    public static readonly Color Bad = Color.FromArgb(248, 113, 113);

    private static readonly string Ui = PickFont("Segoe UI Variable Text", "Segoe UI");
    private static readonly string UiDisplay = PickFont("Segoe UI Variable Display", "Segoe UI");
    public static readonly Font Body = new(Ui, 9.25f);
    public static readonly Font Small = new(Ui, 8.25f);
    public static readonly Font Bold = new(UiDisplay, 9.25f, FontStyle.Bold);
    public static readonly Font Key = new(UiDisplay, 8f, FontStyle.Bold);
    public static readonly Font CardTitle = new(UiDisplay, 10.5f, FontStyle.Bold);
    public static readonly Font Title = new(UiDisplay, 17f, FontStyle.Bold);
    public static readonly Font Sub = new(Ui, 9f);
    public static readonly Font StatValue = new(UiDisplay, 11.5f, FontStyle.Bold);

    private static string PickFont(string want, string fallback)
    {
        using var f = new Font(want, 9f);
        return f.Name == want ? want : fallback;
    }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Fill(Graphics g, RectangleF r, float rad, Color c)
    {
        using var p = Round(r, rad);
        using var b = new SolidBrush(c);
        g.FillPath(b, p);
    }

    public static void Stroke(Graphics g, RectangleF r, float rad, Color c, float w = 1f)
    {
        using var p = Round(r, rad);
        using var pen = new Pen(c, w);
        g.DrawPath(pen, p);
    }

    public static Color Blend(Color a, Color b, float t) =>
        Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}

/// <summary>Base for flicker-free custom-painted controls.</summary>
internal abstract class Painted : Control
{
    protected Painted()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
    }

    protected Color ParentColor => Parent?.BackColor ?? Theme.Bg;
}

/// <summary>Rounded card with an optional title and subtitle.</summary>
internal sealed class Card : Panel
{
    public string Title { get; set; }
    public string Subtitle { get; set; }

    public Card(string title = null, string subtitle = null)
    {
        Title = title; Subtitle = subtitle;
        BackColor = Theme.Card;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.Fill(g, r, 12, Theme.Card);
        Theme.Stroke(g, r, 12, Theme.Line);
        if (Title != null)
        {
            using (var b = new LinearGradientBrush(new Rectangle(16, 18, 4, 16), Theme.Accent, Theme.Accent2, 90f))
                g.FillRectangle(b, 16, 17, 3, 16);
            TextRenderer.DrawText(g, Title, Theme.CardTitle, new Point(26, 14), Theme.Text);
            if (Subtitle != null)
            {
                int w = TextRenderer.MeasureText(Title, Theme.CardTitle).Width;
                TextRenderer.DrawText(g, Subtitle, Theme.Small, new Point(26 + w + 2, 17), Theme.Faint);
            }
        }
    }
}

internal enum OptKind { Toggle, Action }
internal enum EditKind { None, Number, Stepper }

/// <summary>A cheat row: [keycap]  Label  [value]  ............  (switch | run button)</summary>
internal sealed class OptionRow : Painted
{
    public OptKind Kind { get; }
    public string Id { get; }
    public string HotkeyText { get; }
    public Keys Key { get; }
    public bool Ctrl { get; }
    public EditKind Edit { get; }
    public double Value { get; set; }
    public double Step { get; set; } = 0.5;
    public double Min { get; set; } = 0;
    public double Max { get; set; } = 999999999;

    private bool on, hover;
    private float anim, flash;
    private readonly TextBox box;
    private Rectangle leftArrow, rightArrow, editRect;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    private const int KeyW = 86;

    public event Action<OptionRow> Activated;
    public event Action<OptionRow> ValueChanged;

    public bool On { get => on; set { if (on != value) { on = value; timer.Start(); } } }

    public OptionRow(OptKind kind, string id, string label, string hotkey, Keys key, bool ctrl, EditKind edit = EditKind.None, double value = 0)
    {
        Kind = kind; Id = id; Text = label; HotkeyText = hotkey; Key = key; Ctrl = ctrl; Edit = edit; Value = value;
        Height = 36;
        Cursor = Cursors.Hand;
        timer.Tick += (_, _) =>
        {
            float target = on ? 1 : 0;
            anim += (target - anim) * 0.3f;
            if (flash > 0) flash = Math.Max(0, flash - 0.04f);
            if (Math.Abs(target - anim) < 0.01f && flash <= 0) { anim = target; timer.Stop(); }
            Invalidate();
        };

        if (edit == EditKind.Number)
        {
            box = new TextBox
            {
                Text = ((long)value).ToString(CultureInfo.InvariantCulture), BorderStyle = BorderStyle.None,
                BackColor = Theme.Field, ForeColor = Theme.Accent, Font = Theme.Bold, TextAlign = HorizontalAlignment.Center, Width = 78
            };
            box.KeyPress += (_, e) => { if (!char.IsDigit(e.KeyChar) && e.KeyChar != '\b') e.Handled = true; };
            box.TextChanged += (_, _) =>
            {
                if (double.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out double v))
                { Value = Math.Clamp(v, Min, Max); ValueChanged?.Invoke(this); }
            };
            Controls.Add(box);
        }
    }

    public void Flash() { flash = 1f; timer.Start(); }

    private int LabelRight => KeyW + 14 + TextRenderer.MeasureText(Text, Theme.Body).Width;

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (box != null)
        {
            editRect = new Rectangle(LabelRight + 10, 6, box.Width + 16, Height - 12);
            box.Location = new Point(editRect.X + 8, editRect.Y + (editRect.Height - box.Height) / 2 + 1);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (Edit == EditKind.Stepper && leftArrow.Contains(e.Location)) { Nudge(-1); return; }
        if (Edit == EditKind.Stepper && rightArrow.Contains(e.Location)) { Nudge(+1); return; }
        if (Edit == EditKind.Number && editRect.Contains(e.Location)) { box.Focus(); return; }
        Activated?.Invoke(this);
        base.OnMouseClick(e);
    }

    private void Nudge(int dir)
    {
        Value = Math.Clamp(Math.Round(Value + dir * Step, 2), Min, Max);
        Invalidate();
        ValueChanged?.Invoke(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(ParentColor);
        var row = new RectangleF(0, 1, Width - 1, Height - 2);
        Color rowBg = Theme.Blend(hover ? Theme.CardHi : ParentColor, Color.FromArgb(40, Theme.On), Math.Max(anim * 0.6f, flash));
        Theme.Fill(g, row, 8, rowBg);

        // keycap
        var key = new RectangleF(8, (Height - 22) / 2f, KeyW - 8, 22);
        Theme.Fill(g, new RectangleF(key.X, key.Y + 2, key.Width, key.Height), 6, Color.FromArgb(8, 9, 12));
        Theme.Fill(g, key, 6, Theme.Field);
        Theme.Stroke(g, key, 6, Theme.Line);
        TextRenderer.DrawText(g, HotkeyText, Theme.Key, Rectangle.Round(key), on ? Theme.On : Theme.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        // label
        Color labelColor = Theme.Blend(Theme.Text, Theme.On, Math.Max(anim, flash));
        TextRenderer.DrawText(g, Text, Theme.Body, new Rectangle(KeyW + 12, 0, LabelRight - KeyW - 8, Height), labelColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // inline editors
        if (Edit == EditKind.Number)
        {
            Theme.Fill(g, editRect, 7, Theme.Field);
            Theme.Stroke(g, editRect, 7, box.Focused ? Theme.Accent : Theme.Line);
        }
        else if (Edit == EditKind.Stepper)
        {
            string v = "x" + Value.ToString("0.0#", CultureInfo.InvariantCulture);
            int vw = Math.Max(44, TextRenderer.MeasureText(v, Theme.Bold).Width + 12);
            var r = new Rectangle(LabelRight + 10, 6, vw + 48, Height - 12);
            Theme.Fill(g, r, 7, Theme.Field);
            Theme.Stroke(g, r, 7, Theme.Line);
            leftArrow = new Rectangle(r.X, r.Y, 24, r.Height);
            rightArrow = new Rectangle(r.Right - 24, r.Y, 24, r.Height);
            TextRenderer.DrawText(g, "‹", Theme.CardTitle, leftArrow, Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, v, Theme.Bold, new Rectangle(r.X + 24, r.Y, vw, r.Height), Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "›", Theme.CardTitle, rightArrow, Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // right side: switch or run button
        if (Kind == OptKind.Toggle)
        {
            var sw = new RectangleF(Width - 52, (Height - 20) / 2f, 40, 20);
            Theme.Fill(g, sw, 10, Theme.Blend(Theme.Line, Theme.On, anim));
            float kx = sw.X + 3 + anim * (sw.Width - 20);
            using var kb = new SolidBrush(Color.White);
            g.FillEllipse(kb, kx, sw.Y + 3, 14, 14);
        }
        else
        {
            var btn = new RectangleF(Width - 46, (Height - 24) / 2f, 28, 24);
            Theme.Fill(g, btn, 7, Theme.Blend(hover ? Theme.Line : Theme.Field, Theme.On, flash));
            using var b = new SolidBrush(flash > 0 ? Color.White : Theme.Accent);
            float cx = btn.X + btn.Width / 2 + 1, cy = btn.Y + btn.Height / 2;
            g.FillPolygon(b, new[] { new PointF(cx - 4, cy - 5), new PointF(cx + 5, cy), new PointF(cx - 4, cy + 5) });
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}

/// <summary>Gradient (primary) or outline rounded button.</summary>
internal sealed class ModernButton : Painted
{
    private bool hover, down;
    public bool Primary { get; set; }

    public ModernButton(string text, bool primary = true)
    {
        Text = text; Primary = primary; Height = 32; Cursor = Cursors.Hand; Font = Theme.Bold;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(ParentColor);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        if (Primary)
        {
            using var path = Theme.Round(r, 8);
            Color a = Theme.Accent, b = Theme.Accent2;
            if (hover) { a = ControlPaint.Light(a, 0.2f); b = ControlPaint.Light(b, 0.2f); }
            if (down) { a = ControlPaint.Dark(a, 0.05f); b = ControlPaint.Dark(b, 0.05f); }
            using var br = new LinearGradientBrush(Rectangle.Round(r), a, b, 0f);
            g.FillPath(br, path);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, Color.FromArgb(20, 14, 10), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else
        {
            Theme.Fill(g, r, 8, hover ? Theme.CardHi : Theme.Field);
            Theme.Stroke(g, r, 8, hover ? Theme.Accent : Theme.Line);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}

/// <summary>Status badge: coloured dot + text inside a pill.</summary>
internal sealed class StatusPill : Painted
{
    private Color color = Theme.Dim;
    public StatusPill() { Height = 28; Font = Theme.Bold; }

    public void Set(string text, Color c)
    {
        if (Text == text && color == c) return;
        Text = text; color = c;
        Width = TextRenderer.MeasureText(text, Font).Width + 40;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(ParentColor);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.Fill(g, r, Height / 2f, Color.FromArgb(28, color));
        Theme.Stroke(g, r, Height / 2f, Color.FromArgb(90, color));
        using (var glow = new SolidBrush(Color.FromArgb(70, color))) g.FillEllipse(glow, 9, Height / 2f - 7, 14, 14);
        using (var dot = new SolidBrush(color)) g.FillEllipse(dot, 12, Height / 2f - 4, 8, 8);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(28, 0, Width - 32, Height), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>Small stat tile: LABEL / value.</summary>
internal sealed class StatTile : Painted
{
    private string value = "—";
    public string Label { get; }
    public StatTile(string label) { Label = label; Size = new Size(86, 46); }
    public string Value { get => value; set { if (this.value != value) { this.value = value; Invalidate(); } } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(ParentColor);
        Theme.Fill(g, new RectangleF(0, 0, Width - 1, Height - 1), 8, Theme.Field);
        TextRenderer.DrawText(g, Label.ToUpperInvariant(), Theme.Small, new Rectangle(9, 5, Width - 12, 16), Theme.Faint, TextFormatFlags.Left);
        TextRenderer.DrawText(g, value, Theme.StatValue, new Rectangle(9, 20, Width - 12, 22), Theme.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Rounded search/text field wrapper.</summary>
internal sealed class FieldBox : Painted
{
    public readonly TextBox Box;
    public FieldBox(string placeholder)
    {
        Height = 32;
        Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Field, ForeColor = Theme.Text, Font = Theme.Body, PlaceholderText = placeholder };
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Controls.Add(Box);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Box?.SetBounds(30, (Height - Box.Height) / 2 + 1, Width - 40, Box.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(ParentColor);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.Fill(g, r, 8, Theme.Field);
        Theme.Stroke(g, r, 8, Box.Focused ? Theme.Accent : Theme.Line);
        using var pen = new Pen(Theme.Dim, 1.6f);
        g.DrawEllipse(pen, 10, Height / 2f - 6, 10, 10);
        g.DrawLine(pen, 18.5f, Height / 2f + 2.5f, 22, Height / 2f + 6);
    }
}

/// <summary>Small "−  N  +" stepper.</summary>
internal sealed class Stepper : Painted
{
    private int value = 1;
    public int Minimum { get; set; } = 1;
    public int Maximum { get; set; } = 100;
    public int Value { get => value; set { this.value = Math.Clamp(value, Minimum, Maximum); Invalidate(); } }
    public Stepper() { Size = new Size(104, 32); Cursor = Cursors.Hand; }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int step = ModifierKeys.HasFlag(Keys.Shift) ? 10 : 1;
        if (e.X < 32) Value -= step; else if (e.X > Width - 32) Value += step;
        base.OnMouseClick(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e) { Value += e.Delta > 0 ? 1 : -1; base.OnMouseWheel(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(ParentColor);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.Fill(g, r, 8, Theme.Field);
        Theme.Stroke(g, r, 8, Theme.Line);
        TextRenderer.DrawText(g, "−", Theme.CardTitle, new Rectangle(0, 0, 32, Height), Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "+", Theme.CardTitle, new Rectangle(Width - 32, 0, 32, Height), Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, value.ToString(CultureInfo.InvariantCulture), Theme.Bold, new Rectangle(32, 0, Width - 64, Height), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>Pill switch with a caption (used for the dev-tools option).</summary>
internal sealed class SwitchLabel : Painted
{
    private bool on;
    public string Caption { get; set; }
    public string Hint { get; set; }
    public event Action<bool> Toggled;
    public SwitchLabel(string caption, string hint) { Caption = caption; Hint = hint; Height = 40; Cursor = Cursors.Hand; }
    public bool On { get => on; set { if (on != value) { on = value; Invalidate(); } } }

    protected override void OnMouseClick(MouseEventArgs e) { On = !On; Toggled?.Invoke(On); base.OnMouseClick(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(ParentColor);
        var sw = new RectangleF(0, 4, 36, 18);
        Theme.Fill(g, sw, 9, on ? Theme.On : Theme.Line);
        using (var kb = new SolidBrush(Color.White)) g.FillEllipse(kb, on ? sw.Right - 15 : sw.X + 3, sw.Y + 3, 12, 12);
        TextRenderer.DrawText(g, Caption, Theme.Body, new Point(44, 3), Theme.Text);
        TextRenderer.DrawText(g, Hint, Theme.Small, new Point(44, 21), Theme.Faint);
    }
}

/// <summary>Dark owner-drawn list.</summary>
internal sealed class DarkList : ListBox
{
    public DarkList()
    {
        DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = 26; BorderStyle = BorderStyle.None;
        BackColor = Theme.Field; ForeColor = Theme.Text; Font = Theme.Body; IntegralHeight = false;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(Theme.Field)) g.FillRectangle(bg, e.Bounds);
        if (sel) Theme.Fill(g, new RectangleF(e.Bounds.X + 2, e.Bounds.Y + 1, e.Bounds.Width - 4, e.Bounds.Height - 2), 6, Color.FromArgb(60, Theme.Accent));
        string s = Items[e.Index].ToString();
        int cut = s.IndexOf("   [", StringComparison.Ordinal);
        string name = cut > 0 ? s[..cut] : s;
        string cat = cut > 0 ? s[(cut + 4)..].TrimEnd(']') : "";
        TextRenderer.DrawText(g, name, Font, new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 100, e.Bounds.Height), sel ? Theme.Accent : Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (cat.Length > 0)
            TextRenderer.DrawText(g, cat, Theme.Small, new Rectangle(e.Bounds.Right - 96, e.Bounds.Y, 88, e.Bounds.Height), Theme.Faint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Owner-drawn dark combo box.</summary>
internal sealed class DarkCombo : ComboBox
{
    public DarkCombo()
    {
        DrawMode = DrawMode.OwnerDrawFixed; DropDownStyle = ComboBoxStyle.DropDownList; FlatStyle = FlatStyle.Flat;
        BackColor = Theme.Field; ForeColor = Theme.Text; Font = Theme.Body; ItemHeight = 24;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using (var bg = new SolidBrush(sel ? Theme.CardHi : Theme.Field)) e.Graphics.FillRectangle(bg, e.Bounds);
        TextRenderer.DrawText(e.Graphics, Items[e.Index].ToString(), Font, new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height),
            sel ? Theme.Accent : Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }
}
