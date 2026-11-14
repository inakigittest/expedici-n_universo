using System;
using System.Linq;
using AresX.AI;
using AresX.Sim;
using Godot;

namespace AresX.UI;

/// <summary>Estado CONFIGURACIÓN: planeta, modo, mapa, dificultad, semilla, batería, prioridades y ablaciones (RF-01).</summary>
public partial class ConfigScreen : CanvasLayer
{
    public Action<MissionConfig>? Start;
    public MissionConfig Config = new();

    private OptionButton _planet = null!, _mode = null!, _diff = null!, _month = null!, _control = null!, _terrain = null!;
    private Label _terrainLabel = null!, _importMsg = null!;
    private HBoxContainer _terrainRow = null!;
    private Button? _importBtn;
    private FileDialog? _pdfDialog;
    private System.Threading.Tasks.Task<Core.GeoImportResult>? _importTask;
    private SpinBox _seed = null!;
    private HSlider _battery = null!, _science = null!;
    private Label _batteryVal = null!, _scienceVal = null!, _validation = null!;
    private CheckBox _rna = null!, _fuzzy = null!, _ga = null!;
    private RichTextLabel _info = null!, _train = null!;
    private Button _startBtn = null!, _fixBtn = null!;
    private Control _root = null!;
    public bool RnaReady;
    private string _trainLog = "";

    public override void _Ready()
    {
        Layer = 5;
        _root = new Control { Theme = UiTheme.Get() };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);
        var dim = new ColorRect { Color = UiTheme.Tactical ? new Color(0.02f, 0.01f, 0.003f, 0.8f) : new Color(0.01f, 0.02f, 0.04f, 0.72f), MouseFilter = Control.MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(dim);
        var panel = new PanelContainer();
        _root.AddChild(panel);
        Hud.Anchor(panel, 0.5f, 0.5f, 0.5f, 0.5f, -600, -330, 600, 330);
        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 8);
        panel.AddChild(outer);
        var title = UiTheme.Label("EXPEDICIÓN UNIVERSO · exploración planetaria con IA híbrida", 22, UiTheme.Tactical ? UiTheme.Accent : Colors.White);
        if (UiTheme.Tactical) title.AddThemeFontOverride("font", UiTheme.TitleFont);
        outer.AddChild(title);
        outer.AddChild(UiTheme.Label("IA híbrida: RNA (VER) + algoritmo genético (ELEGIR) + lógica difusa (AJUSTAR) + supervisor de seguridad · simulación ficticia con parámetros de referencia", 12, UiTheme.Dim));
        var cols = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        cols.AddThemeConstantOverride("separation", 14);
        outer.AddChild(cols);

        // ---- columna de opciones
        var form = new GridContainer { Columns = 2, CustomMinimumSize = new Vector2(430, 0) };
        form.AddThemeConstantOverride("h_separation", 10);
        form.AddThemeConstantOverride("v_separation", 8);
        cols.AddChild(form);
        void Row(string label, Control c)
        {
            form.AddChild(UiTheme.Label(label, 12, UiTheme.Dim));
            c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            form.AddChild(c);
        }
        _planet = new OptionButton();
        foreach (var p in PlanetProfile.All) _planet.AddItem(p.Name);
        _planet.Selected = 3;
        _planet.ItemSelected += _ => Refresh();
        Row("Cuerpo", _planet);
        _mode = new OptionButton();
        _mode.AddItem("Rover de ruedas (superficie)");
        _mode.AddItem("Sonda atmosférica / aerostato virtual");
        _mode.AddItem("Helicóptero no tripulado (solo Tierra)");
        _mode.ItemSelected += _ => Refresh();
        Row("Modo", _mode);
        _terrain = new OptionButton { ClipText = true, TooltipText = "Escenarios a escala real convertidos desde informes GeoClick (DEM GLO-30 30 m, ESA WorldCover, Sentinel-2, ERA5)" };
        _terrain.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        FillTerrain();
        _terrain.ItemSelected += _ => { FillMonths(); Refresh(); };
        _terrainRow = new HBoxContainer();
        _terrainRow.AddThemeConstantOverride("separation", 4);
        _terrainRow.AddChild(_terrain);
        if (!Core.Platform.IsMobile)
        {
            _importBtn = UiTheme.Button("Importar PDF…", OpenPdfDialog, "Convertir un informe PDF de GeoClick en un escenario a escala real (también puede arrastrar el PDF a la ventana)");
            _terrainRow.AddChild(_importBtn);
        }
        Row("Terreno (Tierra)", _terrainRow);
        _terrainLabel = (Label)form.GetChild(form.GetChildCount() - 2);
        _diff = new OptionButton();
        _diff.AddItem("Fácil (pocos obstáculos, 4 sitios)");
        _diff.AddItem("Normal (5 sitios)");
        _diff.AddItem("Difícil (más rocas y cráteres, 6 sitios)");
        _diff.Selected = 1;
        Row("Dificultad / mapa", _diff);
        _seed = new SpinBox { MinValue = 0, MaxValue = 999999, Value = 42, Rounded = true };
        var seedRow = new HBoxContainer();
        seedRow.AddChild(_seed);
        seedRow.AddChild(UiTheme.Button("🎲", () => _seed.Value = (int)(Time.GetTicksUsec() % 99999), "Semilla aleatoria"));
        Row("Semilla", seedRow);
        (_battery, _batteryVal) = Slider(1, 100, 80, v => $"{v:0} %");
        Row("Batería inicial", Pair(_battery, _batteryVal));
        (_science, _scienceVal) = Slider(0, 3, 1, v => $"×{v:0.0}");
        Row("Prioridad científica", Pair(_science, _scienceVal));
        _month = new OptionButton();
        FillMonths();
        _month.Selected = 10;
        Row("Mes (clima Tierra)", _month);
        var abl = new VBoxContainer();
        _rna = new CheckBox { Text = "Red neuronal (percepción)", ButtonPressed = true };
        _fuzzy = new CheckBox { Text = "Lógica difusa (velocidad/cautela)", ButtonPressed = true };
        _ga = new CheckBox { Text = "Algoritmo genético (rutas y plan)", ButtonPressed = true };
        foreach (var c in new[] { _rna, _fuzzy, _ga }) { c.Toggled += _ => Refresh(); abl.AddChild(c); }
        Row("Técnicas (ablación)", abl);
        _control = new OptionButton();
        _control.AddItem("Destino por clic (el operador elige, el rover evalúa y va)");
        _control.AddItem("Misión autónoma (el AG planifica sitios y regreso)");
        _control.AddItem("Rescate en helicóptero (personas atrapadas → hospital)");
        _control.AddItem("Combate contra incendio (helicópteros + carros de bomberos)");
        Row("Dinámica", _control);
        // flota y planificación en el mapa (rescate e incendio)
        var planRow = new HBoxContainer();
        planRow.AddThemeConstantOverride("separation", 6);
        _planLabel = UiTheme.Label("", 11, UiTheme.Text);
        _planLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _planLabel.ClipText = true;
        _planLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        planRow.AddChild(_planLabel);
        planRow.AddChild(UiTheme.Button("🗺 Planificar en el mapa…", OpenPlanner, "Punto de partida, personas o focos, tipo de misión y número de vehículos"));
        Row("Flota y mapa", planRow);
        _planRowLabel = (Label)planRow.GetParent().GetChild(planRow.GetIndex() - 1);
        _planRow = planRow;
        var style = new OptionButton { TooltipText = "Táctico: estética de HUD de anime de mechas (biseles, letra condensada, naranjo/verde/rojo, barras segmentadas). Clásico: azul y ámbar." };
        style.AddItem("Táctico (naranjo, biselado)");
        style.AddItem("Clásico (azul, redondeado)");
        style.Selected = UiTheme.Tactical ? 0 : 1;
        style.ItemSelected += i =>
        {
            UiTheme.SetTactical(i == 0);
            dim.Color = UiTheme.Tactical ? new Color(0.02f, 0.01f, 0.003f, 0.8f) : new Color(0.01f, 0.02f, 0.04f, 0.72f);
            title.AddThemeColorOverride("font_color", UiTheme.Tactical ? UiTheme.Accent : Colors.White);
            if (UiTheme.Tactical) title.AddThemeFontOverride("font", UiTheme.TitleFont); else title.RemoveThemeFontOverride("font");
            _importMsg.Text = "Estilo aplicado · algunos rótulos fijos cambian al reiniciar el juego";
        };
        Row("Estilo de interfaz", style);
        Row("Radio de fondo", new RadioWidget(false));

        // ---- columna de información del planeta y RNA
        var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 6);
        cols.AddChild(right);
        _info = new RichTextLabel { BbcodeEnabled = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 300) };
        right.AddChild(_info);
        right.AddChild(UiTheme.Label("Entrenamiento de la RNA (offline, una vez por ejecución)", 12, UiTheme.Rna));
        _train = new RichTextLabel { BbcodeEnabled = true, CustomMinimumSize = new Vector2(0, 120), ScrollFollowing = true };
        right.AddChild(_train);

        _importMsg = UiTheme.Label("", 11, UiTheme.Ok);
        _importMsg.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _importMsg.Visible = false;
        outer.AddChild(_importMsg);
        if (!Core.Platform.IsMobile) GetWindow().FilesDropped += OnFilesDropped;
        _validation = UiTheme.Label("", 12, UiTheme.Crit);
        _validation.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        outer.AddChild(_validation);
        var bottom = new HBoxContainer();
        bottom.AddThemeConstantOverride("separation", 8);
        _fixBtn = UiTheme.Button("Cambiar a modo sonda", () =>
        {
            if (_control.Selected >= 2)
            {
                // rescate e incendio: helicóptero en la Tierra
                _planet.Selected = PlanetProfile.All.FindIndex(p => p.Id == "tierra");
                _mode.Selected = 2;
            }
            else _mode.Selected = PlanetProfile.All[_planet.Selected].SolidSurface ? 0 : 1;
            Refresh();
        });
        _fixBtn.Visible = false;
        bottom.AddChild(_fixBtn);
        var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        bottom.AddChild(spacer);
        bottom.AddChild(UiTheme.Label(Core.Platform.IsMobile ? "En la misión: toque el terreno = destino · mantener = ver celda · pellizco = zoom" : "Enter = iniciar · en la misión: clic en el terreno = destino · F1–F11 eventos", 11, UiTheme.Dim));
        _startBtn = UiTheme.Button("  VALIDAR E INICIAR MISIÓN  ", TryStart);
        _startBtn.AddThemeFontSizeOverride("font_size", 15);
        bottom.AddChild(_startBtn);
        outer.AddChild(bottom);
        Refresh();
    }

    private void FillTerrain()
    {
        _terrain.Clear();
        _terrain.AddItem("Procedural 200 m (calibrado con GeoClick 40e9efb1)");
        foreach (var g in Core.Scenarios.All) _terrain.AddItem($"GeoClick {g.Title}{(Core.Scenarios.IsImported(g.Id) ? " · importado" : "")}");
    }

    // ------------------------------------------------------------------ importar un informe PDF
    private void OpenPdfDialog()
    {
        if (_pdfDialog == null)
        {
            _pdfDialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true,
                Title = "Informe GeoClick (PDF)", Filters = new[] { "*.pdf ; Informe GeoClick (PDF)" },
                CurrentDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile) + "/Downloads",
            };
            _pdfDialog.FileSelected += StartImport;
            AddChild(_pdfDialog);
        }
        _pdfDialog.PopupCentered(new Vector2I(900, 560));
    }

    private void OnFilesDropped(string[] files)
    {
        if (!_root.Visible) return;
        var pdf = files.FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdf != null) StartImport(pdf);
    }

    public void SelectHeliForTest()
    {
        _mode.Selected = 2;
        Refresh();
    }

    /// <summary>Captura automática: importa como si el usuario hubiera elegido el archivo.</summary>
    public void ImportForTest(string path) => StartImport(path);

    private void StartImport(string path)
    {
        if (_importTask != null) return;
        _importMsg.Visible = true;
        _importMsg.AddThemeColorOverride("font_color", UiTheme.Accent);
        _importMsg.Text = $"Importando {System.IO.Path.GetFileName(path)}: leyendo figuras, coordenadas y tablas del informe…";
        if (_importBtn != null) _importBtn.Disabled = true;
        _importTask = System.Threading.Tasks.Task.Run(() => Core.GeoClickPdf.Import(path));
    }

    public override void _Process(double delta)
    {
        if (_importTask == null || !_importTask.IsCompleted) return;
        var r = _importTask.Result;
        _importTask = null;
        if (_importBtn != null) _importBtn.Disabled = false;
        _importMsg.AddThemeColorOverride("font_color", r.Ok ? (r.Notes.Count > 0 ? UiTheme.Warn : UiTheme.Ok) : UiTheme.Crit);
        _importMsg.Text = (r.Ok ? "✓ " : "✗ ") + r.Summary;
        if (!r.Ok) return;
        Core.Scenarios.Refresh();
        FillTerrain();
        SelectScenario(r.Id);
    }

    private GeoReport? SelectedScenario => _terrain.Selected > 0 && _terrain.Selected - 1 < Core.Scenarios.All.Count ? Core.Scenarios.All[_terrain.Selected - 1] : null;
    private GeoReport Geo => SelectedScenario ?? GeoReport.Santiago;

    private void FillMonths()
    {
        int sel = Math.Max(0, _month.Selected);
        _month.Clear();
        for (int m = 1; m <= 12; m++) _month.AddItem($"{GeoClick.MonthName(m)} · lluvia media {Geo.MonthlyRainMm[m - 1]:0} mm · {Geo.MonthlyTempC[m - 1]:0} °C");
        _month.Selected = sel;
    }

    private static (HSlider, Label) Slider(float min, float max, float val, Func<float, string> fmt)
    {
        var s = new HSlider { MinValue = min, MaxValue = max, Value = val, Step = max <= 3 ? 0.1 : 1, CustomMinimumSize = new Vector2(220, 18) };
        var l = UiTheme.Label(fmt(val), 12);
        s.ValueChanged += v => l.Text = fmt((float)v);
        return (s, l);
    }

    private static HBoxContainer Pair(Control a, Control b)
    {
        var h = new HBoxContainer();
        h.AddChild(a);
        h.AddChild(b);
        return h;
    }

    public void ShowUi(bool v)
    {
        _root.Visible = v;
        if (v) Refresh();
    }

    public bool IsShown => _root.Visible;

    public void TrainProgress(string line)
    {
        _trainLog += line + "\n";
        _train.Text = _trainLog;
    }

    public void TrainDone(NeuralPerception rna)
    {
        RnaReady = true;
        var t = rna.Report.Test!;
        TrainProgress($"[color=#5ad87a]Listo en {rna.Report.Seconds:0.0} s · prueba: exactitud {t.Accuracy:P1} (base {t.Baseline:P1}) · macro-F1 {t.MacroF1:0.000} · recall barranco {t.Recall[2]:P1} · ECE {t.Ece:0.000}[/color]");
        Refresh();
    }

    private MissionPlanner.Result _plan = new();
    private string _planKey = "";
    private Label _planLabel = null!, _planRowLabel = null!;
    private HBoxContainer _planRow = null!;
    private Control? _plannerOverlay;

    /// <summary>Clave del mapa: el plan (celdas) solo vale para el mismo terreno, semilla y dificultad.</summary>
    private string PlanKey => $"{PlanetProfile.All[_planet.Selected].Id}|{SelectedScenario?.Id}|{(int)_seed.Value}|{_diff.Selected}";
    private bool PlanValid => _planKey == PlanKey;

    /// <summary>Captura automática: abre el planificador con puntos dados ("x,y;x,y") y flota.</summary>
    public void OpenPlannerForTest(bool fire, string points, int helis, int trucks)
    {
        _control.Selected = fire ? 3 : 2;
        _plan.Helis = helis;
        _plan.Trucks = trucks;
        _plan.Points.Clear();
        foreach (var p in points.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var xy = p.Split(',');
            if (xy.Length == 2) _plan.Points.Add(new AresX.Core.Cell(int.Parse(xy[0]), int.Parse(xy[1])));
        }
        _planKey = PlanKey;
        Refresh();
        OpenPlanner();
    }

    private void OpenPlanner()
    {
        // rescate / incendio requieren helicóptero en la Tierra
        if (_control.Selected < 2) _control.Selected = 2;
        if (PlanetProfile.All[_planet.Selected].Style != TerrainStyle.Tierra || _mode.Selected != 2)
        {
            _planet.Selected = PlanetProfile.All.FindIndex(p => p.Id == "tierra");
            _mode.Selected = 2;
            FillMonths();
        }
        if (!PlanValid) { _plan.Points.Clear(); _plan.Start = null; }
        _plan.Mode = _control.Selected == 3 ? MissionMode.Incendio : MissionMode.Rescate;
        var cfg = Build();
        _plannerOverlay = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        _root.AddChild(_plannerOverlay);
        Hud.Anchor(_plannerOverlay, 0, 0, 1, 1, 0, 0, 0, 0);
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = Control.MouseFilterEnum.Stop };
        _plannerOverlay.AddChild(dim);
        Hud.Anchor(dim, 0, 0, 1, 1, 0, 0, 0, 0);
        var planner = new MissionPlanner(cfg, _plan, r =>
        {
            if (r != null)
            {
                _plan = r;
                _planKey = PlanKey;
                _control.Selected = r.Mode == MissionMode.Incendio ? 3 : 2;
            }
            _plannerOverlay?.QueueFree();
            _plannerOverlay = null;
            Refresh();
        });
        _plannerOverlay.AddChild(planner);
        Hud.Anchor(planner, 0.5f, 0.5f, 0.5f, 0.5f, -500, -345, 500, 345);
    }

    private MissionConfig Build() => new()
    {
        Helis = _plan.Helis,
        Trucks = _plan.Trucks,
        StartCell = PlanValid ? _plan.Start : null,
        Points = PlanValid ? new System.Collections.Generic.List<AresX.Core.Cell>(_plan.Points) : new(),
        PlanetId = PlanetProfile.All[_planet.Selected].Id,
        Probe = _mode.Selected == 1,
        Heli = _mode.Selected == 2,
        Difficulty = _diff.Selected,
        Seed = (int)_seed.Value,
        BatteryPct = (float)_battery.Value,
        ScienceWeight = (float)_science.Value,
        Month = _month.Selected + 1,
        UseRna = _rna.ButtonPressed,
        UseFuzzy = _fuzzy.ButtonPressed,
        UseGa = _ga.ButtonPressed,
        Mode = _control.Selected switch { 0 => MissionMode.Destino, 1 => MissionMode.Autonomo, 2 => MissionMode.Rescate, _ => MissionMode.Incendio },
        Scenario = PlanetProfile.All[_planet.Selected].Style == TerrainStyle.Tierra && _mode.Selected != 1 ? SelectedScenario : null,
    };

    /// <summary>Selecciona un escenario por id (captura automática).</summary>
    public void SelectScenario(string id)
    {
        _planet.Selected = PlanetProfile.All.FindIndex(p => p.Id == "tierra");
        _mode.Selected = 0;
        for (int i = 0; i < Core.Scenarios.All.Count; i++)
            if (Core.Scenarios.All[i].Id == id) _terrain.Selected = i + 1;
        FillMonths();
        Refresh();
    }

    private void Refresh()
    {
        if (_info == null) return;
        var p = PlanetProfile.All[_planet.Selected];
        var cfg = Build();
        string? err = cfg.Validate();
        _validation.Text = err ?? "";
        _fixBtn.Visible = err != null && (err.Contains("modo") || err.Contains("Helicóptero"));
        _fixBtn.Text = err != null && err.Contains("Helicóptero") ? "Usar helicóptero en la Tierra" : p.SolidSurface ? "Cambiar a rover de ruedas" : "Cambiar a modo sonda";
        _startBtn.Disabled = err != null || !RnaReady;
        bool fleetMode = _control.Selected >= 2;
        _planRow.Visible = _planRowLabel.Visible = fleetMode;
        if (fleetMode)
            _planLabel.Text = $"helicóptero H-1{(_plan.Trucks > 0 ? " + carro de bomberos (si hay zona urbana)" : " solo")} · " +
                              (PlanValid && _plan.Start != null ? "partida elegida" : "partida automática") + " · " +
                              (PlanValid && _plan.Points.Count > 0 ? $"{_plan.Points.Count} {(_control.Selected == 3 ? "focos" : "personas")} en el mapa" : "puntos al azar");
        _startBtn.Text = !RnaReady ? "  Entrenando RNA…  " : "  VALIDAR E INICIAR MISIÓN  ";
        string gas = p.SolidSurface
            ? $"O₂ {p.O2Pct:0.###} % · CO₂ {p.CO2Pct:0.###} % · N₂ {p.N2Pct:0.#} % · SO₂ {p.SO2Ppm:0} ppm"
            : $"H₂ {p.H2Pct:0.#} % · He {p.HePct:0.#} % · CH₄ {p.CH4Pct:0.#} %";
        bool earthRover = p.Style == TerrainStyle.Tierra && _mode.Selected != 1;
        _terrainRow.Visible = _terrainLabel.Visible = earthRover;
        var geo = Geo;
        var sc = earthRover ? SelectedScenario : null;
        string heliWarn = "";
        if (p.Style == TerrainStyle.Tierra && _mode.Selected == 2)
        {
            // margen de potencia del helicóptero en la zona más baja y en la mediana del área (densidad por altura y temperatura)
            float tMonth = geo.MonthlyTempC[cfg.Month - 1];
            float Margin(float elev) => HeliModel.Margin(HeliModel.Density(1.01325f * MathF.Exp(-elev / 1000f / p.ScaleHeightKm), tMonth - 0.0065f * (elev - geo.ElevMedian) + 6f));
            float mLow = Margin(geo.ElevMin), mMed = Margin(geo.ElevMedian);
            heliWarn = mLow < 1f
                ? $"\n[color=#ff6a5a]⚠ Helicóptero: a {geo.ElevMin:0}–{geo.ElevMedian:0} m el aire es muy poco denso (margen de potencia {mLow:0.00}–{mMed:0.00} < 1): no podrá sostener el vuelo y el supervisor lo mantendrá en tierra (H-01). Use el rover.[/color]\n"
                : mMed < 1.12f
                    ? $"\n[color=#ffb347]⚠ Helicóptero: margen de potencia estrecho a esta altura ({mLow:0.00} abajo, {mMed:0.00} en la mediana): volará más lento y con alertas H-01.[/color]\n"
                    : $"\n[color=#8a96aa]Helicóptero: margen de potencia {mLow:0.00} (zona baja) · {mMed:0.00} (mediana).[/color]\n";
        }
        string earth = p.Style != TerrainStyle.Tierra ? "" : heliWarn +
            (sc != null
                ? $"\n[color=#5ab0ff]ESCENARIO A ESCALA REAL · {geo.Consulta}[/color]\n" +
                  $"{sc.N}×{sc.N} celdas de {sc.CellM:0} m · {sc.WidthM / 1000:0.0} × {sc.HeightM / 1000:0.0} km · periodo {geo.Period}\n" +
                  $"Elevación {geo.ElevMin:0}–{geo.ElevMax:0} m (mediana {geo.ElevMedian:0}) · pendiente mediana {geo.SlopeMedian:0.0}° · {geo.Km2Over25:0.0} km² sobre 25°\n" +
                  $"Cobertura: {string.Join(" · ", geo.Cover.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key} {kv.Value / MathF.Max(0.01f, geo.AreaKm2):P0}"))}\n" +
                  $"Agua {geo.WaterKm2:0.0} km² · cicatrices {geo.ScarsHa:0.#} ha · quemado {geo.BurnedHa:0.#} ha · inundaciones {geo.Floods} · heladas {geo.FrostDaysYear:0.#}/año\n"
                : $"\n[color=#5ab0ff]Calibración {geo.Consulta}[/color] (mapa procedural de 200 m)\n") +
            $"Pendientes: {string.Join(" · ", GeoReport.SlopeLabels.Zip(geo.SlopeBins, (l, b) => $"{l} {b:P0}"))}\n" +
            $"Lluvia normal {geo.AnnualRainMm:0} mm/año · máx. diaria {geo.MaxDailyMm:0.#} mm · retorno 1 día: {string.Join(", ", geo.ReturnPeriods.Select(r => $"T{r.Years}={r.Mm:0.#}"))} mm\n" +
            $"Mes elegido: {GeoClick.MonthName(cfg.Month)} · {geo.MonthlyRainMm[cfg.Month - 1]:0} mm · T media {geo.MonthlyTempC[cfg.Month - 1]:0.0} °C\n" +
            "[color=#8a96aa]Datos: Copernicus DEM GLO-30 © DLR/Airbus (ESA), ESA WorldCover 2021 (CC-BY 4.0), Sentinel-2 (Copernicus), ERA5 (Copernicus/ECMWF) vía GeoClick.[/color]";
        _info.Text = $"[font_size=17][color=#ffd860]{p.Name}[/color][/font_size]  [color=#8a96aa]{(p.SolidSurface ? "superficie rocosa" : "sin superficie sólida")}[/color]\n{p.Summary}\n\n" +
            $"[color=#8a96aa]Valores de referencia (aprox., NASA):[/color]\n" +
            $"Gravedad {p.Gravity} m/s² · día solar {p.DayHours:0.#} h · presión {(p.PressureBar < 0.01f ? (p.PressureBar * 1e5f).ToString("0.###") + " Pa" : p.PressureBar.ToString("0.###") + " bar")}\n" +
            $"Temperatura media {p.TempMeanC:0} °C ± {p.TempAmpC:0} · viento {p.WindMean} m/s (ráfagas {p.WindGust})\n{gas}\n" +
            $"Irradiancia solar {p.SolarTopWm2:0} W/m² (tope) · radiación ≈ {p.RadiationMsvH} mSv/h · latencia de enlace ≈ {p.LightTimeMin:0.#} min\n\n" +
            $"[color=#8a96aa]Supuestos de simulación:[/color] día comprimido a {p.SimDayMinutes:0} min · tolerancia térmica interna {p.HardwareMinC:0}…{p.HardwareMaxC:0} °C · límite de viento {p.WindLimit:0} m/s" +
            (p.CorrosionPerStep > 0 ? $" · corrosión {p.CorrosionPerStep} %/paso" : "") + "\n" +
            (sc != null ? "" : string.Join("\n", p.Notes.Select(n => "• " + n))) + earth;
    }

    private void TryStart()
    {
        var cfg = Build();
        string? err = cfg.Validate();
        if (err != null || !RnaReady)
        {
            _validation.Text = err ?? "La RNA todavía se está entrenando…";
            return;
        }
        Config = cfg;
        Start?.Invoke(cfg);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!_root.Visible) return;
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter })
        {
            TryStart();
            GetViewport().SetInputAsHandled();
        }
    }
}
