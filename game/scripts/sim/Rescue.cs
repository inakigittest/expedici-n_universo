using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AresX.AI;
using AresX.Core;

namespace AresX.Sim;

public enum VictimKind { Montana, Aluvion, Inundacion, Urbano }

/// <summary>Persona atrapada (misión de rescate). La supervivencia baja con la exposición; a bordo, cuatro veces más lento.</summary>
public sealed class Victim
{
    public int Id;
    public string Name = "";
    public Cell Cell;
    public VictimKind Kind;
    public float SurvivalS, SurvivalMaxS, Urgency;
    public bool OnBoard, Delivered, Lost, Unreachable;
    public int Carrier = -1;           // vehículo que la lleva (0 = H-1, n = unidad de apoyo)
    public bool Active => !Delivered && !Lost && !Unreachable;
    public static string KindName(VictimKind k) => k switch
    {
        VictimKind.Montana => "alta montaña", VictimKind.Aluvion => "aluvión", VictimKind.Inundacion => "inundación", _ => "zona urbana",
    };
}

public sealed class MedicalPoint
{
    public string Name = "";
    public Cell Cell;
}

/// <summary>Urgencia difusa de una víctima: supervivencia restante, exposición y dificultad de acceso → urgencia.</summary>
public static class RescueFuzzy
{
    public static FuzzySystem Build()
    {
        var fs = new FuzzySystem();
        fs.Inputs["supervivencia"] = new FuzzyVar { Id = "supervivencia", Name = "Supervivencia restante", Unit = "%", Min = 0, Max = 100 }
            .Set("critica", new Trap(0, 0, 15, 35)).Set("media", new Tri(20, 45, 70)).Set("holgada", new Trap(55, 75, 100, 100));
        fs.Inputs["exposicion"] = new FuzzyVar { Id = "exposicion", Name = "Exposición (frío, noche, agua)", Min = 0, Max = 1 }
            .Set("baja", new Trap(0, 0, 0.2f, 0.4f)).Set("media", new Tri(0.25f, 0.5f, 0.75f)).Set("alta", new Trap(0.6f, 0.8f, 1, 1));
        fs.Inputs["acceso"] = new FuzzyVar { Id = "acceso", Name = "Distancia de acceso", Unit = "km", Min = 0, Max = 10 }
            .Set("cerca", new Trap(0, 0, 1, 2.5f)).Set("media", new Tri(1.5f, 3.5f, 5.5f)).Set("lejos", new Trap(4.5f, 6.5f, 10, 10));
        fs.Outputs["urgencia"] = new FuzzyVar { Id = "urgencia", Name = "Urgencia", Min = 0, Max = 1 }
            .Set("baja", new Trap(0, 0, 0.2f, 0.4f)).Set("media", new Tri(0.3f, 0.5f, 0.7f)).Set("alta", new Trap(0.6f, 0.8f, 1, 1));
        fs.FallbackValue["urgencia"] = 0.5f;
        void R(string id, string why, (string, string)[] c, params (string, string)[] t) => fs.Rules.Add(new FuzzyRule { Id = id, If = c, Then = t, Justification = why });
        R("U1", "Supervivencia crítica: rescate inmediato.", new[] { ("supervivencia", "critica") }, ("urgencia", "alta"));
        R("U2", "Exposición alta (frío, noche o agua): la condición empeora rápido.", new[] { ("exposicion", "alta") }, ("urgencia", "alta"));
        R("U3", "Supervivencia media y exposición media: prioridad intermedia.", new[] { ("supervivencia", "media"), ("exposicion", "media") }, ("urgencia", "media"));
        R("U4", "Holgada y poco expuesta: puede esperar.", new[] { ("supervivencia", "holgada"), ("exposicion", "baja") }, ("urgencia", "baja"));
        R("U5", "Supervivencia media pero lejos: hay que salir ya para llegar a tiempo.", new[] { ("supervivencia", "media"), ("acceso", "lejos") }, ("urgencia", "alta"));
        R("U6", "Holgada y cerca: se atiende en el camino.", new[] { ("supervivencia", "holgada"), ("acceso", "cerca") }, ("urgencia", "baja"));
        return fs;
    }
}

public sealed partial class Mission
{
    public bool Rescue => Config.Mode == MissionMode.Rescate;
    public readonly List<Victim> Victims = new();
    public readonly List<MedicalPoint> Medical = new();
    public int Capacity => Heli ? 4 : 2;
    public int OnBoard => Victims.Count(v => v.OnBoard && v.Carrier == 0 && !v.Lost && !v.Delivered);
    public int Saved => Victims.Count(v => v.Delivered);
    public int LostCount => Victims.Count(v => v.Lost);
    public string RescueNote = "";
    public readonly List<object> RescueOrder = new();   // Victim o MedicalPoint, en orden
    private readonly FuzzySystem _urgencyFuzzy = RescueFuzzy.Build();
    public FuzzyResult? LastUrgency;
    private Rng _rngRescue = null!;

    // ------------------------------------------------------------------ preparación
    private void InitRescue(ulong seed)
    {
        if (!Rescue) return;
        _rngRescue = new Rng(seed * 13 + 4447);
        Map.Sites.Clear();   // en rescate no hay muestreo científico
        // puntos médicos: hospitales de la ciudad simulada (helipuerto) y un puesto médico en la base
        if (Map.City != null)
            foreach (int hi in Map.City.Hospitals)
            {
                var b = Map.City.Buildings[hi];
                var c = NearestFree(new Cell((int)(b.X / Map.CellM), (int)(b.Z / Map.CellM)), 6);
                if (c is Cell cc) Medical.Add(new MedicalPoint { Name = $"Hospital {Medical.Count + 1}", Cell = cc });
            }
        Medical.Add(new MedicalPoint { Name = "Puesto médico (base)", Cell = Map.Start });
        // los hospitales tienen helipuerto con combustible: también sirven para recargar (refugio)
        foreach (var mp in Medical) if (!Map.Refuges.Contains(mp.Cell)) Map.Refuges.Add(mp.Cell);

        var rng = _rngRescue;
        int n = Map.N;
        var inside = Enumerable.Range(0, n * n).Where(i => Map.InBounds(new Cell(i % n, i / n))).ToList();
        var elev = inside.Select(i => Map.H[i]).OrderBy(h => h).ToList();
        float p85 = elev[(int)(elev.Count * 0.85f)], p30 = elev[(int)(elev.Count * 0.3f)];
        bool NearWater(Cell c, int r)
        {
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
                if (Map.HazardAt(new Cell(c.X + dx, c.Y + dy)) == Hazard.Water) return true;
            return false;
        }
        var pools = new Dictionary<VictimKind, List<int>>
        {
            [VictimKind.Montana] = inside.Where(i => Map.H[i] >= p85 && Map.Slope[i] is > 12 and < 38).ToList(),
            [VictimKind.Aluvion] = inside.Where(i => ((Map.Geo.Marks?[i] ?? 0) & 1) != 0 || Map.Hazards[i] == Hazard.Debris).ToList(),
            [VictimKind.Inundacion] = inside.Where(i => Map.H[i] <= p30 && Map.Hazards[i] != Hazard.Water && NearWater(new Cell(i % n, i / n), 3)).ToList(),
            [VictimKind.Urbano] = inside.Where(i => Map.Soils[i] == Soil.Urbano && Map.Hazards[i] == Hazard.None && NearWater(new Cell(i % n, i / n), 6)).ToList(),
        };
        if (!Map.IsScenario)   // terreno procedural: montaña = celdas altas, aluvión = laderas, inundación = bajos
        {
            pools[VictimKind.Aluvion] = inside.Where(i => Map.Slope[i] is > 15 and < 30).ToList();
            pools[VictimKind.Inundacion] = inside.Where(i => Map.H[i] <= p30).ToList();
        }
        if (Config.Points.Count > 0)
        {
            foreach (var p in Config.Points)
            {
                if (!Map.InBounds(p)) continue;
                var c = NearestFree(p, 4) ?? p;
                int i = Map.Idx(c);
                var kind = Map.Soils[i] == Soil.Urbano ? VictimKind.Urbano
                         : NearWater(c, 3) && Map.H[i] <= p30 * 1.05f ? VictimKind.Inundacion
                         : Map.H[i] >= p85 ? VictimKind.Montana
                         : ((Map.Geo.Marks?[i] ?? 0) & 1) != 0 || Map.Slope[i] is > 15 and < 32 ? VictimKind.Aluvion
                         : Map.H[i] >= (p30 + p85) / 2 ? VictimKind.Montana : VictimKind.Inundacion;
                Map.Hazards[i] = Hazard.None;
                float bh = kind switch { VictimKind.Montana => 3.0f, VictimKind.Aluvion => 2.2f, VictimKind.Inundacion => 1.4f, _ => 3.6f };
                float sv = bh * 3600f * rng.Range(0.75f, 1.25f);
                Victims.Add(new Victim { Id = Victims.Count, Name = $"V{Victims.Count + 1}", Cell = c, Kind = kind, SurvivalS = sv, SurvivalMaxS = sv });
            }
        }
        int count = Config.Points.Count > 0 ? Victims.Count : 5 + Config.Difficulty * 2;
        var kinds = pools.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToList();
        if (kinds.Count == 0) kinds.Add(VictimKind.Urbano);
        int minSpacing = Math.Max(8, n / 14);
        for (int k = 0, tries = 0; Victims.Count < count && tries < 4000; tries++)
        {
            var kind = kinds[k % kinds.Count];
            var pool = pools.GetValueOrDefault(kind) is { Count: > 0 } p ? p : inside;
            int idx = pool[rng.Int(0, pool.Count)];
            var c = new Cell(idx % n, idx / n);
            if (c.Dist(Map.Start) < minSpacing || Victims.Any(v => v.Cell.Dist(c) < minSpacing)) continue;
            if (Map.Hazards[Map.Idx(c)] != Hazard.None && Map.Hazards[Map.Idx(c)] != Hazard.Debris) continue;
            Map.Hazards[Map.Idx(c)] = Hazard.None;   // la persona está en un punto alcanzable a pie desde las inmediaciones
            float baseH = kind switch { VictimKind.Montana => 3.0f, VictimKind.Aluvion => 2.2f, VictimKind.Inundacion => 1.4f, _ => 3.6f };
            float s = baseH * 3600f * rng.Range(0.75f, 1.25f);
            Victims.Add(new Victim { Id = Victims.Count, Name = $"V{Victims.Count + 1}", Cell = c, Kind = kind, SurvivalS = s, SurvivalMaxS = s });
            k++;
        }
        Map.Version++;
        if (Config.Points.Count > 0) AddLog("info", $"Personas ubicadas por el operador en el mapa: {Victims.Count}");
        AddLog("evento", $"RESCATE: {Victims.Count} personas atrapadas ({string.Join(", ", Victims.GroupBy(v => v.Kind).Select(g => $"{g.Count()} en {Victim.KindName(g.Key)}"))}) · " +
                         $"{Medical.Count} punto(s) médico(s): {string.Join(", ", Medical.Select(m => m.Name))} · capacidad {Capacity}");
        _needMissionPlan = true;
    }

    private Cell? NearestFree(Cell c, int maxR)
    {
        for (int r = 0; r <= maxR; r++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            var o = new Cell(c.X + dx, c.Y + dy);
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && Map.InBounds(o) && Map.Hazards[Map.Idx(o)] == Hazard.None && (Heli || Map.Slope[Map.Idx(o)] < WorldMap.MaxSlopeDeg)) return o;
        }
        return null;
    }

    // ------------------------------------------------------------------ exposición y urgencia
    private float Exposure(Victim v)
    {
        float t = Env.TempAt(v.Cell);
        float cold = MathX.Clamp01((5f - t) / 20f);
        float wet = v.Kind == VictimKind.Inundacion ? 0.6f : 0f;
        float night = Env.IsDay ? 0 : 0.3f;
        float rain = Planet.HasRain ? MathX.Clamp01(Env.RainMmH / 10f) * 0.3f : 0;
        return MathX.Clamp01(cold * 0.6f + wet + night + rain);
    }

    private void UpdateVictims(float dt)
    {
        if (!Rescue) return;
        foreach (var v in Victims.Where(v => v.Active))
        {
            float rate = v.OnBoard ? 0.25f : 1f + Exposure(v);
            v.SurvivalS -= dt * rate;
            if (v.SurvivalS <= 0)
            {
                v.SurvivalS = 0;
                v.Lost = true;
                v.OnBoard = false;
                AddLog("fallo", $"{v.Name} ({Victim.KindName(v.Kind)}) no sobrevivió a la espera");
                _needMissionPlan = true;
            }
            float accessKm = v.Cell.Dist(Rover.Pos) * Map.CellM / 1000f;
            LastUrgency = _urgencyFuzzy.Infer(new Dictionary<string, float>
            {
                ["supervivencia"] = 100f * v.SurvivalS / v.SurvivalMaxS, ["exposicion"] = Exposure(v), ["acceso"] = MathF.Min(10, accessKm),
            });
            v.Urgency = LastUrgency.Crisp["urgencia"];
        }
    }

    // ------------------------------------------------------------------ plan de rescate (AG: orden con plazos y capacidad)
    private float SpeedMs => Heli ? HeliModel.CruiseMs * 0.85f : Rover.VmaxMs * 0.7f;
    private float PickupS(Cell c) => Heli ? (Landable(c) ? 120f : 240f) : 150f;
    private const float DeliverS = 90f;

    private void PlanRescue()
    {
        var sw = Stopwatch.StartNew();
        // personas pendientes (no reservadas por ningún vehículo)
        var active = Victims.Where(v => v.Active && !v.OnBoard).ToList();
        RescueOrder.Clear();
        int nVeh = 1 + Fleet.Count;
        if (active.Count == 0 && OnBoard == 0)
        {
            RescueNote = "Sin personas por rescatar";
            SetTarget(Map.Start, "base (rescate terminado)");
            foreach (var u in Fleet.Where(u => u.Busy == "")) { u.Queue.Clear(); u.Goal = null; u.Path.Clear(); }
            return;
        }
        int V = active.Count, M = Medical.Count;
        // matrices de distancia (m) por vehículo: nodo 0 = posición actual, 1..V = personas, V+1..V+M = puntos médicos
        int N = 1 + V + M;
        var dist = new float[nVeh][,];
        var fRover = Cost.DistanceField(Rover.Pos, 1f);
        var fVictim = active.Select(v => Cost.DistanceField(v.Cell, 1f)).ToList();
        var fMed = Medical.Select(m => Cost.DistanceField(m.Cell, 1f)).ToList();
        Cell NodeCell(int k) => k == 0 ? Rover.Pos : k <= V ? active[k - 1].Cell : Medical[k - V - 1].Cell;
        {
            var d = new float[N, N];
            for (int a = 0; a < N; a++)
            {
                var f = a == 0 ? fRover : a <= V ? fVictim[a - 1] : fMed[a - V - 1];
                for (int b = 0; b < N; b++) d[a, b] = f[Map.Idx(NodeCell(b))];
            }
            dist[0] = d;
        }
        for (int j = 1; j < nVeh; j++)
        {
            var u = Fleet[j - 1];
            var d = new float[N, N];
            if (u.Heli)
            {
                // vuelo directo; inalcanzable si el margen de potencia no permite el estacionario en el punto
                Cell UCell(int k) => k == 0 ? u.Pos : NodeCell(k);
                for (int a = 0; a < N; a++)
                for (int b = 0; b < N; b++)
                    d[a, b] = b >= 1 && b <= V && HeliMarginAt(active[b - 1].Cell) < 1.0f ? float.MaxValue : FlightM(UCell(a), UCell(b));
            }
            else
            {
                Cell? Acc(int k) => k == 0 ? u.Pos : TruckAccess(NodeCell(k));
                for (int a = 0; a < N; a++)
                {
                    var ca = Acc(a);
                    if (ca == null) { for (int b = 0; b < N; b++) d[a, b] = float.MaxValue; continue; }
                    var f = TruckField(ca.Value);
                    for (int b = 0; b < N; b++) d[a, b] = Acc(b) is Cell cb ? f[Map.Idx(cb)] : float.MaxValue;
                }
            }
            dist[j] = d;
        }
        float Speed(int j) => j == 0 ? SpeedMs : Fleet[j - 1].SpeedMs;
        int Cap(int j) => j == 0 ? Capacity : Fleet[j - 1].Capacity;
        float Pick(int j, int vk) => j == 0 ? PickupS(active[vk].Cell) : Fleet[j - 1].Heli ? (Landable(active[vk].Cell) ? 120f : 240f) : 150f;
        // vehículos capaces por persona (llegan a ella y a algún punto médico)
        var capable = new List<int>[V];
        for (int k = 0; k < V; k++)
        {
            capable[k] = new List<int>();
            for (int j = 0; j < nVeh; j++)
                if (dist[j][0, k + 1] < float.MaxValue && Enumerable.Range(0, M).Any(m => dist[j][k + 1, V + 1 + m] < float.MaxValue)) capable[k].Add(j);
        }
        for (int k = V - 1; k >= 0; k--)
            if (capable[k].Count == 0)
            {
                active[k].Unreachable = true;
                AddLog("alerta", $"{active[k].Name}: ningún vehículo de la flota puede llegar (relieve, potencia o sin acceso por calles)");
            }
        var keep = Enumerable.Range(0, V).Where(k => capable[k].Count > 0).ToArray();

        // decodificación: cada vehículo recoge sus personas en el orden de la permutación y va al punto médico más
        // cercano al llenarse o al final; la supervivencia baja con la exposición mientras espera
        (float Fit, List<object>[] Seq, int SavedN) Eval(int[] perm, int[] assign)
        {
            var seqs = new List<object>[nVeh];
            float saved = 0, margin = 0, makespan = 0, total = 0;
            for (int j = 0; j < nVeh; j++)
            {
                var seq = seqs[j] = new List<object>();
                var d = dist[j];
                float t = j == 0 ? 0 : Fleet[j - 1].BusyS;
                int cur = 0;
                var aboard = (j == 0 ? Victims.Where(v => v.OnBoard && v.Active && v.Carrier == 0) : Fleet[j - 1].Aboard.Where(v => v.Active))
                             .Select(v => (v, pickT: 0f)).ToList();
                int load = aboard.Count;
                float spd = Speed(j);
                void GoMed()
                {
                    int bm = -1;
                    float bd = float.MaxValue;
                    for (int m = 0; m < M; m++) if (d[cur, V + 1 + m] < bd) { bd = d[cur, V + 1 + m]; bm = m; }
                    if (bm < 0) { aboard.Clear(); load = 0; return; }
                    t += bd / spd + DeliverS;
                    foreach (var (v, pt) in aboard)
                    {
                        float left = v.SurvivalS - pt * (1 + Exposure(v)) - (t - pt) * 0.25f;
                        if (left > 0) { saved++; margin += left / v.SurvivalMaxS; }
                    }
                    aboard.Clear();
                    load = 0;
                    seq.Add(Medical[bm]);
                    cur = V + 1 + bm;
                }
                if (load >= Cap(j)) GoMed();
                foreach (int k in perm)
                {
                    if (assign[k] != j) continue;
                    var v = active[k];
                    float dd = d[cur, k + 1];
                    if (dd == float.MaxValue) continue;
                    float arrive = t + dd / spd;
                    if (arrive * (1 + Exposure(v)) >= v.SurvivalS) continue;   // no alcanza a llegar con vida
                    t = arrive + Pick(j, k);
                    seq.Add(v);
                    aboard.Add((v, t));
                    cur = k + 1;
                    if (++load >= Cap(j)) GoMed();
                }
                if (aboard.Count > 0) GoMed();
                makespan = MathF.Max(makespan, t);
                total += t;
            }
            float fit = saved * 10f + margin * 2f - makespan / 3600f - 0.2f * total / 3600f;
            return (fit, seqs, (int)saved);
        }

        var rng = _rngRescue;
        // solución inicial: por urgencia, cada persona al vehículo capaz que llega antes
        int[] perm0 = keep.OrderByDescending(k => active[k].Urgency).ToArray();
        int[] assign0 = new int[V];
        for (int k = 0; k < V; k++) assign0[k] = capable[k].Count == 0 ? 0 : capable[k].OrderBy(j => dist[j][0, k + 1] / Speed(j)).First();
        int[] bestP = perm0, bestA = assign0;
        var bestEval = Eval(bestP, bestA);
        int gens = 0;
        if (Config.UseGa && keep.Length > 1)
        {
            // AG multivehículo: permutación (OX) + asignación (uniforme), torneo, mutación por intercambio y por reasignación
            int Pop = 48, Gens = nVeh > 1 ? 60 : 40;
            var popP = new List<int[]> { perm0 };
            var popA = new List<int[]> { assign0 };
            while (popP.Count < Pop)
            {
                popP.Add(keep.OrderBy(_ => rng.F()).ToArray());
                popA.Add(Enumerable.Range(0, V).Select(k => capable[k].Count == 0 ? 0 : capable[k][rng.Int(0, capable[k].Count)]).ToArray());
            }
            var fit = popP.Select((p, i) => Eval(p, popA[i]).Fit).ToList();
            int L = keep.Length;
            for (gens = 0; gens < Gens; gens++)
            {
                int bi = fit.IndexOf(fit.Max());
                var nextP = new List<int[]> { popP[bi] };
                var nextA = new List<int[]> { popA[bi] };
                int Tour()
                {
                    int a = rng.Int(0, popP.Count), b = rng.Int(0, popP.Count), c = rng.Int(0, popP.Count);
                    int w = fit[a] > fit[b] ? a : b;
                    return fit[w] > fit[c] ? w : c;
                }
                while (nextP.Count < Pop)
                {
                    int i1 = Tour(), i2 = Tour();
                    var p1 = popP[i1];
                    var p2 = popP[i2];
                    int c0 = rng.Int(0, L), c1 = rng.Int(c0, L);
                    var child = Enumerable.Repeat(-1, L).ToArray();
                    for (int i = c0; i <= Math.Min(c1, L - 1); i++) child[i] = p1[i];
                    int pos = 0;
                    foreach (int g in p2)
                    {
                        if (child.Contains(g)) continue;
                        while (child[pos] != -1) pos++;
                        child[pos] = g;
                    }
                    if (rng.Chance(0.3f)) { int x = rng.Int(0, L), y = rng.Int(0, L); (child[x], child[y]) = (child[y], child[x]); }
                    var ca = new int[V];
                    for (int k = 0; k < V; k++) ca[k] = rng.Chance(0.5f) ? popA[i1][k] : popA[i2][k];
                    if (nVeh > 1 && rng.Chance(0.35f))
                    {
                        int k = keep[rng.Int(0, L)];
                        if (capable[k].Count > 0) ca[k] = capable[k][rng.Int(0, capable[k].Count)];
                    }
                    nextP.Add(child);
                    nextA.Add(ca);
                }
                popP = nextP;
                popA = nextA;
                fit = popP.Select((p, i) => Eval(p, popA[i]).Fit).ToList();
            }
            int b2 = fit.IndexOf(fit.Max());
            bestP = popP[b2];
            bestA = popA[b2];
            bestEval = Eval(bestP, bestA);
        }
        RescueOrder.AddRange(bestEval.Seq[0]);
        string Seq(List<object> s) => s.Count == 0 ? "sin tareas" : string.Join(" → ", s.Select(o => o is Victim v ? v.Name : ((MedicalPoint)o).Name));
        RescueNote = $"{(Config.UseGa ? $"AG de rescate{(nVeh > 1 ? $" multivehículo ({nVeh})" : "")} ({gens} gen, {sw.ElapsedMilliseconds} ms)" : "Sin AG: por urgencia difusa y llegada más temprana")}: " +
                     (nVeh > 1 ? string.Join(" | ", Enumerable.Range(0, nVeh).Select(j => $"{(j == 0 ? "H-1" : Fleet[j - 1].Name)}: {Seq(bestEval.Seq[j])}")) : Seq(bestEval.Seq[0])) +
                     $" · se esperan {bestEval.SavedN} con vida";
        AddLog("ag", RescueNote);
        // tareas a la flota y aviso por radio cuando cambian
        for (int j = 1; j < nVeh; j++)
        {
            var u = Fleet[j - 1];
            string before = string.Join(",", u.Queue.Select(o => o is Victim v ? v.Name : o is MedicalPoint mp ? mp.Name : ""));
            u.Queue.Clear();
            u.Queue.AddRange(bestEval.Seq[j]);
            string after = string.Join(",", u.Queue.Select(o => o is Victim v ? v.Name : o is MedicalPoint mp ? mp.Name : ""));
            if (u.Busy == "" && (u.Goal == null || !u.Queue.Contains(u.Goal) || u.Queue.FirstOrDefault() != u.Goal)) { u.Goal = null; u.Path.Clear(); }
            if (after != before && u.Queue.Count > 0) Radio(0, u.Id, Seq(bestEval.Seq[j]));
        }
        ChooseRescueTarget();
    }

    private void ChooseRescueTarget()
    {
        if (_goingRefuge || Charging) return;   // primero la energía: no pisar el viaje a recargar
        object? next = RescueOrder.FirstOrDefault(o => o is MedicalPoint || o is Victim v && v.Active && !v.OnBoard);
        if (next is Victim vv) SetTarget(vv.Cell, $"{vv.Name} · {Victim.KindName(vv.Kind)}");
        else if (next is MedicalPoint mp) SetTarget(mp.Cell, mp.Name);
        else SetTarget(OnBoard > 0 ? Medical[0].Cell : Map.Start, OnBoard > 0 ? Medical[0].Name : "base (rescate terminado)");
        _needRoute = true;
    }

    /// <summary>Al llegar al objetivo: subir a la persona (con el piloto; el helicóptero usa cabrestante si no puede aterrizar) o entregarla.</summary>
    private float DoRescueAction()
    {
        PendingSample = false;
        var v = Victims.FirstOrDefault(x => x.Cell == Rover.Pos && x.Active && !x.OnBoard);
        if (v != null)
        {
            if (OnBoard >= Capacity)
            {
                AddLog("alerta", $"{v.Name}: vehículo lleno ({OnBoard}/{Capacity}) — primero hay que entregar");
                _needMissionPlan = true;
                return 30f;
            }
            v.OnBoard = true;
            v.Carrier = 0;
            string how = Heli && !Landable(Rover.Pos) ? "con cabrestante en vuelo estacionario" : "por la tripulación";
            AddLog("ok", $"{v.Name} rescatada {how} ({Victim.KindName(v.Kind)}; le quedaban {v.SurvivalS / 60:0} min) · a bordo {OnBoard}/{Capacity}");
            RescueOrder.Remove(v);
            LastRescueAction = "rescate";
            ChooseRescueTarget();
            return PickupS(Rover.Pos);
        }
        var mp = Medical.FirstOrDefault(m => m.Cell == Rover.Pos);
        if (mp != null && OnBoard > 0)
        {
            var list = Victims.Where(x => x.OnBoard && x.Active && x.Carrier == 0).ToList();
            foreach (var x in list) { x.Delivered = true; x.OnBoard = false; }
            AddLog("ok", $"{mp.Name}: entregadas {list.Count} persona(s) ({string.Join(", ", list.Select(x => x.Name))}) · salvadas {Saved}/{Victims.Count}");
            RescueOrder.Remove(mp);
            LastRescueAction = "entrega";
            _needMissionPlan = true;
            return DeliverS;
        }
        RescueOrder.Remove(mp!);
        _needMissionPlan = true;
        return 20f;
    }

    public string LastRescueAction = "";

    /// <summary>¿Terminó el rescate? Todas las personas entregadas, perdidas o inalcanzables y nadie a bordo.</summary>
    private bool RescueDone => Rescue && Victims.Count > 0 && Victims.All(v => !v.Active) && OnBoard == 0;
}
