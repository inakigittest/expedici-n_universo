using System;
using System.Collections.Generic;
using System.Linq;
using AresX.AI;
using AresX.Core;

namespace AresX.Sim;

public enum UnitKind { Helicoptero, Bomberos }

/// <summary>
/// Vehículo de apoyo de la flota (el helicóptero principal H-1 es el que usa toda la IA híbrida: RNA + difuso + AG +
/// supervisor). Los de apoyo reciben tareas del AG multivehículo y navegan por su cuenta:
///   * HELICÓPTERO de apoyo: vuela en línea recta a 90 m sobre el terreno, con las mismas limitaciones de lluvia,
///     ráfagas, visibilidad y margen de potencia por densidad del aire; capacidad 4 personas.
///   * CARRO DE BOMBEROS (6 bomberos): solo por calles y zona urbana (pendiente &lt; 12°). Los bomberos bajan y caminan
///     hasta 60 m desde el carro: rescatan personas (capacidad 3, hacia el hospital) o combaten el fuego con mangueras
///     (estanque de 3 000 L).
/// Avanzan en tiempo simulado continuo dentro de cada paso de la misión.
/// </summary>
public sealed class FleetUnit
{
    public int Id;                     // 1.. (0 = helicóptero principal)
    public string Name = "";
    public UnitKind Kind;
    public Cell Pos, Dir = new(1, 0), Home;
    public float SpeedMs, MoveCarryM;
    public readonly List<Cell> Path = new();
    public int PathIdx;
    public readonly List<object> Queue = new();   // Victim · MedicalPoint · FireTask
    public object? Goal;
    public Cell GoalCell;
    public readonly List<Victim> Aboard = new();
    public int Capacity;
    public float BusyS;
    public string Busy = "";           // "rescate" · "entrega" · "agua" · "carga"
    public string Status = "en base";
    public float AglM;
    public bool Grounded;
    public float WaterL, WaterCapL;
    public float DistanceM;
    public int Rescued, Delivered, Drops;
    public int Crew;                   // bomberos a bordo
    public int ActionSerial;           // sube al empezar una acción con tripulación (la vista lanza la salida)
    public bool Heli => Kind == UnitKind.Helicoptero;
    public string KindName => Heli ? "helicóptero" : "carro de bomberos";
}

public sealed partial class Mission
{
    public readonly List<FleetUnit> Fleet = new();
    public bool[]? TruckPass;                         // celdas por donde puede ir un carro de bomberos
    public Cell FireStation;
    /// <summary>Mensajes de radio recientes entre vehículos (la vista dibuja la línea): (de, a, paso).</summary>
    public readonly List<(int From, int To, int Step)> Comms = new();
    public int FleetSize => 1 + Fleet.Count;
    public static string UnitName(int id, Mission m) => id == 0 ? "H-1" : id < 0 ? "flota" : m.Fleet.FirstOrDefault(u => u.Id == id)?.Name ?? "?";

    /// <summary>Punto de partida elegido en el mapa: la base se mueve a la celda despejada más cercana.</summary>
    private void ApplyStartCell(Cell? want)
    {
        if (want is not Cell w || !Map.InBounds(w)) return;
        Cell? best = null;
        for (int r = 0; r <= 8 && best == null; r++)
        for (int dy = -r; dy <= r && best == null; dy++)
        for (int dx = -r; dx <= r && best == null; dx++)
        {
            var o = new Cell(w.X + dx, w.Y + dy);
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && Map.InBounds(o) && Map.Hazards[Map.Idx(o)] is Hazard.None or Hazard.Tree or Hazard.Debris && Map.Slope[Map.Idx(o)] < 20f)
                best = o;
        }
        if (best is not Cell b) return;
        Map.Hazards[Map.Idx(b)] = Hazard.None;
        int k = Map.Refuges.IndexOf(Map.Start);
        Map.Start = b;
        if (k >= 0) Map.Refuges[k] = b; else Map.Refuges.Insert(0, b);
        Map.Version++;
    }

    // ------------------------------------------------------------------ preparación
    private void InitFleet()
    {
        // flota simplificada: el H-1 (IA híbrida completa) y, si hay zona urbana, un carro de bomberos (el código de
        // helicópteros de apoyo se conserva, pero la misión usa solo uno)
        int helis = 1, trucks = Math.Clamp(Config.Trucks, 0, 1);
        if (!Heli || !(Rescue || FireMode)) return;
        int id = 1;
        for (int i = 2; i <= helis; i++)
        {
            var home = NearestFree(new Cell(Map.Start.X + (i - 1) * 2, Map.Start.Y), 6) ?? Map.Start;
            Fleet.Add(new FleetUnit
            {
                Id = id++, Name = $"H-{i}", Kind = UnitKind.Helicoptero, Pos = home, Home = home, SpeedMs = HeliModel.CruiseMs * 0.85f, Capacity = 4,
                WaterCapL = 1000f, WaterL = 1000f,
            });
        }
        if (trucks > 0)
        {
            BuildTruckNetwork();
            if (TruckPass == null)
                AddLog("alerta", "Carros de bomberos: el escenario no tiene zona urbana con calles (restricción de los carros): no se despliegan");
            else
                for (int i = 1; i <= trucks; i++)
                {
                    var home = NearestTruckCell(FireStation, 8) ?? FireStation;
                    Fleet.Add(new FleetUnit
                    {
                        Id = id++, Name = $"B-{i}", Kind = UnitKind.Bomberos, Pos = home, Home = home, SpeedMs = 9f, Capacity = 3, Crew = 6,
                        WaterCapL = 3000f, WaterL = 3000f,
                    });
                }
        }
        if (Fleet.Count > 0)
            AddLog("info", $"FLOTA: H-1 (líder, IA híbrida completa){string.Concat(Fleet.Select(u => $" · {u.Name} ({u.KindName})"))}" +
                           (Fleet.Any(u => !u.Heli) ? $" · cuartel de bomberos en {FireStation}" : ""));
    }

    /// <summary>Red de los carros: calles de la ciudad simulada y celdas urbanas despejadas con pendiente &lt; 12°.</summary>
    private void BuildTruckNetwork()
    {
        var city = Map.City;
        if (city == null || city.Street.Length != Map.N * Map.N || !city.Street.Any(s => s)) return;
        int n = Map.N;
        var pass = new bool[n * n];
        for (int i = 0; i < n * n; i++)
        {
            var c = new Cell(i % n, i / n);
            if (!Map.InBounds(c) || Map.Slope[i] >= 12f) continue;
            if (city.Street[i] || (Map.Soils[i] == Soil.Urbano && Map.Hazards[i] == Hazard.None)) pass[i] = true;
        }
        // cuartel: celda de calle más cercana al primer hospital (o al centro de la ciudad)
        Cell anchor;
        if (city.Hospitals.Count > 0)
        {
            var b = city.Buildings[city.Hospitals[0]];
            anchor = new Cell((int)(b.X / Map.CellM), (int)(b.Z / Map.CellM));
        }
        else
        {
            var streets = Enumerable.Range(0, n * n).Where(i => pass[i]).ToList();
            anchor = new Cell((int)streets.Average(i => i % n), (int)streets.Average(i => i / n));
        }
        TruckPass = pass;
        FireStation = NearestTruckCell(anchor, 40) ?? anchor;
    }

    private Cell? NearestTruckCell(Cell c, int maxR)
    {
        if (TruckPass == null) return null;
        for (int r = 0; r <= maxR; r++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            var o = new Cell(c.X + dx, c.Y + dy);
            if (Map.InGrid(o) && TruckPass[Map.Idx(o)]) return o;
        }
        return null;
    }

    /// <summary>Celda de la red de calles desde la que los bomberos llegan a pie (≤ 2 celdas = 60 m) a un punto.</summary>
    public Cell? TruckAccess(Cell target) => TruckPass == null ? null : NearestTruckCell(target, Math.Max(1, (int)MathF.Ceiling(60f / Map.CellM)));

    // ------------------------------------------------------------------ distancias por tipo de vehículo
    /// <summary>Dijkstra (m) sobre la red de calles desde una celda (float.MaxValue = sin acceso).</summary>
    public float[] TruckField(Cell from)
    {
        int n = Map.N;
        var d = new float[n * n];
        Array.Fill(d, float.MaxValue);
        if (TruckPass == null || !Map.InGrid(from) || !TruckPass[Map.Idx(from)]) return d;
        var pq = new PriorityQueue<int, float>();
        d[Map.Idx(from)] = 0;
        pq.Enqueue(Map.Idx(from), 0);
        float cm = Map.CellM, diag = cm * 1.41421f;
        while (pq.TryDequeue(out int i, out float di))
        {
            if (di > d[i]) continue;
            int x = i % n, y = i / n;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
                int j = ny * n + nx;
                if (!TruckPass[j]) continue;
                float nd = di + (dx != 0 && dy != 0 ? diag : cm);
                if (nd < d[j]) { d[j] = nd; pq.Enqueue(j, nd); }
            }
        }
        return d;
    }

    private List<Cell> TruckPath(Cell from, Cell to)
    {
        var f = TruckField(to);
        var path = new List<Cell> { from };
        if (f[Map.Idx(from)] == float.MaxValue) return path;
        var cur = from;
        for (int guard = 0; cur != to && guard < Map.N * 4; guard++)
        {
            Cell best = cur;
            float bd = f[Map.Idx(cur)];
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var o = new Cell(cur.X + dx, cur.Y + dy);
                if (Map.InGrid(o) && f[Map.Idx(o)] < bd) { bd = f[Map.Idx(o)]; best = o; }
            }
            if (best == cur) break;
            path.Add(best);
            cur = best;
        }
        return path;
    }

    /// <summary>Línea recta de celdas (vuelo del helicóptero de apoyo).</summary>
    private static List<Cell> LinePath(Cell a, Cell b)
    {
        var path = new List<Cell> { a };
        int dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y), sx = a.X < b.X ? 1 : -1, sy = a.Y < b.Y ? 1 : -1, err = dx - dy;
        int x = a.X, y = a.Y;
        while (x != b.X || y != b.Y)
        {
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x += sx; }
            if (e2 < dx) { err += dx; y += sy; }
            path.Add(new Cell(x, y));
        }
        return path;
    }

    public float FlightM(Cell a, Cell b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) * Map.CellM;

    /// <summary>Margen de potencia del helicóptero en una celda (densidad del aire por altura y temperatura).</summary>
    public float HeliMarginAt(Cell c) => HeliModel.Margin(HeliModel.Density(Env.PressureAt(c), Env.TempAt(c)));

    private bool FlightWeatherOk(out string why)
    {
        why = Env.RainMmH >= HeliModel.RainLimitMmH ? "lluvia intensa" : Env.WindGust >= HeliModel.GustLimitMs ? "ráfagas" : Env.Visibility < HeliModel.MinVisM ? "visibilidad" : "";
        return why == "";
    }

    // ------------------------------------------------------------------ comunicación
    private void Radio(int from, int to, string text)
    {
        AddLog("radio", $"📻 {UnitName(from, this)} → {UnitName(to, this)}: {text}");
        Comms.Add((from, to, StepCount));
        if (Comms.Count > 40) Comms.RemoveAt(0);
    }

    // ------------------------------------------------------------------ avance de la flota en un paso
    private void UpdateFleet(float dt)
    {
        if (Fleet.Count == 0) return;
        bool flyOk = FlightWeatherOk(out string why);
        foreach (var u in Fleet) StepUnit(u, dt, flyOk, why);
    }

    private void StepUnit(FleetUnit u, float dt, bool flyOk, string why)
    {
        float t = dt;
        for (int guard = 0; t > 1e-3f && guard < 400; guard++)
        {
            if (u.BusyS > 0)
            {
                float use = MathF.Min(t, u.BusyS);
                u.BusyS -= use;
                t -= use;
                if (u.BusyS <= 1e-3f) { u.BusyS = 0; CompleteAction(u); }
                continue;
            }
            if (u.Heli && !flyOk)
            {
                if (!u.Grounded) AddLog("alerta", $"{u.Name}: {why} sobre el límite de vuelo · aterriza y espera");
                u.Grounded = true;
                u.AglM = 0;
                u.Status = $"en tierra ({why})";
                return;
            }
            if (u.Grounded) { u.Grounded = false; AddLog("info", $"{u.Name}: condiciones de vuelo restablecidas · despega"); }
            if (u.Goal == null && !NextGoal(u))
            {
                // sin tareas: volver a la base / cuartel
                if (u.Pos == u.Home) { u.Status = u.Heli ? "en base" : "en el cuartel"; u.AglM = 0; return; }
                SetUnitGoal(u, null, u.Home);
                u.Status = "volviendo a " + (u.Heli ? "la base" : "el cuartel");
            }
            if (u.Pos == u.GoalCell) { StartAction(u); continue; }
            if (u.Path.Count == 0 || u.PathIdx >= u.Path.Count - 1 || u.Path[u.PathIdx] != u.Pos)
            {
                u.Path.Clear();
                u.Path.AddRange(u.Heli ? LinePath(u.Pos, u.GoalCell) : TruckPath(u.Pos, u.GoalCell));
                u.PathIdx = 0;
                if (u.Path.Count < 2)
                {
                    AddLog("alerta", $"{u.Name}: sin acceso a {u.GoalCell} · se replanifica la flota");
                    DropGoal(u);
                    _needMissionPlan = true;
                    return;
                }
            }
            var next = u.Path[u.PathIdx + 1];
            float seg = (next.X != u.Pos.X && next.Y != u.Pos.Y ? 1.41421f : 1f) * Map.CellM;
            float need = seg - u.MoveCarryM, can = u.SpeedMs * t;
            u.AglM = u.Heli ? HeliModel.CruiseAglM : 0;
            if (can >= need)
            {
                t -= need / u.SpeedMs;
                u.MoveCarryM = 0;
                u.Dir = next - u.Pos;
                u.Pos = next;
                u.PathIdx++;
                u.DistanceM += seg;
            }
            else
            {
                u.MoveCarryM += can;
                t = 0;
            }
        }
    }

    private void SetUnitGoal(FleetUnit u, object? goal, Cell cell)
    {
        u.Goal = goal;
        u.GoalCell = cell;
        u.Path.Clear();
        u.PathIdx = 0;
        u.MoveCarryM = 0;
    }

    private void DropGoal(FleetUnit u)
    {
        if (u.Goal != null) u.Queue.Remove(u.Goal);
        SetUnitGoal(u, null, u.Pos);
        u.Goal = null;
    }

    /// <summary>Toma la siguiente tarea vigente de la cola. false = no hay.</summary>
    private bool NextGoal(FleetUnit u)
    {
        while (u.Queue.Count > 0)
        {
            var g = u.Queue[0];
            switch (g)
            {
                case Victim v when v.Active && !v.OnBoard:
                {
                    var cell = u.Heli ? v.Cell : TruckAccess(v.Cell);
                    if (cell is Cell c)
                    {
                        SetUnitGoal(u, v, c);
                        u.Status = $"rumbo a {v.Name} ({Victim.KindName(v.Kind)})";
                        return true;
                    }
                    break;
                }
                case MedicalPoint mp when u.Aboard.Count > 0:
                {
                    var cell = u.Heli ? mp.Cell : TruckAccess(mp.Cell);
                    if (cell is Cell c)
                    {
                        SetUnitGoal(u, mp, c);
                        u.Status = $"rumbo a {mp.Name} con {u.Aboard.Count}";
                        return true;
                    }
                    break;
                }
                case FireTask ft when FireTaskValid(u, ft):
                    SetUnitGoal(u, ft, ft.Access);
                    u.Status = ft.Refill ? "a cargar agua" : $"rumbo al foco {ft.Name}";
                    return true;
            }
            u.Queue.RemoveAt(0);
        }
        // personas a bordo sin punto médico en la cola: al más cercano accesible
        if (u.Aboard.Count > 0)
        {
            var mp = Medical.Select(m => (m, c: u.Heli ? m.Cell : TruckAccess(m.Cell))).Where(x => x.c != null)
                            .OrderBy(x => FlightM(u.Pos, x.c!.Value)).Select(x => x.m).FirstOrDefault();
            if (mp != null) { u.Queue.Add(mp); return NextGoal(u); }
        }
        return false;
    }

    private void StartAction(FleetUnit u)
    {
        switch (u.Goal)
        {
            case Victim v when v.Active && !v.OnBoard && u.Aboard.Count < u.Capacity:
                u.BusyS = u.Heli ? (Landable(u.Pos) ? 120f : 240f) : 150f;
                u.Busy = "rescate";
                u.AglM = u.Heli ? (Landable(u.Pos) ? 0 : 15f) : 0;
                u.Status = u.Heli ? $"rescatando a {v.Name}{(u.AglM > 0 ? " (cabrestante)" : "")}" : $"bomberos rescatando a {v.Name}";
                u.ActionSerial++;
                v.Carrier = u.Id;   // reservada: nadie más va por ella
                v.OnBoard = true;
                break;
            case MedicalPoint mp when u.Aboard.Count > 0:
                u.BusyS = 90f;
                u.Busy = "entrega";
                u.AglM = 0;
                u.Status = $"entregando en {mp.Name}";
                u.ActionSerial++;
                break;
            case FireTask ft:
                StartFireAction(u, ft);
                break;
            default:
                if (u.Goal == null && u.Pos == u.Home) { u.Status = u.Heli ? "en base" : "en el cuartel"; return; }
                DropGoal(u);
                break;
        }
    }

    private void CompleteAction(FleetUnit u)
    {
        switch (u.Goal)
        {
            case Victim v when u.Busy == "rescate":
                u.Aboard.Add(v);
                u.Rescued++;
                RescueOrder.Remove(v);
                Radio(u.Id, 0, $"{v.Name} a bordo ({Victim.KindName(v.Kind)}, le quedaban {v.SurvivalS / 60:0} min) · {u.Aboard.Count}/{u.Capacity}");
                break;
            case MedicalPoint mp when u.Busy == "entrega":
                foreach (var x in u.Aboard.Where(x => x.Active)) { x.Delivered = true; x.OnBoard = false; }
                u.Delivered += u.Aboard.Count;
                Radio(u.Id, 0, $"entregadas {string.Join(", ", u.Aboard.Select(x => x.Name))} en {mp.Name} · salvadas {Saved}/{Victims.Count}");
                u.Aboard.Clear();
                _needMissionPlan = true;
                break;
            case FireTask ft:
                CompleteFireAction(u, ft);
                break;
        }
        u.Busy = "";
        if (u.Goal != null) u.Queue.Remove(u.Goal);
        u.Goal = null;
    }

    /// <summary>Resumen de la flota para el HUD y la bitácora final.</summary>
    public string FleetSummary() => Fleet.Count == 0 ? "" :
        "H-1: " + (Rescue ? $"{Victims.Count(v => v.Delivered && v.Carrier == 0)} entregadas" : $"{FireDrops} descargas") +
        string.Concat(Fleet.Select(u => $" · {u.Name}: " + (Rescue ? $"{u.Delivered} entregadas" : $"{u.Drops} descargas") + $", {u.DistanceM / 1000:0.0} km"));
}
