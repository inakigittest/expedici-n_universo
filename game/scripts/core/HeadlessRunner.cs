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
        if (args.Contains("--radio-test"))
        {
            // directorio de radios chilenas + sintonía real de las primeras N (decodifica ~4 s de cada una)
            int n = args.FirstOrDefault(a => a.StartsWith("--radio-n="))?[10..].ToInt() ?? 6;
            var list = RadioPlayer.TestDirectory(out string err);
            GD.Print($"[Radio] directorio: {list.Count} radios chilenas en MP3 {err}");
            int ok = 0;
            string? only = args.FirstOrDefault(a => a.StartsWith("--radio-name="))?[13..].ToLowerInvariant();
            foreach (var st in (only != null ? list.Where(x => x.Name.ToLowerInvariant().Contains(only)) : list).Take(n))
            {
                string res = RadioPlayer.TestTune(st.Url, 4f);
                if (res.StartsWith("OK")) ok++;
                GD.Print($"[Radio] {st.Name,-28} {res}");
            }
            GD.Print($"[Radio] sintonizadas {ok}/{Math.Min(n, list.Count)}");
            exitCode = ok > 0 ? 0 : 1;
            return true;
        }
        if (args.FirstOrDefault(a => a.StartsWith("--import-pdf=")) is string ip)
        {
            // importa uno o varios PDF (separados por ;) y compara con el escenario incluido del mismo informe, si existe
            foreach (var path in ip[13..].Split(';'))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = GeoClickPdf.Import(path);
                GD.Print($"[Importar] {System.IO.Path.GetFileName(path)} · {sw.ElapsedMilliseconds} ms · {r.Summary}");
                if (!r.Ok) { exitCode = 1; continue; }
                var mine = GeoReport.FromJson(Godot.FileAccess.GetFileAsString(r.JsonPath));
                string refPath = Scenarios.Dir + r.Id + ".json";
                if (!Godot.FileAccess.FileExists(refPath)) continue;
                var py = GeoReport.FromJson(Godot.FileAccess.GetFileAsString(refPath));
                float de = 0;
                int cmpN = Math.Min(mine.N, py.N);
                for (int y = 0; y < cmpN; y++) for (int x = 0; x < cmpN; x++) de += Math.Abs(mine.Elev![y * mine.N + x] - py.Elev![y * py.N + x]);
                int coverSame = 0, coverTot = 0;
                for (int i = 0; i < Math.Min(mine.CoverGrid!.Length, py.CoverGrid!.Length); i++) { if (py.CoverGrid[i] != 255 || mine.CoverGrid[i] != 255) { coverTot++; if (py.CoverGrid[i] == mine.CoverGrid[i]) coverSame++; } }
                GD.Print($"[Importar]   vs conversor Python: N {mine.N}/{py.N} · |Δelev| medio {de / (cmpN * cmpN):0.0} m · cobertura igual {coverSame * 100f / Math.Max(1, coverTot):0.0} % · " +
                         $"lluvia mensual {(mine.MonthlyRainMm.SequenceEqual(py.MonthlyRainMm) ? "igual" : "DISTINTA")} · T mensual {(mine.MonthlyTempC.Zip(py.MonthlyTempC).All(t => Math.Abs(t.First - t.Second) < 0.15f) ? "igual" : "DISTINTA")} · " +
                         $"retorno {(mine.ReturnPeriods.SequenceEqual(py.ReturnPeriods) ? "igual" : "DISTINTO")} · pendientes {(mine.SlopeBins.Zip(py.SlopeBins).All(t => Math.Abs(t.First - t.Second) < 0.001f) ? "igual" : "DISTINTAS")} · " +
                         $"cobertura km² {(mine.Cover.Count == py.Cover.Count && mine.Cover.All(kv => py.Cover.TryGetValue(kv.Key, out var v) && Math.Abs(v - kv.Value) < 0.01f) ? "igual" : "DISTINTA")} · lat {mine.Lat:0.0000}/{py.Lat:0.0000} · elev {mine.ElevMin}-{mine.ElevMax}");
            }
            return true;
        }
        string? diag = args.FirstOrDefault(a => a.StartsWith("--diag="));
        if (diag != null)
        {
            var rna = NeuralPerception.TrainShared();
            string planet = diag[7..];
            var p = PlanetProfile.Get(planet);
            int seed = args.Where(a => a.StartsWith("--seed=")).Select(a => int.Parse(a[7..])).DefaultIfEmpty(42).First();
            string? scId = args.Where(a => a.StartsWith("--scenario=")).Select(a => a[11..]).FirstOrDefault();
            var swGen = System.Diagnostics.Stopwatch.StartNew();
            var sc = scId != null ? Scenarios.Load(scId) : null;
            double loadMs = swGen.Elapsed.TotalMilliseconds;
            var dcfg = new MissionConfig
            {
                Mode = args.Contains("--rescate") ? MissionMode.Rescate : args.Contains("--incendio") ? MissionMode.Incendio : MissionMode.Autonomo,
                PlanetId = sc != null ? "tierra" : planet, Probe = sc == null && !p.SolidSurface, Seed = seed,
                MaxSteps = args.Contains("--rescate") || args.Contains("--incendio") ? 900 : 400, Scenario = sc, Heli = args.Contains("--heli"),
                Helis = args.FirstOrDefault(a => a.StartsWith("--helis="))?[8..].ToInt() ?? 1, Trucks = args.FirstOrDefault(a => a.StartsWith("--trucks="))?[9..].ToInt() ?? 0,
            };
            if (args.FirstOrDefault(a => a.StartsWith("--start=")) is string st0) { var xy = st0[8..].Split(','); dcfg.StartCell = new Cell(xy[0].ToInt(), xy[1].ToInt()); }
            if (args.FirstOrDefault(a => a.StartsWith("--points=")) is string pt0)
                foreach (var pp in pt0[9..].Split(';', StringSplitOptions.RemoveEmptyEntries)) { var xy = pp.Split(','); dcfg.Points.Add(new Cell(xy[0].ToInt(), xy[1].ToInt())); }
            var m = new Mission(dcfg, rna);
            if (dcfg.StartCell != null || dcfg.Points.Count > 0)
                GD.Print($"[Diag] plan del operador: base {m.Map.Start} · " + (m.Rescue ? string.Join(", ", m.Victims.Select(v => $"{v.Name} {v.Cell} {Victim.KindName(v.Kind)}")) : $"{m.Burning.Count} celdas en llamas en {m.Fires.Count} focos"));
            double genMs = swGen.Elapsed.TotalMilliseconds - loadMs;
            var stepMs = new System.Collections.Generic.List<double>();
            while (!m.Finished)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                m.Step();
                stepMs.Add(sw.Elapsed.TotalMilliseconds);
                if (m.FireMode && args.Contains("--fire-log") && m.StepCount % 100 == 0)
                    GD.Print($"[Fuego] paso {m.StepCount} · t {m.Env.TimeS / 60:0} min · en llamas {m.Burning.Count} celdas · quemadas {m.BurnedHa:0.0} ha · focos {m.Fires.Count} · descargas H-1 {m.FireDrops} · " +
                             string.Join(" · ", m.Fleet.Select(u => $"{u.Name} {u.Drops} ({u.Status})")) + $" · H-1 {m.Rover.Pos}→{m.Target} {m.TargetName} · {m.LastDecision?.Code} {m.LastDecision?.Action} · ruta {m.Route.Count} idx {m.RouteIdx} · pend {m.PendingSample}");
            }
            if (sc != null)
                GD.Print($"[Diag] Escenario {sc.Consulta}: {m.Map.N}×{m.Map.N} celdas de {m.Map.CellM} m · carga {loadMs:0} ms · generación {genMs:0} ms · paso medio {stepMs.Average():0.0} ms · p95 {stepMs.OrderBy(x => x).ElementAt((int)(stepMs.Count * 0.95)):0.0} ms · máx {stepMs.Max():0} ms · {stepMs.Count} pasos · {m.Map.Calibration}");
            if (sc != null)
            {
                var dd = m.Map.BfsDistance(m.Map.Start, m.Map.Passable);
                int inside = m.Map.Inside!.Count(x => x), reach = dd.Count(x => x >= 0);
                if (m.Map.City is { } city)
                    GD.Print($"[Diag] ciudad simulada: {city.UrbanKm2:0.0} km² urbanos · {city.Buildings.Count} edificios · {city.Hospitals.Count} hospitales · " +
                             string.Join(", ", city.Buildings.GroupBy(b => b.Type).OrderByDescending(g => g.Count()).Select(g => $"{CityPlan.Name(g.Key)} {g.Count()}")) +
                             $" · celdas edificio {m.Map.Hazards.Count(h => h == Hazard.Building)} · calles {city.Street.Count(x => x)}");
                GD.Print($"[Diag] celdas en polígono {inside} · alcanzables desde la base {reach} ({(float)reach / inside:P0}) · sitios {m.Map.Sites.Count} · refugios {m.Map.Refuges.Count} · base {m.Map.Start} · pendientes calc {string.Join(" ", m.Map.SlopeHistogram.Select(b => b.ToString("P0")))} vs informe {string.Join(" ", sc.SlopeBins.Select(b => b.ToString("P0")))}");
            }
            GD.Print($"[Diag] {m.WorldName}: {Mission.StateName(m.State)} · {m.EndReason}");
            foreach (var g in m.Trace.GroupBy(r => r.Code + " " + r.Action).OrderByDescending(g => g.Count()))
                GD.Print($"[Diag] {g.Count(),4} × {g.Key} · ej: {g.First().Reason}");
            foreach (var l in m.Log.TakeLast(25)) GD.Print($"[Diag] T+{l.Step:0000} [{l.Kind}] {l.Text}");
            if (args.Contains("--trail")) GD.Print($"[DiagTrail] ruta {m.Route.Count} idx {m.RouteIdx} objetivo {m.Target} · últimos: {string.Join(" ", m.Trail.TakeLast(30))}");
            if (args.Contains("--full-log")) foreach (var l in m.Log.Take(160)) GD.Print($"[DiagLog] T+{l.Step:0000} [{l.Kind}] {l.Text[..Math.Min(170, l.Text.Length)]}");
            if (args.Contains("--ag-log")) foreach (var l in m.Log.Where(l => l.Kind == "ag")) GD.Print($"[DiagAG] T+{l.Step:0000} {l.Text[..Math.Min(140, l.Text.Length)]}");
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

        // dron explorador: despega, escanea, actualiza las creencias y vuelve; no vuela sin atmósfera
        var mdr = new Mission(new MissionConfig { Mode = MissionMode.Autonomo, PlanetId = "marte", Seed = 33 }, rna);
        mdr.Drone.Auto = false;
        mdr.Step();
        int obsBefore = Enumerable.Range(0, mdr.Map.N * mdr.Map.N).Count(i => mdr.Belief.Observed[i]);
        bool launched = mdr.DeployDrone(mdr.Map.Clamp(mdr.Rover.Pos + new Cell(8, -8)), "operador");
        for (int i = 0; i < 80 && mdr.Drone.Flying && !mdr.Finished; i++) mdr.Step();
        int obsAfter = Enumerable.Range(0, mdr.Map.N * mdr.Map.N).Count(i => mdr.Belief.Observed[i]);
        Check("Dron: despega, escanea y vuelve a acoplarse", launched && !mdr.Drone.Flying && mdr.Drone.LastScan != null && obsAfter > obsBefore + 30,
            $"{mdr.Drone.LastScan?.Summary} · celdas observadas {obsBefore}→{obsAfter} · batería {mdr.Drone.BatteryPct:0} %");
        var mme = new Mission(new MissionConfig { Mode = MissionMode.Autonomo, PlanetId = "mercurio", Seed = 1 }, rna);
        Check("Dron no disponible sin atmósfera (Mercurio)", !mme.DroneCanDeploy(out var whyNot) && whyNot.Contains("atmósfera"), whyNot);

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

        // escenarios GeoClick a escala real: área del polígono = área del informe, base conectada y sitios alcanzables
        int scOk = 0;
        var scDetail = new System.Collections.Generic.List<string>();
        foreach (var meta in Scenarios.All)
        {
            var g = Scenarios.Load(meta.Id)!;
            var map = new WorldMap(PlanetProfile.Get("tierra"), g, 7, 1);
            float areaKm2 = map.Inside!.Count(x => x) * map.CellM * map.CellM / 1e6f;
            var dist = map.BfsDistance(map.Start, map.Passable);
            int reachableSites = map.Sites.Count(s => dist[map.Idx(s.Cell)] >= 0);
            bool ok = MathF.Abs(areaKm2 - g.AreaKm2) / g.AreaKm2 < 0.03f && reachableSites >= 4 && map.Refuges.Count == 2 && map.Refuges.All(r => dist[map.Idx(r)] >= 0);
            if (ok) scOk++;
            scDetail.Add($"{g.Id} {areaKm2:0.0}/{g.AreaKm2:0.0} km² {reachableSites} sitios");
        }
        Check($"Escenarios GeoClick a escala ({Scenarios.All.Count})", Scenarios.All.Count > 0 && scOk == Scenarios.All.Count, string.Join(" · ", scDetail));
        var sm = new Mission(new MissionConfig { Mode = MissionMode.Autonomo, PlanetId = "tierra", Seed = 7, MaxSteps = 120, Scenario = Scenarios.Load("40e9efb1") }, rna);
        while (!sm.Finished) sm.Step();
        // importador de PDF (solo si el informe de referencia está en el equipo: no se versiona)
        string refPdf = ProjectSettings.GlobalizePath("res://").TrimEnd('/') + "/../referencia/geoclick_40e9efb1.pdf";
        if (File.Exists(refPdf))
        {
            bool had = Godot.FileAccess.FileExists(GeoClickPdf.UserDir + "40e9efb1.json");
            var imp = GeoClickPdf.Import(refPdf);
            var g = imp.Ok ? GeoReport.FromJson(Godot.FileAccess.GetFileAsString(imp.JsonPath)) : null;
            var imap = g != null ? new WorldMap(PlanetProfile.Get("tierra"), g, 7, 1) : null;
            Check("Importar informe PDF dentro del juego", imp.Ok && Math.Abs(imp.AreaRebuiltKm2 / imp.AreaReportKm2 - 1) < 0.03f && imap!.Sites.Count >= 4 && g!.MonthlyRainMm[5] > 100,
                imp.Summary);
            if (!had)
            {
                DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(GeoClickPdf.UserDir + "40e9efb1.json"));
                DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(GeoClickPdf.UserDir + "40e9efb1_sat.jpg"));
            }
        }
        else GD.Print("[SelfTest] (omitido) importar PDF: referencia/geoclick_40e9efb1.pdf no está en este equipo");

        // helicóptero: solo Tierra, potencia según densidad del aire, misión completa en un escenario a escala
        Check("Helicóptero bloqueado fuera de la Tierra", new MissionConfig { PlanetId = "marte", Heli = true }.Validate() != null);
        float mSea = HeliModel.Margin(HeliModel.Density(1.01325f, 15f)), mHigh = HeliModel.Margin(HeliModel.Density(1.01325f * MathF.Exp(-2.6f / 8.5f), 25f));
        Check("Helicóptero: el margen de potencia cae con la altura y el calor", mHigh < mSea - 0.15f && HeliModel.HoverW(1.225f) is > 20000 and < 35000,
            $"nivel del mar 15 °C {mSea:0.00} · 2600 m 25 °C {mHigh:0.00} · estacionario {HeliModel.HoverW(1.225f) / 1000:0.0} kW · crucero 25 m/s {HeliModel.ForwardW(1.225f, 25f) / 1000:0.0} kW");
        var hm = new Mission(new MissionConfig { Mode = MissionMode.Autonomo, PlanetId = "tierra", Seed = 7, MaxSteps = 400, Heli = true, Scenario = Scenarios.Load("40e9efb1") }, rna);
        while (!hm.Finished) hm.Step();
        Check("Helicóptero: misión en escenario 40e9efb1", hm.State == MissionState.Exito && hm.Samples > 0 && !hm.Drone.Available,
            $"{Mission.StateName(hm.State)} · muestras {hm.Samples} · {hm.Metrics()["distancia_m"]} m · batería {hm.Rover.BatteryPct:0} %");
        Check("Rescate solo en helicóptero", new MissionConfig { PlanetId = "tierra", Mode = MissionMode.Rescate }.Validate() != null);
        var rm = new Mission(new MissionConfig { Mode = MissionMode.Rescate, PlanetId = "tierra", Seed = 7, MaxSteps = 900, Heli = true, Scenario = Scenarios.Load("40e9efb1") }, rna);
        while (!rm.Finished) rm.Step();
        Check("Rescate en helicóptero (40e9efb1): AG de rescate, urgencia difusa y hospital", rm.State == MissionState.Exito && rm.Saved >= rm.Victims.Count / 2 && rm.Medical.Count >= 1,
            $"{rm.EndReason} · puntos médicos {rm.Medical.Count} · pasos {rm.StepCount}");
        // flota: rescate con 2 helicópteros + 1 carro (el AG reparte personas entre vehículos capaces)
        var fm = new Mission(new MissionConfig { Mode = MissionMode.Rescate, PlanetId = "tierra", Seed = 3, MaxSteps = 900, Heli = true, Helis = 2, Trucks = 1, Scenario = Scenarios.Load("40e9efb1") }, rna);
        while (!fm.Finished) fm.Step();
        Check("Flota de rescate (2 helicópteros + 1 carro de bomberos)", fm.State == MissionState.Exito && fm.Saved >= rm.Saved && fm.Fleet.Sum(u => u.Delivered) > 0 && fm.Comms.Count > 0,
            $"{fm.EndReason}");
        Check("Carros de bomberos solo con zona urbana", new Mission(new MissionConfig { Mode = MissionMode.Rescate, PlanetId = "tierra", Seed = 3, Heli = true, Trucks = 2 }, rna).Fleet.Count(u => !u.Heli) == 0,
            "terreno procedural sin ciudad: no se despliegan");
        // incendio: propagación, prioridad difusa de focos y descargas de la flota (200 pasos)
        var im = new Mission(new MissionConfig { Mode = MissionMode.Incendio, PlanetId = "tierra", Seed = 3, Heli = true, Helis = 2, Trucks = 1, Scenario = Scenarios.Load("40e9efb1") }, rna);
        int burn0 = im.Burning.Count;
        for (int k = 0; k < 300 && !im.Finished; k++) im.Step();
        Check("Incendio: focos, prioridad difusa y descargas de la flota", burn0 > 0 && im.Fires.Count > 0 && im.FireDrops + im.Fleet.Sum(u => u.Drops) > 0 && im.Fires.All(f => f.Priority is >= 0 and <= 1),
            $"celdas en llamas al inicio {burn0} · tras 300 pasos {im.Burning.Count} · descargas {im.FireDrops + im.Fleet.Sum(u => u.Drops)} · {im.FireNote}");
        Check("Incendio solo con helicóptero en la Tierra", new MissionConfig { PlanetId = "tierra", Mode = MissionMode.Incendio }.Validate() != null);
        Check("Misión en escenario 40e9efb1 (30 m/celda)", sm.Samples > 0 && sm.Rover.Collisions == 0, $"{Mission.StateName(sm.State)} · muestras {sm.Samples} · {sm.Metrics()["distancia_m"]} m · {sm.EndReason}");
        GD.Print($"[SelfTest] {(fails == 0 ? "TODO OK" : $"{fails} FALLOS")}");
        return fails == 0 ? 0 : 1;
    }
}
