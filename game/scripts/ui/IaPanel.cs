using System;
using System.Linq;
using AresX.AI;
using AresX.Sim;
using Godot;

namespace AresX.UI;

/// <summary>
/// Panel vertical "IA en vivo": las figuras principales de los tres laboratorios y del supervisor, apiladas, con íconos
/// y el mínimo de texto (los detalles van en la descripción emergente al pasar el mouse). Clic en una sección = abrir su
/// laboratorio completo.
///   RNA     · clase de la celda evaluada + anillo de confianza, barras de probabilidad, rasgos de entrada (z).
///   DIFUSO  · curva agregada de velocidad con su centroide, medidores de velocidad y cautela, reglas activas.
///   AG      · fitness por generación (mejor / media) y candidatas A/B/C (la elegida resaltada).
///   SUPERV. · ícono de la acción, código y una luz por restricción (verde cumple, ámbar limita, rojo veta).
/// </summary>
public partial class IaPanel : Control
{
    public Mission? Mission;
    public Action<string>? OpenWindow;
    private const float SecH = 98, Head = 18;
    private static readonly (string Id, string Icon, string Title, string Window)[] Sections =
    {
        ("rna", "rna", "RNA", "lab_rna"), ("fuzzy", "fuzzy", "DIFUSO", "lab_fuzzy"), ("ga", "ga", "AG", "lab_ga"), ("sup", "shield", "SUPERVISOR", "dec"),
    };

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(0, Sections.Length * SecH);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
    }

    public override void _Process(double delta)
    {
        if (IsVisibleInTree()) QueueRedraw();
    }

    private int SectionAt(Vector2 p) => Math.Clamp((int)(p.Y / SecH), 0, Sections.Length - 1);

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
        {
            OpenWindow?.Invoke(Sections[SectionAt(mb.Position)].Window);
            AcceptEvent();
        }
    }

    public override string _GetTooltip(Vector2 at)
    {
        var m = Mission;
        if (m == null) return "";
        return Sections[SectionAt(at)].Id switch
        {
            "rna" => "RNA (VER): clase de la celda que el vehículo evalúa delante y confianza (anillo).\nBarras: libre · obstáculo · barranco.\nAbajo: los 11 rasgos de entrada normalizados.\nClic: laboratorio RNA completo.",
            "fuzzy" => "Lógica difusa (AJUSTAR): curva agregada de velocidad y su centroide (línea blanca).\nMedidores: velocidad y cautela.\nAbajo: reglas activas con su fuerza.\nClic: laboratorio difuso completo." +
                       (m.LastFuzzy != null ? "\n\n" + string.Join("\n", m.LastFuzzy.Fired.OrderByDescending(f => f.Strength).Take(4).Select(f => $"{f.Rule.Id} {f.Strength:0.00}: {f.Rule.Text}")) : ""),
            "ga" => "Algoritmo genético (ELEGIR): fitness por generación (verde mejor, violeta media) y las 3 rutas candidatas.\nClic: laboratorio genético completo." +
                    (m.LastGa != null ? $"\n\nÚltima corrida: {m.LastGa.Trigger} · {m.LastGa.Generations} gen · {m.LastGa.Ms:0} ms" : ""),
            _ => "Supervisor (DECIDIR): acción final y una luz por restricción de seguridad.\nClic: panel de decisión con el detalle." +
                 (m.LastDecision != null ? $"\n\n{m.LastDecision.Code} · {m.LastDecision.Reason}\n" + string.Join("\n", m.LastDecision.Checks.Where(c => !c.Passed || c.Applied).Select(c => $"{(c.Passed ? "!" : "✗")} {c.Code} {c.Name}: {c.Detail}")) : ""),
        };
    }

    public override void _Draw()
    {
        var m = Mission;
        if (m == null) return;
        float w = Size.X;
        for (int i = 0; i < Sections.Length; i++)
        {
            var sec = Sections[i];
            float y0 = i * SecH;
            var col = sec.Id switch { "rna" => UiTheme.Rna, "fuzzy" => UiTheme.Fuzzy, "ga" => UiTheme.Ga, _ => UiTheme.Sup };
            DrawRect(new Rect2(2, y0 + 2, w - 4, SecH - 4), new Color(col, 0.06f));
            DrawRect(new Rect2(2, y0 + 2, 3, SecH - 4), col);
            Icons.Draw(this, sec.Icon, new Vector2(16, y0 + 12), 14, col);
            Gfx.Text(this, new Vector2(28, y0 + 4), sec.Title, 10, col);
            var r = new Rect2(10, y0 + Head + 4, w - 18, SecH - Head - 10);
            switch (sec.Id)
            {
                case "rna": DrawRna(m, r); break;
                case "fuzzy": DrawFuzzy(m, r); break;
                case "ga": DrawGa(m, r, y0); break;
                default: DrawSup(m, r, y0); break;
            }
        }
    }

    private static readonly Color[] ClassCol = { new("5ad87a"), new("ff9a3a"), new("ff4a4a") };

    private void DrawRna(Mission m, Rect2 r)
    {
        var p = m.FrontCell != null ? m.FrontProbs : new[] { 1f, 0f, 0f };
        int k = Array.IndexOf(p, p.Max());
        // clase + anillo de confianza
        var c = r.Position + new Vector2(22, 24);
        DrawArc(c, 20, 0, MathF.Tau, 32, new Color(1, 1, 1, 0.12f), 4, true);
        DrawArc(c, 20, -MathF.PI / 2, -MathF.PI / 2 + MathF.Tau * p[k], 32, ClassCol[k], 4, true);
        Icons.Draw(this, k == 0 ? "ok" : k == 1 ? "warn" : "veto", c, 18, ClassCol[k]);
        if (m.FrontCell == null) Gfx.Text(this, c + new Vector2(-14, 24), "—", 10, UiTheme.Dim);
        // barras de probabilidad
        float bx = r.Position.X + 52, bw = r.Size.X - 52 - 30;
        for (int i = 0; i < 3; i++)
        {
            float y = r.Position.Y + 4 + i * 14;
            DrawCircle(new Vector2(bx + 4, y + 5), 4, ClassCol[i]);
            Gfx.Bar(this, new Rect2(bx + 12, y + 1, bw - 12, 8), p[i], ClassCol[i]);
            Gfx.Text(this, new Vector2(bx, y - 1), $"{p[i] * 100:0}", 9, Colors.White, r.Size.X - 52, HorizontalAlignment.Right);
        }
        // rasgos de entrada (z)
        var rna = m.Rna;
        float sy = r.Position.Y + 52, sw = r.Size.X / FeatureModel.NF;
        for (int i = 0; i < FeatureModel.NF; i++)
        {
            float z = rna != null && m.FrontCell != null ? (m.FrontFeatures[i] - rna.Mean[i]) / rna.Std[i] : 0;
            float hh = Mathf.Clamp(z / 3f, -1, 1) * 14;
            var rr = new Rect2(r.Position.X + i * sw, sy, sw - 2, 28);
            DrawRect(rr, new Color(1, 1, 1, 0.04f));
            DrawRect(new Rect2(rr.Position.X, sy + 14 - MathF.Max(0, hh), rr.Size.X, MathF.Abs(hh)), hh >= 0 ? new Color(UiTheme.Rna, 0.9f) : new Color("e07050"));
        }
    }

    private void DrawFuzzy(Mission m, Rect2 r)
    {
        var fz = m.LastFuzzy;
        if (fz == null)
        {
            Icons.Draw(this, "veto", r.Position + new Vector2(20, 24), 18, UiTheme.Dim);
            return;
        }
        // curva agregada de velocidad
        var cr = new Rect2(r.Position, new Vector2(r.Size.X - 64, 44));
        Gfx.Frame(this, cr);
        var agg = fz.Aggregated["velocidad"];
        var pts = new System.Collections.Generic.List<Vector2> { new(cr.Position.X, cr.End.Y) };
        for (int i = 0; i < agg.Length; i++) pts.Add(new Vector2(cr.Position.X + cr.Size.X * i / (agg.Length - 1), cr.End.Y - cr.Size.Y * agg[i]));
        pts.Add(cr.End);
        if (agg.Any(a => a > 0)) DrawColoredPolygon(pts.ToArray(), new Color(UiTheme.Fuzzy, 0.4f));
        float vx = cr.Position.X + cr.Size.X * fz.Crisp["velocidad"];
        DrawLine(new Vector2(vx, cr.Position.Y), new Vector2(vx, cr.End.Y), Colors.White, 2);
        // medidores
        Gauge(new Vector2(r.End.X - 44, r.Position.Y + 16), fz.Crisp["velocidad"], UiTheme.Fuzzy, "move");
        Gauge(new Vector2(r.End.X - 12, r.Position.Y + 16), fz.Crisp["cautela"], UiTheme.Warn, "safe");
        // reglas activas: chip con su fuerza
        float x = r.Position.X;
        foreach (var (rule, st) in fz.Fired.OrderByDescending(f => f.Strength).Take(4))
        {
            if (st < 0.01f) continue;
            float bw = (r.Size.X - 6) / 4;
            DrawRect(new Rect2(x, r.Position.Y + 52, bw - 4, 22), new Color(UiTheme.Fuzzy, 0.08f + 0.3f * st));
            Gfx.Text(this, new Vector2(x + 3, r.Position.Y + 53), rule.Id, 9, Colors.White);
            Gfx.Bar(this, new Rect2(x + 3, r.Position.Y + 67, bw - 10, 4), st, UiTheme.Fuzzy);
            x += bw;
        }
    }

    private void Gauge(Vector2 c, float v, Color col, string icon)
    {
        DrawArc(c, 13, MathF.PI * 0.75f, MathF.PI * 2.25f, 20, new Color(1, 1, 1, 0.12f), 3, true);
        DrawArc(c, 13, MathF.PI * 0.75f, MathF.PI * (0.75f + 1.5f * Mathf.Clamp(v, 0, 1)), 20, col, 3, true);
        Icons.Draw(this, icon, c, 11, col);
        Gfx.Text(this, c + new Vector2(-16, 14), $"{v:0.00}", 9, Colors.White, 32, HorizontalAlignment.Center);
    }

    private void DrawGa(Mission m, Rect2 r, float y0)
    {
        var ga = m.LastGa;
        if (ga == null || ga.History.Count < 2)
        {
            Icons.Draw(this, "target", r.Position + new Vector2(20, 24), 18, UiTheme.Dim);
            Gfx.Text(this, r.Position + new Vector2(40, 18), m.TargetName, 10, UiTheme.Dim, r.Size.X - 44);
            return;
        }
        var cr = new Rect2(r.Position, new Vector2(r.Size.X, 42));
        Gfx.Frame(this, cr);
        float lo = ga.History.Min(h => h.Mean), hi = ga.History.Max(h => h.Best);
        if (hi - lo < 1e-4f) hi = lo + 1e-4f;
        Vector2 P(int i, float v) => new(cr.Position.X + cr.Size.X * i / (ga.History.Count - 1), cr.End.Y - cr.Size.Y * (v - lo) / (hi - lo));
        DrawPolyline(ga.History.Select((h, i) => P(i, h.Mean)).ToArray(), new Color(UiTheme.Ga, 0.7f), 1.2f, true);
        DrawPolyline(ga.History.Select((h, i) => P(i, h.Best)).ToArray(), UiTheme.Ok, 1.8f, true);
        Gfx.Text(this, new Vector2(cr.End.X - 60, y0 + 4), $"{ga.Generations} gen", 9, UiTheme.Dim, 56, HorizontalAlignment.Right);
        // candidatas
        if (ga.Candidates.Count == 0) return;
        float fmin = ga.Candidates.Min(c => c.Fitness), fmax = ga.Candidates.Max(c => c.Fitness);
        for (int i = 0; i < ga.Candidates.Count; i++)
        {
            var c = ga.Candidates[i];
            bool chosen = ga.Best != null && c.Label == ga.Best.Label;
            float y = r.Position.Y + 48 + i * 10;
            var cc = View.OverlayView.CandidateColors[i % 3];
            Gfx.Text(this, new Vector2(r.Position.X, y - 3), c.Label, 9, chosen ? UiTheme.Ok : cc);
            float t = fmax - fmin < 1e-5f ? 1 : 0.25f + 0.75f * (c.Fitness - fmin) / (fmax - fmin);
            DrawRect(new Rect2(r.Position.X + 12, y, (r.Size.X - 14) * t, 6), chosen ? UiTheme.Ok : new Color(cc, 0.6f));
        }
    }

    private void DrawSup(Mission m, Rect2 r, float y0)
    {
        var d = m.LastDecision;
        if (d == null) return;
        var col = d.Veto ? UiTheme.Crit : d.Action == RoverAction.Avanzar ? (d.Alert ? UiTheme.Warn : UiTheme.Ok) : UiTheme.Warn;
        var c = r.Position + new Vector2(20, 18);
        DrawCircle(c, 18, new Color(col, 0.18f));
        Icons.Draw(this, Icons.Action(d.Action), c, 22, col);
        Gfx.Text(this, r.Position + new Vector2(46, 2), Icons.Verb(d.Action) + (d.Action == RoverAction.Avanzar ? $" {d.Speed:0.00}" : ""), 12, col, r.Size.X - 46);
        Gfx.Text(this, r.Position + new Vector2(46, 18), d.Code + (d.Veto ? " · VETO" : ""), 10, UiTheme.Dim, r.Size.X - 46);
        // una luz por restricción
        int n = d.Checks.Count, perRow = Math.Max(1, (int)((r.Size.X - 4) / 14));
        for (int i = 0; i < n; i++)
        {
            var ch = d.Checks[i];
            var lc = !ch.Passed ? UiTheme.Crit : ch.Applied ? UiTheme.Warn : UiTheme.Ok;
            DrawCircle(r.Position + new Vector2(6 + (i % perRow) * 14, 48 + (i / perRow) * 14), 4.5f, lc);
        }
    }
}
