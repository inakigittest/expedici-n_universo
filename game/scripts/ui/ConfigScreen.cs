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

    private OptionButton _planet = null!, _mode = null!, _diff = null!, _month = null!, _control = null!;
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
        var dim = new ColorRect { Color = new Color(0.01f, 0.02f, 0.04f, 0.72f), MouseFilter = Control.MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(dim);
        var panel = new PanelContainer();
        _root.AddChild(panel);
        Hud.Anchor(panel, 0.5f, 0.5f, 0.5f, 0.5f, -600, -330, 600, 330);
        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 8);
        panel.AddChild(outer);
        outer.AddChild(UiTheme.Label("EXPEDICIÓN UNIVERSO · exploración planetaria con IA híbrida", 22, Colors.White));
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
        _mode.ItemSelected += _ => Refresh();
        Row("Modo", _mode);
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
        for (int m = 1; m <= 12; m++) _month.AddItem($"{GeoClick.MonthName(m)} · lluvia media {GeoClick.MonthlyRainMm[m - 1]:0} mm");
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
        Row("Dinámica", _control);

        // ---- columna de información del planeta y RNA
        var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 6);
        cols.AddChild(right);
        _info = new RichTextLabel { BbcodeEnabled = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 300) };
        right.AddChild(_info);
        right.AddChild(UiTheme.Label("Entrenamiento de la RNA (offline, una vez por ejecución)", 12, UiTheme.Rna));
        _train = new RichTextLabel { BbcodeEnabled = true, CustomMinimumSize = new Vector2(0, 120), ScrollFollowing = true };
        right.AddChild(_train);

        _validation = UiTheme.Label("", 12, UiTheme.Crit);
        _validation.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        outer.AddChild(_validation);
        var bottom = new HBoxContainer();
        bottom.AddThemeConstantOverride("separation", 8);
        _fixBtn = UiTheme.Button("Cambiar a modo sonda", () => { _mode.Selected = PlanetProfile.All[_planet.Selected].SolidSurface ? 0 : 1; Refresh(); });
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

    private MissionConfig Build() => new()
    {
        PlanetId = PlanetProfile.All[_planet.Selected].Id,
        Probe = _mode.Selected == 1,
        Difficulty = _diff.Selected,
        Seed = (int)_seed.Value,
        BatteryPct = (float)_battery.Value,
        ScienceWeight = (float)_science.Value,
        Month = _month.Selected + 1,
        UseRna = _rna.ButtonPressed,
        UseFuzzy = _fuzzy.ButtonPressed,
        UseGa = _ga.ButtonPressed,
        Mode = _control.Selected == 0 ? MissionMode.Destino : MissionMode.Autonomo,
    };

    private void Refresh()
    {
        if (_info == null) return;
        var p = PlanetProfile.All[_planet.Selected];
        var cfg = Build();
        string? err = cfg.Validate();
        _validation.Text = err ?? "";
        _fixBtn.Visible = err != null && err.Contains("modo");
        _fixBtn.Text = p.SolidSurface ? "Cambiar a rover de ruedas" : "Cambiar a modo sonda";
        _startBtn.Disabled = err != null || !RnaReady;
        _startBtn.Text = !RnaReady ? "  Entrenando RNA…  " : "  VALIDAR E INICIAR MISIÓN  ";
        string gas = p.SolidSurface
            ? $"O₂ {p.O2Pct:0.###} % · CO₂ {p.CO2Pct:0.###} % · N₂ {p.N2Pct:0.#} % · SO₂ {p.SO2Ppm:0} ppm"
            : $"H₂ {p.H2Pct:0.#} % · He {p.HePct:0.#} % · CH₄ {p.CH4Pct:0.#} %";
        string earth = p.Style == TerrainStyle.Tierra
            ? $"\n[color=#5ab0ff]Calibración {GeoClick.Consulta}[/color]\nPendientes GeoClick: {string.Join(" · ", GeoClick.SlopeLabels.Zip(GeoClick.SlopeBins, (l, b) => $"{l} {b:P0}"))}\n" +
              $"Lluvia normal {GeoClick.AnnualRainMm} mm/año · retorno 1 día: {string.Join(", ", GeoClick.ReturnPeriods.Select(r => $"T{r.Years}={r.Mm} mm"))}\nMes elegido: {GeoClick.MonthName(cfg.Month)} · {GeoClick.MonthlyRainMm[cfg.Month - 1]} mm · T media {GeoClick.MonthlyTempC[cfg.Month - 1]} °C"
            : "";
        _info.Text = $"[font_size=17][color=#ffd860]{p.Name}[/color][/font_size]  [color=#8a96aa]{(p.SolidSurface ? "superficie rocosa" : "sin superficie sólida")}[/color]\n{p.Summary}\n\n" +
            $"[color=#8a96aa]Valores de referencia (aprox., NASA):[/color]\n" +
            $"Gravedad {p.Gravity} m/s² · día solar {p.DayHours:0.#} h · presión {(p.PressureBar < 0.01f ? (p.PressureBar * 1e5f).ToString("0.###") + " Pa" : p.PressureBar.ToString("0.###") + " bar")}\n" +
            $"Temperatura media {p.TempMeanC:0} °C ± {p.TempAmpC:0} · viento {p.WindMean} m/s (ráfagas {p.WindGust})\n{gas}\n" +
            $"Irradiancia solar {p.SolarTopWm2:0} W/m² (tope) · radiación ≈ {p.RadiationMsvH} mSv/h · latencia de enlace ≈ {p.LightTimeMin:0.#} min\n\n" +
            $"[color=#8a96aa]Supuestos de simulación:[/color] día comprimido a {p.SimDayMinutes:0} min · tolerancia térmica interna {p.HardwareMinC:0}…{p.HardwareMaxC:0} °C · límite de viento {p.WindLimit:0} m/s" +
            (p.CorrosionPerStep > 0 ? $" · corrosión {p.CorrosionPerStep} %/paso" : "") + "\n" +
            string.Join("\n", p.Notes.Select(n => "• " + n)) + earth;
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
