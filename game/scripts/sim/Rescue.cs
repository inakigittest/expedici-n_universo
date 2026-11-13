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
    public int OnBoard => Victims.Count(v => v.OnBoard && !v.Lost && !v.Delivered);
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
        int count = 5 + Config.Difficulty * 2;
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
        var active = Victims.Where(v => v.Active && !v.OnBoard).ToList();
        RescueOrder.Clear();
        if (active.Count == 0 && OnBoard == 0)
        {
            RescueNote = "Sin personas por rescatar";
            SetTarget(Map.Start, "base (rescate terminado)");
            return;
        }
        // campos de distancia (m) desde el vehículo, cada víctima y cada punto médico
        var fRover = Cost.DistanceField(Rover.Pos, 1f);
        var fVictim = active.ToDictionary(v => v.Id, v => Cost.DistanceField(v.Cell, 1f));
        var fMed = Medical.Select(m => Cost.DistanceField(m.Cell, 1f)).ToList();
        foreach (var v in active.Where(v => fRover[Map.Idx(v.Cell)] == float.MaxValue).ToList())
        {
            v.Unreachable = true;
            AddLog("alerta", $"{v.Name}: inalcanzable para el {(Heli ? "helicóptero" : "rover")} con el mapa estimado (¿otro vehículo?)");
            active.Remove(v);
        }
        int NearestMed(Func<int, float> distTo) => Enumerable.Range(0, Medical.Count).OrderBy(i => distTo(i)).First();

        // decodifica una permutación: recoge en orden, va al punto médico más cercano al llenarse o al final
        (float Fit, List<object> Seq, int SavedN) Eval(int[] perm)
        {
            var seq = new List<object>();
            float t = 0, saved = 0, margin = 0;
            float[] curField = fRover;
            Cell cur = Rover.Pos;
            int load = OnBoard;
            var aboard = Victims.Where(v => v.OnBoard && v.Active).Select(v => (v, pickT: 0f)).ToList();
            void GoMed()
            {
                int mi = NearestMed(i => curField[Map.Idx(Medical[i].Cell)]);
                float d = curField[Map.Idx(Medical[mi].Cell)];
                if (d == float.MaxValue) d = 1e6f;
                t += d / SpeedMs + DeliverS;
                foreach (var (v, pt) in aboard)
                {
                    float left = v.SurvivalS - pt * (1 + Exposure(v)) - (t - pt) * 0.25f;
                    if (left > 0) { saved++; margin += left / v.SurvivalMaxS; }
                }
                aboard.Clear();
                load = 0;
                seq.Add(Medical[mi]);
                curField = fMed[mi];
                cur = Medical[mi].Cell;
            }
            if (load >= Capacity) GoMed();
            foreach (int k in perm)
            {
                var v = active[k];
                float d = curField[Map.Idx(v.Cell)];
                if (d == float.MaxValue) continue;
                float arrive = t + d / SpeedMs;
                if (arrive * (1 + Exposure(v)) >= v.SurvivalS) continue;   // no alcanza a llegar con vida: se omite
                t = arrive + PickupS(v.Cell);
                seq.Add(v);
                aboard.Add((v, t));
                curField = fVictim[v.Id];
                cur = v.Cell;
                if (++load >= Capacity) GoMed();
            }
            if (aboard.Count > 0) GoMed();
            float fit = saved * 10f + margin * 2f - t / 3600f;
            return (fit, seq, (int)saved);
        }

        int nAct = active.Count;
        int[] best = Enumerable.Range(0, nAct).OrderByDescending(i => active[i].Urgency).ToArray();
        var bestEval = Eval(best);
        int gens = 0;
        if (Config.UseGa && nAct > 1)
        {
            // AG de permutaciones: torneo, cruce de orden (OX), mutación por intercambio, elitismo
            var rng = _rngRescue;
            var pop = new List<int[]> { best };
            while (pop.Count < 40) pop.Add(Enumerable.Range(0, nAct).OrderBy(_ => rng.F()).ToArray());
            var fit = pop.Select(p => Eval(p).Fit).ToList();
            for (gens = 0; gens < 40; gens++)
            {
                var next = new List<int[]> { pop[fit.IndexOf(fit.Max())] };
                int[] Tour()
                {
                    int a = rng.Int(0, pop.Count), b = rng.Int(0, pop.Count), c = rng.Int(0, pop.Count);
                    int w = fit[a] > fit[b] ? a : b;
                    return pop[fit[w] > fit[c] ? w : c];
                }
                while (next.Count < pop.Count)
                {
                    var p1 = Tour();
                    var p2 = Tour();
                    int i0 = rng.Int(0, nAct), i1 = rng.Int(i0, nAct);
                    var child = Enumerable.Repeat(-1, nAct).ToArray();
                    for (int i = i0; i <= Math.Min(i1, nAct - 1); i++) child[i] = p1[i];
                    int pos = 0;
                    foreach (int g in p2)
                    {
                        if (child.Contains(g)) continue;
                        while (child[pos] != -1) pos++;
                        child[pos] = g;
                    }
                    if (rng.Chance(0.3f)) { int x = rng.Int(0, nAct), y = rng.Int(0, nAct); (child[x], child[y]) = (child[y], child[x]); }
                    next.Add(child);
                }
                pop = next;
                fit = pop.Select(p => Eval(p).Fit).ToList();
            }
            best = pop[fit.IndexOf(fit.Max())];
            bestEval = Eval(best);
        }
        RescueOrder.AddRange(bestEval.Seq);
        RescueNote = $"{(Config.UseGa ? $"AG de rescate ({gens} gen, {sw.ElapsedMilliseconds} ms)" : "Sin AG: por urgencia difusa")}: " +
                     $"{string.Join(" → ", RescueOrder.Select(o => o is Victim v ? v.Name : ((MedicalPoint)o).Name))} · se esperan {bestEval.SavedN} con vida";
        AddLog("ag", RescueNote);
        ChooseRescueTarget();
    }

    private void ChooseRescueTarget()
    {
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
            var list = Victims.Where(x => x.OnBoard && x.Active).ToList();
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
