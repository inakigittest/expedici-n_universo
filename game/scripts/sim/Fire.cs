using System;
using System.Collections.Generic;
using System.Linq;
using AresX.AI;
using AresX.Core;

namespace AresX.Sim;

/// <summary>Tarea de combate: descargar agua sobre un foco o ir a cargar agua.</summary>
public sealed class FireTask
{
    public string Name = "";
    public Cell Target;        // celda en llamas (o punto de agua)
    public Cell Access;        // donde se ubica el vehículo
    public bool Refill;
    public int Cluster;
}

/// <summary>Foco: componente conexa de celdas en llamas, con su prioridad difusa.</summary>
public sealed class FireCluster
{
    public int Id;
    public string Name = "";
    public readonly List<int> Cells = new();
    public Cell Front;          // celda más amenazante (a sotavento / más cerca de la ciudad)
    public float ThreatKm, AreaHa, Priority;
}

/// <summary>Prioridad difusa de un foco: amenaza (distancia a la zona urbana / base), tamaño y viento.</summary>
public static class FireFuzzy
{
    public static FuzzySystem Build()
    {
        var fs = new FuzzySystem();
        fs.Inputs["amenaza"] = new FuzzyVar { Id = "amenaza", Name = "Distancia a zona poblada", Unit = "km", Min = 0, Max = 6 }
            .Set("inminente", new Trap(0, 0, 0.3f, 0.9f)).Set("cercana", new Tri(0.5f, 1.5f, 2.8f)).Set("lejana", new Trap(2.2f, 3.5f, 6, 6));
        fs.Inputs["tamano"] = new FuzzyVar { Id = "tamano", Name = "Superficie en llamas", Unit = "ha", Min = 0, Max = 40 }
            .Set("pequeno", new Trap(0, 0, 1, 4)).Set("medio", new Tri(2, 8, 16)).Set("grande", new Trap(12, 22, 40, 40));
        fs.Inputs["viento"] = new FuzzyVar { Id = "viento", Name = "Viento", Unit = "m/s", Min = 0, Max = 20 }
            .Set("calma", new Trap(0, 0, 2, 5)).Set("moderado", new Tri(3, 7, 11)).Set("fuerte", new Trap(9, 13, 20, 20));
        fs.Outputs["prioridad"] = new FuzzyVar { Id = "prioridad", Name = "Prioridad", Min = 0, Max = 1 }
            .Set("baja", new Trap(0, 0, 0.2f, 0.4f)).Set("media", new Tri(0.3f, 0.5f, 0.7f)).Set("alta", new Trap(0.6f, 0.8f, 1, 1));
        fs.FallbackValue["prioridad"] = 0.5f;
        void R(string id, string why, (string, string)[] c, params (string, string)[] t) => fs.Rules.Add(new FuzzyRule { Id = id, If = c, Then = t, Justification = why });
        R("F1", "Fuego a pasos de casas: máxima prioridad (vidas y viviendas).", new[] { ("amenaza", "inminente") }, ("prioridad", "alta"));
        R("F2", "Foco pequeño: atacarlo ahora es lo más eficiente (se apaga con pocas descargas).", new[] { ("tamano", "pequeno") }, ("prioridad", "alta"));
        R("F3", "Cercano a la ciudad con viento moderado o fuerte: llegará pronto.", new[] { ("amenaza", "cercana"), ("viento", "fuerte") }, ("prioridad", "alta"));
        R("F4", "Cercano y en calma: prioridad intermedia.", new[] { ("amenaza", "cercana"), ("viento", "calma") }, ("prioridad", "media"));
        R("F5", "Grande y lejano: contener los flancos, sin desatender lo urgente.", new[] { ("tamano", "grande"), ("amenaza", "lejana") }, ("prioridad", "media"));
        R("F6", "Lejano y en calma: puede esperar.", new[] { ("amenaza", "lejana"), ("viento", "calma") }, ("prioridad", "baja"));
        return fs;
    }
}

/// <summary>
/// Misión de COMBATE CONTRA INCENDIOS (helicópteros + carros de bomberos). Autómata celular de propagación:
///   * combustible por cobertura (bosque 1, matorral 0,85, pastizal 0,7, cultivo 0,5, urbano 0,45 — casas —,
///     suelo desnudo 0,15; roca, agua, nieve y lo ya quemado no arden);
///   * tasa de contagio a cada vecina = base × combustible × intensidad × viento (exp. del coseno con la dirección
///     del viento) × pendiente (sube más rápido ladera arriba) × lluvia; celdas mojadas no se encienden un tiempo;
///   * cada celda arde 15–35 min según su combustible y queda quemada.
/// Helicóptero: balde de 1 000 L, apaga y moja 3×3 celdas, recarga en agua (90 s) o en la base (180 s).
/// Carro: 6 bomberos con mangueras hasta 60 m del carro, ~400 L por celda, estanque de 3 000 L (recarga en el cuartel).
/// Éxito: sin celdas en llamas. Fracaso: más del 30 % del combustible quemado o tiempo agotado. Todos los
/// parámetros son SUPUESTOS pedagógicos.
/// </summary>
public sealed partial class Mission
{
    public bool FireMode => Config.Mode == MissionMode.Incendio;
    public byte[] FireState = Array.Empty<byte>();     // 0 nada · 1 en llamas · 2 quemada · 3 mojada/apagada
    public float[] FireHeat = Array.Empty<float>();    // en llamas: intensidad 0..1 · mojada: segundos de humedad
    public float[] FireLeft = Array.Empty<float>();    // segundos de combustión restantes
    public float[] Fuel = Array.Empty<float>();
    public readonly List<int> Burning = new();
    public readonly List<FireCluster> Fires = new();
    public readonly List<Cell> WaterSources = new();
    public int FireVersion, FireDrops, BuildingsLost, StartBurning;
    public float BurnedHa, FuelHa;
    public float MainWaterL = 1000f;
    public string FireNote = "";
    private readonly FuzzySystem _fireFuzzy = FireFuzzy.Build();
    public FuzzyResult? LastFirePriority;
    public System.Collections.Generic.IReadOnlyList<FuzzyRule> FireRules => _fireFuzzy.Rules;
    private Rng _rngFire = null!;
    private float[]? _urbanDist;
    private int _firePlanStep = -99;
    private float _fireT0;
    private string _lastFireRadio = "";
    private const float HoseLPerCell = 400f;

    public static float FuelOf(Soil s) => s switch
    {
        Soil.Bosque => 1f, Soil.Matorral => 0.85f, Soil.Pastizal => 0.7f, Soil.Cultivo => 0.5f, Soil.Urbano => 0.35f,
        Soil.SueloDesnudo => 0.15f, Soil.Regolito => 0.1f, _ => 0f,
    };

    private void InitFire(ulong seed)
    {
        if (!FireMode) return;
        _rngFire = new Rng(seed * 31 + 9001);
        Map.Sites.Clear();
        int n = Map.N;
        FireState = new byte[n * n];
        FireHeat = new float[n * n];
        FireLeft = new float[n * n];
        Fuel = new float[n * n];
        for (int i = 0; i < n * n; i++)
        {
            var c = new Cell(i % n, i / n);
            if (!Map.InBounds(c) || Map.Hazards[i] == Hazard.Water) continue;
            float f = FuelOf(Map.Soils[i]);
            if (Map.Hazards[i] == Hazard.Tree) f = MathF.Max(f, 0.9f);
            if (Map.Hazards[i] is Hazard.Rock or Hazard.Cliff) f *= 0.3f;
            Fuel[i] = f;
            if (f > 0.05f) FuelHa += Map.CellM * Map.CellM / 10000f;
        }
        for (int i = 0; i < n * n; i++) if (Map.Hazards[i] == Hazard.Water && Map.InBounds(new Cell(i % n, i / n))) WaterSources.Add(new Cell(i % n, i / n));
        // distancia (km) a lo que hay que proteger: celdas urbanas y la base
        _urbanDist = UrbanDistanceKm();
        Medical.Clear();
        // focos iniciales: puntos elegidos por el operador o al azar en vegetación (preferentemente cerca de casas)
        var pts = Config.Points.Where(p => Map.InBounds(p)).ToList();
        if (pts.Count == 0)
        {
            var veg = Enumerable.Range(0, n * n).Where(i => Fuel[i] >= 0.6f).ToList();
            var near = veg.Where(i => _urbanDist[i] is > 0.5f and < 3f).ToList();
            var pool = near.Count > 20 ? near : veg;
            int count = 2 + Config.Difficulty;
            int spacing = Math.Max(10, n / 10);
            for (int tries = 0; pts.Count < count && tries < 2000 && pool.Count > 0; tries++)
            {
                int i = pool[_rngFire.Int(0, pool.Count)];
                var c = new Cell(i % n, i / n);
                if (c.Dist(Map.Start) < spacing || pts.Any(p => p.Dist(c) < spacing)) continue;
                pts.Add(c);
            }
        }
        foreach (var p in pts)
        {
            // el foco prende en la celda combustible más cercana al punto
            int best = -1;
            for (int r = 0; r <= 4 && best < 0; r++)
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                var o = new Cell(p.X + dx, p.Y + dy);
                if (Map.InBounds(o) && Fuel[Map.Idx(o)] > 0.2f && best < 0) best = Map.Idx(o);
            }
            if (best >= 0) Ignite(best, 0.6f);
        }
        // propagación previa (el aviso llega con el fuego ya declarado: ~12 min)
        for (int k = 0; k < 4; k++) SpreadFire(120f);
        StartBurning = Burning.Count;
        _fireT0 = Env.TimeS;
        UpdateFireClusters();
        PlaceWaterTank();
        AddLog("evento", $"INCENDIO: {Fires.Count} foco(s) declarados ({string.Join(", ", Fires.Select(f => $"{f.Name} {f.AreaHa:0.0} ha a {f.ThreatKm:0.0} km de zona poblada"))}) · " +
                         $"viento {Env.WindSpeed:0} m/s del {WindFromName()} · fuentes de agua: {(WaterSources.Count > 0 ? $"{WaterSources.Count} celdas de agua" : "solo la base")}");
        _needMissionPlan = true;
    }

    public Cell? WaterTank;

    /// <summary>
    /// Estanque autosoportante (como los de las brigadas forestales): en terreno plano y despejado a ~1 km a barlovento
    /// del foco principal, para que los helicópteros carguen el balde cerca en vez de volver a la base.
    /// </summary>
    private void PlaceWaterTank()
    {
        if (Fires.Count == 0) return;
        var f = Fires[0];
        int n = Map.N;
        float cx = (float)f.Cells.Average(i => i % n), cy = (float)f.Cells.Average(i => i / n);
        float windRad = Env.WindDirDeg * MathF.PI / 180f;
        float ux = MathF.Sin(windRad), uy = -MathF.Cos(windRad);   // hacia donde viene el viento (barlovento)
        int dist = Math.Max(8, (int)(1000f / Map.CellM));
        Cell? best = null;
        float bs = float.MaxValue;
        for (int dy = -dist * 2; dy <= dist * 2; dy++)
        for (int dx = -dist * 2; dx <= dist * 2; dx++)
        {
            var c = new Cell((int)cx + dx, (int)cy + dy);
            if (!Map.InBounds(c)) continue;
            int i = Map.Idx(c);
            if (Map.Hazards[i] != Hazard.None || Map.Slope[i] > 8f || FireState[i] != 0 || Fuel[i] > 0.6f) continue;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            float s = MathF.Abs(d - dist) + 0.6f * dist * (1f - (dx * ux + dy * uy) / MathF.Max(1, d));
            if (s < bs) { bs = s; best = c; }
        }
        if (best is not Cell b) return;
        WaterTank = b;
        WaterSources.Add(b);
        AddLog("info", $"Estanque portátil de agua instalado en {b} (a {FlightM(b, f.Front) / 1000:0.0} km de {f.Name}, a barlovento): los helicópteros cargan ahí el balde");
    }

    private string WindFromName()
    {
        string[] n = { "N", "NE", "E", "SE", "S", "SO", "O", "NO" };
        return n[(int)MathF.Round(((Env.WindDirDeg % 360) + 360) % 360 / 45f) % 8];
    }

    private float[] UrbanDistanceKm()
    {
        int n = Map.N;
        var d = new float[n * n];
        Array.Fill(d, float.MaxValue);
        var q = new Queue<int>();
        for (int i = 0; i < n * n; i++)
            if (Map.Soils[i] == Soil.Urbano || Map.Hazards[i] == Hazard.Building || i == Map.Idx(Map.Start)) { d[i] = 0; q.Enqueue(i); }
        while (q.Count > 0)
        {
            int i = q.Dequeue();
            int x = i % n, y = i / n;
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
                int j = ny * n + nx;
                if (d[j] > d[i] + 1) { d[j] = d[i] + 1; q.Enqueue(j); }
            }
        }
        for (int i = 0; i < n * n; i++) d[i] = d[i] == float.MaxValue ? 99f : d[i] * Map.CellM / 1000f;
        return d;
    }

    private void Ignite(int i, float heat)
    {
        if (FireState[i] == 1 || Fuel[i] <= 0.05f) return;
        FireState[i] = 1;
        FireHeat[i] = heat;
        FireLeft[i] = (10f + 15f * Fuel[i]) * 60f * _rngFire.Range(0.8f, 1.2f);
        Burning.Add(i);
        FireVersion++;
    }

    // ------------------------------------------------------------------ propagación
    private void UpdateFire(float dt)
    {
        if (!FireMode) return;
        // pasos largos: subdividir para que la propagación no salte
        int sub = Math.Max(1, (int)MathF.Ceiling(dt / 60f));
        for (int k = 0; k < sub; k++) SpreadFire(dt / sub);
        // humedad que se seca
        UpdateFireClusters();
    }

    private void SpreadFire(float dt)
    {
        int n = Map.N;
        float windRad = Env.WindDirDeg * MathF.PI / 180f;
        // viento "desde" WindDirDeg (convención meteorológica): sopla hacia el lado opuesto
        float wx = -MathF.Sin(windRad), wy = MathF.Cos(windRad);
        float ws = Env.WindSpeed;
        float rain = Planet.HasRain ? MathF.Exp(-Env.RainMmH / 3f) : 1f;
        var newly = new List<int>();
        var done = new List<int>();
        foreach (int i in Burning)
        {
            FireHeat[i] = MathF.Min(1f, FireHeat[i] + dt / 300f);
            FireLeft[i] -= dt * (1f + 0.5f * (1 - rain));
            if (FireLeft[i] <= 0) { done.Add(i); continue; }
            int x = i % n, y = i / n;
            float hi = Map.H[i];
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
                int j = ny * n + nx;
                if (FireState[j] != 0 || Fuel[j] <= 0.05f) continue;
                float len = dx != 0 && dy != 0 ? 1.41421f : 1f;
                float cosW = (dx * wx + dy * wy) / len;
                float wind = MathF.Exp(0.12f * ws * cosW);
                float rise = (Map.H[j] - hi) / (len * Map.CellM);
                float slope = MathF.Exp(Math.Clamp(rise, -0.6f, 0.8f) * 2.2f);
                float rate = Fuel[j] * FireHeat[i] * wind * slope * rain / (600f * len) * (30f / MathF.Max(10f, Map.CellM));
                if (_rngFire.F() < 1f - MathF.Exp(-rate * dt)) newly.Add(j);
            }
        }
        foreach (int i in done)
        {
            FireState[i] = 2;
            FireHeat[i] = 0;
            BurnedHa += Map.CellM * Map.CellM / 10000f;
            if (Map.Soils[i] == Soil.Urbano || Map.Hazards[i] == Hazard.Building)
            {
                BuildingsLost += Map.Hazards[i] == Hazard.Building ? 2 : 1;
                Map.Soils[i] = Soil.Quemado;
            }
            else Map.Soils[i] = Soil.Quemado;
        }
        if (done.Count > 0) { Burning.RemoveAll(i => FireState[i] != 1); FireVersion++; }
        foreach (int j in newly.Distinct()) Ignite(j, 0.25f);
        // celdas mojadas se secan (2 h)
        for (int i = 0; i < FireState.Length; i++)
            if (FireState[i] == 3 && (FireHeat[i] -= dt) <= 0) { FireState[i] = 0; FireHeat[i] = 0; }
    }

    private void UpdateFireClusters()
    {
        Fires.Clear();
        int n = Map.N;
        var seen = new HashSet<int>();
        float windRad = Env.WindDirDeg * MathF.PI / 180f;
        float wx = -MathF.Sin(windRad), wy = MathF.Cos(windRad);
        foreach (int s in Burning)
        {
            if (!seen.Add(s)) continue;
            var cl = new FireCluster { Id = Fires.Count };
            var q = new Queue<int>();
            q.Enqueue(s);
            while (q.Count > 0)
            {
                int i = q.Dequeue();
                cl.Cells.Add(i);
                int x = i % n, y = i / n;
                for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
                    int j = ny * n + nx;
                    if (FireState[j] == 1 && seen.Add(j)) q.Enqueue(j);
                }
            }
            // frente: la celda más amenazante (cerca de lo poblado y a sotavento)
            int front = cl.Cells.OrderBy(i => (_urbanDist?[i] ?? 0) - 0.05f * ((i % n) * wx + (i / n) * wy)).First();
            cl.Front = new Cell(front % n, front / n);
            cl.ThreatKm = cl.Cells.Min(i => _urbanDist?[i] ?? 9);
            cl.AreaHa = cl.Cells.Count * Map.CellM * Map.CellM / 10000f;
            LastFirePriority = _fireFuzzy.Infer(new Dictionary<string, float>
            {
                ["amenaza"] = MathF.Min(6, cl.ThreatKm), ["tamano"] = MathF.Min(40, cl.AreaHa), ["viento"] = MathF.Min(20, Env.WindSpeed),
            });
            cl.Priority = LastFirePriority.Crisp["prioridad"];
            Fires.Add(cl);
        }
        Fires.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        for (int k = 0; k < Fires.Count; k++) Fires[k].Name = $"F{k + 1}";
    }

    // ------------------------------------------------------------------ asignación de vehículos a focos
    /// <summary>Reparte la flota: cada vehículo va al foco de mayor prioridad / (1 + ETA), sin amontonarse más de lo que el foco necesita.</summary>
    private void PlanFire()
    {
        _firePlanStep = StepCount;
        if (Fires.Count == 0) { SetTarget(Map.Start, "base (sin fuego activo)"); return; }
        var load = new int[Fires.Count];
        int Need(FireCluster f) => Math.Max(1, (int)MathF.Ceiling(f.Cells.Count / 12f));
        // H-1 (IA híbrida)
        var parts = new List<string>();
        var oldTarget = Target;
        // tareas vigentes se mantienen (evita que los vehículos oscilen entre focos con cada reasignación)
        foreach (var u in Fleet)
            if (u.Goal is FireTask cur && StillValid(u, cur) && FireClusterOf(cur.Target) is int ck) load[ck]++;
        if (_goingRefuge || Charging) parts.Add("H-1 → base (energía)");
        else if (MainWaterL <= 0)
        {
            bool goingWater = WaterSources.Contains(Target) || Target == Map.Start && TargetName.Contains("agua");
            if (!goingWater) { var w = RefillPoint(Rover.Pos, true); SetTarget(w.Cell, w.Name); }
            parts.Add($"H-1 → {TargetName}");
        }
        else
        {
            int? keep = TargetName.StartsWith("foco") && BurningNear(Target, 1) ? FireClusterOf(Target) : null;
            int k = keep ?? BestFire(Rover.Pos, true, load, Need);
            load[k]++;
            var f = Fires[k];
            if (keep == null) SetTarget(f.Front, $"foco {f.Name} ({f.AreaHa:0.0} ha, prioridad {f.Priority:0.00})");
            parts.Add($"H-1 → {f.Name}");
        }
        foreach (var u in Fleet)
        {
            if (u.Busy != "") continue;
            if (u.Goal is FireTask cur && StillValid(u, cur)) { parts.Add($"{u.Name} → {cur.Name}"); continue; }
            u.Queue.Clear();
            if (u.WaterL < (u.Heli ? 1f : HoseLPerCell))
            {
                var w = RefillPoint(u.Pos, u.Heli);
                u.Queue.Add(new FireTask { Name = w.Name, Target = w.Cell, Access = w.Cell, Refill = true });
                parts.Add($"{u.Name} → {w.Name}");
            }
            else
            {
                float[]? tf = u.Heli ? null : TruckField(u.Pos);   // carros: solo focos alcanzables por su red de calles
                int k = BestFire(u.Pos, u.Heli, load, Need, tf);
                if (k < 0) { parts.Add($"{u.Name} sin foco accesible"); u.Goal = null; u.Path.Clear(); continue; }
                load[k]++;
                var f = Fires[k];
                var (tgt, acc) = u.Heli ? (f.Front, f.Front) : TruckFireAccess(f, tf) ?? (f.Front, f.Front);
                u.Queue.Add(new FireTask { Name = f.Name, Target = tgt, Access = acc, Cluster = f.Id });
                parts.Add($"{u.Name} → {f.Name}");
            }
            u.Goal = null;
            u.Path.Clear();
        }
        _needRoute = Target != oldTarget || Route.Count < 2;
        FireNote = $"Focos por prioridad difusa: {string.Join(", ", Fires.Take(5).Select(f => $"{f.Name} {f.Priority:0.00}"))} · asignación: {string.Join(" · ", parts)}";
        string msg = string.Join(" · ", parts);
        if (msg != _lastFireRadio)
        {
            _lastFireRadio = msg;
            if (Fleet.Count > 0) Radio(0, -1, msg);
            else AddLog("ag", FireNote);
        }
    }

    private bool BurningNear(Cell c, int r)
    {
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            var o = new Cell(c.X + dx, c.Y + dy);
            if (Map.InGrid(o) && FireState[Map.Idx(o)] == 1) return true;
        }
        return false;
    }

    /// <summary>Índice del foco al que pertenece (o está junto) una celda.</summary>
    private int? FireClusterOf(Cell c)
    {
        int n = Map.N;
        for (int k = 0; k < Fires.Count; k++)
            if (Fires[k].Cells.Any(i => Math.Max(Math.Abs(i % n - c.X), Math.Abs(i / n - c.Y)) <= 2)) return k;
        return null;
    }

    private bool StillValid(FleetUnit u, FireTask t) => t.Refill ? u.WaterL < u.WaterCapL : u.WaterL >= (u.Heli ? 1f : HoseLPerCell) && BurningNear(t.Target, 2);

    private int BestFire(Cell from, bool heli, int[] load, Func<FireCluster, int> need, float[]? truckField = null)
    {
        int best = -1;
        float bs = float.MinValue;
        float speed = heli ? HeliModel.CruiseMs * 0.85f : 9f;
        for (int k = 0; k < Fires.Count; k++)
        {
            var f = Fires[k];
            if (!heli && TruckFireAccess(f, truckField) == null) continue;
            float etaMin = FlightM(from, f.Front) / speed / 60f * (heli ? 1f : 1.4f);
            float crowd = load[k] >= need(f) ? 0.35f : 1f;
            float s = f.Priority * crowd / (1f + etaMin / 8f);
            if (s > bs) { bs = s; best = k; }
        }
        return heli && best < 0 ? 0 : best;
    }

    /// <summary>Para un carro: celda en llamas del foco más cercana a la red de calles, alcanzable por las mangueras (≤ 60 m).</summary>
    private (Cell Target, Cell Access)? TruckFireAccess(FireCluster f, float[]? field = null)
    {
        if (TruckPass == null) return null;
        int n = Map.N;
        foreach (int i in f.Cells.OrderBy(i => _urbanDist?[i] ?? 9))
        {
            var c = new Cell(i % n, i / n);
            if (TruckAccess(c) is Cell a && (field == null || field[Map.Idx(a)] < float.MaxValue)) return (c, a);
        }
        return null;
    }

    private (Cell Cell, string Name) RefillPoint(Cell from, bool heli)
    {
        if (heli && WaterSources.Count > 0)
        {
            var w = WaterSources.OrderBy(c => c.Dist(from)).First();
            if (FlightM(from, w) < FlightM(from, Map.Start)) return (w, w == WaterTank ? "estanque portátil" : "agua (balde)");
        }
        // carros: grifo de la red urbana en la misma calle (o el cuartel si están fuera de la red)
        if (!heli && TruckPass != null && Map.InGrid(from) && TruckPass[Map.Idx(from)]) return (from, "grifo");
        return heli ? (Map.Start, "base (agua)") : (FireStation, "cuartel (agua)");
    }

    private bool FireTaskValid(FleetUnit u, FireTask ft) => ft.Refill || FireState.Length > 0 && Burning.Count > 0 && u.WaterL >= (u.Heli ? 1f : HoseLPerCell);

    // ------------------------------------------------------------------ descargas y mangueras
    private void StartFireAction(FleetUnit u, FireTask ft)
    {
        if (ft.Refill)
        {
            bool lake = WaterSources.Contains(ft.Target);
            u.BusyS = u.Heli ? (lake ? 90f : 180f) : 120f;
            u.Busy = "carga";
            u.AglM = u.Heli && lake ? 10f : 0;
            u.Status = u.Heli ? (lake ? "cargando el balde" : "cargando agua en la base") : "llenando el estanque desde un grifo";
            return;
        }
        if (u.Heli)
        {
            u.BusyS = 30f;
            u.Busy = "agua";
            u.AglM = 30f;
            u.Status = $"descarga sobre {ft.Name}";
            u.ActionSerial++;
        }
        else
        {
            u.BusyS = 45f;
            u.Busy = "agua";
            u.Status = $"6 bomberos con mangueras en {ft.Name}";
            u.ActionSerial++;
        }
    }

    private void CompleteFireAction(FleetUnit u, FireTask ft)
    {
        if (ft.Refill)
        {
            u.WaterL = u.WaterCapL;
            _needMissionPlan = true;
            return;
        }
        if (u.Heli)
        {
            int put = Douse(ft.Target, 1, 99, 2);
            u.WaterL = 0;
            u.Drops++;
            Radio(u.Id, 0, $"descarga sobre {ft.Name}: {put} celdas apagadas · voy a cargar agua");
        }
        else
        {
            int maxCells = (int)(u.WaterL / HoseLPerCell);
            int put = Douse(ft.Access, Math.Max(1, (int)MathF.Ceiling(60f / Map.CellM)), Math.Min(4, maxCells));
            u.WaterL = MathF.Max(0, u.WaterL - MathF.Max(1, put) * HoseLPerCell);
            u.Drops++;
            if (put > 0 && u.WaterL >= HoseLPerCell && Burning.Any(i => new Cell(i % Map.N, i / Map.N).Dist(ft.Access) <= 2))
            {
                // sigue mojando mientras haya llamas al alcance de las mangueras
                u.Queue.Insert(1, new FireTask { Name = ft.Name, Target = ft.Target, Access = ft.Access, Cluster = ft.Cluster });
            }
            else
            {
                if (put > 0) Radio(u.Id, 0, $"{ft.Name}: {put} celdas apagadas con mangueras · estanque {u.WaterL:0} L");
                _needMissionPlan = true;
            }
            return;
        }
        _needMissionPlan = true;
    }

    /// <summary>Apaga y moja las celdas en radio r alrededor de c (máximo maxBurning celdas en llamas). Devuelve las apagadas.</summary>
    private int Douse(Cell c, int r, int maxBurning, int wetR = -1)
    {
        int n = Map.N, put = 0;
        var cells = new List<int>();
        // anillo exterior: solo moja la vegetación aún sin fuego (cortafuego)
        for (int dy = -wetR; dy <= wetR; dy++)
        for (int dx = -wetR; dx <= wetR; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) <= r) continue;
            var o = new Cell(c.X + dx, c.Y + dy);
            if (Map.InGrid(o) && FireState[Map.Idx(o)] == 0) { FireState[Map.Idx(o)] = 3; FireHeat[Map.Idx(o)] = 5400f; }
        }
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            var o = new Cell(c.X + dx, c.Y + dy);
            if (Map.InGrid(o)) cells.Add(Map.Idx(o));
        }
        foreach (int i in cells.OrderBy(i => FireState[i] == 1 ? 0 : 1).ThenBy(i => new Cell(i % n, i / n).Dist(c)))
        {
            if (FireState[i] == 1)
            {
                if (put >= maxBurning) continue;
                put++;
            }
            if (FireState[i] is 0 or 1) { FireState[i] = 3; FireHeat[i] = 7200f; }
        }
        Burning.RemoveAll(i => FireState[i] != 1);
        FireVersion++;
        return put;
    }

    /// <summary>H-1 sobre el foco: descarga; en el punto de agua: carga.</summary>
    private float DoFireAction()
    {
        PendingSample = false;
        bool atWater = WaterSources.Contains(Rover.Pos) || (Rover.Pos == Map.Start && MainWaterL <= 0);
        if (MainWaterL <= 0 || atWater && MainWaterL < 1000f)
        {
            if (!atWater) { _needMissionPlan = true; return 20f; }
            bool lake = WaterSources.Contains(Rover.Pos);
            MainWaterL = 1000f;
            AddLog("ok", $"H-1: balde cargado ({(lake ? "agua superficial" : "base")})");
            HeliAglM = lake ? 10f : 0;
            _needMissionPlan = true;
            return lake ? 90f : 180f;
        }
        int put = Douse(Rover.Pos, 1, 99, 2);
        MainWaterL = 0;
        FireDrops++;
        HeliAglM = 30f;
        AddLog("ok", $"H-1: descarga de 1 000 L sobre {TargetName}: {put} celdas apagadas · a cargar agua");
        _needMissionPlan = true;
        return 30f;
    }

    private bool FireOver(out bool ok, out string why)
    {
        ok = false;
        why = "";
        if (!FireMode || StepCount < 2) return false;
        if (Burning.Count == 0)
        {
            ok = true;
            why = $"incendio controlado: {BurnedHa:0.0} ha quemadas ({100 * BurnedHa / MathF.Max(0.1f, FuelHa):0.0} % del combustible), {BuildingsLost} viviendas perdidas, {FireDrops + Fleet.Sum(u => u.Drops)} descargas";
            return true;
        }
        if (Env.TimeS - _fireT0 > 8 * 3600f)
        {
            why = $"el incendio no se controló en 8 h ({BurnedHa:0} ha quemadas, {Burning.Count * Map.CellM * Map.CellM / 10000f:0.0} ha aún en llamas)";
            return true;
        }
        if (BurnedHa > 0.3f * FuelHa)
        {
            why = $"el incendio consumió más del 30 % de la vegetación ({BurnedHa:0} ha)";
            return true;
        }
        return false;
    }
}
