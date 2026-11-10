using System;
using System.Collections.Generic;
using AresX.Sim;
using Godot;

namespace AresX.UI;

/// <summary>Tema común (viewport 1280×720): paneles oscuros translúcidos, acento ámbar, colores de estado.</summary>
public static class UiTheme
{
    public static readonly Color PanelBg = new(0.035f, 0.05f, 0.08f, 0.86f);
    public static readonly Color Border = new("3a4c6a");
    public static readonly Color Accent = new("ffd860");
    public static readonly Color Text = new("e6ebf4");
    public static readonly Color Dim = new("8a96aa");
    public static readonly Color Ok = new("5ad87a");
    public static readonly Color Warn = new("f0c040");
    public static readonly Color Crit = new("ff4a3a");
    public static readonly Color Info = new("5ab0ff");
    public static readonly Color Rna = new("2ab8b0");     // VER (RNA)
    public static readonly Color Ga = new("a07ae0");      // ELEGIR (AG)
    public static readonly Color Fuzzy = new("e8b040");   // AJUSTAR (difuso)
    public static readonly Color Sup = new("e8eef8");

    private static Theme? _theme;
    public static Font Font => ThemeDB.FallbackFont;

    public static Theme Get()
    {
        if (_theme != null) return _theme;
        var t = new Theme { DefaultFontSize = 12 };
        t.SetStylebox("panel", "PanelContainer", Box(PanelBg, Border, 6));
        t.SetStylebox("panel", "Panel", Box(PanelBg, Border, 6));
        t.SetColor("font_color", "Label", Text);
        t.SetColor("default_color", "RichTextLabel", Text);
        t.SetFontSize("normal_font_size", "RichTextLabel", 11);
        t.SetFontSize("bold_font_size", "RichTextLabel", 11);
        t.SetFontSize("mono_font_size", "RichTextLabel", 11);
        t.SetStylebox("normal", "Button", Box(new Color(1, 1, 1, 0.05f), new Color(1, 1, 1, 0.14f), 4));
        t.SetStylebox("hover", "Button", Box(new Color(1, 1, 1, 0.12f), new Color(1, 1, 1, 0.3f), 4));
        t.SetStylebox("pressed", "Button", Box(new Color("34507e"), Accent, 4));
        t.SetStylebox("focus", "Button", new StyleBoxEmpty());
        t.SetStylebox("disabled", "Button", Box(new Color(0, 0, 0, 0.2f), Colors.Transparent, 4));
        t.SetColor("font_color", "Button", Text);
        t.SetColor("font_hover_color", "Button", Colors.White);
        t.SetColor("font_pressed_color", "Button", Accent);
        t.SetColor("font_disabled_color", "Button", new Color(1, 1, 1, 0.3f));
        t.SetFontSize("font_size", "Button", 11);
        t.SetStylebox("normal", "OptionButton", Box(new Color(1, 1, 1, 0.06f), new Color(1, 1, 1, 0.2f), 5));
        t.SetStylebox("hover", "OptionButton", Box(new Color(1, 1, 1, 0.12f), new Color(1, 1, 1, 0.3f), 5));
        t.SetStylebox("normal", "LineEdit", Box(new Color(0, 0, 0, 0.35f), new Color(1, 1, 1, 0.2f), 5));
        t.SetStylebox("panel", "TabContainer", Box(new Color(0.03f, 0.04f, 0.07f, 0.97f), Border, 8));
        t.SetStylebox("tab_selected", "TabContainer", Box(new Color("26344e"), Accent, 6));
        t.SetStylebox("tab_unselected", "TabContainer", Box(new Color(0, 0, 0, 0.3f), Border, 6));
        t.SetStylebox("tab_hovered", "TabContainer", Box(new Color(1, 1, 1, 0.08f), Border, 6));
        t.SetColor("font_selected_color", "TabContainer", Accent);
        t.SetColor("font_unselected_color", "TabContainer", Dim);
        t.SetFontSize("font_size", "TabContainer", 13);
        t.SetStylebox("panel", "PopupMenu", Box(new Color(0.05f, 0.07f, 0.1f, 0.98f), Border, 4));
        t.SetFontSize("font_size", "PopupMenu", 12);
        t.SetStylebox("slider", "HSlider", Box(new Color(1, 1, 1, 0.12f), Colors.Transparent, 2));
        t.SetStylebox("grabber_area", "HSlider", Box(new Color(Accent, 0.5f), Colors.Transparent, 2));
        t.SetStylebox("grabber_area_highlight", "HSlider", Box(Accent, Colors.Transparent, 2));
        t.SetStylebox("panel", "TooltipPanel", Box(new Color(0.05f, 0.07f, 0.1f, 0.98f), Border, 6));
        return _theme = t;
    }

    public static StyleBoxFlat Box(Color bg, Color border, int margin, int radius = 3)
    {
        var b = new StyleBoxFlat { BgColor = bg, BorderColor = border };
        b.SetBorderWidthAll(border.A > 0 ? 1 : 0);
        b.SetContentMarginAll(margin);
        b.SetCornerRadiusAll(radius);
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

    public static void Bar(CanvasItem c, Rect2 r, float t, Color fill, Color? bg = null)
    {
        c.DrawRect(r, bg ?? new Color(1, 1, 1, 0.07f));
        if (float.IsFinite(t) && t > 0)
            c.DrawRect(new Rect2(r.Position, new Vector2(r.Size.X * Mathf.Clamp(t, 0, 1), r.Size.Y)), fill);
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
        c.DrawRect(r, filled ? col : new Color(col, 0.16f));
        c.DrawRect(r, col, false, 1);
        Text(c, pos + new Vector2(4, 1), text, size, filled ? Colors.Black : col);
    }

    public static float ChipWidth(string text, int size) => TextWidth(text, size) + 8;

    public static void Header(CanvasItem c, Vector2 pos, float width, string text, Color col)
    {
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
        c.DrawRect(r, col ?? new Color(1, 1, 1, 0.12f), false, 1);
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
