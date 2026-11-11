using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AresX.AI;

namespace AresX.Sim;

public sealed class PolicyStats
{
    public string Policy = "";
    public readonly List<Dictionary<string, object>> Runs = new();

    public IEnumerable<float> Col(string key) => Runs.Select(r => Convert.ToSingle(r[key] is bool b ? (b ? 1 : 0) : r[key], CultureInfo.InvariantCulture));
    public float Mean(string k) => Runs.Count == 0 ? 0 : Col(k).Average();
    public float Std(string k)
    {
        if (Runs.Count < 2) return 0;
        float m = Mean(k);
        return MathF.Sqrt(Col(k).Sum(v => (v - m) * (v - m)) / (Runs.Count - 1));
    }
    public float Median(string k)
    {
        var v = Col(k).OrderBy(x => x).ToArray();
        if (v.Length == 0) return 0;
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }
}

/// <summary>
/// Experimento controlado: las mismas semillas y la misma configuración para cada política (sistema híbrido, tres
/// ablaciones, AG puro y control de menor distancia). Ejecuta misiones autónomas completas sin ventana.
/// </summary>
public static class Benchmark
{
    public static readonly (string Name, bool Rna, bool Fuzzy, bool Ga, bool Drone)[] Policies =
    {
        ("Híbrido completo", true, true, true, true),
        ("Híbrido sin dron", true, true, true, false),
        ("Sin RNA", false, true, true, true),
        ("Sin difuso", true, false, true, true),
        ("Sin AG", true, true, false, true),
        ("AG puro", false, false, true, false),
        ("Control (menor distancia)", false, false, false, false),
    };

    public static readonly string[] Keys =
    {
        "exito", "colisiones", "caidas", "barrancos_evitados", "energia_restante_pct", "distancia_m", "ciencia", "replanificaciones",
        "violaciones_seguridad", "pasos", "ms_decision_medio", "ms_ag_total", "vetos", "despliegues_dron", "objetivos_dron",
    };

    public static List<PolicyStats> Run(MissionConfig baseCfg, int seeds, NeuralPerception rna, Action<string>? progress = null,
        Func<bool>? cancel = null, int maxSteps = 500)
    {
        var all = new List<PolicyStats>();
        foreach (var (name, r, f, g, dr) in Policies)
        {
            var ps = new PolicyStats { Policy = name };
            for (int s = 0; s < seeds; s++)
            {
                if (cancel?.Invoke() == true) return all;
                var cfg = new MissionConfig
                {
                    PlanetId = baseCfg.PlanetId, Probe = baseCfg.Probe, Difficulty = baseCfg.Difficulty, Seed = 1000 + s,
                    BatteryPct = baseCfg.BatteryPct, ScienceWeight = baseCfg.ScienceWeight, Month = baseCfg.Month,
                    UseRna = r, UseFuzzy = f, UseGa = g, UseDrone = dr, Mode = MissionMode.Autonomo, MaxSteps = maxSteps, Ga = baseCfg.Ga,
                };
                var m = new Mission(cfg, rna);
                while (!m.Finished) m.Step();
                ps.Runs.Add(m.Metrics());
                progress?.Invoke($"{name}: semilla {s + 1}/{seeds} → {(m.State == MissionState.Exito ? "éxito" : "fracaso: " + m.EndReason)}");
            }
            all.Add(ps);
        }
        return all;
    }

    public static string Csv(List<PolicyStats> stats)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("politica,semilla," + string.Join(",", Keys));
        foreach (var ps in stats)
            for (int i = 0; i < ps.Runs.Count; i++)
                sb.AppendLine($"\"{ps.Policy}\",{1000 + i}," + string.Join(",", Keys.Select(k => Convert.ToSingle(ps.Runs[i][k] is bool b ? (b ? 1 : 0) : ps.Runs[i][k], ci).ToString("0.###", ci))));
        sb.AppendLine();
        sb.AppendLine("politica,metrica,media,mediana,desv_std");
        foreach (var ps in stats)
            foreach (var k in Keys)
                sb.AppendLine($"\"{ps.Policy}\",{k},{ps.Mean(k).ToString("0.###", ci)},{ps.Median(k).ToString("0.###", ci)},{ps.Std(k).ToString("0.###", ci)}");
        return sb.ToString();
    }

    public static string Table(List<PolicyStats> stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{"Política",-26} {"Éxito",7} {"Colis.",7} {"Caídas",7} {"Barr.ev",8} {"Energía%",9} {"Dist m",8} {"Ciencia",8} {"Replan",7} {"Viol.",6}");
        foreach (var ps in stats)
            sb.AppendLine($"{ps.Policy,-26} {ps.Mean("exito"),7:P0} {ps.Mean("colisiones"),7:0.00} {ps.Mean("caidas"),7:0.00} {ps.Mean("barrancos_evitados"),8:0.0} " +
                          $"{ps.Mean("energia_restante_pct"),6:0.0}±{ps.Std("energia_restante_pct"),-4:0} {ps.Mean("distancia_m"),8:0} {ps.Mean("ciencia"),8:0.00} {ps.Mean("replanificaciones"),7:0.0} {ps.Mean("violaciones_seguridad"),6:0.00}");
        return sb.ToString();
    }
}
