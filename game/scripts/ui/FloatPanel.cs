using System;
using System.Collections.Generic;
using System.Linq;
using AresX.Core;
using Godot;

namespace AresX.UI;

public enum DockSide { Float, Left, Right }

/// <summary>
/// Ventana del HUD. Puede ir ACOPLADA a la izquierda o a la derecha (las ventanas de un acople se apilan sin taparse y
/// se reparten el alto: al minimizar o cerrar una, las demás crecen) o FLOTANTE (se arrastra libremente). Arrastrar la
/// barra de título hasta un borde de la pantalla la acopla; el botón ◧/◨/▣ cambia el acople. Doble clic en el título =
/// minimizar. La esquina inferior derecha cambia el tamaño (acoplada: ancho del acople y peso en altura).
/// En móvil (Docked) es un cajón fijo que posiciona el diseño táctil.
/// </summary>
public partial class FloatPanel : PanelContainer
{
    public readonly string Id;
    public readonly VBoxContainer Content = new();
    public bool Docked;                    // móvil: cajón fijo
    private bool _closable = true;
    /// <summary>false = solo se minimiza (CONTROL).</summary>
    public bool Closable
    {
        get => _closable;
        set { _closable = value; _closeBtn.Visible = !Docked && value; }
    }
    public DockSide Side = DockSide.Float;
    public float Weight = 1f;              // parte del alto del acople
    public int Order;                      // posición en el acople (de arriba hacia abajo)
    public bool Minimized { get; private set; }
    public Rect2 DefaultRect;
    public Rect2 FloatRect;                // última posición/tamaño como flotante
    public event Action? Changed;          // movida, redimensionada, minimizada, acoplada o mostrada/ocultada
    public event Action? Shown;            // se hizo visible (para refrescar contenido)

    private readonly Label _title;
    private readonly Button _minBtn, _closeBtn, _dockBtn;
    private readonly HBoxContainer _bar;
    private readonly Control _grip;
    private bool _drag, _resize, _moved;
    private Vector2 _dragOffset;
    public const float TitleH = 26f;
    public static readonly Vector2 MinSize = new(200, 90);

    public FloatPanel(string id, string title, Rect2 defaultRect)
    {
        Id = id;
        DefaultRect = FloatRect = defaultRect;
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(0.03f, 0.045f, 0.075f, 0.93f), UiTheme.Border, 6));
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 3);
        AddChild(v);
        _bar = new HBoxContainer { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.Move, CustomMinimumSize = new Vector2(0, 18) };
        _bar.AddThemeConstantOverride("separation", 2);
        _title = UiTheme.Label(title, 11, UiTheme.Accent);
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _title.ClipText = true;
        _title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _title.MouseFilter = MouseFilterEnum.Ignore;
        _bar.AddChild(_title);
        _dockBtn = SmallButton("▣", "Acoplar: izquierda → derecha → flotante (o arrastre el título a un borde)", CycleDock);
        _minBtn = SmallButton("–", "Minimizar / restaurar (doble clic en el título)", ToggleMinimize);
        _closeBtn = SmallButton("×", "Cerrar (se vuelve a abrir desde VENTANAS)", () => SetOpen(false));
        _bar.AddChild(_dockBtn);
        _bar.AddChild(_minBtn);
        _bar.AddChild(_closeBtn);
        _bar.GuiInput += OnBarInput;
        v.AddChild(_bar);
        Content.SizeFlagsVertical = SizeFlags.ExpandFill;
        Content.AddThemeConstantOverride("separation", 4);
        v.AddChild(Content);
        _grip = new Control { CustomMinimumSize = new Vector2(14, 14), MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.Fdiagsize };
        _grip.Draw += () =>
        {
            var c = new Color(1, 1, 1, 0.35f);
            for (int k = 0; k < 3; k++) _grip.DrawLine(new Vector2(13, 4 + k * 4), new Vector2(4 + k * 4, 13), c, 1);
        };
        _grip.GuiInput += OnGripInput;
        AddChild(_grip);
        Position = defaultRect.Position;
        Size = defaultRect.Size;
        WindowManager.Register(this);
    }

    private static Button SmallButton(string t, string tip, Action a)
    {
        var b = UiTheme.Button(t, a, tip);
        b.CustomMinimumSize = new Vector2(20, 18);
        b.AddThemeFontSizeOverride("font_size", 12);
        return b;
    }

    public override void _Ready()
    {
        _minBtn.Visible = _dockBtn.Visible = !Docked;
        _closeBtn.Visible = !Docked && Closable;
        _grip.Visible = !Docked && !Minimized;
        RefreshDockButton();
    }

    public void SetTitle(string t) => _title.Text = t;

    private void RefreshDockButton()
    {
        _dockBtn.Text = Side switch { DockSide.Left => "◧", DockSide.Right => "◨", _ => "▣" };
        _dockBtn.TooltipText = Side switch
        {
            DockSide.Left => "Acoplada a la izquierda · clic: pasar a la derecha",
            DockSide.Right => "Acoplada a la derecha · clic: dejar flotante",
            _ => "Flotante · clic: acoplar a la izquierda (o arrastre el título a un borde)",
        };
    }

    public override void _Notification(int what)
    {
        // la esquina de redimensionar va siempre abajo a la derecha, encima del contenido
        if (what == NotificationSortChildren && _grip != null)
            FitChildInRect(_grip, new Rect2(Size - new Vector2(15, 15), new Vector2(14, 14)));
    }

    public void SetOpen(bool open)
    {
        if (Visible == open) return;
        Visible = open;
        if (open)
        {
            BringToFront();
            Clamp();
            Shown?.Invoke();
        }
        Changed?.Invoke();
    }

    public void ToggleOpen() => SetOpen(!Visible);

    public void SetMinimized(bool min, bool notify = true)
    {
        if (Docked || Minimized == min) return;
        Minimized = min;
        Content.Visible = !min;
        _grip.Visible = !min;
        if (Side == DockSide.Float)
        {
            if (min)
            {
                FloatRect = new Rect2(Position, Size);
                Size = new Vector2(Size.X, TitleH);
            }
            else Size = FloatRect.Size;
        }
        if (!min) Shown?.Invoke();
        _minBtn.Text = min ? "□" : "–";
        if (notify) Changed?.Invoke();
    }

    public void ToggleMinimize() => SetMinimized(!Minimized);

    public void SetSide(DockSide side, int order = -1, bool notify = true)
    {
        if (Side == DockSide.Float && side != DockSide.Float) FloatRect = new Rect2(Position, Minimized ? new Vector2(Size.X, FloatRect.Size.Y) : Size);
        bool toFloat = Side != DockSide.Float && side == DockSide.Float;
        Side = side;
        if (order >= 0) Order = order;
        if (toFloat)
        {
            Size = Minimized ? new Vector2(FloatRect.Size.X, TitleH) : FloatRect.Size;
            Clamp();
        }
        RefreshDockButton();
        if (notify) Changed?.Invoke();
    }

    private void CycleDock()
    {
        var next = Side switch { DockSide.Float => DockSide.Left, DockSide.Left => DockSide.Right, _ => DockSide.Float };
        if (next == DockSide.Float) Position = FloatRect.Position;
        SetSide(next, next == DockSide.Float ? -1 : 999);
    }

    public void BringToFront()
    {
        if (GetParent() is Node p) p.MoveChild(this, p.GetChildCount() - 1);
    }

    /// <summary>Mantiene la ventana flotante dentro del área visible (con al menos la barra de título a la vista).</summary>
    public void Clamp()
    {
        if (Docked || Side != DockSide.Float || GetParent() is not Control parent) return;
        var area = parent.Size;
        if (area.X < 50 || area.Y < 50) return;
        var s = Size;
        s.X = MathF.Min(MathF.Max(s.X, MinSize.X), area.X - 4);
        if (!Minimized) s.Y = MathF.Min(MathF.Max(s.Y, MinSize.Y), area.Y - 30);
        Size = s;
        var p = Position;
        p.X = Math.Clamp(p.X, 0, MathF.Max(0, area.X - s.X));
        p.Y = Math.Clamp(p.Y, 28, MathF.Max(28, area.Y - 24));
        Position = p;
    }

    /// <summary>Lo coloca el gestor de acoples (sin disparar Changed).</summary>
    public void SetDockRect(Rect2 r)
    {
        Position = r.Position;
        Size = r.Size;
    }

    public void ApplyState(Rect2 floatRect, DockSide side, int order, float weight, bool open, bool minimized)
    {
        if (Docked) return;
        if (Minimized) SetMinimized(false, false);
        FloatRect = floatRect;
        Side = side;
        Order = order;
        Weight = Math.Clamp(weight, 0.2f, 6f);
        if (side == DockSide.Float)
        {
            Position = floatRect.Position;
            Size = floatRect.Size;
        }
        Visible = open;
        if (minimized) SetMinimized(true, false);
        RefreshDockButton();
        Clamp();
    }

    private void OnBarInput(InputEvent e)
    {
        if (Docked) return;
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            if (mb.Pressed && mb.DoubleClick)
            {
                ToggleMinimize();
                _drag = false;
            }
            else if (mb.Pressed)
            {
                _drag = true;
                _moved = false;
                _dragOffset = GetGlobalMousePosition() - GlobalPosition;
                BringToFront();
            }
            else if (_drag)
            {
                _drag = false;
                if (_moved) WindowManager.Drop(this, GetGlobalMousePosition());
            }
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion mm && _drag)
        {
            if (!_moved && mm.Relative.LengthSquared() < 9) return;
            if (!_moved && Side != DockSide.Float)
            {
                // despegar del acople: conserva el tamaño flotante y sigue al mouse
                var keep = FloatRect.Size;
                Side = DockSide.Float;
                Size = Minimized ? new Vector2(keep.X, TitleH) : keep;
                _dragOffset = new Vector2(MathF.Min(_dragOffset.X, keep.X - 40), 10);
                RefreshDockButton();
                WindowManager.Relayout();
            }
            _moved = true;
            GlobalPosition = GetGlobalMousePosition() - _dragOffset;
            WindowManager.ShowDropHint(GetGlobalMousePosition());
            AcceptEvent();
        }
    }

    private void OnGripInput(InputEvent e)
    {
        if (Docked) return;
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            _resize = mb.Pressed;
            if (!mb.Pressed)
            {
                if (Side == DockSide.Float) FloatRect = new Rect2(Position, Size);
                Clamp();
                Changed?.Invoke();
            }
            else BringToFront();
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion mm && _resize)
        {
            if (Side == DockSide.Float)
            {
                var s = Size + mm.Relative;
                Size = new Vector2(MathF.Max(MinSize.X, s.X), MathF.Max(MinSize.Y, s.Y));
            }
            else
            {
                WindowManager.ResizeDock(Side, Side == DockSide.Left ? mm.Relative.X : -mm.Relative.X);
                if (MathF.Abs(mm.Relative.Y) > 0 && Size.Y > 40)
                    Weight = Math.Clamp(Weight * (Size.Y + mm.Relative.Y) / Size.Y, 0.2f, 6f);
                WindowManager.Relayout();
            }
            AcceptEvent();
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true } && Side == DockSide.Float) BringToFront();
    }
}

/// <summary>
/// Registro de las ventanas (HUD y laboratorios): acoples izquierdo/derecho, presets, persistencia en
/// user://hud_layout.cfg y el rectángulo central libre (para ver el vehículo y la franja de decisión).
/// </summary>
public static class WindowManager
{
    private const string LayoutPath = "user://hud_layout.cfg";
    private static readonly Dictionary<string, FloatPanel> Panels = new();
    private static bool _loading;
    public static event Action? AnyChanged;
    /// <summary>Rectángulo central que dejan libres los acoples (coordenadas del lienzo).</summary>
    public static event Action<Rect2>? CenterChanged;
    public static Rect2 Center { get; private set; }
    public static Vector2 Area = new(1280, 720);
    public static float LeftW = 330, RightW = 320;
    public const float TopY = 30, Gap = 4, EdgeSnap = 90;
    public static float BottomReserve;            // alto reservado abajo (p. ej. barra móvil)
    public static Control? HintLayer;
    private static ColorRect? _hint;

    public static IEnumerable<FloatPanel> All => Panels.Values;

    public static void Register(FloatPanel p)
    {
        Panels[p.Id] = p;
        p.Changed += () =>
        {
            if (_loading) return;
            Relayout();
            Save();
            AnyChanged?.Invoke();
        };
    }

    public static FloatPanel? Get(string id) => Panels.GetValueOrDefault(id);

    public static bool IsOpen(string id) => Panels.TryGetValue(id, out var p) && p.Visible;

    public static void Toggle(string id)
    {
        if (Panels.TryGetValue(id, out var p)) p.ToggleOpen();
    }

    public static void Open(string id, bool open = true)
    {
        if (!Panels.TryGetValue(id, out var p)) return;
        _loading = true;
        if (open && p.Minimized) p.SetMinimized(false, false);
        _loading = false;
        p.SetOpen(open);
        if (open) { Relayout(); Save(); AnyChanged?.Invoke(); }
    }

    /// <summary>¿Hay una ventana visible bajo el punto? (para que los clics no lleguen al terreno).</summary>
    public static bool IsOverAny(Vector2 point) =>
        Panels.Values.Any(p => p.IsVisibleInTree() && p.GetGlobalRect().HasPoint(point));

    private static List<FloatPanel> InDock(DockSide side) =>
        Panels.Values.Where(p => !p.Docked && p.Visible && p.Side == side).OrderBy(p => p.Order).ToList();

    public static void ResizeDock(DockSide side, float dx)
    {
        float max = MathF.Max(260, Area.X * 0.42f);
        if (side == DockSide.Left) LeftW = Math.Clamp(LeftW + dx, 240, max);
        else RightW = Math.Clamp(RightW + dx, 240, max);
    }

    /// <summary>
    /// Reparte cada acople: las ventanas minimizadas ocupan solo su barra de título y el resto del alto se divide según
    /// el peso de las abiertas. Después publica el rectángulo central libre.
    /// </summary>
    public static void Relayout()
    {
        if (Platform.IsMobile) return;
        float leftEdge = 0, rightEdge = Area.X;
        foreach (var side in new[] { DockSide.Left, DockSide.Right })
        {
            var list = InDock(side);
            for (int i = 0; i < list.Count; i++) list[i].Order = i;
            if (list.Count == 0) continue;
            float w = side == DockSide.Left ? LeftW : RightW;
            w = MathF.Min(w, MathF.Max(240, Area.X * 0.42f));
            float x = side == DockSide.Left ? 6 : Area.X - w - 6;
            float top = TopY, bottom = Area.Y - 6 - BottomReserve;
            float fixedH = list.Count(p => p.Minimized) * FloatPanel.TitleH + Gap * (list.Count - 1);
            float flex = MathF.Max(0, bottom - top - fixedH);
            float wsum = list.Where(p => !p.Minimized).Sum(p => p.Weight);
            float y = top;
            foreach (var p in list)
            {
                float h = p.Minimized ? FloatPanel.TitleH : wsum <= 0 ? 0 : flex * p.Weight / wsum;
                p.SetDockRect(new Rect2(x, y, w, h));
                y += h + Gap;
            }
            if (side == DockSide.Left) leftEdge = x + w + Gap;
            else rightEdge = x - Gap;
        }
        Center = new Rect2(leftEdge, TopY, MathF.Max(120, rightEdge - leftEdge), Area.Y - TopY - BottomReserve);
        CenterChanged?.Invoke(Center);
    }

    /// <summary>Al soltar una ventana arrastrada: cerca de un borde se acopla (en la posición vertical del mouse); si no, flota.</summary>
    public static void Drop(FloatPanel p, Vector2 mouse)
    {
        HideHint();
        var side = mouse.X < EdgeSnap ? DockSide.Left : mouse.X > Area.X - EdgeSnap ? DockSide.Right : DockSide.Float;
        if (side == DockSide.Float)
        {
            p.FloatRect = new Rect2(p.Position, p.Minimized ? new Vector2(p.Size.X, p.FloatRect.Size.Y) : p.Size);
            p.SetSide(DockSide.Float);
            return;
        }
        var list = InDock(side).Where(o => o != p).ToList();
        int idx = list.Count(o => o.Position.Y + o.Size.Y / 2 < mouse.Y);
        for (int i = 0; i < list.Count; i++) list[i].Order = i < idx ? i : i + 1;
        p.SetSide(side, idx);
    }

    public static void ShowDropHint(Vector2 mouse)
    {
        if (HintLayer == null) return;
        if (_hint == null)
        {
            _hint = new ColorRect { Color = new Color(UiTheme.Accent, 0.18f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
            HintLayer.AddChild(_hint);
        }
        bool left = mouse.X < EdgeSnap, right = mouse.X > Area.X - EdgeSnap;
        _hint.Visible = left || right;
        if (!_hint.Visible) return;
        float w = left ? LeftW : RightW;
        _hint.Position = new Vector2(left ? 6 : Area.X - w - 6, TopY);
        _hint.Size = new Vector2(w, Area.Y - TopY - 6 - BottomReserve);
    }

    private static void HideHint()
    {
        if (_hint != null) _hint.Visible = false;
    }

    private static void Set(string id, DockSide side, int order, float weight, bool open, bool min = false, Rect2? floatRect = null)
    {
        if (!Panels.TryGetValue(id, out var p)) return;
        p.ApplyState(floatRect ?? p.DefaultRect, side, order, weight, open, min);
    }

    /// <summary>
    /// Presets. "conduccion": vehículo al centro, decisión a la izquierda, control y mapa a la derecha (por defecto).
    /// "completa": paneles de misión. "analisis": laboratorios en vivo (en modo resumen al estar acoplados).
    /// "limpia": solo el mundo y la franja de decisión.
    /// </summary>
    public static void ApplyPreset(string name, Vector2 area)
    {
        if (Platform.IsMobile) return;
        Area = area;
        _loading = true;
        foreach (var p in Panels.Values) Set(p.Id, DockSide.Float, 0, 1, false);
        LeftW = 330;
        RightW = 300;
        switch (name)
        {
            case "analisis":
                LeftW = 360;
                RightW = 360;
                Set("lab_rna", DockSide.Left, 0, 1.1f, true);
                Set("lab_fuzzy", DockSide.Left, 1, 1.2f, true);
                Set("lab_ga", DockSide.Right, 1, 1.4f, true);
                Set("ctl", DockSide.Right, 0, 1, true, true);
                break;
            case "limpia":
                RightW = 300;
                Set("ia", DockSide.Right, 0, 1, true, true);
                Set("ctl", DockSide.Right, 1, 1, true, true);
                break;
            case "completa":
                Set("tele", DockSide.Left, 0, 1.6f, true);
                Set("log", DockSide.Left, 1, 1f, true);
                Set("ctl", DockSide.Right, 0, 1f, true, true);
                Set("dec", DockSide.Right, 1, 1.8f, true);
                Set("map", DockSide.Right, 2, 1f, true);
                break;
            default:   // conduccion: vehículo al centro; a la derecha la IA en vivo, el control y el mapa
                RightW = 300;
                Set("ia", DockSide.Right, 0, 3.2f, true);
                Set("ctl", DockSide.Right, 1, 1.3f, true);
                Set("map", DockSide.Right, 2, 1f, true, true);
                break;
        }
        _loading = false;
        Relayout();
        Save();
        AnyChanged?.Invoke();
    }

    public static void Save()
    {
        if (Platform.IsMobile) return;
        var cfg = new ConfigFile();
        cfg.SetValue("_dock", "left", LeftW);
        cfg.SetValue("_dock", "right", RightW);
        cfg.SetValue("_dock", "version", 3);
        foreach (var (id, p) in Panels)
        {
            cfg.SetValue(id, "rect", p.Side == DockSide.Float && !p.Minimized ? new Rect2(p.Position, p.Size) : p.FloatRect);
            cfg.SetValue(id, "open", p.Visible);
            cfg.SetValue(id, "min", p.Minimized);
            cfg.SetValue(id, "side", (int)p.Side);
            cfg.SetValue(id, "order", p.Order);
            cfg.SetValue(id, "weight", p.Weight);
        }
        cfg.Save(LayoutPath);
    }

    /// <summary>Carga la disposición guardada (versión 2); si no hay, aplica "conduccion". Devuelve true si había una.</summary>
    public static bool Load(Vector2 area)
    {
        if (Platform.IsMobile) return false;
        Area = area;
        var cfg = new ConfigFile();
        if (cfg.Load(LayoutPath) != Error.Ok || (int)cfg.GetValue("_dock", "version", 1) < 3)
        {
            ApplyPreset("conduccion", area);
            return false;
        }
        _loading = true;
        LeftW = (float)cfg.GetValue("_dock", "left", 330f);
        RightW = (float)cfg.GetValue("_dock", "right", 300f);
        foreach (var (id, p) in Panels)
        {
            if (!cfg.HasSection(id)) continue;
            p.ApplyState((Rect2)cfg.GetValue(id, "rect", p.DefaultRect), (DockSide)(int)cfg.GetValue(id, "side", 0), (int)cfg.GetValue(id, "order", 0),
                (float)cfg.GetValue(id, "weight", 1f), (bool)cfg.GetValue(id, "open", false), (bool)cfg.GetValue(id, "min", false));
        }
        _loading = false;
        Relayout();
        AnyChanged?.Invoke();
        return true;
    }

    public static void OnResized(Vector2 area)
    {
        Area = area;
        foreach (var p in Panels.Values) p.Clamp();
        Relayout();
    }
}
