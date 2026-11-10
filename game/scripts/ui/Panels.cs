using System;
using System.Collections.Generic;
using System.Linq;
using AresX.AI;
using AresX.Core;
using AresX.Sim;
using Godot;

namespace AresX.UI;

/// <summary>Panel B · TELEMETRÍA: todos los canales de sensores con unidad, barra frente a límites y estado de validación.</summary>
public partial class TelemetryPanel : Control
{
    public Mission? Mission;
    private const int Row = 15;
    private static readonly string[] GroupOrder = { "Energía", "Térmico", "Atmósfera", "Terreno", "Proximidad", "Dinámica", "Radiación", "Cámara", "Navegación", "Sistema" };

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        var m = Mission;
        if (m?.Reading == null) return;
        var s = m.Reading;
        float w = Size.X, y = 0;
        foreach (var g in GroupOrder)
        {
            var chans = s.Order.Select(id => s[id]).Where(c => c.Group == g).ToList();
            if (chans.Count == 0) continue;
            Gfx.Header(this, new Vector2(0, y), w, g.ToUpperInvariant(), UiTheme.Accent);
            y += 17;
            foreach (var c in chans)
            {
                DrawRow(c, y, w);
                y += Row;
            }
            y += 3;
        }
        // estado derivado (estimaciones del sistema, no lecturas directas)
        Gfx.Header(this, new Vector2(0, y), w, "ESTIMACIONES DERIVADAS", UiTheme.Info);
        y += 17;
        var r = m.Rover;
        float netW = r.SolarW + r.RtgW - r.LoadW;
        string autonomy = netW >= 0 ? "carga neta +" + netW.ToString("0") + " W" : $"{r.BatteryWh / -netW:0.0} h al consumo actual";
        Derived("Autonomía", autonomy, y, w, netW >= 0 ? UiTheme.Ok : r.BatteryWh / -netW < 0.5f ? UiTheme.Crit : UiTheme.Text); y += Row;
        Derived("Energía al refugio (+25 %)", m.RefugeReachable ? $"{m.EnergyToRefuge:0.0} Wh + reserva {m.ReserveWh:0}" : "inalcanzable", y, w,
            m.RefugeReachable && r.BatteryWh - m.EnergyToRefuge > m.ReserveWh ? UiTheme.Text : UiTheme.Crit); y += Row;
        Derived("Calefactor / refrigeración", $"{r.HeaterW:0} W / {r.CoolerW:0} W", y, w, UiTheme.Text); y += Row;
        Derived("Tracción (último paso)", $"{r.DriveW:0} W · v {r.LastSpeed * r.VmaxMs:0.00} m/s", y, w, UiTheme.Text); y += Row;
        if (r.RtgW > 0) { Derived("Generador radioisótopos", $"{r.RtgW:0} W", y, w, UiTheme.Text); y += Row; }
        Derived("Polvo en paneles", $"{r.PanelDust * 100:0.0} %", y, w, r.PanelDust > 0.3f ? UiTheme.Warn : UiTheme.Text); y += Row;
        Derived("Dosis acumulada", $"{r.DoseMsv:0.000} mSv", y, w, UiTheme.Text); y += Row;
        Derived("Desgaste / corrosión", $"{r.MechWear:0.0} % / {r.Corrosion:0.0} %", y, w, r.Corrosion > 30 ? UiTheme.Warn : UiTheme.Text); y += Row;
        Derived("Bodega de muestras", $"{r.SampleKg:0.0} / {r.SampleCapKg:0} kg", y, w, UiTheme.Text); y += Row;
        Derived("Valor científico", $"{r.Science:0.00} ({m.Samples} muestras)", y, w, UiTheme.Text); y += Row;
        Derived("Posición real / estimada", $"{r.Pos} / {r.EstCell}", y, w, r.EstCell == r.Pos ? UiTheme.Text : UiTheme.Warn); y += Row;
        Derived("Pendiente celda / suelo", $"{m.Map.Slope[m.Map.Idx(r.Pos)]:0.0}° · {WorldMap.SoilName(m.Map.SoilAt(r.Pos))}", y, w, UiTheme.Text); y += Row;
        float margin = MathF.Min(r.TempInt - m.Planet.HardwareMinC, m.Planet.HardwareMaxC - r.TempInt);
        Derived("Margen térmico al límite", $"{margin:0.0} °C", y, w, margin < 10 ? UiTheme.Crit : margin < 25 ? UiTheme.Warn : UiTheme.Text); y += Row;
        if (m.Planet.HasRain)
        {
            float li = m.Env.LandslideIndex(r.Pos);
            Derived("Índice de remoción (celda)", $"{li:0.00}" + (m.Env.Rain24h > 0 ? $" · retorno ≈{GeoClick.ReturnYears(m.Env.Rain24h):0.0} años" : ""), y, w, li > 0.5f ? UiTheme.Crit : li > 0.25f ? UiTheme.Warn : UiTheme.Text);
            y += Row;
        }
        Derived("Latencia de órdenes (sim.)", m.Env.CommandDelaySteps == 0 ? "inmediata" : $"{m.Env.CommandDelaySteps} pasos" + (m.PendingDestination != null ? " · DESTINO EN TRÁNSITO" : ""), y, w,
            m.Env.CommandDelaySteps > 0 ? UiTheme.Warn : UiTheme.Text); y += Row;
        CustomMinimumSize = new Vector2(0, y + 4);
    }

    private void Derived(string name, string value, float y, float w, Color col)
    {
        Gfx.Text(this, new Vector2(4, y), name, 11, UiTheme.Dim);
        Gfx.Text(this, new Vector2(0, y), value, 11, col, w - 4, HorizontalAlignment.Right);
    }

    private void DrawRow(Channel c, float y, float w)
    {
        var col = UiTheme.StatusColor(c.Status);
        Gfx.Text(this, new Vector2(4, y), c.Name, 11, c.Applicable ? UiTheme.Text.Darkened(0.12f) : UiTheme.Dim);
        float tagW = 40, barW = 46, valR = w - tagW - barW - 8;
        Gfx.Text(this, new Vector2(0, y), c.Text, 11, c.Applicable ? Colors.White : UiTheme.Dim, valR, HorizontalAlignment.Right);
        if (c.Applicable && MathX.Finite(c.Value))
        {
            float lo, hi;
            if (float.IsFinite(c.CritLo) && float.IsFinite(c.CritHi) && c.CritHi < 1e8f) { lo = c.CritLo; hi = c.CritHi; }
            else if (float.IsFinite(c.CritHi) && c.CritHi < 1e8f) { lo = MathF.Max(c.Min, 0); hi = c.CritHi * 1.15f; }
            else { lo = c.Min; hi = MathF.Min(c.Max, c.Min + MathF.Max(1, MathF.Abs(c.Value) * 2.5f)); }
            float t = MathX.InvLerp(lo, hi, c.Value);
            Gfx.Bar(this, new Rect2(valR + 4, y + 4, barW, 6), t, new Color(col, 0.85f));
        }
        Gfx.Chip(this, new Vector2(w - tagW + 2, y + 1), UiTheme.StatusTag(c.Status), 9, col);
    }
}

/// <summary>Panel C · DECISIÓN EXPLICABLE: RNA, difuso, AG y supervisor con valores concretos del paso actual.</summary>
public partial class DecisionPanel : Control
{
    public Mission? Mission;

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        var m = Mission;
        if (m == null) return;
        float w = Size.X, y = 0;
        if (m.Config.ClickMode) y = DrawDestination(m, w, y) + 6;
        y = DrawRna(m, w, y) + 6;
        y = DrawFuzzy(m, w, y) + 6;
        y = DrawGa(m, w, y) + 6;
        y = DrawSupervisor(m, w, y);
        CustomMinimumSize = new Vector2(0, y + 4);
    }

    private float DrawDestination(Mission m, float w, float y)
    {
        Gfx.Header(this, new Vector2(0, y), w, "EVALUACIÓN DEL DESTINO (clic del operador)", UiTheme.Info);
        y += 18;
        var r = m.DestReport;
        if (!m.HasDestination || r == null)
        {
            Gfx.Text(this, new Vector2(4, y), m.PendingDestination != null ? "Orden en tránsito por el enlace degradado…" : "Sin destino: haga clic en el terreno o en el mapa 2D.", 11, UiTheme.Dim, w - 8);
            return y + 14;
        }
        var col = r.Verdict switch { DestVerdict.Viable => UiTheme.Ok, DestVerdict.Arriesgado or DestVerdict.SoloIda => UiTheme.Warn, _ => UiTheme.Crit };
        Gfx.Chip(this, new Vector2(4, y), r.VerdictText, 11, col, true);
        Gfx.Text(this, new Vector2(Gfx.ChipWidth(r.VerdictText, 11) + 10, y + 1), $"{m.TargetName} {r.Cell}", 11, UiTheme.Text, w - Gfx.ChipWidth(r.VerdictText, 11) - 14);
        y += 18;
        int left = Math.Max(0, m.Route.Count - 1 - m.RouteIdx);
        string ret = float.IsFinite(r.ReturnWh) ? $"{r.ReturnWh:0.0} Wh" : "sin ruta";
        (string, string, Color)[] rows =
        {
            ("Distancia recta / ruta est.", $"{r.StraightM:0} m / {r.PathM:0} m ({r.Steps} pasos)", UiTheme.Text),
            ("Restante en la ruta vigente", $"{left} pasos · {left * WorldMap.CellM:0} m", UiTheme.Accent),
            ("Energía ida (+25 %) / regreso", $"{r.EnergyWh:0.0} Wh / {ret}", r.Verdict is DestVerdict.EnergiaInsuficiente or DestVerdict.SoloIda ? UiTheme.Crit : UiTheme.Text),
            ("Batería al llegar / tras volver", $"{r.BatteryAtArrivalPct:0} % / {(float.IsFinite(r.BatteryAfterReturnPct) ? r.BatteryAfterReturnPct.ToString("0") + " %" : "—")}", r.BatteryAfterReturnPct < 10 ? UiTheme.Warn : UiTheme.Text),
            ("Tiempo estimado (ETA)", $"{r.EtaS / 60:0.0} min de misión", UiTheme.Text),
            ("Riesgo RNA medio / máx.", $"{r.RiskMean:0.00} / {r.RiskMax:0.00} · sin observar {r.UnknownFrac:P0}", r.RiskMax > 0.6f ? UiTheme.Warn : UiTheme.Text),
            ("Destino: elev. / pend. / T", $"{r.ElevEst:0} m · {r.SlopeEst:0}° · {r.TempNow:0} °C", UiTheme.Text),
            ("Destino: suelo / presión", $"{r.Soil} · {(r.PressureBar < 0.05f ? (r.PressureBar * 1e5f).ToString("0.#") + " Pa" : r.PressureBar.ToString("0.000") + " bar")}", UiTheme.Text),
        };
        foreach (var (n, v, c) in rows)
        {
            Gfx.Text(this, new Vector2(4, y), n, 10, UiTheme.Dim);
            Gfx.Text(this, new Vector2(0, y), v, 10, c, w - 4, HorizontalAlignment.Right);
            y += 12;
        }
        foreach (var note in r.Notes.Take(4))
        {
            Gfx.Text(this, new Vector2(4, y), "• " + note, 10, UiTheme.Warn.Lightened(0.2f), w - 8);
            y += 12;
        }
        return y;
    }

    private float DrawRna(Mission m, float w, float y)
    {
        Gfx.Header(this, new Vector2(0, y), w, m.UseRna ? "RNA · PERCEPCIÓN (VER)  11→32→16→3" : "PERCEPCIÓN SIN RNA (regla de umbrales · ablación)", UiTheme.Rna);
        y += 18;
        if (m.FrontCell is not Cell f)
        {
            Gfx.Text(this, new Vector2(4, y), "Sin celda propuesta en este paso (observación/espera)", 11, UiTheme.Dim);
            return y + 14;
        }
        var names = m.Map.ProbeMode ? NeuralPerception.ProbeClassNames : NeuralPerception.ClassNames;
        var p = m.FrontProbs;
        int k = Array.IndexOf(p, p.Max());
        Gfx.Text(this, new Vector2(4, y), $"Celda {f} → {names[k].ToUpperInvariant()}", 12, k == 0 ? UiTheme.Ok : UiTheme.Crit);
        Gfx.Text(this, new Vector2(0, y), $"confianza {p.Max():0.00}", 11, p.Max() < SafetySupervisor.LowConfidence ? UiTheme.Warn : UiTheme.Dim, w - 4, HorizontalAlignment.Right);
        y += 16;
        Color[] cc = { UiTheme.Ok, new("ff9a3a"), UiTheme.Crit };
        for (int i = 0; i < 3; i++)
        {
            Gfx.Text(this, new Vector2(4, y), names[i], 11, UiTheme.Text);
            Gfx.Bar(this, new Rect2(76, y + 3, w - 130, 8), p[i], cc[i]);
            Gfx.Text(this, new Vector2(0, y), p[i].ToString("0.000"), 11, Colors.White, w - 4, HorizontalAlignment.Right);
            y += 13;
        }
        // vector de entrada normalizado (z-score con estadísticas del entrenamiento)
        var rna = m.Rna;
        float bw = (w - 8) / FeatureModel.NF;
        for (int i = 0; i < FeatureModel.NF; i++)
        {
            float z = rna != null ? (m.FrontFeatures[i] - rna.Mean[i]) / rna.Std[i] : 0;
            var r = new Rect2(4 + i * bw, y, bw - 2, 18);
            DrawRect(r, new Color(1, 1, 1, 0.05f));
            float h = Mathf.Clamp(z / 3f, -1, 1) * 9;
            DrawRect(new Rect2(r.Position.X, y + 9 - MathF.Max(0, h), r.Size.X, MathF.Abs(h)), h >= 0 ? new Color(UiTheme.Rna, 0.9f) : new Color("e07050"));
            Gfx.Text(this, new Vector2(r.Position.X, y + 18), FeatureModel.Short[i], 8, UiTheme.Dim, bw, HorizontalAlignment.Center);
        }
        y += 31;
        if (m.FrontActivations is { } acts)
        {
            int on1 = acts[1].Count(a => a > 0), on2 = acts[2].Count(a => a > 0);
            Gfx.Text(this, new Vector2(4, y), $"ocultas activas {on1}/32 · {on2}/16 · caída {m.FrontFeatures[1]:0.0} m · eco {m.FrontFeatures[3]:0.0} m", 10, UiTheme.Dim);
            y += 13;
        }
        return y;
    }

    private float DrawFuzzy(Mission m, float w, float y)
    {
        var fz = m.LastFuzzy;
        Gfx.Header(this, new Vector2(0, y), w, fz != null ? "DIFUSO · CONTROL (AJUSTAR)  Mamdani min-max · centroide" : "SIN DIFUSO (velocidad fija 0,60 · ablación)", UiTheme.Fuzzy);
        y += 18;
        if (fz == null) return y;
        // entradas con su etiqueta dominante
        float colW = (w - 4) / 3;
        int idx = 0;
        foreach (var (id, deg) in fz.Degrees)
        {
            var best = deg.OrderByDescending(kv => kv.Value).First();
            var v = m.Fuzzy.Inputs[id];
            float x = 4 + (idx % 3) * colW, yy = y + idx / 3 * 26;
            Gfx.Text(this, new Vector2(x, yy), $"{v.Name}", 9, UiTheme.Dim);
            Gfx.Text(this, new Vector2(x, yy + 10), $"{fz.Inputs[id]:0.#}{v.Unit} {best.Key} {best.Value:0.00}", 10, best.Value > 0 ? UiTheme.Fuzzy.Lightened(0.3f) : UiTheme.Dim);
            idx++;
        }
        y += 54;
        Gfx.Text(this, new Vector2(4, y), $"Reglas activas: {fz.Fired.Count}/{m.Fuzzy.Rules.Count}", 11, UiTheme.Text);
        y += 14;
        foreach (var (rule, s) in fz.Fired.Take(4))
        {
            Gfx.Bar(this, new Rect2(4, y + 3, 40, 7), s, UiTheme.Fuzzy);
            Gfx.Text(this, new Vector2(48, y), $"{rule.Id} {s:0.00} {Short(rule)}", 10, UiTheme.Text, w - 50);
            y += 12;
        }
        if (fz.Fallback) { Gfx.Text(this, new Vector2(4, y), fz.FallbackNote, 10, UiTheme.Warn); y += 12; }
        // curva agregada de velocidad y centroide
        var agg = fz.Aggregated["velocidad"];
        var r = new Rect2(4, y + 2, w - 140, 34);
        Gfx.Frame(this, r);
        var pts = new List<Vector2> { new(r.Position.X, r.End.Y) };
        for (int i = 0; i < agg.Length; i++) pts.Add(new Vector2(r.Position.X + r.Size.X * i / (agg.Length - 1), r.End.Y - r.Size.Y * agg[i]));
        pts.Add(new Vector2(r.End.X, r.End.Y));
        if (agg.Any(a => a > 0)) DrawColoredPolygon(pts.ToArray(), new Color(UiTheme.Fuzzy, 0.35f));
        float vx = r.Position.X + r.Size.X * fz.Crisp["velocidad"];
        DrawLine(new Vector2(vx, r.Position.Y), new Vector2(vx, r.End.Y), Colors.White, 2);
        Gfx.Text(this, new Vector2(r.End.X + 6, y), $"velocidad {fz.Crisp["velocidad"]:0.00}", 12, UiTheme.Fuzzy);
        Gfx.Text(this, new Vector2(r.End.X + 6, y + 15), $"cautela   {fz.Crisp["cautela"]:0.00}", 12, UiTheme.Fuzzy);
        return y + 40;
    }

    private static string Short(FuzzyRule r) =>
        string.Join("∧", r.If.Select(a => $"{a.Var[..Math.Min(4, a.Var.Length)]}={a.Set}")) + " → " + string.Join(",", r.Then.Select(t => t.Set));

    private float DrawGa(Mission m, float w, float y)
    {
        var ga = m.LastGa;
        Gfx.Header(this, new Vector2(0, y), w, m.Config.UseGa ? "AG · PLANIFICACIÓN (ELEGIR)" : "SIN AG (ruta de menor distancia · ablación)", UiTheme.Ga);
        y += 18;
        if (m.Plan != null)
        {
            string order = m.Plan.Order.Count == 0 ? "sin muestras" : string.Join("→", m.Plan.Order.Select(s => s.Visited ? $"✓{s.Name}" : s.Name));
            Gfx.Text(this, new Vector2(4, y), $"Plan: {order} → refugio", 11, UiTheme.Text, w - 8);
            y += 13;
            if (m.Config.UseGa)
            {
                Gfx.Text(this, new Vector2(4, y), $"mochila {m.Plan.MassKg:0.0}/{m.Rover.SampleCapKg - m.Rover.SampleKg + m.Plan.MassKg:0.0} kg · energía est. {m.Plan.EnergyWh:0} Wh · {(m.Plan.MassOk && m.Plan.EnergyOk ? "factible" : "PENALIZADO")}", 10, UiTheme.Dim, w - 8);
                y += 13;
            }
        }
        Gfx.Text(this, new Vector2(4, y), $"Objetivo: {m.TargetName} {m.Target}", 11, UiTheme.Accent, w - 8);
        y += 14;
        if (ga == null)
        {
            if (m.Config.UseGa) Gfx.Text(this, new Vector2(4, y), "Aún sin replanificación", 10, UiTheme.Dim);
            return y + 12;
        }
        Gfx.Text(this, new Vector2(4, y), $"Disparo: {ga.Trigger} · {ga.Generations} gen{(ga.EarlyStop ? " (parada temprana)" : "")} · pobl. {ga.Params.Population} · {ga.Ms:0} ms", 10, UiTheme.Dim, w - 8);
        y += 13;
        for (int i = 0; i < ga.Candidates.Count; i++)
        {
            var c = ga.Candidates[i];
            bool chosen = ReferenceEquals(c.Path, m.Route) || (ga.Best != null && c.Label == ga.Best.Label);
            var col = View.OverlayView.CandidateColors[i % 3];
            Gfx.Chip(this, new Vector2(4, y), c.Label, 10, chosen ? UiTheme.Ok : col, chosen);
            Gfx.Text(this, new Vector2(24, y), $"f={c.Fitness:0.000} · {c.Path.Count - 1} pasos · {c.EnergyWh:0} Wh{(c.Feasible ? "" : " · ✗ INVIABLE")}{(chosen ? " · ELEGIDA" : "")}", 10, c.Feasible ? UiTheme.Text : UiTheme.Crit, w - 26);
            y += 13;
            // desglose del fitness: barras por componente
            (string N, float V, Color C)[] parts =
            {
                ("C", ga.Params.WScience * c.Science, UiTheme.Ok), ("D", -ga.Params.WDistance * c.Distance, UiTheme.Info),
                ("E", -ga.Params.WEnergy * c.Energy, UiTheme.Fuzzy), ("R", -ga.Params.WRisk * c.Risk, UiTheme.Crit),
                ("T", -ga.Params.WTime * c.Time, UiTheme.Dim), ("P", -c.Penalty, new Color("ff60c0")),
            };
            float pw = (w - 30) / parts.Length;
            for (int k = 0; k < parts.Length; k++)
            {
                float x = 24 + k * pw;
                Gfx.Text(this, new Vector2(x, y - 1), parts[k].N, 9, parts[k].C);
                Gfx.SignedBar(this, new Rect2(x + 9, y + 2, pw - 12, 6), parts[k].V, 0.5f, parts[k].C, parts[k].C);
            }
            y += 11;
        }
        return y;
    }

    private float DrawSupervisor(Mission m, float w, float y)
    {
        var d = m.LastDecision;
        Gfx.Header(this, new Vector2(0, y), w, "SUPERVISOR DE SEGURIDAD · ÁRBITRO", UiTheme.Sup);
        y += 18;
        if (d == null) return y;
        var col = d.Veto ? UiTheme.Crit : d.Action == RoverAction.Avanzar ? (d.Alert ? UiTheme.Warn : UiTheme.Ok) : UiTheme.Warn;
        string act = SafetySupervisor.ActionName(d.Action) + (d.Action == RoverAction.Avanzar ? $" {d.Speed:0.00}" : "");
        Gfx.Chip(this, new Vector2(4, y), act, 13, col, true);
        Gfx.Text(this, new Vector2(Gfx.ChipWidth(act, 13) + 10, y + 2), d.Code + (d.Veto ? " · VETO" : ""), 12, col);
        y += 21;
        Gfx.Text(this, new Vector2(4, y), d.Reason, 10, UiTheme.Text, w - 8);
        y += 14;
        float colW = (w - 4) / 2;
        int i = 0;
        foreach (var c in d.Checks)
        {
            float x = 4 + (i % 2) * colW, yy = y + i / 2 * 12;
            var cc = c.Passed ? (c.Applied ? UiTheme.Warn : UiTheme.Ok.Darkened(0.1f)) : UiTheme.Crit;
            Gfx.Text(this, new Vector2(x, yy), $"{(c.Passed ? "✓" : "✗")} {c.Code} {c.Name}", 9, cc, colW - 4);
            i++;
        }
        y += (d.Checks.Count + 1) / 2 * 12 + 2;
        var failed = d.Checks.FirstOrDefault(c => !c.Passed || c.Applied);
        if (failed != null)
        {
            Gfx.Text(this, new Vector2(4, y), $"{failed.Code}: {failed.Detail}", 10, UiTheme.Dim, w - 8);
            y += 12;
        }
        return y;
    }
}

/// <summary>
/// Panel A (mini) · MAPA 2D: creencias del rover (o la verdad del simulador con T), rutas, sitios, refugios y rover.
/// Mayús+clic = barranco · Ctrl+clic = roca · Alt+clic = despejar · clic = inspeccionar celda.
/// </summary>
public partial class MiniMap : Control
{
    public Mission? Mission;
    public bool Truth;
    public Action<Cell, string>? Edit;
    public Cell? Hover;

    public override void _Process(double delta) => QueueRedraw();

    private float CellPx => Mission == null ? 4 : MathF.Min(Size.X, Size.Y) / Mission.Map.N;

    private Cell? CellAt(Vector2 p)
    {
        if (Mission == null) return null;
        var c = new Cell((int)(p.X / CellPx), (int)(p.Y / CellPx));
        return Mission.Map.InBounds(c) ? c : null;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm) Hover = CellAt(mm.Position);
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb && CellAt(mb.Position) is Cell c)
        {
            string what = mb.ShiftPressed ? "cliff" : mb.CtrlPressed ? "rock" : mb.AltPressed ? "clear" : "dest";
            Edit?.Invoke(c, what);
            AcceptEvent();
        }
    }

    public override void _Draw()
    {
        var m = Mission;
        if (m == null) return;
        var map = m.Map;
        var b = m.Belief;
        float cp = CellPx;
        float minH = map.H.Min(), maxH = map.H.Max();
        for (int y = 0; y < map.N; y++)
        for (int x = 0; x < map.N; x++)
        {
            var c = new Cell(x, y);
            int i = map.Idx(c);
            float hn = MathX.InvLerp(minH, maxH, map.H[i]);
            Color col;
            if (Truth)
            {
                col = View.Palette.Soil(map.Soils[i], map.Planet.Style).Darkened(0.35f - hn * 0.3f);
                var cls = map.ClassAt(c);
                if (cls == TerrainClass.Obstaculo) col = new Color("ff9a3a");
                if (cls == TerrainClass.Barranco) col = new Color("5a0a0a");
            }
            else
            {
                col = new Color(0.16f + hn * 0.18f, 0.18f + hn * 0.18f, 0.22f + hn * 0.18f);
                if (b.Observed[i])
                {
                    float r = b.Risk(c);
                    col = new Color(0.25f + hn * 0.2f, 0.32f + hn * 0.2f, 0.3f + hn * 0.15f).Lerp(new Color("ff6a2a"), Mathf.Clamp(r * 1.2f, 0, 1));
                }
                if (b.ConfirmedObstacle[i]) col = new Color("ff9a3a");
                if (b.ConfirmedCliff[i]) col = new Color("c01818");
                if (b.Vetoed[i]) col = new Color("c040c0");
            }
            DrawRect(new Rect2(x * cp, y * cp, cp - (cp > 4 ? 0.5f : 0), cp - (cp > 4 ? 0.5f : 0)), col);
        }
        Vector2 Ctr(Cell c) => new((c.X + 0.5f) * cp, (c.Y + 0.5f) * cp);
        void Poly(IReadOnlyList<Cell> path, Color col, float width)
        {
            if (path.Count < 2) return;
            DrawPolyline(path.Select(Ctr).ToArray(), col, width, true);
        }
        if (m.LastGa != null)
            for (int k = 0; k < m.LastGa.Candidates.Count; k++)
                Poly(m.LastGa.Candidates[k].Path, new Color(View.OverlayView.CandidateColors[k % 3], 0.7f), 1f);
        Poly(m.Trail, new Color(0.3f, 0.65f, 1f), 2f);
        if (m.Route.Count > 1) Poly(m.Route.Skip(m.RouteIdx).ToList(), new Color(0.3f, 1f, 0.5f), 1.6f);
        foreach (var r in map.Refuges)
        {
            DrawRect(new Rect2(r.X * cp - 1, r.Y * cp - 1, cp + 2, cp + 2), new Color("5aff8a"), false, 1.5f);
        }
        foreach (var s in map.Sites)
        {
            var col = s.Visited ? UiTheme.Dim : s.Anomaly ? new Color("d08cff") : new Color("ffb347");
            DrawCircle(Ctr(s.Cell), cp * 0.6f + 1, col);
            Gfx.Text(this, Ctr(s.Cell) + new Vector2(cp * 0.7f, -8), s.Name, 9, col);
        }
        DrawArc(Ctr(m.Target), cp * 1.1f + 2, 0, MathF.Tau, 16, UiTheme.Accent, 1.5f);
        if (m.AlienPos is Cell a) DrawCircle(Ctr(a), cp * 0.6f + 1, new Color("60ffd0"));
        // rover: triángulo con el rumbo + elipse del error de posición
        var rp = Ctr(m.Rover.Pos);
        float ang = MathF.Atan2(m.Rover.Dir.Y, m.Rover.Dir.X);
        var tri = new[] { rp + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (cp + 3), rp + new Vector2(MathF.Cos(ang + 2.4f), MathF.Sin(ang + 2.4f)) * (cp + 1), rp + new Vector2(MathF.Cos(ang - 2.4f), MathF.Sin(ang - 2.4f)) * (cp + 1) };
        DrawColoredPolygon(tri, Colors.White);
        float errPx = m.Rover.PosErrorM / WorldMap.CellM * cp;
        if (errPx > 1) DrawArc(rp, errPx, 0, MathF.Tau, 20, new Color(1, 1, 1, 0.4f), 1);
        if (m.FrontCell is Cell f && map.InBounds(f))
            DrawRect(new Rect2(f.X * cp, f.Y * cp, cp, cp), m.LastDecision?.Veto == true ? UiTheme.Crit : UiTheme.Ok, false, 1.5f);
        if (Hover is Cell h) DrawRect(new Rect2(h.X * cp, h.Y * cp, cp, cp), Colors.White, false, 1);
        Gfx.Text(this, new Vector2(2, map.N * cp - 13), Truth ? "VERDAD (simulador)" : "CREENCIA del rover", 9, Truth ? new Color("8ab0ff") : UiTheme.Dim);
    }

    public string Describe(Cell c)
    {
        var m = Mission!;
        var map = m.Map;
        int i = map.Idx(c);
        var p = m.Belief.P[i];
        return $"Celda {c} · elev {map.Elevation(c):0.0} m · pend {map.Slope[i]:0.0}° · {WorldMap.SoilName(map.Soils[i])}\n" +
               $"Creencia: p(libre/obst/barr) = {p[0]:0.00}/{p[1]:0.00}/{p[2]:0.00}{(m.Belief.Forbidden(c) ? " · PROHIBIDA" : "")}{(m.Belief.Observed[i] ? "" : " · no observada")}\n" +
               $"Verdad: {WorldMap.HazardName(map.Hazards[i])} · T {m.Env.TempAt(c):0.0} °C · p {m.Env.PressureAt(c):0.0000} bar";
    }
}

/// <summary>Minigráficos de la historia reciente (batería, temperatura interior, viento, velocidad).</summary>
public partial class Sparklines : Control
{
    public readonly List<float> Battery = new(), TempInt = new(), Wind = new(), Speed = new(), Risk = new();
    public Mission? Mission;

    public void Push(Mission m)
    {
        void P(List<float> l, float v) { l.Add(v); if (l.Count > 160) l.RemoveAt(0); }
        P(Battery, m.Rover.BatteryPct);
        P(TempInt, m.Rover.TempInt);
        P(Wind, m.Env.WindGust);
        P(Speed, m.LastDecision?.Action == RoverAction.Avanzar ? m.LastDecision.Speed : 0);
        P(Risk, m.FrontProbs[1] + m.FrontProbs[2]);
        QueueRedraw();
    }

    public void Clear()
    {
        Battery.Clear(); TempInt.Clear(); Wind.Clear(); Speed.Clear(); Risk.Clear();
    }

    public override void _Draw()
    {
        var m = Mission;
        if (m == null) return;
        float w = Size.X, h = (Size.Y - 4) / 5;
        void S(int i, string name, List<float> v, float min, float max, Color c, string val)
        {
            var r = new Rect2(0, i * h + 12, w, h - 14);
            Gfx.Frame(this, r);
            Gfx.Text(this, new Vector2(2, i * h), name, 9, UiTheme.Dim);
            Gfx.Text(this, new Vector2(0, i * h), val, 9, c, w - 2, HorizontalAlignment.Right);
            Gfx.Series(this, r, v, min, max, c);
        }
        S(0, "Batería %", Battery, 0, 100, UiTheme.Ok, $"{m.Rover.BatteryPct:0.0}");
        S(1, "T interior °C", TempInt, m.Planet.HardwareMinC, m.Planet.HardwareMaxC, UiTheme.Fuzzy, $"{m.Rover.TempInt:0.0}");
        float wmax = MathF.Max(5, m.Planet.WindLimit * 1.2f);
        S(2, "Ráfaga m/s", Wind, 0, wmax, UiTheme.Info, $"{m.Env.WindGust:0.0}");
        S(3, "Velocidad (0-1)", Speed, 0, 1, UiTheme.Rna, $"{(Speed.Count > 0 ? Speed[^1] : 0):0.00}");
        S(4, "Riesgo RNA", Risk, 0, 1, UiTheme.Crit, $"{(Risk.Count > 0 ? Risk[^1] : 0):0.00}");
    }
}
