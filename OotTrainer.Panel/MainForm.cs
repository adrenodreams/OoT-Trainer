using System.Drawing;
using System.Windows.Forms;

namespace OotTrainer.Panel;

/// <summary>
/// The panel window. Top: connection status and the master Activate button. Middle: one row per item with its live value,
/// a number box, a Set button (write once) and an Infinite checkbox (hold at the box number while Activated).
/// </summary>
public sealed class MainForm : Form
{
    private sealed class Row
    {
        public required ItemDef Def;
        public required Label Now;
        public required NumericUpDown Value;
        public required Button Set;
        public CheckBox? Infinite;
    }

    private const string CoffeeUrl = "https://buymeacoffee.com/adrenodreams";

    private readonly ITableSource _source;
    private readonly HoldEngine _engine = new();
    private readonly EponaCarrots _epona;
    private string _lastEponaStatus = "";
    private readonly Settings _settings;
    private readonly string _settingsPath;
    private readonly List<Row> _rows = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 25 };

    private readonly Label _status = new();
    private readonly Label _message = new();
    private readonly Button _activate = new();
    private int _tick;

    private static readonly Color Green = Color.FromArgb(46, 160, 67);
    private static readonly Color Orange = Color.FromArgb(214, 137, 16);
    private static readonly Color Red = Color.FromArgb(207, 34, 46);
    private static readonly Color Grey = Color.FromArgb(110, 118, 129);

    public MainForm(ITableSource source, Settings settings, string settingsPath, string title = "Hookshot item panel")
    {
        _source = source;
        _settings = settings;
        _settingsPath = settingsPath;
        _epona = new EponaCarrots(() => _source.Table);

        Text = title;
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        TopMost = settings.TopMost;

        BuildUi();
        ApplySettings();

        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        FormClosing += (_, _) => { _timer.Stop(); SaveSettings(); _epona.Dispose(); };
    }

    // ---------------------------------------------------------------- UI

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14, 12, 14, 12) };

        // status + master switch
        var top = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.Height = 44;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Font = new Font(Font, FontStyle.Bold);
        _activate.Text = "Activate";
        _activate.Size = new Size(190, 44);
        _activate.FlatStyle = FlatStyle.Flat;
        _activate.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        _activate.Click += (_, _) => SetActive(!_engine.Active);
        top.Controls.Add(_status, 0, 0);
        top.Controls.Add(_activate, 1, 0);
        root.Controls.Add(top);

        var hint = new Label
        {
            AutoSize = true,
            ForeColor = Grey,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 4, 0, 6),
            Text = "Activate turns the holds on or off. Tick Infinite on an item to hold it at the number in its box while Activated. " +
                   "Set writes the box number once, and the game then counts it down normally.",
        };
        root.Controls.Add(hint);

        // grid
        var grid = new TableLayoutPanel { ColumnCount = 5, AutoSize = true, Dock = DockStyle.Top };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));

        string? lastGroup = null;
        foreach (var def in ItemLayout.All)
        {
            if (def.Group != lastGroup)
            {
                lastGroup = def.Group;
                string headerText = def.Group switch
                {
                    ItemLayout.GroupLimits => "Limits (most you can carry)",
                    ItemLayout.GroupVitals => "Hearts and magic (Infinite on \"now\" keeps you full)",
                    ItemLayout.GroupHorse => "Epona (Infinite = endless sprint while riding)",
                    _ => "Items",
                };
                var header = new Label { Text = headerText, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 12, 0, 4) };
                grid.Controls.Add(header);
                grid.SetColumnSpan(header, 5);
                if (def.Group == ItemLayout.GroupItems)
                {
                    foreach (var (t, align) in new[] { ("", ContentAlignment.MiddleLeft), ("Now", ContentAlignment.MiddleRight), ("Value", ContentAlignment.MiddleLeft), ("", ContentAlignment.MiddleLeft), ("", ContentAlignment.MiddleLeft) })
                        grid.Controls.Add(new Label { Text = t, ForeColor = Grey, AutoSize = false, Width = 60, TextAlign = align, Dock = DockStyle.Fill });
                }
            }

            var name = new Label { Text = def.Label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 6) };
            var now = new Label { Text = "—", AutoSize = false, Width = 62, TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Left | AnchorStyles.Right, ForeColor = Grey };
            var nud = new NumericUpDown { Minimum = 0, Maximum = def.Max, Value = Math.Min(def.Default, def.Max), Width = 88, TextAlign = HorizontalAlignment.Right };
            var set = new Button { Text = "Set", Width = 56, Enabled = false };
            CheckBox? inf = def.CanHold ? new CheckBox { Text = "Infinite", AutoSize = true, Anchor = AnchorStyles.Left } : null;

            var row = new Row { Def = def, Now = now, Value = nud, Set = set, Infinite = inf };
            _rows.Add(row);

            nud.TextChanged += (_, _) => { if (int.TryParse(nud.Text, out int v) && v >= nud.Minimum && v <= nud.Maximum && v != nud.Value) nud.Value = v; };
            nud.ValueChanged += (_, _) => SyncHold(row);
            nud.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; WriteOnce(row); } };
            set.Click += (_, _) => WriteOnce(row);
            if (inf is not null) inf.CheckedChanged += (_, _) => SyncHold(row);

            grid.Controls.Add(name);
            grid.Controls.Add(now);
            grid.Controls.Add(nud);
            grid.Controls.Add(set);
            grid.Controls.Add(inf ?? (Control)new Label { AutoSize = true });
        }
        root.Controls.Add(grid);

        // bulk buttons
        var bulk = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        var setAll = new Button { Text = "Set all items now", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        var allOn = new Button { Text = "Infinite: all", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        var allOff = new Button { Text = "Infinite: none", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        var onTop = new CheckBox { Text = "Always on top", AutoSize = true, Margin = new Padding(16, 7, 0, 0), Checked = TopMost };
        setAll.Click += (_, _) => { foreach (var r in _rows.Where(r => r.Def.Group == ItemLayout.GroupItems)) WriteOnce(r, quiet: true); Say("Set all items to the numbers in their boxes."); };
        allOn.Click += (_, _) => { foreach (var r in _rows) if (r.Infinite is not null) r.Infinite.Checked = true; };
        allOff.Click += (_, _) => { foreach (var r in _rows) if (r.Infinite is not null) r.Infinite.Checked = false; };
        onTop.CheckedChanged += (_, _) => TopMost = onTop.Checked;
        bulk.Controls.AddRange([setAll, allOn, allOff, onTop]);
        root.Controls.Add(bulk);

        _message.AutoSize = true;
        _message.ForeColor = Grey;
        _message.MaximumSize = new Size(560, 0);
        _message.Margin = new Padding(0, 10, 0, 0);
        _message.Text = "The game may cap a number at the item's limit. Raise the limit first if a value does not stick.";
        root.Controls.Add(_message);

        // support link, bottom right
        var coffee = new LinkLabel { Text = "Buy me a coffee", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        coffee.LinkClicked += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CoffeeUrl) { UseShellExecute = true }); }
            catch { Say("Could not open the browser. The link is " + CoffeeUrl); }
        };
        root.Controls.Add(coffee);

        Controls.Add(root);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        SetActive(false);
    }

    private void SetActive(bool on)
    {
        _engine.Active = on;
        _epona.Active = on;
        _activate.Text = on ? "Deactivate" : "Activate";
        _activate.BackColor = on ? Green : SystemColors.Control;
        _activate.ForeColor = on ? Color.White : SystemColors.ControlText;
        Say(on ? "Active: items ticked Infinite are held." : "Inactive: nothing is being held.");
    }

    private void Say(string text) => _message.Text = text;

    // ---------------------------------------------------------------- behaviour

    /// <summary>The box number in the game's own units (hearts are stored as quarter hearts).</summary>
    private static int Raw(Row r) => (int)Math.Round(r.Value.Value * r.Def.Scale);

    /// <summary>A raw game number as shown in the window: whole numbers, or hearts with quarters ("3.25").</summary>
    private static string Display(ItemDef d, int raw) => d.Scale == 1 ? raw.ToString() : (raw / (double)d.Scale).ToString("0.##");

    private void SyncHold(Row r)
    {
        bool hold = r.Infinite?.Checked == true;
        if (r.Def.ByteOffset is not null) _epona.Target = hold ? Raw(r) : null; // Epona's carrots have their own fast holder
        else _engine.Set(r.Def.Key, hold ? Raw(r) : null);
    }

    private void WriteOnce(Row r, bool quiet = false)
    {
        var table = _source.Table;
        if (table is null) { Say("Not connected to the game's item table yet."); return; }
        int v = Raw(r);
        bool ok = r.Def.ByteOffset is not null && _epona.TrySetNow(v) // the real counter, if found; the copy alone is overwritten by the game
                  || table.TryWriteValue(r.Def, v);
        if (!quiet) Say(ok ? $"Set {r.Def.Label} to {Display(r.Def, v)}." : $"Could not write {r.Def.Label} (the table may have moved).");
    }

    private void OnTick()
    {
        var table = _source.Table;
        _engine.Tick(table);

        if (++_tick % 8 != 0) return; // refresh the display about 5 times a second
        _status.Text = (_source.Kind == StatusKind.Ready ? "● " : "○ ") + _source.Status;
        _status.ForeColor = _source.Kind switch { StatusKind.Ready => Green, StatusKind.Working => Orange, StatusKind.Error => Red, _ => Grey };

        string ep = _epona.Status;
        if (ep != _lastEponaStatus && ep.Length > 0) { _lastEponaStatus = ep; Say(ep); }

        foreach (var r in _rows)
        {
            r.Set.Enabled = table is not null;
            if (table is not null && table.TryReadValue(r.Def, out int v)) { r.Now.Text = Display(r.Def, v); r.Now.ForeColor = ForeColor; }
            else { r.Now.Text = "—"; r.Now.ForeColor = Grey; }
        }
    }

    // ---------------------------------------------------------------- settings

    private void ApplySettings()
    {
        foreach (var r in _rows)
        {
            if (_settings.Values.TryGetValue(r.Def.Key, out int v)) r.Value.Value = Math.Clamp(Math.Round((decimal)v / r.Def.Scale), 0m, r.Def.Max); // settings hold game units
            if (r.Infinite is not null && _settings.Infinite.TryGetValue(r.Def.Key, out bool on)) r.Infinite.Checked = on;
            SyncHold(r);
        }
    }

    private void SaveSettings()
    {
        foreach (var r in _rows)
        {
            _settings.Values[r.Def.Key] = Raw(r);
            if (r.Infinite is not null) _settings.Infinite[r.Def.Key] = r.Infinite.Checked;
        }
        _settings.TopMost = TopMost;
        _settings.Save(_settingsPath);
    }

    private static IEnumerable<Control> Descendants(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(Descendants(x)));

    /// <summary>Drive the real buttons and boxes against the synthetic game (used by --uitest). Proves the window's wiring, not the real offsets.</summary>
    internal async Task<List<(string Name, bool Pass, string Detail)>> RunUiChecks(SyntheticGame game)
    {
        var res = new List<(string, bool, string)>();
        void C(string n, bool p, string d = "") => res.Add((n, p, d));
        Row R(string key) => _rows.First(r => r.Def.Key == key);
        Button B(string text) => Descendants(this).OfType<Button>().First(b => b.Text == text);

        await Task.Delay(500); // let the tick enable the Set buttons and fill the Now column
        C("Now column shows the game's value", R("bombs").Now.Text == "12", $"now={R("bombs").Now.Text}");
        C("Set buttons are enabled once connected", R("bombs").Set.Enabled);

        R("bombs").Value.Value = 50;
        R("bombs").Set.PerformClick();
        C("Set writes the box number", game.Read("bombs") == 50, $"bombs={game.Read("bombs")}");

        R("bombs").Value.Text = "77"; // typing, without leaving the box
        R("bombs").Set.PerformClick();
        C("a typed number is used without leaving the box", game.Read("bombs") == 77, $"bombs={game.Read("bombs")}");

        R("seeds").Value.Value = 99;
        R("seeds").Infinite!.Checked = true;
        await Task.Delay(200);
        C("Infinite does nothing before Activate", game.Read("seeds") == 40, $"seeds={game.Read("seeds")}");
        B("Activate").PerformClick();
        await Task.Delay(250);
        C("Activate holds the item", game.Read("seeds") == 99 && _engine.Active, $"seeds={game.Read("seeds")}");
        game.GameSets("seeds", 98);
        await Task.Delay(250);
        C("a used item is topped back up", game.Read("seeds") == 99, $"seeds={game.Read("seeds")}");
        R("seeds").Value.Value = 60;
        await Task.Delay(250);
        C("changing the box changes the held number", game.Read("seeds") == 60, $"seeds={game.Read("seeds")}");
        R("seeds").Infinite!.Checked = false;
        game.GameSets("seeds", 10);
        await Task.Delay(250);
        C("unticking Infinite releases the item", game.Read("seeds") == 10, $"seeds={game.Read("seeds")}");

        B("Infinite: all").PerformClick();
        game.GameSets("nuts", 1);
        await Task.Delay(250);
        C("Infinite: all holds every item", game.Read("nuts") == 99, $"nuts={game.Read("nuts")}");
        B("Deactivate").PerformClick();
        game.GameSets("nuts", 4);
        await Task.Delay(250);
        C("Deactivate stops the holds", game.Read("nuts") == 4 && !_engine.Active, $"nuts={game.Read("nuts")}");
        B("Infinite: none").PerformClick();

        R("wallet").Value.Value = 999;
        R("wallet").Set.PerformClick();
        C("limits can be set too", game.Read("wallet") == 999, $"wallet={game.Read("wallet")}");
        B("Set all items now").PerformClick();
        C("Set all items now writes every item box", game.Read("rupees") == 1000 && game.Read("skulltula") == 999 && game.Read("bombchu") == 99 && game.Read("beans") == 10,
            $"rupees={game.Read("rupees")} skulltula={game.Read("skulltula")} chu={game.Read("bombchu")} beans={game.Read("beans")}");

        // health / magic: Infinite means "stay at the live maximum"
        game.GameSets("max_health", 24); game.GameSets("max_magic", 240);
        R("health").Infinite!.Checked = true;
        R("magic").Infinite!.Checked = true;
        B("Activate").PerformClick();
        game.GameSets("health", 8); game.GameSets("magic", 30);
        await Task.Delay(250);
        C("health Infinite refills to max health", game.Read("health") == 24, $"health={game.Read("health")}");
        C("magic Infinite refills to max magic", game.Read("magic") == 240, $"magic={game.Read("magic")}");
        game.GameSets("max_health", 32);
        await Task.Delay(250);
        C("full follows a raised max health", game.Read("health") == 32, $"health={game.Read("health")}");
        R("max_health").Value.Value = 10; // hearts
        R("max_health").Set.PerformClick();
        await Task.Delay(250);
        C("max hearts box is in hearts (10 hearts = 40 raw)", game.Read("max_health") == 40 && game.Read("health") == 40, $"max={game.Read("max_health")} health={game.Read("health")}");
        C("Now column shows hearts, not quarter hearts", R("max_health").Now.Text == "10" && R("health").Now.Text == "10", $"max={R("max_health").Now.Text} now={R("health").Now.Text}");
        B("Deactivate").PerformClick();
        game.GameSets("health", 14);
        await Task.Delay(300);
        C("quarter hearts show as decimals (14 raw = 3.5 hearts)", R("health").Now.Text == "3.5", $"now={R("health").Now.Text}");

        // Epona's carrots: held on the real counter, found by itself
        _epona.HitFilter = a => a >= SyntheticGame.G && a < SyntheticGame.G + 0x10000;
        R("carrots").Infinite!.Checked = true;
        B("Activate").PerformClick();
        for (int i = 0; i < 40 && !_epona.Found; i++) await Task.Delay(100);
        C("Epona's real carrot counter is found", _epona.Found, _epona.Status);
        game.RealCarrots = 1; // sprinting
        await Task.Delay(300);
        C("Epona carrots Infinite refills the real counter", game.RealCarrots == 6, $"real={game.RealCarrots}");
        B("Deactivate").PerformClick();
        return res.ToList();
    }

    /// <summary>Render the window to an image (used by --screenshot to check the layout without the game).</summary>
    public void SaveScreenshot(string path)
    {
        using var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
