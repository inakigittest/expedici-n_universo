using System;
using System.IO;
using System.Linq;
using AresX.AI;
using AresX.Sim;
using Godot;

namespace AresX.Core;

/// <summary>
/// Modos sin ventana (después de "--"):
///   --selftest                 entrena la RNA, prueba los casos borde y una misión por planeta; código de salida 0 = OK
///   --benchmark[=planeta]      20 semillas × 6 políticas; escribe exports/benchmark_&lt;planeta&gt;.csv
///   --seeds=N --battery=P      opciones del benchmark
/// </summary>
public static class HeadlessRunner
{
    public static string ExportDir()
    {
        string dir = !OS.HasFeature("template") ? ProjectSettings.GlobalizePath("res://exports")
            : OS.HasFeature("mobile") ? ProjectSettings.GlobalizePath("user://exports")
            : Path.Combine(OS.GetSystemDir(OS.SystemDir.Documents), "Expedicion Universo");   // versión instalada en el PC
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        string? bench = args.FirstOrDefault(a => a.StartsWith("--benchmark"));
        if (args.Contains("--selftest"))
        {
            exitCode = SelfTest();
            return true;
        }
        if (args.Contains("--export-rna"))
        {
            var rna = NeuralPerception.Train(2026, s => GD.Print($"[RNA] {s}"));
            string path = ProjectSettings.GlobalizePath(Platform.ModelPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, rna.ToJson());
            var back = NeuralPerception.FromJson(File.ReadAllText(path));
            bool same = rna.TestX.Take(500).All(x => rna.Predict(x).SequenceEqual(back.Predict(x)));
            GD.Print($"[RNA] Modelo guardado en {path} ({new FileInfo(path).Length / 1024} KB) · ida y vuelta idéntica: {same}");
            exitCode = same ? 0 : 1;
            return true;
        }
        string? diag = args.FirstOrDefault(a => a.StartsWith("--diag="));
        if (diag != null)
        {
            var rna = NeuralPerception.TrainShared();
            string planet = diag[7..];
            var p = PlanetProfile.Get(planet);
            int seed = args.Where(a => a.StartsWith("--seed=")).Select(a => int.Parse(a[7..])).DefaultIfEmpty(42).First();
            var m = new Mission(new MissionConfig { Mode = MissionMode.Autonomo, PlanetId = planet, Probe = !p.SolidSurface, Seed = seed, MaxSteps = 400 }, rna);
            while (!m.Finished) m.Step();
            GD.Print($"[Diag] {p.Name}: {Mission.StateName(m.State)} · {m.EndReason}");
            foreach (var g in m.Trace.GroupBy(r => r.Code + " " + r.Action).OrderByDescending(g => g.Count()))
                GD.Print($"[Diag] {g.Count(),4} × {g.Key} · ej: {g.First().Reason}");
            foreach (var l in m.Log.TakeLast(25)) GD.Print($"[Diag] T+{l.Step:0000} [{l.Kind}] {l.Text}");
            return true;
        }
        if (bench != null)
        {
            string planet = bench.Contains('=') ? bench.Split('=')[1] : "marte";
            int seeds = args.Where(a => a.StartsWith("--seeds=")).Select(a => int.Parse(a[8..])).DefaultIfEmpty(20).First();
            float bat = args.Where(a => a.StartsWith("--battery=")).Select(a => float.Parse(a[10..], System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(80).First();
            var rna = NeuralPerception.TrainShared(s => GD.Print($"[RNA] {s}"));
            var p = PlanetProfile.Get(planet);
            var cfg = new MissionConfig { Mode = MissionMode.Autonomo, PlanetId = planet, Probe = !p.SolidSurface, BatteryPct = bat };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var stats = Benchmark.Run(cfg, seeds, rna, s => GD.Print($"[Bench] {s}"));
            string path = Path.Combine(ExportDir(), $"benchmark_{planet}.csv");
            File.WriteAllText(path, Benchmark.Csv(stats));
            GD.Print($"[Bench] {p.Name} · {seeds} semillas · {sw.Elapsed.TotalSeconds:0.0} s\n" + Benchmark.Table(stats));
            GD.Print($"[Bench] CSV: {path}");
            return true;
        }
        return false;
    }

    private static int SelfTest()
    {
        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            GD.Print($"[SelfTest] {(ok ? "OK   " : "FALLO")} {name}{(detail == "" ? "" : " · " + detail)}");
            if (!ok) fails++;
        }
        var rna = NeuralPerception.TrainShared(s => GD.Print($"[RNA] {s}"));
        var t = rna.Report.Test!;
        Check("RNA entrenada y mejor que la línea base", t.Accuracy > t.Baseline + 0.2f, $"acc {t.Accuracy:P1} vs base {t.Baseline:P1}, macro-F1 {t.MacroF1:0.000}, recall barranco {t.Recall[2]:P1}, ECE {t.Ece:0.000}");

        var rt = NeuralPerception.FromJson(rna.ToJson());
        Check("RNA serializada (APK) predice idéntico", rna.TestX.Take(300).All(x => rna.Predict(x).SequenceEqual(rt.Predict(x))) && Math.Abs(rt.Report.Test!.Accuracy - t.Accuracy) < 1e-6f,
            $"acc recalculada {rt.Report.Test!.Accuracy:P1}");
        if (Godot.FileAccess.FileExists(Platform.ModelPath))
        {
            var shipped = NeuralPerception.FromJson(Godot.FileAccess.GetFileAsString(Platform.ModelPath));
            Check("Modelo incluido en el juego = modelo entrenado", rna.TestX.Take(300).All(x => rna.Predict(x).SequenceEqual(shipped.Predict(x))), Platform.ModelPath);
        }

        // difuso: CA-05 (10 m → 1 m reduce la velocidad) y reglas múltiples activas
        var fz = RoverFuzzy.Build();
        var baseIn = new System.Collections.Generic.Dictionary<string, float> { ["distancia"] = 10, ["bateria"] = 70, ["pendiente"] = 5, ["temperatura"] = 20, ["viento"] = 10, ["riesgo"] = 0.55f };
        var r10 = fz.Infer(baseIn);
        baseIn["distancia"] = 1;
        var r1 = fz.Infer(baseIn);
        Check("Difuso CA-05: 10 m → 1 m reduce velocidad", r1.Crisp["velocidad"] < r10.Crisp["velocidad"] - 0.05f, $"{r10.Crisp["velocidad"]:0.00} → {r1.Crisp["velocidad"]:0.00}; reglas activas {r1.Fired.Count}");
        Check("Difuso: ≥ 8 reglas y ≥ 3 conjuntos por variable", fz.Rules.Count >= 8 && fz.Inputs.Values.All(v => v.Sets.Count >= 3), $"{fz.Rules.Count} reglas");

        // validación de configuración (CA-07, NaN)
        Check("CA-07 Júpiter + ruedas bloqueado", new MissionConfig { Mode = MissionMode.Autonomo, PlanetId = "jupiter", Probe = false }.Validate() != null);
        Check("Slider NaN rechazado", new MissionConfig { BatteryPct = float.NaN }.Validate() != null);

        // determinismo
        string RunTrace(int seed)
        {
            var m = new Mission(new MissionConfig { Mode = MissionMode.Autonomo, PlanetId = "marte", Seed = seed, MaxSteps = 60 }, rna);
            for (int i = 0; i < 60 && !m.Finished; i++) m.Step();
            return string.Join(";", m.Trace.Select(r => $"{r.Row},{r.Col},{r.Code},{r.BatteryWh:0.000}"));
        }
        Check("Misma semilla → traza equivalente", RunTrace(7) == RunTrace(7));

        // batería 0 nunca negativa y sin movimiento
        var m0 = new Mission(new MissionConfig { Mode = MissionMode.Autonomo, PlanetId = "marte", Seed = 3, BatteryPct = 1 }, rna);
        for (int i = 0; i < 80 && !m0.Finished; i++) m0.Step();
        Check("Batería 1 %: nunca negativa", m0.Rover.BatteryWh >= 0, $"estado {Mission.StateName(m0.State)} · {m0.EndReason}");

        // todos los sensores de navegación fallan → modo seguro, sin avanzar
        var ms = new Mission(new MissionConfig { Mode = MissionMode.Autonomo, PlanetId = "marte", Seed = 5 }, rna);
        foreach (var id in SensorSuite.NavSensors) ms.Sensors.InjectFault(id, FaultMode.NaN);
        var before = ms.Rover.Pos;
        ms.Step();
        Check("Sensores de navegación caídos → modo seguro", ms.LastDecision!.Action == RoverAction.ModoSeguro && ms.Rover.Pos == before, ms.LastDecision.Explanation);

        // barranco justo delante → no entra
        var mc = new Mission(new MissionConfig { Mode = MissionMode.Autonomo, PlanetId = "marte", Seed = 9 }, rna);
        mc.Step();
        if (mc.Route.Count > 2)
        {
            var next = mc.Route[mc.RouteIdx + 1];
            mc.EditMap(next, "cliff");
            mc.Step();
            Check("CA-02 barranco delante: se detiene antes", mc.Rover.Pos != next && mc.Map.ClassAt(mc.Rover.Pos) == TerrainClass.Libre, mc.LastDecision!.Explanation);
        }

        // destino por clic: evalúa el destino, esquiva un barranco imprevisto puesto en la ruta y llega
        var md = new Mission(new MissionConfig { PlanetId = "marte", Seed = 21 }, rna);
        var startPos = md.Map.Start;
        for (int i = 0; i < 4; i++) md.Step();
        bool idleOk = md.Rover.Pos == startPos && !md.Finished;
        var reach = md.Map.BfsDistance(startPos, md.Map.Passable);
        var goal = Enumerable.Range(0, md.Map.N * md.Map.N).Select(i => new Cell(i % md.Map.N, i / md.Map.N))
            .Where(c => reach[md.Map.Idx(c)] >= 9 && reach[md.Map.Idx(c)] <= 13).OrderBy(c => c.X * 7 + c.Y).First();
        md.SetDestination(goal);
        bool evaluated = md.DestReport != null;
        md.Step();
        if (md.Route.Count > md.RouteIdx + 4) md.EditMap(md.Route[md.RouteIdx + 3], "cliff");
        for (int i = 0; i < 300 && (md.HasDestination || md.PendingSample) && !md.Finished; i++) md.Step();
        Check("Sin destino el rover espera (no se mueve)", idleOk, md.Trace.First().Explanation);
        Check("Destino por clic: evalúa, esquiva el barranco imprevisto y llega", evaluated && md.Rover.Pos == goal && md.Falls == 0,
            $"{md.DestReport?.VerdictText} · {md.Rover.Pos} vs {goal} · vetos {md.Vetoes} · replan {md.Replans} · pasos {md.StepCount}");

        // una misión autónoma por planeta, sin excepciones
        foreach (var p in PlanetProfile.All)
        {
            var m = new Mission(new MissionConfig { Mode = MissionMode.Autonomo, PlanetId = p.Id, Probe = !p.SolidSurface, Seed = 42, MaxSteps = 400 }, rna);
            try
            {
                while (!m.Finished) m.Step();
                var met = m.Metrics();
                Check($"Misión {p.Name}", true, $"{Mission.StateName(m.State)} ({m.EndReason}) · pasos {m.StepCount} · ciencia {met["ciencia"]} · energía {met["energia_restante_pct"]}% · colisiones {met["colisiones"]} · caídas {met["caidas"]} · replan {met["replanificaciones"]}");
            }
            catch (Exception e)
            {
                Check($"Misión {p.Name}", false, e.ToString());
            }
        }
        // exportación
        var me = new Mission(new MissionConfig { Mode = MissionMode.Autonomo, PlanetId = "tierra", Seed = 11, MaxSteps = 30 }, rna);
        while (!me.Finished) me.Step();
        string dir = ExportDir();
        File.WriteAllText(Path.Combine(dir, "selftest_traza.csv"), me.TraceCsv());
        File.WriteAllText(Path.Combine(dir, "selftest_resultado.json"), me.ResultJson());
        Check("Exportación CSV/JSON", File.Exists(Path.Combine(dir, "selftest_traza.csv")), dir);
        GD.Print($"[SelfTest] {(fails == 0 ? "TODO OK" : $"{fails} FALLOS")}");
        return fails == 0 ? 0 : 1;
    }
}
