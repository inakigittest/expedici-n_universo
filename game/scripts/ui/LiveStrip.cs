using System;
using System.Linq;
using AresX.AI;
using AresX.Core;
using AresX.Sim;
using Godot;

namespace AresX.UI;

/// <summary>
/// Franja de decisión en vivo (centro abajo, bajo el vehículo): el ciclo del paso actual en tarjetas encadenadas
/// VEHÍCULO → RNA (ver) → DIFUSO (ajustar) → AG (elegir) → SUPERVISOR (decidir). Se adapta al ancho libre que dejan
/// los acoples: con poco espacio oculta la tarjeta del vehículo y abrevia los textos.
/// </summary>
public partial class LiveStrip : Control
{
    public Mission? Mission;

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        var m = Mission;
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.02f, 0.035f, 0.06f, 0.86f));
        DrawRect(new Rect2(Vector2.Zero, Size), UiTheme.Border, false, 1);
        if (m == null) return;
        bool wide = Size.X >= 640;
        int n = wide ? 5 : 4;
        float gap = 14, cw = (Size.X - 8 - gap * (n - 1)) / n, h = Size.Y - 8;
        int k = 0;
        Rect2 Next()
        {
            var r = new Rect2(4 + k * (cw + gap), 4, cw, h);
            if (k > 0)
                Gfx.Text(this, new Vector2(r.Position.X - gap + 1, h / 2 - 4), "▸", 13, UiTheme.Dim);
            k++;
            return r;
        }
        if (wide) DrawVehicle(m, Next());
        DrawRna(m, Next());
        DrawFuzzy(m, Next());
        DrawGa(m, Next());
        DrawSupervisor(m, Next());
    }

    private void Card(Rect2 r, string title, Color col)
    {
        DrawRect(r, new Color(col, 0.07f));
        DrawRect(new Rect2(r.Position, new Vector2(3, r.Size.Y)), col);
        Gfx.Text(this, r.Position + new Vector2(7, 1), Fit(title, 10, r.Size.X - 10), 10, col);
    }

    /// <summary>Recorta el texto con "…" para que quepa en el ancho dado.</summary>
    private static string Fit(string t, int size, float w)
    {
        if (w <= 8) return "";
        if (Gfx.TextWidth(t, size) <= w) return t;
        while (t.Length > 1 && Gfx.TextWidth(t + "…", size) > w) t = t[..^1];
        return t + "…";
    }

    /// <summary>Fila etiqueta/valor: el valor manda (a la derecha, hasta 62 % del ancho) y la etiqueta usa lo que queda.</summary>
    private void Row(Rect2 r, int line, string label, string value, Color vc)
    {
        float y = r.Position.Y + 17 + line * 14;
        float inner = r.Size.X - 11;
        string v = Fit(value, 10, inner * 0.62f);
        float vw = Gfx.TextWidth(v, 10);
        Gfx.Text(this, new Vector2(r.Position.X + 7, y), Fit(label, 10, inner - vw - 6), 10, UiTheme.Dim);
        Gfx.Text(this, new Vector2(r.Position.X, y), v, 10, vc, r.Size.X - 4, HorizontalAlignment.Right);
    }

    private void Line(Rect2 r, int line, string text, Color c) =>
        Gfx.Text(this, new Vector2(r.Position.X + 7, r.Position.Y + 17 + line * 14), Fit(text, 10, r.Size.X - 11), 10, c);

    private void Bar(Rect2 r, int line, string label, float v, Color c, string txt)
    {
        float y = r.Position.Y + 17 + line * 14;
        Gfx.Text(this, new Vector2(r.Position.X + 7, y), Fit(label, 10, 52), 10, UiTheme.Dim);
        float bx = r.Position.X + 62, bw = r.Size.X - 62 - 40;
        Gfx.Bar(this, new Rect2(bx, y + 3, MathF.Max(10, bw), 7), Mathf.Clamp(v, 0, 1), c);
        Gfx.Text(this, new Vector2(r.Position.X, y), txt, 10, Colors.White, r.Size.X - 4, HorizontalAlignment.Right);
    }

    private void DrawVehicle(Mission m, Rect2 r)
    {
        Card(r, m.Heli ? "HELICÓPTERO" : m.Config.Probe ? "SONDA" : "VEHÍCULO", UiTheme.Info);
        Bar(r, 0, "Batería", m.Rover.BatteryPct / 100f, m.Rover.BatteryPct < 20 ? UiTheme.Crit : UiTheme.Ok, $"{m.Rover.BatteryPct:0} %");
        float v = m.Rover.LastSpeed * m.Rover.VmaxMs;
        Row(r, 1, "Velocidad", $"{v:0.0} m/s", UiTheme.Text);
        if (m.Heli)
        {
            Row(r, 2, "Alt · margen", $"{m.HeliAglM:0} m · {m.HeliMargin:0.00}", m.HeliMargin < 1.12f ? UiTheme.Warn : UiTheme.Text);
            Row(r, 3, "Ráfaga", $"{m.Env.WindGust:0}/{m.WindLimitMs:0} m/s", m.Env.WindGust > m.WindLimitMs * 0.75f ? UiTheme.Warn : UiTheme.Text);
        }
        else
        {
            Line(r, 2, $"{m.Map.Slope[m.Map.Idx(m.Rover.Pos)]:0}° · {WorldMap.SoilName(m.Map.SoilAt(m.Rover.Pos))}", UiTheme.Text);
            Row(r, 3, "T int · ráfaga", $"{m.Rover.TempInt:0} °C · {m.Env.WindGust:0} m/s", UiTheme.Text);
        }
        Line(r, 4, Mission.StateName(m.State), UiTheme.Accent);
    }

    private void DrawRna(Mission m, Rect2 r)
    {
        Card(r, m.UseRna ? "RNA · VER" : "PERCEPCIÓN (regla)", UiTheme.Rna);
        if (m.FrontCell is not Cell f)
        {
            Row(r, 0, "Celda", "sin propuesta", UiTheme.Dim);
            Row(r, 1, "Observadas", $"{m.Belief.Observed.Count(o => o) * 100f / m.Belief.Observed.Length:0.0} %", UiTheme.Text);
            return;
        }
        var names = m.Map.ProbeMode ? NeuralPerception.ProbeClassNames : m.Heli ? new[] { "apta", "obstruida", "escarpada" } : NeuralPerception.ClassNames;
        var p = m.FrontProbs;
        int best = Array.IndexOf(p, p.Max());
        Line(r, 0, $"{f} → {names[best].ToUpperInvariant()} {p[best]:0.00}", best == 0 ? UiTheme.Ok : UiTheme.Crit);
        Color[] cc = { UiTheme.Ok, new("ff9a3a"), UiTheme.Crit };
        for (int i = 0; i < 3; i++) Bar(r, 1 + i, names[i], p[i], cc[i], p[i].ToString("0.00"));
        if (p.Max() < SafetySupervisor.LowConfidence) Row(r, 4, "Confianza", "BAJA → cautela", UiTheme.Warn);
    }

    private void DrawFuzzy(Mission m, Rect2 r)
    {
        Card(r, "DIFUSO · AJUSTAR", UiTheme.Fuzzy);
        var fz = m.LastFuzzy;
        if (fz == null)
        {
            Row(r, 0, "Sin difuso", "ablación", UiTheme.Dim);
            return;
        }
        Bar(r, 0, "velocidad", fz.Crisp["velocidad"], UiTheme.Fuzzy, fz.Crisp["velocidad"].ToString("0.00"));
        Bar(r, 1, "cautela", fz.Crisp["cautela"], UiTheme.Warn, fz.Crisp["cautela"].ToString("0.00"));
        int line = 2;
        foreach (var (rule, st) in fz.Fired.OrderByDescending(x => x.Strength).Take(3))
        {
            if (st < 0.01f) continue;
            string then = string.Join(",", rule.Then.Select(t => t.Set));
            Line(r, line++, $"{rule.Id} {st:0.00} → {then}", UiTheme.Fuzzy.Lightened(0.3f));
        }
        if (fz.Fallback) Row(r, line, "Respaldo", fz.FallbackNote, UiTheme.Warn);
    }

    private void DrawGa(Mission m, Rect2 r)
    {
        Card(r, m.Config.UseGa ? "AG · ELEGIR" : "RUTA (sin AG)", UiTheme.Ga);
        Line(r, 0, $"→ {m.TargetName} {m.Target}", UiTheme.Accent);
        var ga = m.LastGa;
        if (ga == null)
        {
            Row(r, 1, "Ruta", m.Route.Count > 1 ? $"{m.Route.Count - 1 - m.RouteIdx} pasos" : "—", UiTheme.Text);
            return;
        }
        Line(r, 1, $"{ga.Generations} gen · {ga.Ms:0} ms{(ga.EarlyStop ? " · parada temprana" : "")}", UiTheme.Text);
        if (ga.Best != null) Line(r, 2, $"ruta {ga.Best.Label} · f {ga.Best.Fitness:0.000} · {ga.Best.EnergyWh:0} Wh", UiTheme.Ok);
        int left = Math.Max(0, m.Route.Count - 1 - m.RouteIdx);
        Line(r, 3, $"faltan {left} pasos · {left * m.Map.CellM / 1000f:0.0} km", UiTheme.Text);
        Line(r, 4, ga.Trigger, UiTheme.Dim);
    }

    private void DrawSupervisor(Mission m, Rect2 r)
    {
        Card(r, "SUPERVISOR · DECIDIR", UiTheme.Sup);
        var d = m.LastDecision;
        if (d == null) return;
        var col = d.Veto ? UiTheme.Crit : d.Action == RoverAction.Avanzar ? (d.Alert ? UiTheme.Warn : UiTheme.Ok) : UiTheme.Warn;
        string act = SafetySupervisor.ActionName(d.Action) + (d.Action == RoverAction.Avanzar ? $" {d.Speed:0.00}" : "");
        Gfx.Chip(this, r.Position + new Vector2(7, 17), act, 11, col, true);
        Gfx.Text(this, r.Position + new Vector2(Gfx.ChipWidth(act, 11) + 12, 18), Fit(d.Code + (d.Veto ? " VETO" : ""), 11, r.Size.X - Gfx.ChipWidth(act, 11) - 16), 11, col);
        // motivo en dos líneas
        string reason = d.Reason;
        float maxW = r.Size.X - 12;
        var words = reason.Split(' ');
        string l1 = "", l2 = "";
        foreach (var w in words)
        {
            if (l2 == "" && Gfx.TextWidth((l1 + " " + w).Trim(), 10) <= maxW) l1 = (l1 + " " + w).Trim();
            else l2 = (l2 + " " + w).Trim();
        }
        Gfx.Text(this, r.Position + new Vector2(7, 36), l1, 10, UiTheme.Text, maxW);
        Gfx.Text(this, r.Position + new Vector2(7, 49), Fit(l2, 10, maxW), 10, UiTheme.Text);
        var flagged = d.Checks.Where(c => !c.Passed || c.Applied).Take(2).ToList();
        int line = 0;
        foreach (var c in flagged)
            Gfx.Text(this, r.Position + new Vector2(7, 64 + line++ * 13), Fit($"{(c.Passed ? "!" : "✗")} {c.Code} {c.Name}", 9, maxW), 9, c.Passed ? UiTheme.Warn : UiTheme.Crit, maxW);
        if (flagged.Count == 0)
            Gfx.Text(this, r.Position + new Vector2(7, 64), Fit($"✓ {d.Checks.Count(c => c.Passed)}/{d.Checks.Count} restricciones cumplidas", 9, maxW), 9, UiTheme.Ok, maxW);
    }
}
