using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AresX.Core;
using AresX.Sim;
using Godot;

namespace AresX.UI;

/// <summary>
/// PLANIFICADOR EN MAPA (rescate / combate contra incendio). Muestra el terreno de la misión (imagen satelital del
/// informe o relieve procedural) con capas de factibilidad:
///   * naranjo: red urbana de calles donde puede operar un CARRO DE BOMBEROS (solo zona urbana, pendiente &lt; 12°);
///   * rojo rayado: zonas donde el HELICÓPTERO no tiene potencia para el vuelo estacionario (aire poco denso por altura
///     y temperatura del mes elegido); además vuela solo con lluvia &lt; 8 mm/h, ráfagas &lt; 15 m/s y visibilidad ≥ 1,5 km;
///   * azul: agua (carga del balde en incendios).
/// Clic izquierdo: poner el punto de partida o un punto de interés (persona atrapada o foco). Clic derecho: quitar el
/// punto más cercano. Se elige el número de helicópteros y de carros; cada punto indica qué vehículos lo alcanzan.
/// </summary>
public partial class MissionPlanner : PanelContainer
{
    public sealed class Result
    {
        public MissionMode Mode;
        public int Helis = 1, Trucks = 1;   // helicóptero H-1 + (opcional) 1 carro de bomberos
        public Cell? Start;
        public List<Cell> Points = new();
    }

    private readonly MissionConfig _cfg;
    private readonly Result _r;
    private readonly Action<Result?> _done;
    private WorldMap? _map;
    private int _genFrames = 2;   // generar en el hilo principal tras mostrar "generando…"
    private Texture2D? _sat;
    private ImageTexture? _layers;
    private bool[]? _truck;
    private float[]? _margin;
    private PlannerCanvas _canvas = null!;
    private OptionButton _type = null!;
    private CheckBox _trucks = null!;
    private Label _truckNote = null!;
    private RichTextLabel _side = null!;
    private Button _toolStart = null!, _toolPoint = null!;
    private bool _placeStart;
    private Cell? _hover;

    public MissionPlanner(MissionConfig cfg, Result initial, Action<Result?> done)
    {
        _cfg = cfg;
        _r = new Result { Mode = initial.Mode, Helis = initial.Helis, Trucks = initial.Trucks, Start = initial.Start, Points = new List<Cell>(initial.Points) };
        _done = done;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        AddChild(v);
        var title = UiTheme.Label("PLANIFICAR MISIÓN EN EL MAPA", 18, UiTheme.Accent);
        if (UiTheme.Tactical) title.AddThemeFontOverride("font", UiTheme.TitleFont);
        v.AddChild(title);
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 8);
        v.AddChild(top);
        top.AddChild(UiTheme.Label("Tipo", 12, UiTheme.Dim));
        _type = new OptionButton();
        _type.AddItem("Rescate de personas");
        _type.AddItem("Combate contra incendio");
        _type.Selected = _r.Mode == MissionMode.Incendio ? 1 : 0;
        _type.ItemSelected += i => { _r.Mode = i == 1 ? MissionMode.Incendio : MissionMode.Rescate; _r.Points.Clear(); RefreshTools(); };
        top.AddChild(_type);
        top.AddChild(UiTheme.Label("  Helicóptero H-1", 12, UiTheme.Rna));
        _trucks = new CheckBox { Text = "+ carro de bomberos B-1 (6 bomberos)", ButtonPressed = _r.Trucks > 0, TooltipText = "Solo opera en zona urbana (calles con pendiente < 12°)" };
        _trucks.Toggled += on => { _r.Trucks = on ? 1 : 0; RefreshSide(); };
        top.AddChild(_trucks);
        _truckNote = UiTheme.Label("", 11, UiTheme.Dim);
        top.AddChild(_truckNote);

        var tools = new HBoxContainer();
        tools.AddThemeConstantOverride("separation", 6);
        v.AddChild(tools);
        _toolStart = UiTheme.Button("◎ Punto de partida", () => { _placeStart = true; RefreshTools(); }, "Clic en el mapa: base de los vehículos");
        _toolPoint = UiTheme.Button("✚ Personas", () => { _placeStart = false; RefreshTools(); }, "Clic en el mapa: agregar un punto de interés · clic derecho: quitar");
        tools.AddChild(_toolStart);
        tools.AddChild(_toolPoint);
        tools.AddChild(UiTheme.Button("✕ Quitar puntos", () => { _r.Points.Clear(); RefreshSide(); }));
        tools.AddChild(UiTheme.Button("⟲ Todo automático", () => { _r.Points.Clear(); _r.Start = null; RefreshSide(); }, "Partida y puntos al azar (como antes)"));

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10);
        v.AddChild(body);
        _canvas = new PlannerCanvas { Owner2 = this, CustomMinimumSize = new Vector2(560, 560) };
        body.AddChild(_canvas);
        _side = new RichTextLabel { BbcodeEnabled = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(360, 0) };
        body.AddChild(_side);

        var bottom = new HBoxContainer();
        bottom.AddThemeConstantOverride("separation", 8);
        v.AddChild(bottom);
        bottom.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        bottom.AddChild(UiTheme.Button("Cancelar", () => _done(null)));
        var ok = UiTheme.Button("  ACEPTAR PLAN  ", () => _done(_r));
        ok.AddThemeFontSizeOverride("font_size", 14);
        bottom.AddChild(ok);

        AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(UiTheme.WindowBg, 0.985f), UiTheme.Border, 10));
        if (_cfg.Scenario != null) _sat = Scenarios.Satellite(_cfg.Scenario);
        RefreshTools();
    }

    public override void _Process(double delta)
    {
        if (_map == null && _genFrames >= 0 && --_genFrames < 0)
        {
            // el mismo mapa que generará la misión (misma semilla, dificultad y escenario)
            var planet = PlanetProfile.Get(_cfg.PlanetId);
            if (_cfg.Scenario is { IsScenario: false } meta) _cfg.Scenario = Scenarios.Load(meta.Id);   // grillas a escala real
            try { _map = _cfg.Scenario != null ? new WorldMap(planet, _cfg.Scenario, _cfg.Seed, _cfg.Difficulty) : new WorldMap(planet, false, _cfg.Seed, _cfg.Difficulty); }
            catch (Exception ex) { _side.Text = $"[color=#ff6a5a]No se pudo generar el mapa: {ex.Message}[/color]"; return; }
            BuildLayers();
            RefreshSide();
            _canvas.QueueRedraw();
        }
    }

    private void RefreshTools()
    {
        bool fire = _r.Mode == MissionMode.Incendio;
        _toolPoint.Text = fire ? "🔥 Focos de incendio" : "✚ Personas atrapadas";
        _toolStart.AddThemeColorOverride("font_color", _placeStart ? UiTheme.Accent : UiTheme.Text);
        _toolPoint.AddThemeColorOverride("font_color", !_placeStart ? UiTheme.Accent : UiTheme.Text);
        RefreshSide();
        _canvas?.QueueRedraw();
    }

    // ------------------------------------------------------------------ capas de factibilidad
    private void BuildLayers()
    {
        var m = _map!;
        int n = m.N;
        var p = PlanetProfile.Get(_cfg.PlanetId);
        var geo = m.Geo;
        float tMonth = geo.MonthlyTempC[Math.Clamp(_cfg.Month - 1, 0, 11)];
        _margin = new float[n * n];
        _truck = new bool[n * n];
        var city = m.City;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        for (int i = 0; i < n * n; i++)
        {
            var c = new Cell(i % n, i / n);
            float elev = m.Elevation(c);
            _margin[i] = HeliModel.Margin(HeliModel.Density(1.01325f * MathF.Exp(-elev / 1000f / p.ScaleHeightKm), tMonth - 0.0065f * (elev - geo.ElevMedian) + 6f));
            bool inside = m.InBounds(c);
            _truck[i] = inside && city != null && city.Street.Length == n * n && m.Slope[i] < 12f && (city.Street[i] || m.Soils[i] == Soil.Urbano && m.Hazards[i] == Hazard.None);
            Color col = new(0, 0, 0, 0);
            if (!inside) col = new Color(0.02f, 0.02f, 0.03f, 0.75f);
            else if (m.Hazards[i] == Hazard.Water) col = new Color(0.2f, 0.5f, 1f, 0.55f);
            else if (_truck[i]) col = new Color(1f, 0.55f, 0.15f, 0.42f);
            else if (_margin[i] < 1f) col = ((c.X + c.Y) % 6 < 2) ? new Color(1f, 0.15f, 0.1f, 0.55f) : new Color(1f, 0.15f, 0.1f, 0.15f);
            img.SetPixel(c.X, c.Y, col);
        }
        _layers = ImageTexture.CreateFromImage(img);
        // fondo procedural (relieve sombreado + cobertura) si no hay imagen satelital
        if (_sat == null)
        {
            var bg = Image.CreateEmpty(n, n, false, Image.Format.Rgb8);
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                var c = new Cell(x, y);
                float dz = m.Elevation(new Cell(Math.Min(n - 1, x + 1), y)) - m.Elevation(new Cell(Math.Max(0, x - 1), y));
                float shade = Mathf.Clamp(0.75f - dz / (m.CellM * 4f), 0.35f, 1.1f);
                var soil = m.Soils[m.Idx(c)] switch
                {
                    Soil.Bosque => new Color("2f5a2c"), Soil.Matorral => new Color("6b7a3a"), Soil.Pastizal => new Color("9aa45a"), Soil.Urbano => new Color("8a8580"),
                    Soil.Agua => new Color("2a5a9a"), Soil.Cultivo => new Color("b0a050"), Soil.Hielo => new Color("e8eef2"), _ => new Color("8a7a66"),
                };
                bg.SetPixel(x, y, soil * shade);
            }
            _sat = ImageTexture.CreateFromImage(bg);
        }
        bool urban = _truck.Any(b => b);
        _trucks.Disabled = !urban;
        if (!urban) { _trucks.ButtonPressed = false; _r.Trucks = 0; }
        _truckNote.Text = urban ? $"zona urbana: {_truck.Count(b => b) * m.CellM * m.CellM / 1e6f:0.0} km² (naranjo)" : "sin zona urbana: el carro no puede operar aquí";
        _truckNote.AddThemeColorOverride("font_color", urban ? new Color("ff9a4a") : UiTheme.Warn);
    }

    /// <summary>Qué vehículos alcanzan un punto: helicóptero (potencia) y carro (calle a ≤ 60 m).</summary>
    private (bool Heli, bool Truck) Reach(Cell c)
    {
        var m = _map!;
        if (!m.InBounds(c)) return (false, false);
        bool heli = _margin![m.Idx(c)] >= 1f;
        int r = Math.Max(1, (int)MathF.Ceiling(60f / m.CellM));
        bool truck = false;
        for (int dy = -r; dy <= r && !truck; dy++)
        for (int dx = -r; dx <= r && !truck; dx++)
        {
            var o = new Cell(c.X + dx, c.Y + dy);
            if (m.InGrid(o) && _truck![m.Idx(o)]) truck = true;
        }
        return (heli, truck);
    }

    private void RefreshSide()
    {
        if (_side == null) return;
        if (_map == null) { _side.Text = "Generando el mapa de la misión…"; return; }
        var m = _map;
        bool fire = _r.Mode == MissionMode.Incendio;
        var sb = new System.Text.StringBuilder();
        sb.Append($"[color=#ffd860]{(fire ? "COMBATE CONTRA INCENDIO" : "RESCATE DE PERSONAS")}[/color] · helicóptero H-1{(_r.Trucks > 0 ? " + carro de bomberos B-1" : " solo")}\n");
        sb.Append($"Partida: {(_r.Start is Cell s ? $"({s.X},{s.Y}) · {m.Elevation(s):0} m" : "automática (zona plana más amplia)")}\n");
        sb.Append(_r.Points.Count == 0
            ? $"Puntos: al azar ({(fire ? "focos en vegetación cerca de casas" : "personas en montaña, aluvión, inundación y zona urbana")})\n"
            : $"Puntos elegidos: {_r.Points.Count}\n");
        for (int k = 0; k < _r.Points.Count; k++)
        {
            var c = _r.Points[k];
            var (h, t) = Reach(c);
            string what = fire ? (Mission.FuelOf(m.Soils[m.Idx(c)]) > 0.2f ? WorldMap.SoilName(m.Soils[m.Idx(c)]) : "sin combustible (prende la vegetación más cercana)") : WorldMap.SoilName(m.Soils[m.Idx(c)]);
            sb.Append($"  {(fire ? "F" : "V")}{k + 1} ({c.X},{c.Y}) {m.Elevation(c):0} m · {what} · " +
                      $"[color={(h ? "#5ad87a" : "#ff6a5a")}]helicóptero {(h ? "✓" : "✗")}[/color] · [color={(t ? "#5ad87a" : "#8a96aa")}]carro {(t ? "✓" : "✗")}[/color]\n");
        }
        if (_r.Points.Any(c => !Reach(c).Heli && (!Reach(c).Truck || _r.Trucks == 0)))
            sb.Append("[color=#ffb347]⚠ Hay puntos que ningún vehículo de la flota elegida alcanza.[/color]\n");
        if (_r.Trucks > 0 && _r.Points.Count > 0 && !_r.Points.Any(c => Reach(c).Truck))
            sb.Append("[color=#8a96aa]El carro solo actúa en zona urbana: con estos puntos quedará de apoyo en el cuartel.[/color]\n");
        sb.Append("\n[color=#8a96aa]Capas: [color=#ff9a4a]naranjo[/color] calles y zona urbana (carros de bomberos, pendiente < 12°) · " +
                  "[color=#ff6a5a]rojo rayado[/color] sin potencia para el vuelo estacionario del helicóptero · [color=#5ab0ff]azul[/color] agua.\n\n" +
                  "Helicóptero: 4 personas o balde de 1 000 L; vuela con lluvia < 8 mm/h, ráfagas < 15 m/s y visibilidad ≥ 1,5 km; aterriza en pendiente < 12° (si no, cabrestante).\n" +
                  "Carro de bomberos: 6 bomberos, 3 personas o mangueras hasta 60 m con estanque de 3 000 L; solo por calles.\n\n" +
                  "Clic izquierdo: poner · clic derecho: quitar el punto más cercano.[/color]");
        if (_hover is Cell hc && m.InGrid(hc))
        {
            var (h, t) = Reach(hc);
            sb.Append($"\n\n[color=#ffd860]Cursor ({hc.X},{hc.Y})[/color] · {m.Elevation(hc):0} m · pendiente {m.Slope[m.Idx(hc)]:0}° · {WorldMap.SoilName(m.Soils[m.Idx(hc)])}" +
                      $" · margen de potencia {_margin![m.Idx(hc)]:0.00} · helicóptero {(h ? "✓" : "✗")} · carro {(t ? "✓" : "✗")}");
        }
        _side.Text = sb.ToString();
    }

    // ------------------------------------------------------------------ lienzo
    internal void Click(Cell c, bool right)
    {
        if (_map == null || !_map.InBounds(c)) return;
        if (right)
        {
            if (_r.Points.Count > 0)
            {
                var near = _r.Points.OrderBy(p => p.Dist(c)).First();
                if (near.Dist(c) <= Math.Max(4, _map.N / 40)) _r.Points.Remove(near);
            }
        }
        else if (_placeStart) { _r.Start = c; _placeStart = false; }
        else if (_r.Points.Count < 12) _r.Points.Add(c);
        RefreshTools();
    }

    internal void Hover(Cell? c)
    {
        if (c == _hover) return;
        _hover = c;
        RefreshSide();
    }

    internal void DrawMap(Control cv)
    {
        var size = MathF.Min(cv.Size.X, cv.Size.Y);
        var r = new Rect2(0, 0, size, size);
        cv.DrawRect(r, new Color(0, 0, 0, 0.5f));
        if (_map == null) { Gfx.Text(cv, new Vector2(10, size / 2), "generando el mapa…", 14, UiTheme.Dim); return; }
        int n = _map.N;
        float cp = size / n;
        if (_sat != null) cv.DrawTextureRect(_sat, r, false);
        if (_layers != null) cv.DrawTextureRect(_layers, r, false);
        Gfx.Frame(cv, r);
        Vector2 P(Cell c) => new((c.X + 0.5f) * cp, (c.Y + 0.5f) * cp);
        // base (automática o elegida)
        var start = _r.Start ?? _map.Start;
        var sp = P(start);
        cv.DrawRect(new Rect2(sp - new Vector2(7, 7), new Vector2(14, 14)), new Color("5aff8a"), false, 2);
        Gfx.Text(cv, sp + new Vector2(9, -16), _r.Start == null ? "base (auto)" : "PARTIDA", 11, new Color("5aff8a"));
        bool fire = _r.Mode == MissionMode.Incendio;
        for (int k = 0; k < _r.Points.Count; k++)
        {
            var p = P(_r.Points[k]);
            var (h, t) = Reach(_r.Points[k]);
            var col = fire ? new Color("ff6a2a") : h || t ? new Color("ffd860") : new Color("ff4a4a");
            if (fire)
            {
                cv.DrawColoredPolygon(new[] { p + new Vector2(0, -10), p + new Vector2(6, 4), p + new Vector2(0, 8), p + new Vector2(-6, 4) }, col);
                cv.DrawColoredPolygon(new[] { p + new Vector2(0, -3), p + new Vector2(3, 4), p + new Vector2(0, 7), p + new Vector2(-3, 4) }, new Color("ffe08a"));
            }
            else Icons.Draw(cv, "person", p, 16, col);
            Gfx.Text(cv, p + new Vector2(9, -8), $"{(fire ? "F" : "V")}{k + 1}", 11, col);
        }
        if (_hover is Cell hc) cv.DrawRect(new Rect2(hc.X * cp, hc.Y * cp, MathF.Max(2, cp), MathF.Max(2, cp)), Colors.White, false, 1);
        Gfx.Text(cv, new Vector2(6, size - 18), $"{n * _map.CellM / 1000f:0.0} km de lado · celdas de {_map.CellM:0} m", 10, Colors.White);
    }

    internal Cell? CellAt(Control cv, Vector2 pos)
    {
        if (_map == null) return null;
        float size = MathF.Min(cv.Size.X, cv.Size.Y);
        if (pos.X < 0 || pos.Y < 0 || pos.X >= size || pos.Y >= size) return null;
        float cp = size / _map.N;
        return new Cell((int)(pos.X / cp), (int)(pos.Y / cp));
    }
}

/// <summary>Lienzo del planificador (dibujo y clics).</summary>
public partial class PlannerCanvas : Control
{
    public MissionPlanner? Owner2;

    public override void _Ready() => MouseFilter = MouseFilterEnum.Stop;

    public override void _Draw() => Owner2?.DrawMap(this);

    public override void _GuiInput(InputEvent e)
    {
        if (Owner2 == null) return;
        if (e is InputEventMouseMotion mm) { Owner2.Hover(Owner2.CellAt(this, mm.Position)); QueueRedraw(); }
        if (e is InputEventMouseButton { Pressed: true } mb && mb.ButtonIndex is MouseButton.Left or MouseButton.Right && Owner2.CellAt(this, mb.Position) is Cell c)
        {
            Owner2.Click(c, mb.ButtonIndex == MouseButton.Right);
            QueueRedraw();
            AcceptEvent();
        }
    }
}
