using System;
using System.Collections.Generic;
using System.Linq;
using AresX.Sim;
using Godot;

namespace AresX.UI;

/// <summary>
/// Tema común (viewport 1280×720). Dos estilos:
///   * TÁCTICO (por defecto): estética de HUD de anime de mechas, solo como referencia: negro cálido, naranjo de
///     señal, verde de lectura, rojo de alarma; esquinas biseladas, letra condensada, barras segmentadas y cabeceras con
///     franja de peligro.
///   * CLÁSICO: paneles azul oscuro translúcidos con acento ámbar.
/// Se elige en la configuración y se guarda en user://ajustes.cfg. Los colores son propiedades: lo que se dibuja cada
/// cuadro cambia al instante; algunos rótulos fijos cambian al reiniciar el juego.
/// </summary>
public static class UiTheme
{
    private const string SettingsPath = "user://ajustes.cfg";
    public static bool Tactical { get; private set; } = LoadStyle();
    public static event Action? StyleChanged;

    private static bool LoadStyle()
    {
        var cfg = new ConfigFile();
        return cfg.Load(SettingsPath) != Error.Ok || (bool)cfg.GetValue("ui", "tactico", true);
    }

    public static void SetTactical(bool on)
    {
        if (on == Tactical) return;
        Tactical = on;
        var cfg = new ConfigFile();
        cfg.Load(SettingsPath);
        cfg.SetValue("ui", "tactico", on);
        cfg.Save(SettingsPath);
        if (_theme != null) Fill(_theme);
        StyleChanged?.Invoke();
    }

    // paletas (táctico, clásico)
    private static readonly Color TPanelBg = new(0.03f, 0.016f, 0.006f, 0.88f), CPanelBg = new(0.035f, 0.05f, 0.08f, 0.86f);
    private static readonly Color TWindowBg = new(0.025f, 0.013f, 0.005f, 0.93f), CWindowBg = new(0.03f, 0.045f, 0.075f, 0.93f);
    private static readonly Color TBorder = new(1f, 0.48f, 0.1f, 0.7f), CBorder = new("3a4c6a");
    private static readonly Color TAccent = new("ff8a1e"), CAccent = new("ffd860");
    private static readonly Color TText = new("ffeedd"), CText = new("e6ebf4");
    private static readonly Color TDim = new("b49278"), CDim = new("8a96aa");
    private static readonly Color TOk = new("46ff86"), COk = new("5ad87a");
    private static readonly Color TWarn = new("ffc02e"), CWarn = new("f0c040");
    private static readonly Color TCrit = new("ff2b2b"), CCrit = new("ff4a3a");
    private static readonly Color TInfo = new("6ad8ff"), CInfo = new("5ab0ff");
    private static readonly Color TRna = new("2ee6c8"), CRna = new("2ab8b0");
    private static readonly Color TGa = new("b07aff"), CGa = new("a07ae0");
    private static readonly Color TFuzzy = new("ffd23a"), CFuzzy = new("e8b040");
    private static readonly Color TSup = new("ffeedd"), CSup = new("e8eef8");
    private static readonly Color TBar = new("120904"), CBar = new("0e1a2e");

    public static Color PanelBg => Tactical ? TPanelBg : CPanelBg;
    public static Color WindowBg => Tactical ? TWindowBg : CWindowBg;
    public static Color Border => Tactical ? TBorder : CBorder;
    public static Color Accent => Tactical ? TAccent : CAccent;
    public static Color Text => Tactical ? TText : CText;
    public static Color Dim => Tactical ? TDim : CDim;
    public static Color Ok => Tactical ? TOk : COk;
    public static Color Warn => Tactical ? TWarn : CWarn;
    public static Color Crit => Tactical ? TCrit : CCrit;
    public static Color Info => Tactical ? TInfo : CInfo;
    public static Color Rna => Tactical ? TRna : CRna;       // VER (RNA)
    public static Color Ga => Tactical ? TGa : CGa;          // ELEGIR (AG)
    public static Color Fuzzy => Tactical ? TFuzzy : CFuzzy; // AJUSTAR (difuso)
    public static Color Sup => Tactical ? TSup : CSup;
    public static Color BarBg => Tactical ? TBar : CBar;

    private static Theme? _theme;
    private static FontVariation? _cond, _condBold;
    /// <summary>Letra de trazo condensado (táctico): conserva el avance, así que queda levemente espaciada y no cambia el ancho de los textos.</summary>
    private static FontVariation Condensed => _cond ??= new FontVariation
    {
        BaseFont = ThemeDB.FallbackFont, VariationTransform = new Transform2D(new Vector2(0.9f, 0), new Vector2(0, 1), Vector2.Zero),
    };
    private static FontVariation CondensedBold => _condBold ??= new FontVariation
    {
        BaseFont = ThemeDB.FallbackFont, VariationTransform = new Transform2D(new Vector2(0.84f, 0), new Vector2(0, 1), Vector2.Zero), VariationEmbolden = 0.7f,
    };
    public static Font Font => Tactical ? Condensed : ThemeDB.FallbackFont;
    /// <summary>Títulos (ventanas, cabeceras): condensada y gruesa en el estilo táctico.</summary>
    public static Font TitleFont => Tactical ? CondensedBold : ThemeDB.FallbackFont;

    public static Theme Get()
    {
        if (_theme != null) return _theme;
        _theme = new Theme { DefaultFontSize = 12 };
        Fill(_theme);
        return _theme;
    }

    private static void Fill(Theme t)
    {
        t.DefaultFont = Tactical ? Condensed : null;
        var o = Accent;
        t.SetStylebox("panel", "PanelContainer", Box(PanelBg, Border, 6));
        t.SetStylebox("panel", "Panel", Box(PanelBg, Border, 6));
        t.SetColor("font_color", "Label", Text);
        t.SetColor("default_color", "RichTextLabel", Text);
        t.SetFontSize("normal_font_size", "RichTextLabel", 11);
        t.SetFontSize("bold_font_size", "RichTextLabel", 11);
        t.SetFontSize("mono_font_size", "RichTextLabel", 11);
        if (Tactical)
        {
            t.SetStylebox("normal", "Button", Box(new Color(o, 0.06f), new Color(o, 0.35f), 4));
            t.SetStylebox("hover", "Button", Box(new Color(o, 0.2f), o, 4));
            t.SetStylebox("pressed", "Button", Box(new Color(o, 0.55f), o, 4));
            t.SetColor("font_pressed_color", "Button", Colors.White);
        }
        else
        {
            t.SetStylebox("normal", "Button", Box(new Color(1, 1, 1, 0.05f), new Color(1, 1, 1, 0.14f), 4));
            t.SetStylebox("hover", "Button", Box(new Color(1, 1, 1, 0.12f), new Color(1, 1, 1, 0.3f), 4));
            t.SetStylebox("pressed", "Button", Box(new Color("34507e"), Accent, 4));
            t.SetColor("font_pressed_color", "Button", Accent);
        }
        t.SetStylebox("focus", "Button", new StyleBoxEmpty());
        t.SetStylebox("disabled", "Button", Box(new Color(0, 0, 0, 0.2f), Colors.Transparent, 4));
        t.SetColor("font_color", "Button", Text);
        t.SetColor("font_hover_color", "Button", Colors.White);
        t.SetColor("font_disabled_color", "Button", new Color(1, 1, 1, 0.3f));
        t.SetFontSize("font_size", "Button", 11);
        var optBg = Tactical ? new Color(o, 0.07f) : new Color(1, 1, 1, 0.06f);
        var optBorder = Tactical ? new Color(o, 0.45f) : new Color(1, 1, 1, 0.2f);
        t.SetStylebox("normal", "OptionButton", Box(optBg, optBorder, 5));
        t.SetStylebox("hover", "OptionButton", Box(Tactical ? new Color(o, 0.18f) : new Color(1, 1, 1, 0.12f), Tactical ? o : new Color(1, 1, 1, 0.3f), 5));
        t.SetStylebox("pressed", "OptionButton", Box(Tactical ? new Color(o, 0.3f) : new Color(1, 1, 1, 0.16f), Tactical ? o : new Color(1, 1, 1, 0.3f), 5));
        t.SetStylebox("normal", "LineEdit", Box(new Color(0, 0, 0, 0.35f), optBorder, 5));
        t.SetStylebox("panel", "TabContainer", Box(Tactical ? new Color(0.025f, 0.012f, 0.004f, 0.97f) : new Color(0.03f, 0.04f, 0.07f, 0.97f), Border, 8));
        t.SetStylebox("tab_selected", "TabContainer", Box(Tactical ? new Color(o, 0.3f) : new Color("26344e"), Accent, 6));
        t.SetStylebox("tab_unselected", "TabContainer", Box(new Color(0, 0, 0, 0.3f), Border, 6));
        t.SetStylebox("tab_hovered", "TabContainer", Box(Tactical ? new Color(o, 0.12f) : new Color(1, 1, 1, 0.08f), Border, 6));
        t.SetColor("font_selected_color", "TabContainer", Tactical ? Colors.White : Accent);
        t.SetColor("font_unselected_color", "TabContainer", Dim);
        t.SetFontSize("font_size", "TabContainer", 13);
        t.SetStylebox("panel", "PopupMenu", Box(Tactical ? new Color(0.04f, 0.02f, 0.008f, 0.98f) : new Color(0.05f, 0.07f, 0.1f, 0.98f), Border, 4));
        t.SetColor("font_hover_color", "PopupMenu", Tactical ? Colors.Black : Colors.White);
        t.SetStylebox("hover", "PopupMenu", Box(Tactical ? o : new Color(1, 1, 1, 0.12f), Colors.Transparent, 2));
        t.SetFontSize("font_size", "PopupMenu", 12);
        t.SetStylebox("slider", "HSlider", Box(new Color(1, 1, 1, 0.12f), Colors.Transparent, 2));
        t.SetStylebox("grabber_area", "HSlider", Box(new Color(Accent, 0.5f), Colors.Transparent, 2));
        t.SetStylebox("grabber_area_highlight", "HSlider", Box(Accent, Colors.Transparent, 2));
        t.SetStylebox("panel", "TooltipPanel", Box(Tactical ? new Color(0.04f, 0.02f, 0.008f, 0.98f) : new Color(0.05f, 0.07f, 0.1f, 0.98f), Border, 6));
        t.SetColor("font_color", "TooltipLabel", Text);
        foreach (var st in new[] { "normal", "pressed", "hover", "hover_pressed", "focus", "disabled" }) t.SetStylebox(st, "CheckBox", new StyleBoxEmpty());
        t.SetColor("font_color", "CheckBox", Text);
        t.SetColor("font_hover_color", "CheckBox", Colors.White);
        t.SetColor("font_pressed_color", "CheckBox", Text);
    }

    /// <summary>Caja: en el estilo táctico con esquinas biseladas (arriba-izquierda y abajo-derecha); en el clásico, redondeadas.</summary>
    public static StyleBoxFlat Box(Color bg, Color border, int margin, int radius = 3)
    {
        var b = new StyleBoxFlat { BgColor = bg, BorderColor = border, AntiAliasing = true };
        b.SetBorderWidthAll(border.A > 0 ? 1 : 0);
        b.SetContentMarginAll(margin);
        if (Tactical)
        {
            int cut = Math.Max(5, Math.Min(10, margin + 3));
            b.CornerDetail = 1;   // esquina recta = bisel
            b.CornerRadiusTopLeft = cut;
            b.CornerRadiusBottomRight = cut;
        }
        else b.SetCornerRadiusAll(radius);
        return b;
    }

    public static Color StatusColor(SensorStatus s) => s switch
    {
        SensorStatus.OK => Ok, SensorStatus.Aviso => Warn, SensorStatus.Critico => Crit, SensorStatus.Ruido => Warn,
        SensorStatus.NoAplica => Dim, _ => new Color("ff60c0"),
    };

    public static string StatusTag(SensorStatus s) => s switch
    {
        SensorStatus.OK => "OK", SensorStatus.Aviso => "AVISO", SensorStatus.Critico => "CRÍT", SensorStatus.Ruido => "RUIDO",
        SensorStatus.Saturado => "SAT", SensorStatus.NaN => "NaN", SensorStatus.Atascado => "ATASC", SensorStatus.Contradiccion => "CONTR",
        SensorStatus.NoAplica => "n/a", _ => "?",
    };

    public static Color StateColor(MissionState s) => s switch
    {
        MissionState.Exploracion => Ok, MissionState.Alerta => Warn, MissionState.Replanificacion => Ga, MissionState.Refugio => Info,
        MissionState.ModoSeguro => new Color("ff8a2a"), MissionState.Exito => new Color("8cff9a"), MissionState.Fracaso => Crit, _ => Dim,
    };

    public static Color LogColor(string kind) => kind switch
    {
        "ok" => Ok, "alerta" => Warn, "veto" => Crit, "evento" => new Color("ff9ad0"), "ag" => Ga, "rna" => Rna, "dif" => Fuzzy,
        "fallo" => new Color("ff7a50"), _ => Text,
    };

    public static Label Label(string text, int size = 12, Color? color = null)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", size);
        if (color is Color c) l.AddThemeColorOverride("font_color", c);
        return l;
    }

    public static Button Button(string text, Action onPressed, string tooltip = "")
    {
        var b = new Button { Text = text, TooltipText = tooltip, FocusMode = Control.FocusModeEnum.None };
        b.Pressed += onPressed;
        return b;
    }
}

/// <summary>Ayudas de dibujo inmediato para controles personalizados.</summary>
public static class Gfx
{
    public static Font F => UiTheme.Font;

    public static void Text(CanvasItem c, Vector2 pos, string text, int size, Color col, float width = -1, HorizontalAlignment align = HorizontalAlignment.Left) =>
        c.DrawString(F, pos + new Vector2(0, size), text, align, width, size, col);

    public static float TextWidth(string text, int size) => F.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;

    /// <summary>Barra de nivel; en el estilo táctico, segmentada (como un medidor de celdas).</summary>
    public static void Bar(CanvasItem c, Rect2 r, float t, Color fill, Color? bg = null)
    {
        float v = float.IsFinite(t) ? Mathf.Clamp(t, 0, 1) : 0;
        if (UiTheme.Tactical && r.Size.X >= 24 && r.Size.Y >= 3)
        {
            float seg = r.Size.Y >= 7 ? 5f : 4f, gap = 1.5f;
            int n = Math.Max(1, (int)((r.Size.X + gap) / (seg + gap)));
            float sw = (r.Size.X - gap * (n - 1)) / n;
            int on = (int)MathF.Round(v * n);
            var off = bg ?? new Color(fill, 0.13f);
            for (int i = 0; i < n; i++)
                c.DrawRect(new Rect2(r.Position.X + i * (sw + gap), r.Position.Y, sw, r.Size.Y), i < on ? fill : off);
            return;
        }
        c.DrawRect(r, bg ?? new Color(1, 1, 1, 0.07f));
        if (v > 0) c.DrawRect(new Rect2(r.Position, new Vector2(r.Size.X * v, r.Size.Y)), fill);
    }

    /// <summary>Polígono biselado (arriba-izquierda y abajo-derecha) para cajas dibujadas a mano.</summary>
    public static Vector2[] Bevel(Rect2 r, float cut)
    {
        cut = MathF.Min(cut, MathF.Min(r.Size.X, r.Size.Y) / 2);
        return new[]
        {
            r.Position + new Vector2(cut, 0), new Vector2(r.End.X, r.Position.Y), new Vector2(r.End.X, r.End.Y - cut),
            r.End - new Vector2(cut, 0), new Vector2(r.Position.X, r.End.Y), r.Position + new Vector2(0, cut),
        };
    }

    /// <summary>Caja de fondo + borde con el estilo vigente (biselada en el táctico).</summary>
    public static void Panel(CanvasItem c, Rect2 r, Color bg, Color border, float cut = 7)
    {
        if (!UiTheme.Tactical)
        {
            c.DrawRect(r, bg);
            c.DrawRect(r, border, false, 1);
            return;
        }
        var pts = Bevel(r, cut);
        c.DrawColoredPolygon(pts, bg);
        c.DrawPolyline(pts.Append(pts[0]).ToArray(), border, 1.2f, true);
    }

    /// <summary>Franja de peligro (diagonales) dentro de un rectángulo.</summary>
    public static void Hazard(CanvasItem c, Rect2 r, Color col, float phase = 0)
    {
        float sw = MathF.Max(3, r.Size.Y * 0.45f);
        for (float x = r.Position.X - r.Size.Y + (phase % (sw * 2)); x < r.End.X; x += sw * 2)
        {
            var poly = new[]
            {
                new Vector2(x, r.End.Y), new Vector2(x + sw, r.End.Y), new Vector2(x + sw + r.Size.Y, r.Position.Y), new Vector2(x + r.Size.Y, r.Position.Y),
            };
            for (int i = 0; i < 4; i++) poly[i].X = Mathf.Clamp(poly[i].X, r.Position.X, r.End.X);
            if (poly[1].X - poly[0].X < 0.5f && poly[2].X - poly[3].X < 0.5f) continue;
            c.DrawColoredPolygon(poly, col);
        }
    }

    /// <summary>Barra centrada en cero para valores con signo (rasgos normalizados).</summary>
    public static void SignedBar(CanvasItem c, Rect2 r, float v, float max, Color pos, Color neg)
    {
        c.DrawRect(r, new Color(1, 1, 1, 0.06f));
        float mid = r.Position.X + r.Size.X / 2;
        float w = Mathf.Clamp(v / max, -1, 1) * r.Size.X / 2;
        if (w >= 0) c.DrawRect(new Rect2(mid, r.Position.Y, w, r.Size.Y), pos);
        else c.DrawRect(new Rect2(mid + w, r.Position.Y, -w, r.Size.Y), neg);
        c.DrawLine(new Vector2(mid, r.Position.Y), new Vector2(mid, r.End.Y), new Color(1, 1, 1, 0.3f));
    }

    public static void Chip(CanvasItem c, Vector2 pos, string text, int size, Color col, bool filled = false)
    {
        float w = TextWidth(text, size) + 8;
        var r = new Rect2(pos, new Vector2(w, size + 4));
        if (UiTheme.Tactical) Panel(c, r, filled ? col : new Color(col, 0.14f), col, 4);
        else
        {
            c.DrawRect(r, filled ? col : new Color(col, 0.16f));
            c.DrawRect(r, col, false, 1);
        }
        Text(c, pos + new Vector2(4, 1), text, size, filled ? Colors.Black : col);
    }

    public static float ChipWidth(string text, int size) => TextWidth(text, size) + 8;

    public static void Header(CanvasItem c, Vector2 pos, float width, string text, Color col)
    {
        if (UiTheme.Tactical)
        {
            // cabecera biselada con franja de peligro al final
            var r = new Rect2(pos, new Vector2(width, 15));
            c.DrawColoredPolygon(Bevel(r, 5), new Color(col, 0.14f));
            c.DrawRect(new Rect2(pos, new Vector2(3, 15)), col);
            c.DrawLine(new Vector2(pos.X, r.End.Y + 1), new Vector2(r.End.X, r.End.Y + 1), new Color(col, 0.45f), 1);
            if (width > 140) Hazard(c, new Rect2(r.End.X - 30, pos.Y + 3, 24, 9), new Color(col, 0.55f));
            c.DrawString(UiTheme.TitleFont, pos + new Vector2(7, 11), text.ToUpperInvariant(), HorizontalAlignment.Left, width - 44, 11, col);
            return;
        }
        c.DrawRect(new Rect2(pos, new Vector2(width, 15)), new Color(col, 0.13f));
        c.DrawRect(new Rect2(pos, new Vector2(3, 15)), col);
        Text(c, pos + new Vector2(7, 0), text, 11, col);
    }

    /// <summary>Serie temporal: polilínea normalizada en el rectángulo.</summary>
    public static void Series(CanvasItem c, Rect2 r, IReadOnlyList<float> v, float min, float max, Color col, float width = 1.5f)
    {
        if (v.Count < 2 || max - min < 1e-9f) return;
        var pts = new Vector2[v.Count];
        for (int i = 0; i < v.Count; i++)
        {
            float x = r.Position.X + r.Size.X * i / (v.Count - 1);
            float val = float.IsFinite(v[i]) ? v[i] : min;
            float y = r.End.Y - r.Size.Y * Mathf.Clamp((val - min) / (max - min), 0, 1);
            pts[i] = new Vector2(x, y);
        }
        c.DrawPolyline(pts, col, width, true);
    }

    public static void Frame(CanvasItem c, Rect2 r, Color? col = null)
    {
        c.DrawRect(r, new Color(0, 0, 0, 0.25f));
        if (!UiTheme.Tactical)
        {
            c.DrawRect(r, col ?? new Color(1, 1, 1, 0.12f), false, 1);
            return;
        }
        // marco táctico: línea tenue y esquinas marcadas
        var lc = col ?? new Color(UiTheme.Accent, 0.22f);
        c.DrawRect(r, lc, false, 1);
        var k = new Color(UiTheme.Accent, 0.75f);
        float L = MathF.Min(8, MathF.Min(r.Size.X, r.Size.Y) / 3);
        c.DrawLine(r.Position, r.Position + new Vector2(L, 0), k, 1.5f);
        c.DrawLine(r.Position, r.Position + new Vector2(0, L), k, 1.5f);
        c.DrawLine(r.End, r.End - new Vector2(L, 0), k, 1.5f);
        c.DrawLine(r.End, r.End - new Vector2(0, L), k, 1.5f);
    }

    /// <summary>Panel posicionado con anclas en el viewport.</summary>
    public static PanelContainer Panel(Control parent, Rect2 rect, Control.LayoutPreset anchor)
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        parent.AddChild(p);
        p.SetAnchorsPreset(anchor);
        p.Position = rect.Position;
        p.Size = rect.Size;
        return p;
    }
}
