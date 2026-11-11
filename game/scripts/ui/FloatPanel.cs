using System;
using System.Collections.Generic;
using System.Linq;
using AresX.Core;
using Godot;

namespace AresX.UI;

/// <summary>
/// Ventana flotante del HUD: barra de título arrastrable (doble clic = minimizar), botones minimizar y cerrar, esquina
/// para redimensionar y se mantiene dentro de la pantalla. En móvil (Docked) se comporta como un panel fijo: la
/// posición la pone el diseño de cajones y no se arrastra.
/// </summary>
public partial class FloatPanel : PanelContainer
{
    public readonly string Id;
    public readonly VBoxContainer Content = new();
    public bool Docked;
    public bool Minimized { get; private set; }
    public Rect2 DefaultRect;
    public event Action? Changed;          // movida, redimensionada, minimizada o mostrada/ocultada
    public event Action? Shown;            // se hizo visible (para refrescar contenido)

    private readonly Label _title;
    private readonly Button _minBtn, _closeBtn;
    private readonly HBoxContainer _bar;
    private readonly Control _grip;
    private Vector2 _restoreSize;
    private bool _drag, _resize;
    public static readonly Vector2 MinSize = new(190, 90);

    public FloatPanel(string id, string title, Rect2 defaultRect)
    {
        Id = id;
        DefaultRect = defaultRect;
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(0.03f, 0.045f, 0.075f, 0.95f), UiTheme.Border, 6));
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 3);
        AddChild(v);
        _bar = new HBoxContainer { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.Move };
        _bar.AddThemeConstantOverride("separation", 2);
        _title = UiTheme.Label(title, 11, UiTheme.Accent);
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _title.ClipText = true;
        _title.MouseFilter = MouseFilterEnum.Ignore;
        _bar.AddChild(_title);
        _minBtn = SmallButton("–", "Minimizar / restaurar (doble clic en el título)", ToggleMinimize);
        _closeBtn = SmallButton("×", "Cerrar (se vuelve a abrir desde VENTANAS)", () => SetOpen(false));
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
        _restoreSize = defaultRect.Size;
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
        _minBtn.Visible = _closeBtn.Visible = !Docked;
        _grip.Visible = !Docked && !Minimized;
    }

    public void SetTitle(string t) => _title.Text = t;

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

    public void SetMinimized(bool min)
    {
        if (Docked || Minimized == min) return;
        Minimized = min;
        Content.Visible = !min;
        _grip.Visible = !min;
        if (min)
        {
            _restoreSize = Size;
            Size = new Vector2(Size.X, 0);   // se ajusta a la barra de título
        }
        else
        {
            Size = _restoreSize;
            Shown?.Invoke();
        }
        _minBtn.Text = min ? "□" : "–";
        Changed?.Invoke();
    }

    public void ToggleMinimize() => SetMinimized(!Minimized);

    public void BringToFront()
    {
        if (GetParent() is Node p) p.MoveChild(this, p.GetChildCount() - 1);
    }

    /// <summary>Mantiene la ventana dentro del área visible (con al menos la barra de título a la vista).</summary>
    public void Clamp()
    {
        if (Docked || GetParent() is not Control parent) return;
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

    public void ApplyRect(Rect2 r, bool open, bool minimized)
    {
        if (Docked) return;
        if (Minimized) SetMinimized(false);
        Position = r.Position;
        Size = r.Size;
        _restoreSize = r.Size;
        Visible = open;
        if (minimized) SetMinimized(true);
        Clamp();
    }

    public Rect2 CurrentRect => new(Position, Minimized ? new Vector2(Size.X, _restoreSize.Y) : Size);

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
                BringToFront();
            }
            else if (_drag)
            {
                _drag = false;
                Clamp();
                Changed?.Invoke();
            }
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion mm && _drag)
        {
            Position += mm.Relative;
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
                _restoreSize = Size;
                Clamp();
                Changed?.Invoke();
            }
            else BringToFront();
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion mm && _resize)
        {
            var s = Size + mm.Relative;
            Size = new Vector2(MathF.Max(MinSize.X, s.X), MathF.Max(MinSize.Y, s.Y));
            AcceptEvent();
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true }) BringToFront();
    }
}

/// <summary>
/// Registro de las ventanas flotantes (HUD y laboratorios): mostrar/ocultar, presets de disposición y persistencia
/// en user://hud_layout.cfg.
/// </summary>
public static class WindowManager
{
    private const string LayoutPath = "user://hud_layout.cfg";
    private static readonly Dictionary<string, FloatPanel> Panels = new();
    private static bool _loading;
    public static event Action? AnyChanged;

    public static IEnumerable<FloatPanel> All => Panels.Values;

    public static void Register(FloatPanel p)
    {
        Panels[p.Id] = p;
        p.Changed += () =>
        {
            if (!_loading) Save();
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
        p.SetOpen(open);
        if (open && p.Minimized) p.SetMinimized(false);
    }

    /// <summary>¿Hay una ventana visible bajo el punto? (para que los toques no lleguen al terreno).</summary>
    public static bool IsOverAny(Vector2 point) =>
        Panels.Values.Any(p => p.IsVisibleInTree() && p.GetGlobalRect().HasPoint(point));

    /// <summary>Presets: "completa" (paneles de misión), "analisis" (laboratorios en vivo) y "limpia" (solo el mundo).</summary>
    public static void ApplyPreset(string name, Vector2 area)
    {
        if (Platform.IsMobile) return;
        _loading = true;
        float W = area.X, H = area.Y, col = 184;   // ancho de la columna de controles a la derecha
        float right = W - col - 6;
        void Set(string id, float x, float y, float w, float h, bool open, bool min = false)
        {
            if (Panels.TryGetValue(id, out var p)) p.ApplyRect(new Rect2(x, y, w, h), open, min);
        }
        switch (name)
        {
            case "analisis":
                float lw = MathF.Min(560, (right - 12) / 2);
                Set("tele", 6, 32, 320, 474, false);
                Set("log", 6, H - 210, 520, 204, false);
                Set("map", right - 324, H - 210, 324, 204, false);
                Set("lab_rna", 6, 32, lw, (H - 40) / 2 - 2, true);
                Set("lab_fuzzy", 6, 32 + (H - 40) / 2 + 2, lw, (H - 40) / 2 - 4, true);
                Set("lab_ga", right - lw, 32 + (H - 40) / 2 + 2, lw, (H - 40) / 2 - 4, true);
                Set("dec", right - lw, 32, lw, (H - 40) / 2 - 2, true);
                Set("lab_policies", 300, 80, 760, 470, false);
                Set("lab_help", 320, 100, 720, 470, false);
                break;
            case "limpia":
                foreach (var p in Panels.Values) p.SetOpen(false);
                Set("map", right - 324, H - 210, 324, 204, true, true);
                break;
            default:   // completa
                Set("tele", 6, 32, 320, 474, true);
                Set("dec", right - 336, 32, 336, 474, true);
                Set("log", 6, H - 210, 520, 204, true);
                Set("map", right - 330, H - 210, 330, 204, true);
                Set("lab_rna", 340, 60, 760, 470, false);
                Set("lab_fuzzy", 360, 80, 780, 470, false);
                Set("lab_ga", 380, 100, 760, 470, false);
                Set("lab_policies", 300, 80, 760, 470, false);
                Set("lab_help", 320, 100, 720, 470, false);
                break;
        }
        _loading = false;
        Save();
        AnyChanged?.Invoke();
    }

    public static void Save()
    {
        if (Platform.IsMobile) return;
        var cfg = new ConfigFile();
        foreach (var (id, p) in Panels)
        {
            var r = p.CurrentRect;
            cfg.SetValue(id, "rect", new Rect2(r.Position, r.Size));
            cfg.SetValue(id, "open", p.Visible);
            cfg.SetValue(id, "min", p.Minimized);
        }
        cfg.Save(LayoutPath);
    }

    /// <summary>Carga la disposición guardada; si no hay, aplica "completa". Devuelve true si había una guardada.</summary>
    public static bool Load(Vector2 area)
    {
        if (Platform.IsMobile) return false;
        var cfg = new ConfigFile();
        if (cfg.Load(LayoutPath) != Error.Ok)
        {
            ApplyPreset("completa", area);
            return false;
        }
        _loading = true;
        foreach (var (id, p) in Panels)
        {
            if (!cfg.HasSection(id)) continue;
            var r = (Rect2)cfg.GetValue(id, "rect", p.DefaultRect);
            p.ApplyRect(r, (bool)cfg.GetValue(id, "open", false), (bool)cfg.GetValue(id, "min", false));
        }
        _loading = false;
        AnyChanged?.Invoke();
        return true;
    }

    public static void ClampAll()
    {
        foreach (var p in Panels.Values) p.Clamp();
    }
}
