using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AresX.Core;
using AresX.Sim;

namespace AresX.AI;

/// <summary>Estimaciones de costo que usan SOLO las creencias del rover (mapa estimado), nunca la verdad.</summary>
public sealed class CostModel
{
    public readonly BeliefMap B;
    public readonly WorldMap MapShape;   // solo dimensiones, perfil del planeta y sitios conocidos (no peligros)
    public readonly RoverState R;

    public CostModel(BeliefMap b, WorldMap map, RoverState r)
    {
        B = b;
        MapShape = map;
        R = r;
    }

    public bool InBounds(Cell c) => MapShape.InBounds(c);

    /// <summary>Carga térmica media esperada (W): calefacción nocturna en cuerpos fríos, refrigeración en calientes.</summary>
    public float ExpectedThermalW => MathF.Max(R.HeaterW + R.CoolerW, MapShape.Planet.TempMeanC < -20 ? 75f : MapShape.Planet.TempMeanC > 100 ? 60f : 15f);
    public bool Blocked(Cell c) => !InBounds(c) || B.Forbidden(c) || (!MapShape.ProbeMode && B.SlopeEst[B.Idx(c)] > WorldMap.MaxSlopeDeg + 6);

    public float StepM(Cell a, Cell b) => (b - a).IsDiagonal ? WorldMap.CellM * 1.41421f : WorldMap.CellM;

    /// <summary>Velocidad esperada (fracción de vmax) según el riesgo estimado.</summary>
    public float ExpectedSpeed(Cell c) => MathX.Clamp(0.85f - 0.7f * B.Risk(c), 0.15f, 0.9f);

    /// <summary>Energía estimada (Wh) de un paso a → b: rodadura + subida + consumo base durante el tiempo de marcha.</summary>
    public float StepWh(Cell a, Cell b)
    {
        float d = StepM(a, b);
        float dh = MathF.Max(0, B.HeightEst[B.Idx(b)] - B.HeightEst[B.Idx(a)]);
        float crr = B.Observed[B.Idx(b)] ? WorldMap.RollingCoeff(MapShape.SoilAt(b)) : 0.12f;
        float g = MapShape.Planet.Gravity;
        float driveJ = R.MassKg * g * (crr * d + dh) / R.Efficiency;
        float t = d / (R.VmaxMs * ExpectedSpeed(b));
        return (driveJ + (R.BaseW + ExpectedThermalW) * t) / 3600f;
    }

    public float StepTimeS(Cell a, Cell b) => StepM(a, b) / (R.VmaxMs * ExpectedSpeed(b));

    /// <summary>Camino de menor costo (Dijkstra) en el mapa estimado. riskWeight = 0 → menor distancia (baseline de control).</summary>
    public List<Cell>? Shortest(Cell from, Cell to, float riskWeight)
    {
        int n = MapShape.N;
        var cost = new float[n * n];
        var prev = new int[n * n];
        Array.Fill(cost, float.MaxValue);
        Array.Fill(prev, -1);
        var pq = new PriorityQueue<Cell, float>();
        cost[B.Idx(from)] = 0;
        pq.Enqueue(from, 0);
        while (pq.Count > 0)
        {
            var c = pq.Dequeue();
            if (c == to) break;
            foreach (var d in Cell.Dirs8)
            {
                var o = c + d;
                if (Blocked(o) && o != to) continue;
                float step = StepM(c, o) * (1 + riskWeight * B.Risk(o));
                float nc = cost[B.Idx(c)] + step;
                if (nc < cost[B.Idx(o)])
                {
                    cost[B.Idx(o)] = nc;
                    prev[B.Idx(o)] = B.Idx(c);
                    pq.Enqueue(o, nc);
                }
            }
        }
        if (cost[B.Idx(to)] == float.MaxValue) return null;
        var path = new List<Cell>();
        for (int i = B.Idx(to); i >= 0; i = prev[i]) path.Add(new Cell(i % n, i / n));
        path.Reverse();
        return path;
    }

    /// <summary>Distancias (m) desde un origen a todas las celdas con costo por riesgo (para el plan de misión).</summary>
    public float[] DistanceField(Cell from, float riskWeight)
    {
        int n = MapShape.N;
        var cost = new float[n * n];
        Array.Fill(cost, float.MaxValue);
        var pq = new PriorityQueue<Cell, float>();
        cost[B.Idx(from)] = 0;
        pq.Enqueue(from, 0);
        while (pq.Count > 0)
        {
            var c = pq.Dequeue();
            foreach (var d in Cell.Dirs8)
            {
                var o = c + d;
                if (Blocked(o)) continue;
                float nc = cost[B.Idx(c)] + StepM(c, o) * (1 + riskWeight * B.Risk(o));
                if (nc < cost[B.Idx(o)])
                {
                    cost[B.Idx(o)] = nc;
                    pq.Enqueue(o, nc);
                }
            }
        }
        return cost;
    }

    /// <summary>Energía media estimada por metro (Wh/m) en terreno llano, para presupuestos.</summary>
    public float WhPerMeter => (R.MassKg * MapShape.Planet.Gravity * 0.12f / R.Efficiency + (R.BaseW + ExpectedThermalW) / (R.VmaxMs * 0.6f)) / 3600f;
}

// ====================================================================== AG de rutas
public sealed class GaParams
{
    public int Population = 40, Generations = 40, Waypoints = 4, Tournament = 3, Elite = 2, Patience = 10;
    public float CrossoverP = 0.85f, MutationP = 0.25f;
    public float WScience = 0.3f, WDistance = 0.25f, WEnergy = 0.2f, WRisk = 0.35f, WTime = 0.1f;
}

public sealed class RouteCandidate
{
    public Cell[] Genes = Array.Empty<Cell>();
    public List<Cell> Path = new();
    public float Fitness, Science, Distance, Energy, Risk, Time, Penalty;
    public float EnergyWh, LengthM, MaxRisk;
    public int Forbidden, Repairs, SteepCells;
    public bool ReachesGoal, Feasible;
    public string Origin = "aleatorio";
    public string Label = "";

    public RouteCandidate Clone() => new()
    {
        Genes = (Cell[])Genes.Clone(), Path = new List<Cell>(Path), Fitness = Fitness, Science = Science, Distance = Distance,
        Energy = Energy, Risk = Risk, Time = Time, Penalty = Penalty, EnergyWh = EnergyWh, LengthM = LengthM, MaxRisk = MaxRisk,
        Forbidden = Forbidden, Repairs = Repairs, SteepCells = SteepCells, ReachesGoal = ReachesGoal, Feasible = Feasible,
        Origin = Origin, Label = Label,
    };

    public string GenesText => string.Join(" ", Genes.Select(g => $"({g.Y},{g.X})"));
    public string Breakdown => $"C {Science:+0.00;-0.00} D {Distance:0.00} E {Energy:0.00} R {Risk:0.00} T {Time:0.00} P {Penalty:0.00}";
}

public sealed class CrossoverRecord
{
    public Cell[] ParentA = Array.Empty<Cell>(), ParentB = Array.Empty<Cell>(), Child = Array.Empty<Cell>();
    public int Cut;
    public float FitA, FitB;
}

public sealed class MutationRecord
{
    public Cell[] Before = Array.Empty<Cell>(), After = Array.Empty<Cell>();
    public int Gene;
    public string Kind = "";
    public bool Repaired;
}

public sealed class GenerationSnapshot
{
    public int Gen;
    public float Best, Mean, Worst;
    public int FeasibleCount;
    public List<RouteCandidate> Top = new();
    public CrossoverRecord? Crossover;
    public MutationRecord? Mutation;
}

public sealed class GaResult
{
    public RouteCandidate? Best;
    public readonly List<RouteCandidate> Candidates = new();
    public readonly List<GenerationSnapshot> History = new();
    public int Generations;
    public bool EarlyStop, NoSolution;
    public string Trigger = "", Note = "";
    public float Ms;
    public Cell Start, Goal;
    public GaParams Params = new();
}

/// <summary>
/// AG de rutas. Cromosoma = K puntos de paso discretos. Decodificación a ruta contigua por caminata codiciosa hacia
/// cada punto (reparación local: esquiva celdas prohibidas conocidas; si queda atrapada la ruta es inviable).
/// Selección por torneo, cruce de un punto, mutación gaussiana o reinicio del gen, elitismo, parada temprana.
/// Fitness = w1·ciencia − w2·distancia − w3·energía − w4·riesgo − w5·tiempo − P(inviabilidad), todo normalizado.
/// </summary>
public sealed class RouteGA
{
    private readonly CostModel _cm;
    private readonly Rng _rng;
    public readonly GaParams P;
    private Cell _start, _goal;
    private int _minX, _maxX, _minY, _maxY;
    private float _reserveWh;

    public RouteGA(CostModel cm, Rng rng, GaParams p)
    {
        _cm = cm;
        _rng = rng;
        P = p;
    }

    public GaResult Run(Cell start, Cell goal, float reserveWh, string trigger)
    {
        var sw = Stopwatch.StartNew();
        _start = start;
        _goal = goal;
        _reserveWh = reserveWh;
        int n = _cm.MapShape.N, pad = 8;
        _minX = Math.Max(0, Math.Min(start.X, goal.X) - pad);
        _maxX = Math.Min(n - 1, Math.Max(start.X, goal.X) + pad);
        _minY = Math.Max(0, Math.Min(start.Y, goal.Y) - pad);
        _maxY = Math.Min(n - 1, Math.Max(start.Y, goal.Y) + pad);
        var res = new GaResult { Start = start, Goal = goal, Trigger = trigger, Params = P };

        var pop = new List<RouteCandidate>();
        // individuos sembrados: camino de menor distancia (sin riesgo) muestreado en K puntos y la recta
        var sp = _cm.Shortest(start, goal, 0f);
        if (sp != null && sp.Count > 2)
        {
            pop.Add(Make(Sample(sp, P.Waypoints), "semilla: menor distancia"));
            var spRisk = _cm.Shortest(start, goal, 4f);
            if (spRisk != null && spRisk.Count > 2) pop.Add(Make(Sample(spRisk, P.Waypoints), "semilla: corredor de bajo riesgo"));
        }
        pop.Add(Make(Enumerable.Range(1, P.Waypoints).Select(k => Lerp(start, goal, k / (float)(P.Waypoints + 1))).ToArray(), "semilla: recta"));
        while (pop.Count < P.Population) pop.Add(Make(RandomGenes(), "aleatorio"));

        float bestSoFar = float.MinValue;
        int stale = 0;
        for (int gen = 0; gen < P.Generations; gen++)
        {
            pop.Sort((a, b) => b.Fitness.CompareTo(a.Fitness));
            var snap = new GenerationSnapshot
            {
                Gen = gen, Best = pop[0].Fitness, Mean = pop.Average(c => c.Fitness), Worst = pop[^1].Fitness,
                FeasibleCount = pop.Count(c => c.Feasible), Top = pop.Take(5).Select(c => c.Clone()).ToList(),
            };
            res.History.Add(snap);
            if (pop[0].Fitness > bestSoFar + 1e-4f)
            {
                bestSoFar = pop[0].Fitness;
                stale = 0;
            }
            else if (++stale >= P.Patience)
            {
                res.EarlyStop = true;
                break;
            }
            var next = pop.Take(P.Elite).Select(c => { var k = c.Clone(); k.Origin = "élite"; return k; }).ToList();
            while (next.Count < P.Population)
            {
                var a = Tournament(pop);
                var b = Tournament(pop);
                Cell[] child;
                string origin;
                if (_rng.Chance(P.CrossoverP) && P.Waypoints > 1)
                {
                    int cut = _rng.Int(1, P.Waypoints);
                    child = a.Genes.Take(cut).Concat(b.Genes.Skip(cut)).ToArray();
                    origin = $"cruce (corte {cut})";
                    snap.Crossover ??= new CrossoverRecord { ParentA = (Cell[])a.Genes.Clone(), ParentB = (Cell[])b.Genes.Clone(), Child = (Cell[])child.Clone(), Cut = cut, FitA = a.Fitness, FitB = b.Fitness };
                }
                else
                {
                    child = (Cell[])a.Genes.Clone();
                    origin = "copia";
                }
                for (int g = 0; g < child.Length; g++)
                {
                    if (!_rng.Chance(P.MutationP)) continue;
                    var before = (Cell[])child.Clone();
                    bool reset = _rng.Chance(0.3f);
                    var raw = reset ? new Cell(_rng.Int(_minX, _maxX + 1), _rng.Int(_minY, _maxY + 1))
                                    : new Cell(child[g].X + (int)MathF.Round(_rng.Gauss(0, 3f)), child[g].Y + (int)MathF.Round(_rng.Gauss(0, 3f)));
                    var fixedC = _cm.MapShape.Clamp(raw);   // reparación de coordenada inválida (fuera del mapa)
                    child[g] = fixedC;
                    origin += reset ? " + mutación (reinicio)" : " + mutación (desplazamiento)";
                    snap.Mutation ??= new MutationRecord { Before = before, After = (Cell[])child.Clone(), Gene = g, Kind = reset ? "reinicio del gen" : "desplazamiento gaussiano σ=3", Repaired = fixedC != raw };
                }
                next.Add(Make(child, origin));
            }
            pop = next;
            res.Generations = gen + 1;
        }
        pop.Sort((a, b) => b.Fitness.CompareTo(a.Fitness));

        // candidatas distintas (A, B, C) para inspección; la mejor viable es la propuesta
        var seen = new HashSet<string>();
        foreach (var c in pop)
        {
            string sig = string.Join(";", c.Path.Where((_, i) => i % 3 == 0));
            if (!seen.Add(sig)) continue;
            res.Candidates.Add(c.Clone());
            if (res.Candidates.Count == 3) break;
        }
        for (int i = 0; i < res.Candidates.Count; i++) res.Candidates[i].Label = ((char)('A' + i)).ToString();
        res.Best = res.Candidates.FirstOrDefault(c => c.Feasible);
        res.NoSolution = res.Best == null;
        res.Note = res.NoSolution ? "Ninguna ruta viable en el mapa estimado: sin solución (se ofrece retorno o espera)." :
                   $"Mejor ruta {res.Best!.Label}: fitness {res.Best.Fitness:0.000} ({res.Best.Path.Count - 1} pasos)";
        res.Ms = (float)sw.Elapsed.TotalMilliseconds;
        return res;
    }

    private static Cell Lerp(Cell a, Cell b, float t) => new((int)MathF.Round(a.X + (b.X - a.X) * t), (int)MathF.Round(a.Y + (b.Y - a.Y) * t));

    private static Cell[] Sample(List<Cell> path, int k) =>
        Enumerable.Range(1, k).Select(i => path[Math.Clamp((int)MathF.Round(i * (path.Count - 1) / (float)(k + 1)), 0, path.Count - 1)]).ToArray();

    private Cell[] RandomGenes()
    {
        var g = Enumerable.Range(0, P.Waypoints).Select(_ => new Cell(_rng.Int(_minX, _maxX + 1), _rng.Int(_minY, _maxY + 1))).ToArray();
        // ordenar por proyección sobre la recta inicio→meta para evitar zigzags extremos
        var dir = _goal - _start;
        return g.OrderBy(c => (c.X - _start.X) * dir.X + (c.Y - _start.Y) * dir.Y).ToArray();
    }

    private RouteCandidate Tournament(List<RouteCandidate> pop)
    {
        RouteCandidate best = pop[_rng.Int(0, pop.Count)];
        for (int i = 1; i < P.Tournament; i++)
        {
            var c = pop[_rng.Int(0, pop.Count)];
            if (c.Fitness > best.Fitness) best = c;
        }
        return best;
    }

    public RouteCandidate Make(Cell[] genes, string origin)
    {
        var c = new RouteCandidate { Genes = genes, Origin = origin };
        Decode(c);
        Evaluate(c);
        return c;
    }

    /// <summary>Caminata codiciosa contigua start → g1 → … → gK → goal con reparación local.</summary>
    private void Decode(RouteCandidate c)
    {
        var path = new List<Cell> { _start };
        var visited = new HashSet<Cell> { _start };
        var points = c.Genes.Append(_goal).ToList();
        Cell p = _start;
        int budget = _cm.MapShape.N * 6;
        bool stuck = false;
        foreach (var target in points)
        {
            var wp = _cm.Blocked(target) && target != _goal ? NearestFree(target) : target;
            if (wp != target) c.Repairs++;
            while (p != wp && budget-- > 0)
            {
                var dir = wp - p;
                float ang = MathF.Atan2(dir.Y, dir.X);
                var options = Cell.Dirs8.OrderBy(d => MathF.Abs(AngleDiff(MathF.Atan2(d.Y, d.X), ang))).Take(5).ToList();
                Cell? chosen = null;
                for (int k = 0; k < options.Count && chosen == null; k++)
                {
                    var o = p + options[k];
                    if (!_cm.InBounds(o) || visited.Contains(o) && o != wp) continue;
                    if (_cm.Blocked(o) && o != _goal) continue;
                    // entre las 3 mejores direcciones, preferir la de menor riesgo estimado si el ángulo es parecido
                    if (k < 2 && k + 1 < options.Count)
                    {
                        var alt = p + options[k + 1];
                        if (_cm.InBounds(alt) && !visited.Contains(alt) && !_cm.Blocked(alt) && _cm.B.Risk(alt) + 0.25f < _cm.B.Risk(o)) o = alt;
                    }
                    chosen = o;
                }
                if (chosen == null)
                {
                    stuck = true;
                    break;
                }
                if (chosen.Value.Chebyshev(p) > 1 || options.IndexOf(chosen.Value - p) > 1) c.Repairs++;
                p = chosen.Value;
                path.Add(p);
                visited.Add(p);
            }
            if (stuck || budget <= 0) break;
        }
        c.Path = path;
        c.ReachesGoal = p == _goal;
    }

    private Cell NearestFree(Cell t)
    {
        for (int r = 1; r < 6; r++)
            foreach (var d in Cell.Dirs8)
            {
                var o = t + new Cell(d.X * r, d.Y * r);
                if (_cm.InBounds(o) && !_cm.Blocked(o)) return o;
            }
        return t;
    }

    private static float AngleDiff(float a, float b)
    {
        float d = a - b;
        while (d > MathF.PI) d -= MathF.Tau;
        while (d < -MathF.PI) d += MathF.Tau;
        return d;
    }

    private void Evaluate(RouteCandidate c)
    {
        var path = c.Path;
        int n = _cm.MapShape.N;
        float lenM = 0, wh = 0, timeS = 0, riskSum = 0, maxRisk = 0;
        int forb = 0, steep = 0;
        for (int i = 1; i < path.Count; i++)
        {
            lenM += _cm.StepM(path[i - 1], path[i]);
            wh += _cm.StepWh(path[i - 1], path[i]);
            timeS += _cm.StepTimeS(path[i - 1], path[i]);
            float r = _cm.B.Risk(path[i]);
            riskSum += r;
            maxRisk = MathF.Max(maxRisk, r);
            if (_cm.B.Forbidden(path[i])) forb++;
            if (!_cm.MapShape.ProbeMode && _cm.B.SlopeEst[_cm.B.Idx(path[i])] > WorldMap.MaxSlopeDeg) steep++;
        }
        float sci = 0, sciTotal = 0;
        foreach (var s in _cm.MapShape.Sites)
        {
            if (s.Visited) continue;
            sciTotal += s.Value;
            if (s.Cell != _goal && path.Any(p => p.Chebyshev(s.Cell) <= 1)) sci += s.Value;
        }
        int steps = Math.Max(1, path.Count - 1);
        c.LengthM = lenM;
        c.EnergyWh = wh;
        c.MaxRisk = maxRisk;
        c.Forbidden = forb;
        c.SteepCells = steep;
        c.Science = sciTotal > 0 ? sci / sciTotal : 0;
        c.Distance = lenM / (n * WorldMap.CellM * 2f);
        c.Energy = wh / MathF.Max(20f, _cm.R.CapacityWh * 0.3f);
        c.Risk = riskSum / steps + 0.5f * maxRisk;
        c.Time = timeS / (n * 2f * WorldMap.CellM / _cm.R.VmaxMs);
        float deficit = MathF.Max(0, wh + _reserveWh - _cm.R.BatteryWh);
        c.Penalty = forb * 2f + steep * 0.6f + (c.ReachesGoal ? 0 : 3f + _goal.Dist(path[^1]) / n) + (deficit > 0 ? 1f + deficit / 50f : 0);
        c.Feasible = c.ReachesGoal && forb == 0;
        c.Fitness = P.WScience * c.Science - P.WDistance * c.Distance - P.WEnergy * c.Energy - P.WRisk * c.Risk - P.WTime * c.Time - c.Penalty;
    }
}

// ====================================================================== AG del plan de misión (viajero + mochila)
public sealed class MissionPlan
{
    public readonly List<ScienceSite> Order = new();
    public Cell FinalRefuge;
    public float Value, MassKg, EnergyWh, TourM, Fitness;
    public bool MassOk, EnergyOk, Empty;
    public readonly List<float> BestHistory = new(), MeanHistory = new();
    public int Generations;
    public string Note = "";
    public readonly List<(string Name, bool Included, float Value, float Mass, string Reason)> Decisions = new();
    public float Ms;
}

/// <summary>
/// AG de plan de misión: claves aleatorias (orden de visita, problema del viajero) + bits de inclusión (mochila binaria
/// con capacidad de muestras y presupuesto de energía). Distancias estimadas por Dijkstra en el mapa de creencias.
/// </summary>
public sealed class MissionGA
{
    private readonly CostModel _cm;
    private readonly Rng _rng;

    public MissionGA(CostModel cm, Rng rng)
    {
        _cm = cm;
        _rng = rng;
    }

    public MissionPlan Run(Cell rover, float reserveWh, float scienceWeight)
    {
        var sw = Stopwatch.StartNew();
        var plan = new MissionPlan();
        var refuges = _cm.MapShape.Refuges;
        var roverField = _cm.DistanceField(rover, 2f);
        var sites = _cm.MapShape.Sites.Where(s => !s.Visited && roverField[_cm.B.Idx(s.Cell)] < float.MaxValue).ToList();
        foreach (var s in _cm.MapShape.Sites.Where(s => !s.Visited && roverField[_cm.B.Idx(s.Cell)] == float.MaxValue))
            plan.Decisions.Add((s.Name, false, s.Value, s.MassKg, "inalcanzable en el mapa estimado"));
        Cell NearestRefuge(float[] field) => refuges.OrderBy(r => field[_cm.B.Idx(r)]).First();

        if (sites.Count == 0)
        {
            plan.Empty = true;
            plan.FinalRefuge = NearestRefuge(roverField);
            plan.Note = "Misión sin sitios científicos alcanzables: solo retorno al refugio.";
            plan.MassOk = plan.EnergyOk = true;
            plan.Ms = (float)sw.Elapsed.TotalMilliseconds;
            return plan;
        }
        // matriz de distancias entre puntos de interés: 0 = rover, 1..n = sitios
        var fields = sites.Select(s => _cm.DistanceField(s.Cell, 2f)).ToList();
        int n = sites.Count;
        float D(int a, int b) => a == 0 ? roverField[_cm.B.Idx(sites[b - 1].Cell)] : fields[a - 1][b == 0 ? _cm.B.Idx(rover) : _cm.B.Idx(sites[b - 1].Cell)];
        float RefDist(int a, out Cell refuge)
        {
            var f = a == 0 ? roverField : fields[a - 1];
            refuge = NearestRefuge(f);
            return f[_cm.B.Idx(refuge)];
        }
        float budget = MathF.Max(0, _cm.R.BatteryWh - reserveWh);
        float cap = _cm.R.SampleCapKg - _cm.R.SampleKg;
        float totalValue = sites.Sum(s => s.Value);
        float whm = _cm.WhPerMeter;

        (float fit, List<int> order, float value, float mass, float energy, float tour, Cell refuge) Eval(float[] keys, bool[] bits)
        {
            var order = Enumerable.Range(0, n).Where(i => bits[i]).OrderBy(i => keys[i]).ToList();
            float tour = 0, value = 0, mass = 0, analysis = 0;
            int prev = 0;
            foreach (int i in order)
            {
                float d = D(prev, i + 1);
                if (d == float.MaxValue) d = 9999;
                tour += d;
                value += sites[i].Value;
                mass += sites[i].MassKg;
                analysis += sites[i].AnalysisWh;
                prev = i + 1;
            }
            float back = RefDist(prev, out var refuge);
            if (back == float.MaxValue) back = 9999;
            tour += back;
            float energy = tour * whm + analysis;
            float fit = scienceWeight * value / totalValue - 0.35f * tour / (_cm.MapShape.N * WorldMap.CellM * 4)
                        - (mass > cap ? 1f + (mass - cap) : 0) - (energy > budget ? 1f + (energy - budget) / 40f : 0);
            return (fit, order, value, mass, energy, tour, refuge);
        }

        int popN = 30, gens = 30;
        var pop = new List<(float[] K, bool[] B, float F)>();
        for (int i = 0; i < popN; i++)
        {
            var k = Enumerable.Range(0, n).Select(_ => _rng.F()).ToArray();
            var b = Enumerable.Range(0, n).Select(_ => _rng.Chance(i == 0 ? 1f : 0.6f)).ToArray();
            pop.Add((k, b, Eval(k, b).fit));
        }
        for (int g = 0; g < gens; g++)
        {
            pop.Sort((a, b) => b.F.CompareTo(a.F));
            plan.BestHistory.Add(pop[0].F);
            plan.MeanHistory.Add(pop.Average(p => p.F));
            var next = pop.Take(2).ToList();
            while (next.Count < popN)
            {
                var a = pop[Math.Min(_rng.Int(0, popN), _rng.Int(0, popN))];
                var b = pop[Math.Min(_rng.Int(0, popN), _rng.Int(0, popN))];
                var k = new float[n];
                var bits = new bool[n];
                int cut = _rng.Int(0, n + 1);
                for (int i = 0; i < n; i++)
                {
                    k[i] = i < cut ? a.K[i] : b.K[i];
                    bits[i] = _rng.Chance(0.5f) ? a.B[i] : b.B[i];
                    if (_rng.Chance(0.12f)) bits[i] = !bits[i];
                    if (_rng.Chance(0.2f)) k[i] = MathX.Clamp01(k[i] + _rng.Gauss(0, 0.2f));
                }
                next.Add((k, bits, Eval(k, bits).fit));
            }
            pop = next;
            plan.Generations = g + 1;
        }
        pop.Sort((a, b) => b.F.CompareTo(a.F));
        var best = Eval(pop[0].K, pop[0].B);
        foreach (int i in best.order) plan.Order.Add(sites[i]);
        plan.FinalRefuge = best.refuge;
        plan.Value = best.value;
        plan.MassKg = best.mass;
        plan.EnergyWh = best.energy;
        plan.TourM = best.tour;
        plan.Fitness = best.fit;
        plan.MassOk = best.mass <= cap + 1e-3f;
        plan.EnergyOk = best.energy <= budget + 1e-3f;
        plan.Empty = plan.Order.Count == 0;
        for (int i = 0; i < n; i++)
        {
            bool inc = best.order.Contains(i);
            plan.Decisions.Add((sites[i].Name, inc, sites[i].Value, sites[i].MassKg,
                inc ? $"incluido (orden {best.order.IndexOf(i) + 1})" : "descartado: no compensa distancia/energía/masa"));
        }
        plan.Note = plan.Empty
            ? "El presupuesto de energía no permite muestrear: retorno directo al refugio."
            : $"{plan.Order.Count} sitios · valor {plan.Value:0.00} · {plan.MassKg:0.0}/{cap:0.0} kg · {plan.EnergyWh:0} Wh de {budget:0} Wh disponibles";
        plan.Ms = (float)sw.Elapsed.TotalMilliseconds;
        return plan;
    }
}
