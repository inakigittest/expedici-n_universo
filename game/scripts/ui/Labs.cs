using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AresX.AI;
using AresX.Core;
using AresX.Sim;
using Godot;

namespace AresX.UI;

// ====================================================================== controles de dibujo
public partial class LineChart : Control
{
    public string Title = "";
    public readonly List<(string Name, IReadOnlyList<float> V, Color C)> Series = new();
    public int Marker = -1;
    public string XLabel = "";
    public float? FixedMin, FixedMax;

    public override void _Draw()
    {
        var r = new Rect2(36, 18, Size.X - 44, Size.Y - 38);
        Gfx.Frame(this, r);
        Gfx.Text(this, new Vector2(0, 0), Title, 12, UiTheme.Accent);
        var all = Series.SelectMany(s => s.V).Where(float.IsFinite).ToList();
        if (all.Count == 0) return;
        float min = FixedMin ?? all.Min(), max = FixedMax ?? all.Max();
        if (max - min < 1e-6f) { max += 0.5f; min -= 0.5f; }
        for (int g = 0; g <= 4; g++)
        {
            float y = r.End.Y - r.Size.Y * g / 4;
            DrawLine(new Vector2(r.Position.X, y), new Vector2(r.End.X, y), new Color(1, 1, 1, 0.05f));
            Gfx.Text(this, new Vector2(0, y - 7), (min + (max - min) * g / 4).ToString("0.00"), 9, UiTheme.Dim, 34, HorizontalAlignment.Right);
        }
        int n = Series.Max(s => s.V.Count);
        foreach (var (_, v, c) in Series) Gfx.Series(this, r, v, min, max, c, 2f);
        if (Marker >= 0 && n > 1)
        {
            float x = r.Position.X + r.Size.X * Marker / (n - 1);
            DrawLine(new Vector2(x, r.Position.Y), new Vector2(x, r.End.Y), Colors.White, 1.5f);
        }
        float lx = r.Position.X + 4;
        foreach (var (name, _, c) in Series)
        {
            DrawRect(new Rect2(lx, r.End.Y + 8, 10, 3), c);
            Gfx.Text(this, new Vector2(lx + 13, r.End.Y + 3), name, 10, c);
            lx += Gfx.TextWidth(name, 10) + 28;
        }
        Gfx.Text(this, new Vector2(0, r.End.Y + 3), XLabel, 10, UiTheme.Dim, Size.X - 6, HorizontalAlignment.Right);
    }
}

public partial class ConfusionView : Control
{
    public ClassMetrics? M;
    public string[] Names = NeuralPerception.ClassNames;

    public override void _Draw()
    {
        if (M == null) return;
        Gfx.Text(this, Vector2.Zero, "Confusión (prueba) · fila = verdad", 11, UiTheme.Accent);
        float cs = MathF.Min((Size.X - 80) / 3, (Size.Y - 40) / 3);
        for (int i = 0; i < 3; i++)
        {
            int rowSum = Enumerable.Range(0, 3).Sum(j => M.Confusion[i, j]);
            Gfx.Text(this, new Vector2(0, 38 + i * cs + cs / 2 - 8), Names[i], 11, UiTheme.Text);
            Gfx.Text(this, new Vector2(78 + i * cs, 18), Names[i], 10, UiTheme.Dim, cs, HorizontalAlignment.Center);
            for (int j = 0; j < 3; j++)
            {
                float frac = rowSum == 0 ? 0 : (float)M.Confusion[i, j] / rowSum;
                var r = new Rect2(78 + j * cs, 34 + i * cs, cs - 3, cs - 3);
                DrawRect(r, (i == j ? UiTheme.Rna : UiTheme.Crit) * new Color(1, 1, 1, 0.12f + 0.85f * frac));
                Gfx.Text(this, r.Position + new Vector2(0, cs / 2 - 14), $"{M.Confusion[i, j]}", 13, Colors.White, cs - 3, HorizontalAlignment.Center);
                Gfx.Text(this, r.Position + new Vector2(0, cs / 2 + 2), $"{frac:P0}", 10, UiTheme.Text, cs - 3, HorizontalAlignment.Center);
            }
        }
    }
}

public partial class NetDiagram : Control
{
    public NeuralPerception? Rna;
    public float[]? Raw;

    public override void _Draw()
    {
        if (Rna == null || Raw == null) return;
        var acts = Rna.ForwardRaw(Raw);
        int[] sizes = Rna.Net.Sizes;
        float colW = (Size.X - 150) / (sizes.Length - 1);
        var pos = new List<Vector2[]>();
        for (int l = 0; l < sizes.Length; l++)
        {
            var arr = new Vector2[sizes[l]];
            float spacing = (Size.Y - 40) / sizes[l];
            for (int i = 0; i < sizes[l]; i++) arr[i] = new Vector2(100 + l * colW, 26 + spacing * (i + 0.5f));
            pos.Add(arr);
        }
        // conexiones: solo las de mayor contribución |w·a| para no saturar
        for (int l = 0; l < sizes.Length - 1; l++)
        {
            var w = Rna.Net.W[l];
            int fin = sizes[l];
            var contrib = new List<(int O, int I, float C)>();
            for (int o = 0; o < sizes[l + 1]; o++)
                for (int i = 0; i < fin; i++)
                    contrib.Add((o, i, w[o * fin + i] * acts[l][i]));
            foreach (var (o, i, c) in contrib.OrderByDescending(x => MathF.Abs(x.C)).Take(90))
                DrawLine(pos[l][i], pos[l + 1][o], (c > 0 ? UiTheme.Rna : new Color("e07050")) * new Color(1, 1, 1, Mathf.Clamp(MathF.Abs(c) / 2f, 0.05f, 0.8f)), 1);
        }
        string[] titles = { "entrada (z)", "oculta 1 · 32 ReLU", "oculta 2 · 16 ReLU", "softmax" };
        for (int l = 0; l < sizes.Length; l++)
        {
            Gfx.Text(this, new Vector2(pos[l][0].X - 50, 2), titles[l], 10, UiTheme.Dim, 100, HorizontalAlignment.Center);
            float maxA = acts[l].Select(MathF.Abs).DefaultIfEmpty(1).Max();
            for (int i = 0; i < sizes[l]; i++)
            {
                float a = acts[l][i];
                float t = l == 0 ? Mathf.Clamp(MathF.Abs(a) / 3f, 0, 1) : l == sizes.Length - 1 ? a : maxA > 0 ? a / maxA : 0;
                var col = l == 0 && a < 0 ? new Color("e07050") : UiTheme.Rna;
                float rad = l == 0 || l == sizes.Length - 1 ? 7 : sizes[l] > 20 ? 4.5f : 6;
                DrawCircle(pos[l][i], rad, new Color(0.1f, 0.12f, 0.16f));
                DrawCircle(pos[l][i], rad, col * new Color(1, 1, 1, 0.15f + 0.85f * t));
                if (l == 0) Gfx.Text(this, pos[l][i] + new Vector2(-98, -7), $"{FeatureModel.Short[i]} {Raw[i]:0.0}", 10, UiTheme.Text, 86, HorizontalAlignment.Right);
                if (l == sizes.Length - 1) Gfx.Text(this, pos[l][i] + new Vector2(12, -7), $"{NeuralPerception.ClassNames[i]} {a:0.000}", 11, i == 0 ? UiTheme.Ok : UiTheme.Crit);
            }
        }
    }
}

public partial class MembershipPlot : Control
{
    public FuzzyVar? Var;
    public float Input;
    public float[]? Aggregated;
    public float? Crisp;
    private static readonly Color[] SetColors = { new("2ab8b0"), new("a07ae0"), new("e8b040"), new("ff5a4a") };

    public override void _Draw()
    {
        if (Var == null) return;
        var r = new Rect2(4, 16, Size.X - 8, Size.Y - 32);
        Gfx.Frame(this, r);
        Gfx.Text(this, Vector2.Zero, $"{Var.Name} [{Var.Min:0.#}…{Var.Max:0.#} {Var.Unit}]", 11, UiTheme.Accent);
        float X(float v) => r.Position.X + r.Size.X * (v - Var.Min) / (Var.Max - Var.Min);
        float Y(float mu) => r.End.Y - r.Size.Y * mu;
        if (Aggregated != null)
        {
            var pts = new List<Vector2> { new(r.Position.X, r.End.Y) };
            for (int i = 0; i < Aggregated.Length; i++) pts.Add(new Vector2(r.Position.X + r.Size.X * i / (Aggregated.Length - 1), Y(Aggregated[i])));
            pts.Add(new Vector2(r.End.X, r.End.Y));
            if (Aggregated.Any(a => a > 0)) DrawColoredPolygon(pts.ToArray(), new Color(UiTheme.Fuzzy, 0.35f));
        }
        for (int s = 0; s < Var.Sets.Count; s++)
        {
            var set = Var.Sets[s];
            var pts = new Vector2[81];
            for (int i = 0; i <= 80; i++)
            {
                float v = Var.Min + (Var.Max - Var.Min) * i / 80;
                pts[i] = new Vector2(X(v), Y(set.Fn.Mu(v)));
            }
            DrawPolyline(pts, SetColors[s % 4], 2, true);
            float mu = set.Fn.Mu(Input);
            Gfx.Text(this, new Vector2(r.Position.X + 4 + s * (r.Size.X / Var.Sets.Count), r.End.Y + 2), $"{set.Label} {mu:0.00}", 10, mu > 0 ? SetColors[s % 4] : UiTheme.Dim);
            if (Aggregated == null && mu > 0) DrawCircle(new Vector2(X(Input), Y(mu)), 4, SetColors[s % 4]);
        }
        if (Aggregated == null) DrawLine(new Vector2(X(Input), r.Position.Y), new Vector2(X(Input), r.End.Y), Colors.White, 1.5f);
        if (Crisp is float c)
        {
            DrawLine(new Vector2(X(c), r.Position.Y), new Vector2(X(c), r.End.Y), Colors.White, 2);
            Gfx.Text(this, new Vector2(X(c) + 3, r.Position.Y), $"centroide {c:0.00}", 10, Colors.White);
        }
    }
}

public partial class PathMap : Control
{
    public Mission? Mission;
    public readonly List<(List<Cell> Path, Color C, float W)> Paths = new();

    public override void _Draw()
    {
        var m = Mission;
        if (m == null) return;
        float cp = MathF.Min(Size.X, Size.Y) / m.Map.N;
        for (int y = 0; y < m.Map.N; y++)
        for (int x = 0; x < m.Map.N; x++)
        {
            var c = new Cell(x, y);
            float r = m.Belief.Risk(c);
            var col = new Color(0.2f, 0.24f, 0.28f).Lerp(new Color("ff6a2a"), r);
            if (m.Belief.ConfirmedCliff[m.Belief.Idx(c)]) col = new Color("c01818");
            DrawRect(new Rect2(x * cp, y * cp, cp - 0.4f, cp - 0.4f), col);
        }
        foreach (var (p, c, w) in Paths)
            if (p.Count > 1) DrawPolyline(p.Select(q => new Vector2((q.X + 0.5f) * cp, (q.Y + 0.5f) * cp)).ToArray(), c, w, true);
        foreach (var s in m.Map.Sites) DrawCircle(new Vector2((s.Cell.X + 0.5f) * cp, (s.Cell.Y + 0.5f) * cp), cp * 0.6f, s.Visited ? UiTheme.Dim : new Color("ffb347"));
        foreach (var r in m.Map.Refuges) DrawRect(new Rect2(r.X * cp, r.Y * cp, cp, cp), new Color("5aff8a"), false, 1.5f);
    }
}

// ====================================================================== laboratorios
public partial class Labs : CanvasLayer
{
    public Mission? Mission;
    public NeuralPerception? Rna;
    public Action? Closed;
    /// <summary>Móvil: al abrir un laboratorio se cierran los cajones del HUD.</summary>
    public Action? OpenedOnMobile;
    private Control _root = null!;
    public static readonly string[] Ids = { "lab_rna", "lab_fuzzy", "lab_ga", "lab_policies", "lab_help" };
    private readonly Dictionary<string, FloatPanel> _wins = new();
    private readonly Dictionary<string, CheckButton> _liveChecks = new();
    private bool _dirty;
    private double _sinceRefresh;
    private int _gaSeen = -1;
    private bool _gaPlaying;
    private double _gaPlayT;

    // RNA
    private NetDiagram _net = null!;
    private LineChart _loss = null!, _acc = null!;
    private ConfusionView _conf = null!;
    private RichTextLabel _rnaMetrics = null!, _rnaOut = null!;
    private readonly List<HSlider> _featSliders = new();
    private readonly List<Label> _featLabels = new();
    private float[] _raw = new float[FeatureModel.NF];
    private bool _suppress;
    private int? _sampleLabel;

    // difuso
    private readonly Dictionary<string, MembershipPlot> _mPlots = new();
    private readonly Dictionary<string, HSlider> _fSliders = new();
    private MembershipPlot _outSpeed = null!, _outCaution = null!;
    private RichTextLabel _rules = null!;
    private FuzzySystem _fz = RoverFuzzy.Build();

    // genético
    private OptionButton _gaRun = null!;
    private HSlider _gen = null!;
    private Label _genLabel = null!;
    private LineChart _fit = null!, _missionFit = null!;
    private RichTextLabel _gaInfo = null!, _missionInfo = null!;
    private PathMap _gaMap = null!;

    // políticas
    private RichTextLabel _bench = null!;
    private Label _benchProgress = null!;
    private Button _benchBtn = null!;
    private Control _benchChart = null!;
    private List<PolicyStats>? _benchResult;
    private readonly ConcurrentQueue<string> _benchMsgs = new();
    private Task? _benchTask;
    private volatile bool _benchCancel;
    private RichTextLabel _help = null!;

    public override void _Ready()
    {
        Layer = 4;
        // capa transparente a los clics: solo las ventanas capturan el ratón
        _root = new Control { Theme = UiTheme.Get(), MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);
        BuildRna();
        BuildFuzzy();
        BuildGa();
        BuildPolicies();
        BuildHelp();
    }

    /// <summary>
    /// Crea la ventana flotante de un laboratorio. Los de RNA, difuso y genético llevan el interruptor "En vivo": se
    /// actualizan con cada paso de la simulación hasta que el usuario toca un control para experimentar.
    /// </summary>
    private void AddWindow(string id, string title, Control page, Rect2 rect, bool live)
    {
        var w = new FloatPanel(id, title, rect) { Docked = Platform.IsMobile, Visible = false };
        _root.AddChild(w);
        if (Platform.IsMobile) Hud.Anchor(w, 0, 0, 1, 1, 6, 32, -138, -54);
        if (live)
        {
            var bar = new HBoxContainer();
            var chk = new CheckButton { Text = "En vivo", ButtonPressed = true, FocusMode = Control.FocusModeEnum.None, TooltipText = "Seguir la simulación paso a paso (se apaga al mover un control)" };
            chk.AddThemeFontSizeOverride("font_size", 11);
            chk.Toggled += on => { if (on) { _dirty = true; _sinceRefresh = 1; } };
            bar.AddChild(chk);
            bar.AddChild(UiTheme.Label("· sin pausar la misión", 10, UiTheme.Dim));
            w.Content.AddChild(bar);
            _liveChecks[id] = chk;
        }
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        page.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        page.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        scroll.AddChild(page);
        w.Content.AddChild(scroll);
        w.Shown += () => RefreshWindow(id, true);
        _wins[id] = w;
        // resumen de indicadores en vivo: se muestra cuando la ventana es angosta (p. ej. acoplada) en vez de desbordarse
        string? sections = id switch { "lab_rna" => "rna,rnakpi", "lab_fuzzy" => "fuzzy,sup", "lab_ga" => "ga,gahist", _ => null };
        if (sections != null && !Platform.IsMobile)
        {
            var compact = new DecisionPanel { Sections = sections, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            var cscroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, Visible = false };
            cscroll.AddChild(compact);
            w.Content.AddChild(cscroll);
            var mode = UiTheme.Button("Auto", () => { }, "Vista: Auto (resumen si la ventana es angosta) → Resumen → Completa");
            mode.CustomMinimumSize = new Vector2(70, 0);
            mode.Pressed += () =>
            {
                _viewMode[id] = (_viewMode.GetValueOrDefault(id) + 1) % 3;
                mode.Text = new[] { "Auto", "Resumen", "Completa" }[_viewMode[id]];
            };
            if (w.Content.GetChild(0) is HBoxContainer bar) bar.AddChild(mode);
            _compact[id] = (compact, cscroll, scroll);
        }
    }

    private readonly Dictionary<string, (DecisionPanel Panel, ScrollContainer Compact, ScrollContainer Full)> _compact = new();
    private readonly Dictionary<string, int> _viewMode = new();

    /// <summary>Resumen o vista completa según el tamaño de la ventana (y la preferencia del usuario).</summary>
    private void UpdateCompactViews()
    {
        foreach (var (id, (panel, compact, full)) in _compact)
        {
            var w = _wins[id];
            if (!w.Visible) continue;
            panel.Mission = Mission;
            int mode = _viewMode.GetValueOrDefault(id);
            bool small = w.Size.X < 600 || w.Size.Y < 330;
            bool useCompact = mode == 1 || (mode == 0 && small);
            if (compact.Visible != useCompact)
            {
                compact.Visible = useCompact;
                full.Visible = !useCompact;
                if (!useCompact) RefreshWindow(id, true);
            }
        }
    }

    private bool Live(string id) => _wins.TryGetValue(id, out var w) && w.Visible && !w.Minimized && (!_liveChecks.TryGetValue(id, out var c) || c.ButtonPressed)
        && !(_compact.TryGetValue(id, out var cv) && cv.Compact.Visible);   // en modo resumen no se recalcula la vista completa

    private void SetLive(string id, bool on)
    {
        if (_liveChecks.TryGetValue(id, out var c) && c.ButtonPressed != on) c.SetPressedNoSignal(on);
    }

    public bool IsOpen => _wins.Values.Any(w => w.Visible);

    public bool IsOverUi(Vector2 p) => _wins.Values.Any(w => w.IsVisibleInTree() && w.GetGlobalRect().HasPoint(p));

    public void Open(int tab)
    {
        string id = Ids[Math.Clamp(tab, 0, Ids.Length - 1)];
        if (Platform.IsMobile)
        {
            foreach (var w in _wins.Values) if (w.Id != id) w.SetOpen(false);
            OpenedOnMobile?.Invoke();
        }
        WindowManager.Open(id);
        RefreshWindow(id, true);
    }

    public void Toggle(string id)
    {
        if (!_wins.TryGetValue(id, out var w)) return;
        if (w.Visible) w.SetOpen(false);
        else Open(Array.IndexOf(Ids, id));
    }

    public void Close()
    {
        foreach (var w in _wins.Values) w.SetOpen(false);
        Closed?.Invoke();
    }

    /// <summary>La simulación dio un paso: los laboratorios en vivo se refrescan (como mucho ~4 veces por segundo).</summary>
    public void NotifyStep() => _dirty = true;

    private void RefreshWindow(string id, bool force)
    {
        if (Mission == null) return;
        switch (id)
        {
            case "lab_rna":
                if (force || Live(id))
                {
                    if (Live(id) || force)
                    {
                        _raw = (float[])Mission.FrontFeatures.Clone();
                        _sampleLabel = null;
                    }
                    RefreshRna(true);
                }
                break;
            case "lab_fuzzy":
                if (Live(id) || force) LoadFuzzyInputs();
                RefreshFuzzy();
                break;
            case "lab_ga":
                if (force || _gaSeen != Mission.GaHistory.Count + Mission.Replans * 1000)
                {
                    _gaSeen = Mission.GaHistory.Count + Mission.Replans * 1000;
                    FillGaRuns();
                    if (Live(id) && Mission.GaHistory.Count > 0 && !force)
                    {
                        _gaPlaying = true;   // reproducir la evolución de la corrida nueva
                        _gaPlayT = 0;
                        _gen.SetValueNoSignal(0);
                    }
                }
                RefreshGa();
                break;
        }
    }

    private static VBoxContainer V(int sep = 6) { var v = new VBoxContainer(); v.AddThemeConstantOverride("separation", sep); return v; }
    private static HBoxContainer H(int sep = 8) { var h = new HBoxContainer(); h.AddThemeConstantOverride("separation", sep); return h; }

    // ------------------------------------------------------------------ RNA
    private void BuildRna()
    {
        var page = H(12);
        page.Name = "Laboratorio RNA";
        AddWindow("lab_rna", "LABORATORIO RNA · percepción en vivo", page, new Rect2(340, 60, 760, 470), true);
        var left = V();
        left.CustomMinimumSize = new Vector2(560, 0);
        page.AddChild(left);
        left.AddChild(UiTheme.Label("Fila de entrada → normalización (z) → activaciones → probabilidades → acción posterior", 12, UiTheme.Rna));
        _net = new NetDiagram { CustomMinimumSize = new Vector2(540, 380), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        left.AddChild(_net);
        _rnaOut = new RichTextLabel { BbcodeEnabled = true, CustomMinimumSize = new Vector2(0, 90), FitContent = true };
        left.AddChild(_rnaOut);
        var btns = H(6);
        btns.AddChild(UiTheme.Button("Usar la celda evaluada ahora", () => { if (Mission != null) { _raw = (float[])Mission.FrontFeatures.Clone(); _sampleLabel = null; RefreshRna(true); } }));
        btns.AddChild(UiTheme.Button("Muestra aleatoria del conjunto de prueba", () =>
        {
            SetLive("lab_rna", false);
            if (Rna == null || Rna.TestX.Count == 0) return;
            int i = (int)(GD.Randi() % (uint)Rna.TestX.Count);
            _raw = (float[])Rna.TestX[i].Clone();
            _sampleLabel = Rna.TestY[i];
            RefreshRna(true);
        }));
        left.AddChild(btns);

        var right = V();
        right.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        page.AddChild(right);
        var charts = H(8);
        _loss = new LineChart { Title = "Pérdida (entropía cruzada) por época", XLabel = "época", CustomMinimumSize = new Vector2(300, 170), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _acc = new LineChart { Title = "Exactitud por época", XLabel = "época", CustomMinimumSize = new Vector2(300, 170), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FixedMin = 0.3f, FixedMax = 1f };
        charts.AddChild(_loss);
        charts.AddChild(_acc);
        right.AddChild(charts);
        var mid = H(8);
        _conf = new ConfusionView { CustomMinimumSize = new Vector2(320, 200) };
        mid.AddChild(_conf);
        _rnaMetrics = new RichTextLabel { BbcodeEnabled = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 200) };
        mid.AddChild(_rnaMetrics);
        right.AddChild(mid);
        right.AddChild(UiTheme.Label("Modificar rasgos de entrada (CA-06): las probabilidades se recalculan al instante", 12, UiTheme.Accent));
        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 10);
        for (int i = 0; i < FeatureModel.NF; i++)
        {
            int k = i;
            var lab = UiTheme.Label(FeatureModel.Names[i], 11, UiTheme.Dim);
            lab.CustomMinimumSize = new Vector2(150, 0);
            var s = new HSlider { CustomMinimumSize = new Vector2(150, 16), Step = 0.01 };
            s.ValueChanged += v => { if (_suppress) return; SetLive("lab_rna", false); _raw[k] = (float)v; _sampleLabel = null; RefreshRna(false); };
            grid.AddChild(lab);
            grid.AddChild(s);
            _featSliders.Add(s);
            _featLabels.Add(lab);
        }
        right.AddChild(grid);
    }

    private void RefreshRna(bool sliders)
    {
        if (Rna == null) return;
        var rep = Rna.Report;
        _net.Rna = Rna;
        _net.Raw = _raw;
        _net.QueueRedraw();
        _loss.Series.Clear();
        _loss.Series.Add(("entrenamiento", rep.TrainLoss, UiTheme.Rna));
        _loss.Series.Add(("validación", rep.ValLoss, UiTheme.Fuzzy));
        _loss.Marker = rep.BestEpoch - 1;
        _acc.Series.Clear();
        _acc.Series.Add(("entrenamiento", rep.TrainAcc, UiTheme.Rna));
        _acc.Series.Add(("validación", rep.ValAcc, UiTheme.Fuzzy));
        _acc.Marker = rep.BestEpoch - 1;
        _loss.QueueRedraw();
        _acc.QueueRedraw();
        _conf.M = rep.Test;
        _conf.QueueRedraw();
        var t = rep.Test!;
        _rnaMetrics.Text =
            $"[color=#ffd860]Métricas en PRUEBA (mapas {rep.TestMaps[0]}–{rep.TestMaps[^1]}, nunca vistos)[/color]\n" +
            $"Exactitud {t.Accuracy:P1} · línea base (clase mayoritaria) {t.Baseline:P1}\nMacro-F1 [b]{t.MacroF1:0.000}[/b] · [color=#ff8a7a]recall barranco {t.Recall[2]:P1}[/color] · ECE {t.Ece:0.000}\n" +
            string.Join("\n", Enumerable.Range(0, 3).Select(i => $"  {NeuralPerception.ClassNames[i],-10} P {t.Precision[i]:0.00}  R {t.Recall[i]:0.00}  F1 {t.F1[i]:0.00}")) +
            $"\n[color=#8a96aa]Partición por MAPAS (sin fuga): {rep.TrainMaps.Length} entrenamiento ({rep.TrainN} muestras) · {rep.ValMaps.Length} validación ({rep.ValN}) · {rep.TestMaps.Length} prueba ({rep.TestN}).\n" +
            $"Clases en entrenamiento: {string.Join(" / ", rep.ClassCountsTrain)} (balanceo por submuestreo). Adam, lote 64, L2 1e-4, detención temprana: mejor época {rep.BestEpoch} de {rep.Epochs}. {rep.Seconds:0.0} s en CPU.\n" +
            "Etiqueta = clase de verdad del simulador; los rasgos son lecturas con ruido dependiente de distancia, visibilidad y noche.[/color]";
        if (sliders)
        {
            _suppress = true;
            for (int i = 0; i < FeatureModel.NF; i++)
            {
                _featSliders[i].MinValue = Rna.Mean[i] - 3.5f * Rna.Std[i];
                _featSliders[i].MaxValue = Rna.Mean[i] + 3.5f * Rna.Std[i];
                _featSliders[i].Value = _raw[i];
            }
            _suppress = false;
        }
        for (int i = 0; i < FeatureModel.NF; i++) _featLabels[i].Text = $"{FeatureModel.Names[i]}: {_raw[i]:0.00}";
        var p = Rna.Predict(_raw);
        int k = Array.IndexOf(p, p.Max());
        string action = p[2] >= SafetySupervisor.CliffVetoP || p[1] >= SafetySupervisor.CliffVetoP ? "VETO del supervisor (S-07) y replanificación"
                      : p[1] + p[2] >= SafetySupervisor.AlertP ? "ALERTA: velocidad limitada a 0,30 y riesgo alto en el difuso"
                      : p.Max() < SafetySupervisor.LowConfidence ? "confianza baja → modo conservador (S-12)" : "avance normal (riesgo bajo al difuso)";
        _rnaOut.Text = $"[font_size=15]Predicción: [color={(k == 0 ? "#5ad87a" : "#ff5a4a")}]{NeuralPerception.ClassNames[k].ToUpperInvariant()}[/color] (confianza {p.Max():0.000})[/font_size]" +
                       (_sampleLabel is int y ? $"   ·   verdad: [b]{NeuralPerception.ClassNames[y]}[/b] {(y == k ? "[color=#5ad87a]✓[/color]" : "[color=#ff5a4a]✗ error[/color]")}" : "") +
                       $"\np(libre) {p[0]:0.000} · p(obstáculo) {p[1]:0.000} · p(barranco) {p[2]:0.000}\nAcción posterior: {action}";
    }

    // ------------------------------------------------------------------ difuso
    private void BuildFuzzy()
    {
        var page = H(12);
        page.Name = "Laboratorio Difuso";
        AddWindow("lab_fuzzy", "LABORATORIO DIFUSO · control en vivo", page, new Rect2(360, 80, 780, 470), true);
        var left = V(4);
        left.CustomMinimumSize = new Vector2(720, 0);
        page.AddChild(left);
        left.AddChild(UiTheme.Label("Universos de discurso, funciones de membresía y punto de entrada (mueva los deslizadores · CA-05)", 12, UiTheme.Fuzzy));
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 2);
        foreach (var (id, v) in _fz.Inputs)
        {
            var box = V(1);
            var plot = new MembershipPlot { Var = v, CustomMinimumSize = new Vector2(232, 118) };
            var s = new HSlider { MinValue = v.Min, MaxValue = v.Max, Step = (v.Max - v.Min) / 200, CustomMinimumSize = new Vector2(232, 16) };
            s.ValueChanged += _ => { SetLive("lab_fuzzy", false); RefreshFuzzy(); };
            box.AddChild(plot);
            box.AddChild(s);
            grid.AddChild(box);
            _mPlots[id] = plot;
            _fSliders[id] = s;
        }
        left.AddChild(grid);
        var outs = H(8);
        _outSpeed = new MembershipPlot { Var = _fz.Outputs["velocidad"], CustomMinimumSize = new Vector2(350, 130) };
        _outCaution = new MembershipPlot { Var = _fz.Outputs["cautela"], CustomMinimumSize = new Vector2(350, 130) };
        outs.AddChild(_outSpeed);
        outs.AddChild(_outCaution);
        left.AddChild(UiTheme.Label("Salidas: agregación máx de los consecuentes recortados (mín) y centroide", 12, UiTheme.Fuzzy));
        left.AddChild(outs);
        var right = V();
        right.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        page.AddChild(right);
        var b = H(6);
        b.AddChild(UiTheme.Button("Cargar entradas del paso actual", () => { LoadFuzzyInputs(); RefreshFuzzy(); }));
        b.AddChild(UiTheme.Button("Caso CA-05: distancia 10 m", () => { _fSliders["distancia"].Value = 10; }));
        b.AddChild(UiTheme.Button("→ 1 m", () => { _fSliders["distancia"].Value = 1; }));
        right.AddChild(b);
        _rules = new RichTextLabel { BbcodeEnabled = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        right.AddChild(_rules);
    }

    private void LoadFuzzyInputs()
    {
        var f = Mission?.LastFuzzy;
        foreach (var (id, s) in _fSliders)
            s.SetValueNoSignal(f != null && f.Inputs.TryGetValue(id, out var v) ? v : (s.MinValue + s.MaxValue) / 2);
    }

    private void RefreshFuzzy()
    {
        var x = _fSliders.ToDictionary(kv => kv.Key, kv => (float)kv.Value.Value);
        var r = _fz.Infer(x);
        foreach (var (id, plot) in _mPlots)
        {
            plot.Input = x[id];
            plot.QueueRedraw();
        }
        _outSpeed.Aggregated = r.Aggregated["velocidad"];
        _outSpeed.Crisp = r.Crisp["velocidad"];
        _outCaution.Aggregated = r.Aggregated["cautela"];
        _outCaution.Crisp = r.Crisp["cautela"];
        _outSpeed.QueueRedraw();
        _outCaution.QueueRedraw();
        var sb = new System.Text.StringBuilder();
        sb.Append($"[font_size=15][color=#e8b040]Salida crisp: velocidad {r.Crisp["velocidad"]:0.000} · cautela {r.Crisp["cautela"]:0.000}[/color][/font_size]\n");
        sb.Append(r.Fallback ? $"[color=#f0c040]{r.FallbackNote}. Respaldo documentado: velocidad 0 (parar), cautela 1.[/color]\n" : $"{r.Fired.Count} reglas activas de {_fz.Rules.Count} (AND = mín, agregación = máx).\n");
        sb.Append("\n[color=#ffd860]Base de reglas (fuerza de disparo para la entrada actual):[/color]\n");
        foreach (var rule in _fz.Rules)
        {
            float s = r.Fired.FirstOrDefault(f => f.Rule == rule).Strength;
            string bar = new string('█', (int)MathF.Round(s * 10)).PadRight(10, '·');
            sb.Append($"[color={(s > 0 ? "#e8b040" : "#5a6478")}]{rule.Id} [{bar}] {s:0.00}  {rule.Text}[/color]\n");
            sb.Append($"[color=#7a8698]      ↳ {rule.Justification}[/color]\n");
        }
        sb.Append("\n[color=#8a96aa]Funciones: " + string.Join("; ", _fz.Inputs.Values.Select(v => $"{v.Name}: " + string.Join(", ", v.Sets.Select(s => $"{s.Label}={s.Fn.Describe()}")))) + "[/color]");
        _rules.Text = sb.ToString();
    }

    // ------------------------------------------------------------------ genético
    private void BuildGa()
    {
        var page = H(12);
        page.Name = "Laboratorio Genético";
        AddWindow("lab_ga", "LABORATORIO GENÉTICO · planificación en vivo", page, new Rect2(380, 100, 760, 470), true);
        var left = V();
        left.CustomMinimumSize = new Vector2(640, 0);
        page.AddChild(left);
        var top = H(8);
        top.AddChild(UiTheme.Label("Corrida del AG:", 12, UiTheme.Ga));
        _gaRun = new OptionButton { CustomMinimumSize = new Vector2(380, 0) };
        _gaRun.ItemSelected += _ => { SetLive("lab_ga", false); _gaPlaying = false; _gen.Value = 0; RefreshGa(); };
        top.AddChild(_gaRun);
        left.AddChild(top);
        _fit = new LineChart { Title = "Fitness por generación (mejor / media / peor)", XLabel = "generación", CustomMinimumSize = new Vector2(620, 200) };
        left.AddChild(_fit);
        var gr = H(8);
        gr.AddChild(UiTheme.Label("Generación:", 12, UiTheme.Ga));
        _gen = new HSlider { CustomMinimumSize = new Vector2(420, 18), Step = 1 };
        _gen.ValueChanged += _ => { if (!_gaPlaying) SetLive("lab_ga", false); RefreshGa(); };
        gr.AddChild(_gen);
        _genLabel = UiTheme.Label("", 12);
        gr.AddChild(_genLabel);
        gr.AddChild(UiTheme.Button("▶ Reproducir evolución", () => { _gaPlaying = true; _gaPlayT = 0; _gen.SetValueNoSignal(0); RefreshGa(); }, "Animar la corrida desde la generación 0"));
        left.AddChild(gr);
        _gaInfo = new RichTextLabel { BbcodeEnabled = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        left.AddChild(_gaInfo);
        var right = V();
        right.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        page.AddChild(right);
        right.AddChild(UiTheme.Label("Top-5 de la generación sobre el mapa estimado (riesgo de la RNA)", 12, UiTheme.Ga));
        _gaMap = new PathMap { CustomMinimumSize = new Vector2(300, 300) };
        right.AddChild(_gaMap);
        _missionFit = new LineChart { Title = "AG plan de misión (viajero + mochila): mejor / media", XLabel = "generación", CustomMinimumSize = new Vector2(300, 130) };
        right.AddChild(_missionFit);
        _missionInfo = new RichTextLabel { BbcodeEnabled = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        right.AddChild(_missionInfo);
    }

    private void FillGaRuns()
    {
        _gaRun.Clear();
        if (Mission == null) return;
        for (int i = 0; i < Mission.GaHistory.Count; i++)
        {
            var g = Mission.GaHistory[i];
            _gaRun.AddItem($"#{i + 1} · {g.Trigger} · ({g.Start.Y},{g.Start.X})→({g.Goal.Y},{g.Goal.X}) · {g.Generations} gen");
        }
        if (Mission.GaHistory.Count > 0) _gaRun.Selected = Mission.GaHistory.Count - 1;
        _gen.SetValueNoSignal(Math.Min(5, Mission.GaHistory.LastOrDefault()?.History.Count - 1 ?? 0));   // CA-04: generación 5
    }

    private void RefreshGa()
    {
        var m = Mission;
        if (m == null) return;
        _gaMap.Mission = m;
        _missionFit.Series.Clear();
        if (m.Plan != null)
        {
            _missionFit.Series.Add(("mejor", m.Plan.BestHistory, UiTheme.Ok));
            _missionFit.Series.Add(("media", m.Plan.MeanHistory, UiTheme.Ga));
            _missionInfo.Text = $"[color=#a07ae0]Plan vigente[/color] ({m.Plan.Generations} gen, {m.Plan.Ms:0} ms): {m.Plan.Note}\n" +
                string.Join("\n", m.Plan.Decisions.Select(d => $"[color={(d.Included ? "#5ad87a" : "#8a96aa")}]{d.Name} · valor {d.Value:0.00} · {d.Mass:0.0} kg → {d.Reason}[/color]")) +
                "\n[color=#8a96aa]Cromosoma = claves aleatorias (orden, viajero) + bits de inclusión (mochila: capacidad de bodega y presupuesto de energía).[/color]";
        }
        else _missionInfo.Text = m.Config.UseGa ? "Plan de misión abandonado (rumbo al refugio por energía)." : "Ablación sin AG: orden por vecino más cercano.";
        _missionFit.QueueRedraw();
        if (m.GaHistory.Count == 0 || _gaRun.Selected < 0)
        {
            _gaInfo.Text = m.Config.UseGa ? "Todavía no hay corridas del AG en esta misión." : "Ablación activa: el AG está desactivado (ruta de menor distancia).";
            _fit.Series.Clear();
            _fit.QueueRedraw();
            _gaMap.Paths.Clear();
            _gaMap.QueueRedraw();
            return;
        }
        var ga = m.GaHistory[Math.Clamp(_gaRun.Selected, 0, m.GaHistory.Count - 1)];
        _gen.MaxValue = Math.Max(0, ga.History.Count - 1);
        int gi = (int)Math.Clamp(_gen.Value, 0, ga.History.Count - 1);
        var snap = ga.History[gi];
        _genLabel.Text = $"{gi} / {ga.History.Count - 1}";
        _fit.Series.Clear();
        _fit.Series.Add(("mejor", ga.History.Select(h => h.Best).ToList(), UiTheme.Ok));
        _fit.Series.Add(("media", ga.History.Select(h => h.Mean).ToList(), UiTheme.Ga));
        _fit.Series.Add(("peor", ga.History.Select(h => MathF.Max(h.Worst, h.Best - 8)).ToList(), UiTheme.Crit));
        _fit.Marker = gi;
        _fit.QueueRedraw();
        var p = ga.Params;
        var sb = new System.Text.StringBuilder();
        sb.Append($"[color=#a07ae0]Disparo: {ga.Trigger} · población {p.Population} · máx {p.Generations} gen · K={p.Waypoints} puntos de paso · torneo k={p.Tournament} · cruce p={p.CrossoverP} · mutación p={p.MutationP} · élite {p.Elite} · parada tras {p.Patience} gen sin mejora{(ga.EarlyStop ? " (ACTIVADA)" : "")}[/color]\n");
        sb.Append($"fitness = {p.WScience}·C − {p.WDistance}·D − {p.WEnergy}·E − {p.WRisk}·R − {p.WTime}·T − P   ·   generación {gi}: mejor {snap.Best:0.000}, media {snap.Mean:0.000}, viables {snap.FeasibleCount}/{p.Population}\n\n");
        sb.Append("[color=#ffd860]Top-5 cromosomas (genes = puntos de paso (fila,col)):[/color]\n");
        for (int i = 0; i < snap.Top.Count; i++)
        {
            var c = snap.Top[i];
            sb.Append($"[color=#{View.OverlayView.CandidateColors[i % 3].ToHtml(false)}]{i + 1}. f={c.Fitness:0.000}[/color] {c.GenesText}  [{c.Breakdown}] {(c.Feasible ? "" : "[color=#ff5a4a]INVIABLE[/color] ")}[color=#8a96aa]{c.Origin} · {c.Path.Count - 1} pasos · reparaciones {c.Repairs}[/color]\n");
        }
        if (snap.Crossover is { } x)
            sb.Append($"\n[color=#5ad87a]Cruce de un punto (corte {x.Cut}):[/color]\n  padre A (f={x.FitA:0.00}): {Genes(x.ParentA, x.Cut, true)}\n  padre B (f={x.FitB:0.00}): {Genes(x.ParentB, x.Cut, false)}\n  hijo:            {Genes(x.Child, x.Cut, null)}\n");
        if (snap.Mutation is { } mu)
            sb.Append($"\n[color=#e8b040]Mutación ({mu.Kind}) en el gen {mu.Gene}:[/color] ({mu.Before[mu.Gene].Y},{mu.Before[mu.Gene].X}) → ({mu.After[mu.Gene].Y},{mu.After[mu.Gene].X}){(mu.Repaired ? " · coordenada fuera del mapa REPARADA" : "")}\n");
        sb.Append("\n[color=#8a96aa]C ciencia (sitios cerca de la ruta) · D distancia · E energía estimada · R riesgo predicho (RNA) · T tiempo · P penalización (celdas prohibidas, pendiente, sin llegar, energía insuficiente). Todas normalizadas.[/color]");
        _gaInfo.Text = sb.ToString();
        _gaMap.Paths.Clear();
        for (int i = snap.Top.Count - 1; i >= 0; i--)
            _gaMap.Paths.Add((snap.Top[i].Path, new Color(View.OverlayView.CandidateColors[i % 3], i == 0 ? 1f : 0.6f), i == 0 ? 3 : 1.5f));
        _gaMap.QueueRedraw();
    }

    private static string Genes(Cell[] g, int cut, bool? first) => string.Join(" ", g.Select((c, i) =>
    {
        bool fromA = i < cut;
        string col = first == true ? (fromA ? "#a07ae0" : "#5a6478") : first == false ? (fromA ? "#5a6478" : "#2ab8b0") : (fromA ? "#a07ae0" : "#2ab8b0");
        return $"[color={col}]({c.Y},{c.X})[/color]";
    }));

    // ------------------------------------------------------------------ políticas
    private void BuildPolicies()
    {
        var page = V(8);
        page.Name = "Comparación de políticas";
        AddWindow("lab_policies", "COMPARACIÓN DE POLÍTICAS · 20 semillas × ablaciones", page, new Rect2(300, 80, 760, 470), false);
        page.AddChild(UiTheme.Label("Experimento controlado (RF-14, RF-15, CA-08): mismas 20 semillas y configuración para el híbrido, tres ablaciones, AG puro y control de menor distancia.", 12, UiTheme.Accent));
        var h = H(8);
        _benchBtn = UiTheme.Button("▶ Ejecutar 20 semillas × 6 políticas (planeta actual)", RunBench);
        h.AddChild(_benchBtn);
        h.AddChild(UiTheme.Button("Cancelar", () => _benchCancel = true));
        h.AddChild(UiTheme.Button("⇩ Exportar CSV", ExportBench));
        _benchProgress = UiTheme.Label("", 11, UiTheme.Dim);
        h.AddChild(_benchProgress);
        page.AddChild(h);
        var row = H(10);
        row.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _bench = new RichTextLabel { BbcodeEnabled = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        row.AddChild(_bench);
        _benchChart = new Control { CustomMinimumSize = new Vector2(380, 380) };
        _benchChart.Draw += DrawBenchChart;
        row.AddChild(_benchChart);
        page.AddChild(row);
        _bench.Text = "[color=#8a96aa]Pulse ejecutar. Cada misión es autónoma y completa (máx. 500 pasos); el tiempo de entrenamiento de la RNA (offline) no se cuenta en el tiempo de decisión (online).[/color]";
    }

    private void RunBench()
    {
        if (Mission == null || Rna == null || (_benchTask != null && !_benchTask.IsCompleted)) return;
        _benchCancel = false;
        var cfg = Mission.Config;
        var rna = Rna;
        _benchBtn.Disabled = true;
        _benchTask = Task.Run(() =>
        {
            var res = Benchmark.Run(cfg, 20, rna, s => _benchMsgs.Enqueue(s), () => _benchCancel);
            _benchResult = res;
            _benchMsgs.Enqueue("__done__");
        });
    }

    private void ExportBench()
    {
        if (_benchResult == null) return;
        string path = Path.Combine(HeadlessRunner.ExportDir(), $"benchmark_{Mission?.Planet.Id}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        File.WriteAllText(path, Benchmark.Csv(_benchResult));
        _benchProgress.Text = $"Exportado: {path}";
    }

    private void ShowBench()
    {
        if (_benchResult == null) return;
        string[] keys = { "exito", "colisiones", "caidas", "barrancos_evitados", "energia_restante_pct", "distancia_m", "ciencia", "replanificaciones", "violaciones_seguridad", "ms_decision_medio" };
        string[] names = { "Éxito", "Colisiones", "Caídas", "Barrancos evitados", "Energía %", "Distancia m", "Ciencia", "Replanif.", "Violaciones", "ms/decisión" };
        var sb = new System.Text.StringBuilder();
        sb.Append($"[table={keys.Length + 1}][cell][b]Política (n={_benchResult.FirstOrDefault()?.Runs.Count})[/b][/cell]");
        foreach (var n in names) sb.Append($"[cell][b]{n}[/b][/cell]");
        foreach (var ps in _benchResult)
        {
            sb.Append($"[cell][color=#ffd860]{ps.Policy}[/color][/cell]");
            foreach (var k in keys)
                sb.Append(k == "exito" ? $"[cell]{ps.Mean(k):P0}[/cell]" : $"[cell]{ps.Mean(k):0.##} ± {ps.Std(k):0.##}\n[color=#8a96aa]med {ps.Median(k):0.##}[/color][/cell]");
        }
        sb.Append("[/table]\n\n[color=#8a96aa]media ± desviación estándar (mediana debajo). No se atribuye mejora causal fuera de estas condiciones idénticas (mismas semillas, planeta y batería).[/color]");
        _bench.Text = sb.ToString();
        _benchChart.QueueRedraw();
    }

    private void DrawBenchChart()
    {
        var c = _benchChart;
        Gfx.Text(c, Vector2.Zero, "Éxito de misión y violaciones de seguridad por política", 12, UiTheme.Accent);
        if (_benchResult == null) return;
        float y = 26, bw = c.Size.X - 150;
        foreach (var ps in _benchResult)
        {
            Gfx.Text(c, new Vector2(0, y), ps.Policy, 10, UiTheme.Text, 140);
            Gfx.Bar(c, new Rect2(146, y + 2, bw, 10), ps.Mean("exito"), UiTheme.Ok);
            Gfx.Text(c, new Vector2(146, y), $"{ps.Mean("exito"):P0}", 10, Colors.Black);
            Gfx.Bar(c, new Rect2(146, y + 15, bw, 6), Mathf.Clamp(ps.Mean("violaciones_seguridad") / 3f, 0, 1), UiTheme.Crit);
            Gfx.Bar(c, new Rect2(146, y + 23, bw, 6), ps.Mean("ciencia") / 5f, UiTheme.Fuzzy);
            y += 40;
        }
        Gfx.Text(c, new Vector2(146, y), "verde = éxito · rojo = violaciones (escala 0–3) · ámbar = ciencia (0–5)", 9, UiTheme.Dim);
    }

    // ------------------------------------------------------------------ ayuda
    private void BuildHelp()
    {
        _help = new RichTextLabel { BbcodeEnabled = true, Name = "Ayuda y supuestos", CustomMinimumSize = new Vector2(640, 420) };
        AddWindow("lab_help", "AYUDA Y SUPUESTOS", _help, new Rect2(320, 100, 720, 470), false);
        _help.Text = """
[font_size=16][color=#ffd860]Controles[/color][/font_size]
[b]Clic izquierdo en el terreno[/b] (o en el mapa 2D): asignar destino. El rover primero [b]evalúa el destino[/b] con lo que cree del mapa (ruta estimada, energía de ida y de regreso al refugio con margen ×1,25, riesgo de la RNA, terreno sin observar, pendiente, temperatura, viento, hora de la puesta de sol) y da un veredicto: VIABLE · CON RIESGO · SOLO IDA · ENERGÍA INSUFICIENTE · INALCANZABLE. Luego navega: [b]cada paso[/b] dispara el ciclo completo (sensores → validación → RNA → AG si hay bloqueo → difuso → supervisor → física). Si el supervisor veta, el rover se detiene, marca la zona y replanifica.
[b]Clic derecho[/b] cancelar destino · pasar el mouse = previsualización de la celda · Mayús/Ctrl/Alt+clic = barranco/roca/despejar
[b]Espacio[/b] ejecutar/pausar · [b]N[/b] un paso · [b]1–4[/b] velocidad ×1/×2/×4/×8 · [b]M[/b] destino por clic ↔ misión autónoma · [b]R[/b] reiniciar (configuración) · [b]F12[/b] exportar CSV+JSON
[b]Q/E[/b] girar cámara 45° · [b]rueda[/b] zoom · [b]V[/b] mapa de riesgo · [b]T[/b] mostrar la verdad del simulador · [b]C[/b] rutas candidatas del AG · [b]P[/b] replanificar · [b]Tab[/b] laboratorios · [b]H[/b] esta ayuda
[b]Eventos:[/b] F1 sismo · F2 tormenta · F3 meteorito · F4 fallo de sensor (Mayús+F4 reparar) · F5 noche · F6 enlace · F7 ráfaga · F8 biofirma · F9 objeto alienígena (ficción) · F10 batería 12 % · F11 remoción (Tierra)
[b]Mapa 2D:[/b] clic = destino · Mayús+clic agregar barranco (el rover no lo sabe hasta percibirlo · CA-02) · Ctrl+clic roca · Alt+clic despejar

[font_size=16][color=#ffd860]Leyenda 3D[/color][/font_size]
[color=#33ff73]Verde discontinuo[/color] ruta planificada · [color=#3399ff]azul[/color] ruta ejecutada · [color=#e35d5d]rojo[/color]/[color=#9a7ad8]violeta[/color]/[color=#e0b040]ámbar[/color] candidatas A/B/C · cuadro pulsante = celda evaluada (verde avanzar, ámbar alerta, rojo veto)
Celdas: [color=#ff3020]rojo con X[/color] barranco confirmado (zona prohibida) · [color=#ff8c1a]naranja[/color] obstáculo confirmado · [color=#e040e0]magenta con X[/color] vetada por el supervisor · amarillo→rojo riesgo RNA · anillo cian = alcance de sensores (depende de la visibilidad)
Banderas = sitios científicos (★ valor, ◆ señal anómala simulada) · columnas verdes = refugios (carga 600 W, relocalización)

[font_size=16][color=#ffd860]Arquitectura por paso[/color][/font_size]
generador del mundo → sensores con ruido → validación (NaN, saturación, atasco, contradicción) → RNA (11 rasgos → p(libre/obstáculo/barranco)) → creencias → AG si corresponde (inicio, objetivo alcanzado, ruta bloqueada, veto, evento grave) → difuso (6 entradas, 16 reglas, Mamdani, centroide) → supervisor (S-01…S-13, orden fijo) → acción → física (energía, térmica, desgaste, odometría) → bitácora y paneles.
Estados: CONFIGURACIÓN · EXPLORACIÓN · ALERTA · REPLANIFICACIÓN · REFUGIO · MODO SEGURO · ÉXITO · FRACASO.

[font_size=16][color=#ffd860]Supuestos de simulación (no son especificaciones NASA)[/color][/font_size]
Celda 5 m · pendiente máx. 30° · precipicio = salto > 5 m · rover 900 kg, 0,5 m/s, batería 320 Wh, panel 2,2 m² × 24 %, consumo base 45 W, calefactor 150 W, refrigeración 180 W · sonda 350 kg, 2,5 m/s, 420 Wh + RTG 110 W · día comprimido por planeta · reserva de energía 10 % y margen ×1,25 en la estimación al refugio.
El detector de "vida" solo produce una [b]señal anómala simulada[/b]: nunca confirma vida. Los alienígenas son un evento narrativo ficticio. El O₂ indicado es concentración ambiental: un rover no tripulado no respira.
Referencias: NASA Planetary Fact Sheet, NASA Science (Solar System Temperatures, Venus Facts). Tierra calibrada con GeoClick 40e9efb1 (Santiago): pendiente mediana 6,7°, lluvia mensual ERA5, periodos de retorno de Gumbel, 17,8 heladas/año.
""";
    }

    public override void _Process(double delta)
    {
        UpdateCompactViews();
        _sinceRefresh += delta;
        if (_dirty && _sinceRefresh >= 0.25 && Mission != null)
        {
            _dirty = false;
            _sinceRefresh = 0;
            foreach (var id in new[] { "lab_rna", "lab_fuzzy", "lab_ga" })
                if (Live(id)) RefreshWindow(id, false);
        }
        if (_gaPlaying && _wins["lab_ga"].Visible)
        {
            _gaPlayT += delta;
            if (_gaPlayT >= 0.07)
            {
                _gaPlayT = 0;
                if (_gen.Value < _gen.MaxValue)
                {
                    _gen.SetValueNoSignal(_gen.Value + 1);
                    RefreshGa();
                }
                else _gaPlaying = false;
            }
        }
        while (_benchMsgs.TryDequeue(out var msg))
        {
            if (msg == "__done__")
            {
                _benchBtn.Disabled = false;
                _benchProgress.Text = _benchCancel ? "Cancelado." : "Listo: 20 semillas × 6 políticas.";
                ShowBench();
            }
            else _benchProgress.Text = msg;
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } && IsOpen)
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }
}
