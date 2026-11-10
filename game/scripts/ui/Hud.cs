using System;
using System.Collections.Generic;
using System.Linq;
using AresX.AI;
using AresX.Core;
using AresX.Sim;
using Godot;

namespace AresX.UI;

/// <summary>Acciones que la interfaz pide al juego.</summary>
public interface IGameHost
{
    bool Running { get; }
    float SimSpeed { get; }
    void TogglePlay();
    void StepOnce();
    void Restart();
    void Export();
    void ToggleMode();
    void SetSpeed(float s);
    void Inject(EventKind k);
    void RepairSensors();
    void Replan();
    void ToggleRisk();
    void ToggleTruth();
    void OpenLab(int tab);
    void MapEdit(Cell c, string what);
    void CancelDestination();
    string CycleTool();
    void RotateCam(int dir);
    void ZoomCam(int step);
}

/// <summary>Barra superior del centro de control.</summary>
public partial class TopBar : Control
{
    public Mission? Mission;
    public IGameHost? Host;

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color("0e1a2e"));
        DrawLine(new Vector2(0, Size.Y - 1), new Vector2(Size.X, Size.Y - 1), UiTheme.Border);
        bool compact = Platform.IsMobile;
        Gfx.Text(this, new Vector2(10, 6), compact ? "EXPEDICIÓN" : "EXPEDICIÓN UNIVERSO", 15, Colors.White);
        if (!compact) Gfx.Text(this, new Vector2(196, 8), "| CONTROL", 12, UiTheme.Dim);
        var m = Mission;
        if (m == null || Host == null) return;
        float x = compact ? 112 : 268;
        void Chip(string t, Color c, bool fill = false)
        {
            Gfx.Chip(this, new Vector2(x, 6), t, 11, c, fill);
            x += Gfx.ChipWidth(t, 11) + 6;
        }
        Chip($"{m.Planet.Name.ToUpperInvariant()} · {(m.Config.Probe ? "SONDA" : "ROVER")}", new Color("8ec8ff"));
        int hh = (int)m.Env.LocalHour, mm = (int)((m.Env.LocalHour - hh) * 60);
        Chip($"SOL {m.Env.Sol:000} · {hh:00}:{mm:00} {(m.Env.IsDay ? "☀" : "☾")}", m.Env.IsDay ? UiTheme.Accent : new Color("9aa8ff"));
        Chip(Mission.StateName(m.State), UiTheme.StateColor(m.State), true);
        Chip(m.Config.ClickMode ? (m.HasDestination ? "→ DESTINO" : "ESPERANDO DESTINO") : "AUTÓNOMO", m.Config.ClickMode ? UiTheme.Fuzzy : UiTheme.Rna);
        if (!compact)
        {
            Chip(m.Config.AblationName, m.Config.UseRna && m.Config.UseFuzzy && m.Config.UseGa ? UiTheme.Ok : UiTheme.Warn);
            Chip($"semilla {m.Config.Seed}", UiTheme.Dim);
            Chip($"{m.Clock}", UiTheme.Dim);
        }
        Gfx.Text(this, new Vector2(0, 8), $"{(Host.Running ? "▶" : "Ⅱ")} ×{Host.SimSpeed:0}   {Engine.GetFramesPerSecond()} fps", 11, Host.Running ? UiTheme.Ok : UiTheme.Warn, Size.X - 10, HorizontalAlignment.Right);
    }
}

public partial class Hud : CanvasLayer
{
    public Mission? Mission { get; private set; }
    public IGameHost Host = null!;
    public TelemetryPanel Telemetry = null!;
    public DecisionPanel Decision = null!;
    public MiniMap Map = null!;
    public Sparklines Sparks = null!;
    private TopBar _top = null!;
    private RichTextLabel _log = null!, _endText = null!;
    private Label _banner = null!, _envStrip = null!, _objective = null!;
    private PanelContainer _end = null!, _bannerBox = null!;
    private Button _playBtn = null!, _modeBtn = null!, _riskBtn = null!, _truthBtn = null!;
    private readonly List<Button> _speedBtns = new();
    private int _logSeen, _logBuilt = -1;
    private string _filter = "todo";
    private float _bannerTime;
    private Control _root = null!;

    public static void Anchor(Control c, float al, float at, float ar, float ab, float ol, float ot, float or, float ob)
    {
        c.AnchorLeft = al; c.AnchorTop = at; c.AnchorRight = ar; c.AnchorBottom = ab;
        c.OffsetLeft = ol; c.OffsetTop = ot; c.OffsetRight = or; c.OffsetBottom = ob;
    }

    public override void _Ready()
    {
        Layer = 1;
        _root = new Control { Theme = UiTheme.Get(), MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        _top = new TopBar { MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.AddChild(_top);
        Anchor(_top, 0, 0, 1, 0, 0, 0, 0, 28);

        // Panel B · telemetría (izquierda)
        _pTele = MakePanel(0, 0, 0, 0, 6, 32, 326, 506, "TELEMETRÍA · SENSORES CON RUIDO Y VALIDACIÓN", out var leftBox);
        Telemetry = new TelemetryPanel { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        leftBox.AddChild(Scroll(Telemetry));

        // Panel C · decisión explicable (derecha)
        _pDec = MakePanel(1, 0, 1, 0, -340, 32, -6, 506, "DECISIÓN EXPLICABLE · RNA → DIFUSO + AG → SUPERVISOR", out var rightBox);
        Decision = new DecisionPanel { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rightBox.AddChild(Scroll(Decision));

        // Panel D · traza temporal (abajo izquierda)
        _pLog = MakePanel(0, 1, 0, 1, 6, -210, 526, -6, "TRAZA TEMPORAL / HISTORIAL DE DECISIONES", out var logBox);
        var filters = new HBoxContainer();
        filters.AddThemeConstantOverride("separation", 3);
        foreach (var (id, name) in new[] { ("todo", "Todo"), ("decision", "Decisiones"), ("veto", "Vetos/fallos"), ("evento", "Eventos"), ("ag", "AG") })
            filters.AddChild(UiTheme.Button(name, () => { _filter = id; _logBuilt = -1; }, "Filtrar la bitácora"));
        logBox.AddChild(filters);
        _log = new RichTextLabel { BbcodeEnabled = true, ScrollFollowing = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SelectionEnabled = true };
        logBox.AddChild(_log);

        // Panel E · control (abajo centro)
        _pCtl = MakePanel(0.5f, 1, 0.5f, 1, -108, -210, 306, -6, "CONTROL DEL USUARIO / LABORATORIO", out var ctl);
        BuildControls(ctl);

        // Panel A (mini) · mapa (abajo derecha)
        _pMap = MakePanel(1, 1, 1, 1, -328, -210, -6, -6, "MAPA 2D · creencia / verdad (T)", out var mapBox);
        var hb = new HBoxContainer();
        hb.AddThemeConstantOverride("separation", 6);
        hb.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        Map = new MiniMap { CustomMinimumSize = new Vector2(182, 182), MouseFilter = Control.MouseFilterEnum.Stop, TooltipText = "Clic: inspeccionar celda · Mayús+clic: barranco · Ctrl+clic: roca · Alt+clic: despejar" };
        Map.Edit = (c, w) => Host.MapEdit(c, w);
        Sparks = new Sparklines { CustomMinimumSize = new Vector2(118, 182), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        hb.AddChild(Map);
        hb.AddChild(Sparks);
        mapBox.AddChild(hb);

        // franjas centrales
        _bannerBox = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _root.AddChild(_bannerBox);
        Anchor(_bannerBox, 0.5f, 0, 0.5f, 0, -300, 34, 300, 62);
        _banner = UiTheme.Label("", 12);
        _banner.HorizontalAlignment = HorizontalAlignment.Center;
        _banner.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _bannerBox.AddChild(_banner);
        _envStrip = UiTheme.Label("", 11, new Color("cfe0f4"));
        _envStrip.HorizontalAlignment = HorizontalAlignment.Center;
        _envStrip.AddThemeColorOverride("font_outline_color", Colors.Black);
        _envStrip.AddThemeConstantOverride("outline_size", 4);
        _envStrip.MouseFilter = Control.MouseFilterEnum.Ignore;
        _root.AddChild(_envStrip);
        Anchor(_envStrip, 0.5f, 0, 0.5f, 0, -300, 66, 300, 100);
        _envStrip.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _objective = UiTheme.Label("", 11, UiTheme.Accent);
        _objective.HorizontalAlignment = HorizontalAlignment.Center;
        _objective.AddThemeColorOverride("font_outline_color", Colors.Black);
        _objective.AddThemeConstantOverride("outline_size", 4);
        _objective.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _objective.MouseFilter = Control.MouseFilterEnum.Ignore;
        _root.AddChild(_objective);
        Anchor(_objective, 0.5f, 0, 0.5f, 0, -300, 466, 300, 504);

        // fin de misión
        _end = new PanelContainer { Visible = false };
        _root.AddChild(_end);
        Anchor(_end, 0.5f, 0.5f, 0.5f, 0.5f, -270, -170, 270, 120);
        var ev = new VBoxContainer();
        _end.AddChild(ev);
        _endText = new RichTextLabel { BbcodeEnabled = true, FitContent = true, CustomMinimumSize = new Vector2(520, 220) };
        ev.AddChild(_endText);
        var eb = new HBoxContainer();
        eb.AddChild(UiTheme.Button("⟲ Nueva misión (R)", () => Host.Restart()));
        eb.AddChild(UiTheme.Button("⇩ Exportar CSV/JSON (F12)", () => Host.Export()));
        eb.AddChild(UiTheme.Button("Ver laboratorio genético", () => Host.OpenLab(2)));
        eb.AddChild(UiTheme.Button("Cerrar", () => _end.Visible = false));
        ev.AddChild(eb);
        if (Platform.IsMobile) ApplyMobileLayout();
    }

    // ================================================================== interfaz táctil (Android)
    private PanelContainer _pTele = null!, _pDec = null!, _pLog = null!, _pCtl = null!, _pMap = null!;
    private PanelContainer[] _drawers = System.Array.Empty<PanelContainer>();
    private readonly List<Control> _uiHit = new();
    private int _openDrawer = -1;
    private Label? _status;
    private PanelContainer? _statusCard;
    private Button? _playM, _toolBtn;
    private readonly List<Button> _tabBtns = new();

    /// <summary>
    /// Disposición móvil: los cinco paneles se vuelven cajones desplegables (uno a la vez) con pestañas abajo; una
    /// barra de botones grandes a la derecha (ejecutar, cancelar, herramienta del toque, cámara) y una tarjeta de estado.
    /// </summary>
    private void ApplyMobileLayout()
    {
        _drawers = new[] { _pTele, _pDec, _pLog, _pMap, _pCtl };
        foreach (var d in _drawers)
        {
            Anchor(d, 0, 0, 0, 1, 6, 32, 492, -54);
            d.Visible = false;
            _uiHit.Add(d);
        }
        Anchor(_pMap, 0, 0, 0, 1, 6, 32, 456, -54);
        Map.CustomMinimumSize = new Vector2(300, 300);
        Sparks.CustomMinimumSize = new Vector2(126, 300);

        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 4);
        _root.AddChild(tabs);
        Anchor(tabs, 0, 1, 0, 1, 6, -48, 700, -6);
        string[] names = { "Telemetría", "Decisión", "Bitácora", "Mapa", "Control" };
        for (int i = 0; i < names.Length; i++)
        {
            int k = i;
            var b = Big(names[i], () => ToggleDrawer(k), 13);
            tabs.AddChild(b);
            _tabBtns.Add(b);
        }
        tabs.AddChild(Big("Labs", () => Host.OpenLab(0), 13));
        tabs.AddChild(Big("?", () => Host.OpenLab(4), 13));
        _uiHit.Add(tabs);

        var tb = new VBoxContainer();
        tb.AddThemeConstantOverride("separation", 5);
        _root.AddChild(tb);
        Anchor(tb, 1, 0, 1, 0, -132, 34, -6, 380);
        _playM = Big("▶ Ejecutar", () => Host.TogglePlay(), 14);
        tb.AddChild(_playM);
        tb.AddChild(Big("✕ Cancelar destino", () => Host.CancelDestination(), 12));
        _toolBtn = Big("Toque: destino", () => _toolBtn!.Text = "Toque: " + Host.CycleTool(), 12);
        tb.AddChild(_toolBtn);
        var rot = new HBoxContainer();
        rot.AddChild(Big("⟲", () => Host.RotateCam(-1), 16));
        rot.AddChild(Big("⟳", () => Host.RotateCam(1), 16));
        tb.AddChild(rot);
        var zm = new HBoxContainer();
        zm.AddChild(Big("＋", () => Host.ZoomCam(-1), 16));
        zm.AddChild(Big("－", () => Host.ZoomCam(1), 16));
        tb.AddChild(zm);
        _uiHit.Add(tb);

        var card = _statusCard = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        card.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(0.03f, 0.05f, 0.08f, 0.78f), UiTheme.Border, 5));
        _root.AddChild(card);
        Anchor(card, 0, 1, 0, 1, 6, -156, 210, -54);
        _status = UiTheme.Label("", 12);
        _status.MouseFilter = Control.MouseFilterEnum.Ignore;
        card.AddChild(_status);

        Anchor(_bannerBox, 0.5f, 0, 0.5f, 0, -250, 34, 250, 64);
        Anchor(_envStrip, 0.5f, 0, 0.5f, 0, -250, 68, 250, 104);
        Anchor(_objective, 0.5f, 1, 0.5f, 1, -200, -104, 200, -54);
        _objective.AddThemeFontSizeOverride("font_size", 12);
        Anchor(_end, 0.5f, 0.5f, 0.5f, 0.5f, -270, -190, 270, 150);
        _uiHit.Add(_end);
    }

    private static Button Big(string text, System.Action a, int font)
    {
        var b = UiTheme.Button(text, a);
        b.CustomMinimumSize = new Vector2(52, 40);
        b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        b.AddThemeFontSizeOverride("font_size", font);
        b.AddThemeStyleboxOverride("normal", UiTheme.Box(new Color(0.05f, 0.08f, 0.13f, 0.88f), new Color(1, 1, 1, 0.22f), 4, 6));
        b.AddThemeStyleboxOverride("hover", UiTheme.Box(new Color(0.1f, 0.15f, 0.24f, 0.92f), new Color(1, 1, 1, 0.35f), 4, 6));
        b.AddThemeStyleboxOverride("pressed", UiTheme.Box(new Color("34507e"), UiTheme.Accent, 4, 6));
        return b;
    }

    public void OpenDrawer(int k) { if (_drawers.Length > 0 && k != _openDrawer) ToggleDrawer(k); }

    private void ToggleDrawer(int k)
    {
        _openDrawer = _openDrawer == k ? -1 : k;
        for (int i = 0; i < _drawers.Length; i++) _drawers[i].Visible = i == _openDrawer;
        for (int i = 0; i < _tabBtns.Count; i++) _tabBtns[i].AddThemeColorOverride("font_color", i == _openDrawer ? UiTheme.Accent : UiTheme.Text);
        bool open = _openDrawer >= 0;
        if (_statusCard != null) _statusCard.Visible = !open;
        _objective.Visible = !open;
        if (open)
        {
            Anchor(_bannerBox, 0, 0, 1, 0, 500, 34, -140, 64);
            Anchor(_envStrip, 0, 0, 1, 0, 500, 68, -140, 130);
        }
        else
        {
            Anchor(_bannerBox, 0.5f, 0, 0.5f, 0, -250, 34, 250, 64);
            Anchor(_envStrip, 0.5f, 0, 0.5f, 0, -250, 68, 250, 104);
        }
    }

    /// <summary>Botón Atrás de Android: cierra el cajón abierto. Devuelve true si había uno.</summary>
    public bool CloseDrawer()
    {
        if (_openDrawer < 0) return false;
        ToggleDrawer(_openDrawer);
        return true;
    }

    /// <summary>¿El punto (coordenadas del lienzo) cae sobre la interfaz? Los toques ahí no van al terreno.</summary>
    public bool IsOverUi(Vector2 p)
    {
        foreach (var c in _uiHit)
            if (c.IsVisibleInTree() && c.GetGlobalRect().HasPoint(p)) return true;
        return false;
    }

    private void UpdateMobileStatus(Mission m)
    {
        if (_status == null) return;
        _playM!.Text = Host.Running ? "Ⅱ Pausa" : "▶ Ejecutar";
        var e = m.Env;
        string wx = m.Planet.HasRain && e.RainMmH > 0.2f ? $"lluvia {e.RainMmH:0.0} mm/h" : e.DustOpacity > 0.3f ? $"polvo {e.DustOpacity:0.00}" : $"vis {(e.Visibility >= 1000 ? (e.Visibility / 1000).ToString("0.0") + " km" : e.Visibility.ToString("0") + " m")}";
        _status.Text = $"Batería {m.Rover.BatteryPct:0} % ({m.Rover.BatteryWh:0} Wh)\nT int {m.Rover.TempInt:0} °C · ext {e.TempAt(m.Rover.Pos):0} °C\n" +
                       (m.Planet.AirDensity > 0 ? $"Viento {e.WindGust:0}/{m.Planet.WindLimit:0} m/s · {wx}" : wx) +
                       $"\n{(m.LastDecision != null ? SafetySupervisor.ActionName(m.LastDecision.Action) + " " + m.LastDecision.Code : "")} · integridad {m.Rover.Integrity:0} %";
        _status.AddThemeColorOverride("font_color", m.Rover.BatteryPct < 15 ? UiTheme.Crit : UiTheme.Text);
    }

    private static ScrollContainer Scroll(Control content)
    {
        var sc = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        sc.AddChild(content);
        return sc;
    }

    private PanelContainer MakePanel(float al, float at, float ar, float ab, float ol, float ot, float or, float ob, string title, out VBoxContainer box)
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        _root.AddChild(p);
        Anchor(p, al, at, ar, ab, ol, ot, or, ob);
        box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        p.AddChild(box);
        box.AddChild(UiTheme.Label(title, 11, UiTheme.Accent));
        return p;
    }

    private void BuildControls(VBoxContainer v)
    {
        HBoxContainer Row()
        {
            var h = new HBoxContainer();
            h.AddThemeConstantOverride("separation", 3);
            v.AddChild(h);
            return h;
        }
        var r1 = Row();
        _playBtn = UiTheme.Button("▶ Ejecutar", () => Host.TogglePlay(), "Iniciar/pausar la simulación (Espacio)");
        r1.AddChild(_playBtn);
        r1.AddChild(UiTheme.Button("⏭ Un paso", () => Host.StepOnce(), "Ejecutar un solo ciclo observar→decidir→actuar (N)"));
        r1.AddChild(UiTheme.Button("⟲ Reiniciar", () => Host.Restart(), "Volver a la configuración (R)"));
        r1.AddChild(UiTheme.Button("⇩ Exportar", () => Host.Export(), "Exportar traza CSV y resultado JSON (F12)"));
        _modeBtn = UiTheme.Button("Modo: CLIC", () => Host.ToggleMode(), "Destino por clic ↔ misión autónoma (M). En modo clic, haga clic en el terreno: el rover evalúa y navega hasta el destino");
        r1.AddChild(_modeBtn);
        var r2 = Row();
        r2.AddChild(UiTheme.Label("Velocidad", 11, UiTheme.Dim));
        foreach (float s in new[] { 1f, 2f, 4f, 8f })
        {
            var b = UiTheme.Button($"{s}×", () => Host.SetSpeed(s), "Velocidad de la simulación (1-4)");
            _speedBtns.Add(b);
            r2.AddChild(b);
        }
        r2.AddChild(UiTheme.Button("Replanificar", () => Host.Replan(), "Forzar una corrida del AG (P)"));
        _riskBtn = UiTheme.Button("Riesgo ✓", () => Host.ToggleRisk(), "Mostrar el mapa de riesgo estimado por la RNA (V)");
        _truthBtn = UiTheme.Button("Verdad", () => Host.ToggleTruth(), "Mostrar la verdad del simulador (T) — el rover NO la conoce");
        r2.AddChild(_riskBtn);
        r2.AddChild(_truthBtn);
        v.AddChild(UiTheme.Label("Inyectar eventos durante la ejecución:", 11, UiTheme.Dim));
        var grid = new GridContainer { Columns = 6 };
        grid.AddThemeConstantOverride("h_separation", 3);
        grid.AddThemeConstantOverride("v_separation", 3);
        (string N, EventKind? K, string Tip)[] evs =
        {
            ("Sismo", EventKind.Sismo, "F1 · picos de aceleración y caída de rocas"),
            ("Tormenta", EventKind.Tormenta, "F2 · polvo (Marte), lluvia (Tierra), partículas solares (Mercurio), vientos (gigantes)"),
            ("Meteorito", EventKind.Meteorito, "F3 · cráter nuevo junto a la ruta"),
            ("Fallo sensor", EventKind.FalloSensor, "F4 · NaN / saturación / atasco / contradicción en un sensor crítico (cicla)"),
            ("Noche", EventKind.Noche, "F5 · forzar/quitar noche"),
            ("Enlace", EventKind.BajaConectividad, "F6 · baja conectividad: órdenes con retraso, autonomía local"),
            ("Ráfaga", EventKind.Rafaga, "F7 · ráfaga de viento"),
            ("Biofirma", EventKind.Biofirma, "F8 · señal biológica anómala SIMULADA (nunca confirma vida)"),
            ("Alien", EventKind.Alien, "F9 · objeto no identificado (ficción): obstáculo dinámico"),
            ("Bat. 12 %", EventKind.BateriaBaja, "F10 · caso CA-03"),
            ("Remoción", EventKind.Remocion, "F11 · remoción en masa (Tierra)"),
            ("Reparar", null, "Mayús+F4 · restablecer sensores"),
        };
        foreach (var (n, k, tip) in evs)
            grid.AddChild(UiTheme.Button(n, () => { if (k is EventKind kk) Host.Inject(kk); else Host.RepairSensors(); }, tip));
        v.AddChild(grid);
        var r4 = Row();
        r4.AddChild(UiTheme.Label("Labs:", 11, UiTheme.Dim));
        r4.AddChild(UiTheme.Button("RNA", () => Host.OpenLab(0), "Laboratorio RNA (Tab)"));
        r4.AddChild(UiTheme.Button("Difuso", () => Host.OpenLab(1), "Laboratorio difuso"));
        r4.AddChild(UiTheme.Button("Genético", () => Host.OpenLab(2), "Laboratorio genético"));
        r4.AddChild(UiTheme.Button("Políticas", () => Host.OpenLab(3), "Comparación de políticas: 20 semillas × ablaciones"));
        r4.AddChild(UiTheme.Button("Ayuda", () => Host.OpenLab(4), "Controles, leyenda y supuestos (H)"));
    }

    public void Bind(Mission m)
    {
        Mission = m;
        _top.Mission = m;
        _top.Host = Host;
        Telemetry.Mission = m;
        Decision.Mission = m;
        Map.Mission = m;
        Sparks.Mission = m;
        Sparks.Clear();
        _logSeen = 0;
        _logBuilt = -1;
        _end.Visible = false;
        _root.Visible = true;
    }

    public void ShowUi(bool v) => _root.Visible = v;

    public void Flash(string text, Color c, float seconds = 4f)
    {
        _banner.Text = text;
        _banner.AddThemeColorOverride("font_color", c);
        _bannerBox.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(c.Darkened(0.8f), 0.85f), c, 6));
        _bannerBox.Visible = true;
        _bannerTime = seconds;
    }

    public void OnStep()
    {
        if (Mission == null) return;
        Sparks.Push(Mission);
        if (Mission.Finished && !_end.Visible) ShowEnd();
    }

    private void ShowEnd()
    {
        var m = Mission!;
        var met = m.Metrics();
        bool ok = m.State == MissionState.Exito;
        _endText.Text = $"[font_size=22][color={(ok ? "#8cff9a" : "#ff5a4a")}]{(ok ? "ÉXITO" : "FRACASO")}[/color][/font_size]\n[color=#cfd8e8]{m.EndReason}[/color]\n\n" +
            $"[table=4][cell][color=#8a96aa]Pasos[/color][/cell][cell]{met["pasos"]}[/cell][cell][color=#8a96aa]Tiempo de misión[/color][/cell][cell]{(float)met["tiempo_mision_s"] / 60:0.0} min[/cell]" +
            $"[cell][color=#8a96aa]Ciencia[/color][/cell][cell]{met["ciencia"]} ({met["muestras"]} muestras)[/cell][cell][color=#8a96aa]Energía restante[/color][/cell][cell]{met["energia_restante_pct"]} %[/cell]" +
            $"[cell][color=#8a96aa]Distancia[/color][/cell][cell]{met["distancia_m"]} m[/cell][cell][color=#8a96aa]Replanificaciones[/color][/cell][cell]{met["replanificaciones"]}[/cell]" +
            $"[cell][color=#8a96aa]Vetos[/color][/cell][cell]{met["vetos"]}[/cell][cell][color=#8a96aa]Barrancos evitados[/color][/cell][cell]{met["barrancos_evitados"]}[/cell]" +
            $"[cell][color=#8a96aa]Colisiones / caídas[/color][/cell][cell]{met["colisiones"]} / {met["caidas"]}[/cell][cell][color=#8a96aa]Violaciones seguridad[/color][/cell][cell]{met["violaciones_seguridad"]}[/cell]" +
            $"[cell][color=#8a96aa]Integridad[/color][/cell][cell]{met["integridad_pct"]} %[/cell][cell][color=#8a96aa]Decisión media[/color][/cell][cell]{met["ms_decision_medio"]} ms[/cell][/table]\n\n" +
            $"[color=#8a96aa]Política: {m.Config.AblationName} · semilla {m.Config.Seed}. Con la misma semilla y configuración la traza se repite (modo determinista).[/color]";
        _end.Visible = true;
    }

    public override void _Process(double delta)
    {
        var m = Mission;
        if (m == null) return;
        if (Platform.IsMobile) UpdateMobileStatus(m);
        _playBtn.Text = Host.Running ? "Ⅱ Pausa" : "▶ Ejecutar";
        _modeBtn.Text = m.Config.ClickMode ? "Modo: CLIC" : "Modo: AUTO";
        for (int i = 0; i < _speedBtns.Count; i++)
            _speedBtns[i].AddThemeColorOverride("font_color", Mathf.IsEqualApprox(Host.SimSpeed, new[] { 1f, 2f, 4f, 8f }[i]) ? UiTheme.Accent : UiTheme.Text);
        // banner con el último evento importante
        for (; _logSeen < m.Log.Count; _logSeen++)
        {
            var l = m.Log[_logSeen];
            if (l.Kind is "veto" or "evento" or "fallo" || (l.Kind == "ok" && (l.Text.StartsWith("ÉXITO") || l.Text.StartsWith("Muestra"))) || (l.Kind == "alerta" && l.Text.Contains("refugio")))
                Flash(l.Text.Length > 105 ? l.Text[..105] + "…" : l.Text, UiTheme.LogColor(l.Kind));
        }
        if (_bannerTime > 0)
        {
            _bannerTime -= (float)delta;
            if (_bannerTime <= 0) _bannerBox.Visible = false;
        }
        if (m.Log.Count != _logBuilt) RebuildLog(m);

        var e = m.Env;
        var p = m.Planet;
        string wind = p.AirDensity <= 0 ? "sin atmósfera" : $"viento {e.WindSpeed:0.0} m/s {MathX.Compass(e.WindDirDeg)} (ráfaga {e.WindGust:0.0} / límite {p.WindLimit:0})";
        string rain = p.HasRain ? $" · lluvia {e.RainMmH:0.0} mm/h (24 h ≈ {e.Rain24h:0} mm)" : "";
        string events = e.Events.Count == 0 ? "" : "\nEventos activos: " + string.Join(" · ", e.Events.Select(x => x.Kind + (x.Remaining > 0 ? $" ({x.Remaining})" : "")));
        _envStrip.Text = $"{(e.IsDay ? "☀ día" : "☾ noche")} · sol {e.SunElev * 100:0}% · T ext {e.TempAt(m.Rover.Pos):0.0} °C · {wind}{rain} · vis {(e.Visibility >= 1000 ? (e.Visibility / 1000).ToString("0.0") + " km" : e.Visibility.ToString("0") + " m")} · polvo {e.DustOpacity:0.00} · {e.SolarIrradiance():0} W/m²{events}";
        if (m.Config.ClickMode)
        {
            int left = Math.Max(0, m.Route.Count - 1 - m.RouteIdx);
            _objective.Text = m.HasDestination
                ? $"DESTINO: {m.TargetName} {m.Target} · {m.DestReport?.VerdictText} · faltan {left} pasos ({left * WorldMap.CellM:0} m) · muestras {m.Samples}/{m.Map.Sites.Count}{(Platform.IsMobile ? "" : " · clic derecho = cancelar")}"
                : m.PendingDestination != null ? "Destino en tránsito por el enlace degradado…"
                : $"SIN DESTINO · {(Platform.IsMobile ? "toque el terreno" : "haga clic en el terreno (o en el mapa 2D)")} para enviar el rover · muestras {m.Samples}/{m.Map.Sites.Count} · bodega {m.Rover.SampleKg:0.0}/{m.Rover.SampleCapKg:0} kg";
        }
        else
        {
            string plan = m.Plan == null ? "plan: retorno al refugio" : $"plan: {string.Join("→", m.Plan.Order.Where(s => !s.Visited).Select(s => s.Name).DefaultIfEmpty("—"))}→refugio";
            _objective.Text = $"OBJETIVO: {m.TargetName} {m.Target} · {plan} · muestras {m.Samples}/{m.Map.Sites.Count} · bodega {m.Rover.SampleKg:0.0}/{m.Rover.SampleCapKg:0} kg";
        }
    }

    private void RebuildLog(Mission m)
    {
        _logBuilt = m.Log.Count;
        IEnumerable<LogEntry> q = m.Log;
        q = _filter switch
        {
            "decision" => q.Where(l => l.Text.Contains("| S-") || l.Text.StartsWith("S-") || l.Text.Contains("· S-")),
            "veto" => q.Where(l => l.Kind is "veto" or "fallo"),
            "evento" => q.Where(l => l.Kind is "evento" or "alerta"),
            "ag" => q.Where(l => l.Kind == "ag"),
            _ => q,
        };
        var sb = new System.Text.StringBuilder();
        foreach (var l in q.TakeLast(90))
            sb.Append($"[color=#6a7890]T+{l.Step:0000}[/color] [color=#{UiTheme.LogColor(l.Kind).ToHtml(false)}]{Escape(l.Text)}[/color]\n");
        _log.Text = sb.ToString();
    }

    private static string Escape(string s) => s.Replace("[", "[lb]");

    private PanelContainer? _hoverBox;
    private Label? _hoverLabel;

    /// <summary>Etiqueta que sigue al mouse con la previsualización de la celda del terreno (null = ocultar).</summary>
    public void SetHover(string? text)
    {
        if (_hoverBox == null)
        {
            _hoverBox = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            _hoverBox.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(0.03f, 0.05f, 0.08f, 0.9f), new Color("5a9ad8"), 5));
            _hoverLabel = UiTheme.Label("", 11);
            _hoverLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
            _hoverBox.AddChild(_hoverLabel);
            _root.AddChild(_hoverBox);
        }
        _hoverBox.Visible = text != null && _root.Visible;
        if (text != null)
        {
            _hoverLabel!.Text = text;
            _hoverBox.ResetSize();
        }
    }

    public void MoveHover(Vector2 screen)
    {
        if (_hoverBox == null || !_hoverBox.Visible) return;
        var size = _hoverBox.Size;
        var vp = _root.Size;
        var p = screen + new Vector2(18, 18);
        if (p.X + size.X > vp.X) p.X = screen.X - size.X - 12;
        if (p.Y + size.Y > vp.Y) p.Y = screen.Y - size.Y - 12;
        _hoverBox.Position = p;
    }

    public void UpdateToggles(bool risk, bool truth)
    {
        _riskBtn.Text = risk ? "Riesgo ✓" : "Riesgo";
        _truthBtn.Text = truth ? "Verdad ✓" : "Verdad";
    }
}
