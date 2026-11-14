using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using AresX.AI;
using AresX.Core;

namespace AresX.Sim;

public enum MissionState { Configuracion, Exploracion, Alerta, Replanificacion, Refugio, ModoSeguro, Exito, Fracaso }

public enum MissionMode { Destino, Autonomo, Rescate, Incendio }

public enum DestVerdict { Viable, Arriesgado, SoloIda, EnergiaInsuficiente, Inalcanzable }

/// <summary>Evaluación previa de un destino asignado por el operador (con el mapa de creencias).</summary>
public sealed class DestinationReport
{
    public Cell Cell, Requested;
    public bool Adjusted;
    public string Name = "", Soil = "";
    public float StraightM, PathM, EtaS, EnergyWh, ReturnWh, BatteryAtArrivalPct, BatteryAfterReturnPct;
    public float RiskMean, RiskMax, UnknownFrac, SlopeEst, ElevEst, TempNow, PressureBar, KnownRisk;
    public int Steps, Step;
    public List<Cell> Path = new();
    public DestVerdict Verdict;
    public readonly List<string> Notes = new();

    public string VerdictText => Verdict switch
    {
        DestVerdict.Viable => "VIABLE",
        DestVerdict.Arriesgado => "VIABLE CON RIESGO",
        DestVerdict.SoloIda => "SOLO IDA (sin reserva para volver)",
        DestVerdict.EnergiaInsuficiente => "ENERGÍA INSUFICIENTE",
        _ => "INALCANZABLE",
    };
}

public sealed class MissionConfig
{
    public string PlanetId = "marte";
    public bool Probe;
    public int Difficulty = 1;
    public int Seed = 42;
    public float BatteryPct = 80;
    public float ScienceWeight = 1f;
    public int Month = 11;
    public bool UseRna = true, UseFuzzy = true, UseGa = true, UseDrone = true;
    public MissionMode Mode = MissionMode.Destino;
    public int MaxSteps = 900;
    public bool ClickMode => Mode == MissionMode.Destino;
    public GaParams Ga = new();
    public GeoReport? Scenario;           // escenario GeoClick a escala real (solo Tierra)
    public bool Heli;                     // helicóptero no tripulado (solo Tierra)
    public int Helis = 1, Trucks = 0;     // flota (rescate / incendio): helicópteros y carros de bomberos
    public Cell? StartCell;               // punto de partida elegido en el mapa (null = automático)
    public System.Collections.Generic.List<Cell> Points = new();   // personas (rescate) o focos (incendio) elegidos en el mapa

    public string AblationName => (UseRna, UseFuzzy, UseGa) switch
    {
        (true, true, true) => "Híbrido completo",
        (false, true, true) => "Sin RNA",
        (true, false, true) => "Sin difuso",
        (true, true, false) => "Sin AG",
        (false, false, true) => "AG puro",
        (false, false, false) => "Control (menor distancia)",
        _ => $"RNA {(UseRna ? 1 : 0)} DIF {(UseFuzzy ? 1 : 0)} AG {(UseGa ? 1 : 0)}",
    } + (UseDrone ? "" : " · sin dron");

    /// <summary>Validación de compatibilidad planeta/modo (RF-01, CA-07). null = válida.</summary>
    public string? Validate()
    {
        var p = PlanetProfile.Get(PlanetId);
        if (Mode == MissionMode.Rescate && (p.Style != TerrainStyle.Tierra || !Heli))
            return "La misión de rescate es para el helicóptero en la Tierra (personas atrapadas en montaña, aluviones o inundaciones): elija Modo = Helicóptero.";
        if (Mode == MissionMode.Incendio && (p.Style != TerrainStyle.Tierra || !Heli))
            return "El combate contra incendios es en la Tierra y lo lidera un helicóptero (con apoyo de otros helicópteros y carros de bomberos): elija Modo = Helicóptero.";
        if (Heli && p.Style != TerrainStyle.Tierra)
            return "El helicóptero solo opera en la Tierra: necesita una atmósfera densa (en Marte el aire es ~1 % del terrestre).";
        if (!p.SolidSurface && !Probe)
            return $"{p.Name} no tiene una superficie sólida accesible: un rover de ruedas no puede rodar sobre un gigante gaseoso/helado. " +
                   "Cambie a modo sonda atmosférica (vehículo aerostático virtual).";
        if (p.SolidSurface && Probe)
            return $"{p.Name} tiene superficie rocosa: el modo sonda atmosférica está pensado para los gigantes. Use el rover de ruedas.";
        if (!float.IsFinite(BatteryPct) || BatteryPct < 1 || BatteryPct > 100)
            return "Batería inicial inválida: debe estar entre 1 % y 100 %.";
        if (!float.IsFinite(ScienceWeight) || ScienceWeight < 0 || ScienceWeight > 3)
            return "Prioridad científica inválida (0–3).";
        if (Month < 1 || Month > 12) return "Mes inválido.";
        return null;
    }
}

public sealed class TraceRow
{
    public int Step;
    public float TimeS;
    public string State = "", Action = "", Code = "", Reason = "", Events = "";
    public int Row, Col, TargetRow, TargetCol;
    public float Speed, BatteryPct, BatteryWh, TempExt, TempInt, Wind, Gust, Rain, Visibility, Slope, Pressure, Radiation, Integrity;
    public float PLibre, PObst, PBarr, Confidence;
    public float FuzzySpeed, FuzzyCaution;
    public string TopRules = "";
    public float GaBest;
    public string Route = "";
    public float DecisionMs;
    public string Explanation = "";
}

public sealed class LogEntry
{
    public int Step;
    public float TimeS;
    public string Kind = "info";   // info, ok, alerta, veto, evento, ag, rna, dif, fallo
    public string Text = "";
}

/// <summary>
/// Misión de Expedición Universo: generador del mundo → sensores con ruido → validación → RNA → difuso + AG → supervisor →
/// acción → física simplificada → bitácora. Un paso = un ciclo observar/decidir/actuar (normalmente, una celda).
/// No depende de Godot: se puede ejecutar sin ventana (benchmark de semillas y ablaciones).
/// </summary>
public sealed partial class Mission
{
    public const string Version = "Expedición Universo 1.1 (Godot 4.7 .NET)";
    public readonly MissionConfig Config;
    public readonly PlanetProfile Planet;
    public readonly WorldMap Map;
    public readonly SimEnvironment Env;
    public readonly SensorSuite Sensors;
    public readonly RoverState Rover = new();
    public readonly BeliefMap Belief;
    public readonly FuzzySystem Fuzzy = RoverFuzzy.Build();
    public readonly NeuralPerception? Rna;
    public readonly CostModel Cost;
    private readonly Rng _rngEnv, _rngSense, _rngGa, _rngPhys, _rngEvents;

    public MissionState State = MissionState.Exploracion;
    public int StepCount;
    public string EndReason = "";
    public Cell Target;
    public string TargetName = "";
    public List<Cell> Route = new();
    public int RouteIdx;
    public readonly List<Cell> Trail = new();
    public GaResult? LastGa;
    public readonly List<GaResult> GaHistory = new();
    public MissionPlan? Plan;
    public SensorReading? Reading;
    public float[] FrontFeatures = new float[FeatureModel.NF];
    public float[] FrontProbs = { 1, 0, 0 };
    public float[][]? FrontActivations;
    public Cell? FrontCell;
    public FuzzyResult? LastFuzzy;
    public Decision? LastDecision;
    public readonly List<LogEntry> Log = new();
    public readonly List<TraceRow> Trace = new();
    public Cell? AlienPos;
    public bool HasDestination;               // modo destino: hay un destino del operador pendiente
    public Cell? OperatorTarget;
    public DestinationReport? DestReport;
    public (Cell Cell, int Release)? PendingDestination;
    private Cell? _resumeTarget;
    public bool PendingSample;
    public bool Charging;    // en refugio recargando hasta el 90 %
    public float LastStepSeconds = 20f;   // duración simulada del último paso (la vista anima con esto)
    public bool LastMoved, LastSlip, LastCollision;
    public Cell LastFrom;
    public float ReserveWh => Rover.CapacityWh * 0.10f;
    public float EnergyToRefuge;
    public Cell NearestRefuge;
    public bool RefugeReachable = true;
    private bool _needMissionPlan = true, _needRoute = true;
    private string _replanTrigger = "inicio de misión";
    private readonly HashSet<Cell> _cliffsAvoided = new();
    private int _faultCycle;
    private bool _goingRefuge;

    // métricas
    public int Vetoes, Replans, Falls, Samples, SafetyViolations;
    public float DistanceM, GaMsTotal, DecisionMsTotal;
    public float StartBatteryWh;

    public Mission(MissionConfig cfg, NeuralPerception? rna)
    {
        Config = cfg;
        Planet = PlanetProfile.Get(cfg.PlanetId);
        Rna = rna;
        var root = new Rng((ulong)cfg.Seed * 1_000_003UL + 12345);
        Map = cfg.Scenario != null ? new WorldMap(Planet, cfg.Scenario, cfg.Seed, cfg.Difficulty) : new WorldMap(Planet, cfg.Probe, cfg.Seed, cfg.Difficulty);
        _rngEnv = root.Fork(11);
        _rngSense = root.Fork(12);
        _rngGa = root.Fork(13);
        _rngPhys = root.Fork(14);
        _rngEvents = root.Fork(15);
        ApplyStartCell(cfg.StartCell);
        Env = new SimEnvironment(Planet, Map, _rngEnv, cfg.Month);
        Sensors = new SensorSuite(_rngSense);
        Belief = new BeliefMap(Map, root.Fork(16));
        Rover.Probe = cfg.Probe;
        Rover.CellM = Map.CellM;
        if (cfg.Probe)
        {
            Rover.MassKg = 350;
            Rover.VmaxMs = 2.5f;
            Rover.CapacityWh = 420;
            Rover.BaseW = 35;
            Rover.RtgW = 110;   // generador de radioisótopos (SUPUESTO, como las misiones reales a planetas exteriores)
        }
        else if (Map.IsScenario)
        {
            // escenario a escala real (km): vehículo terrestre de campo eléctrico (SUPUESTO, tipo UGV de exploración)
            Rover.MassKg = 450;
            Rover.VmaxMs = 2.0f;
            Rover.CapacityWh = 6000;
            Rover.BaseW = 120;
            Rover.PanelM2 = 2.4f;
            Rover.SampleCapKg = 12f;
        }
        InitHeliParams();
        Rover.Pos = Map.Start;
        Rover.EstX = Rover.TrueX;
        Rover.EstY = Rover.TrueY;
        Rover.BatteryWh = Rover.CapacityWh * cfg.BatteryPct / 100f;
        Rover.TempInt = MathX.Clamp(Env.TempAt(Map.Start) + 15f, Planet.HardwareMinC + 20, Planet.HardwareMaxC - 20);
        if (Planet.Style == TerrainStyle.Venus) Rover.TempInt = 60f;
        StartBatteryWh = Rover.BatteryWh;
        InitDrone((ulong)cfg.Seed);
        Cost = new CostModel(Belief, Map, Rover);
        InitHeli();
        InitRescue((ulong)cfg.Seed);
        InitFire((ulong)cfg.Seed);
        InitFleet();
        // rescate e incendio: el límite de pasos escala con el mapa (un paso = una celda de vuelo)
        if (Rescue && cfg.MaxSteps == 900) cfg.MaxSteps = Math.Max(900, Map.N * 14);
        if (FireMode && cfg.MaxSteps == 900) cfg.MaxSteps = 40000;   // el incendio se limita por tiempo simulado (8 h)
        Trail.Add(Rover.Pos);
        Belief.ConfirmFree(Rover.Pos);
        Target = Map.Start;
        if (cfg.ClickMode)
        {
            _needMissionPlan = false;
            TargetName = "sin destino";
            if (cfg.MaxSteps == 900) cfg.MaxSteps = 6000;
        }
        AddLog("info", $"Misión {WorldName} · semilla {cfg.Seed} · {cfg.AblationName} · batería {cfg.BatteryPct:0}% · {(cfg.Probe ? "sonda atmosférica" : cfg.Heli ? "helicóptero no tripulado" : "rover de ruedas")} · {(cfg.ClickMode ? "destino por clic del operador" : "misión autónoma (plan del AG)")}");
        if (Planet.Style == TerrainStyle.Tierra) AddLog("info", $"{(Map.IsScenario ? "Escenario" : "Perfil calibrado con")} {Map.Geo.Consulta}. {Map.Calibration}");
        foreach (var n in Map.IsScenario ? ScenarioNotes() : Planet.Notes) AddLog("info", n);
        if (Heli) foreach (var n in HeliNotes()) AddLog("info", n);
        UpdateRefugeEstimate();
    }

    public bool Finished => State is MissionState.Exito or MissionState.Fracaso;
    public bool UseRna => Config.UseRna && Rna != null;

    public void AddLog(string kind, string text) => Log.Add(new LogEntry { Step = StepCount, TimeS = Env.TimeS, Kind = kind, Text = text });

    public string Clock => $"T+{StepCount:0000}";

    // ================================================================== órdenes del operador: destino por clic
    /// <summary>El operador asigna un destino (clic en el terreno). Con enlace degradado la orden llega diferida.</summary>
    public void SetDestination(Cell c)
    {
        if (Finished || !Map.InBounds(c)) return;
        int delay = Env.CommandDelaySteps;
        if (delay > 0)
        {
            PendingDestination = (c, StepCount + delay);
            AddLog("alerta", $"Destino {c} enviado con enlace degradado: llegará al rover en {delay} pasos (sigue con autonomía local)");
            return;
        }
        ApplyDestination(c, "operador");
    }

    public void CancelDestination()
    {
        if (!HasDestination && PendingDestination == null) return;
        HasDestination = false;
        PendingDestination = null;
        _resumeTarget = null;
        Route = new List<Cell> { Rover.Pos };
        RouteIdx = 0;
        TargetName = "sin destino";
        AddLog("info", "Destino cancelado por el operador: el rover se detiene y sigue observando");
    }

    private void ApplyDestination(Cell c, string by)
    {
        var requested = c;
        if (Belief.Forbidden(c))
        {
            Cell? best = null;
            for (int r = 1; r <= 4 && best == null; r++)
                for (int dy = -r; dy <= r && best == null; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        var o = new Cell(c.X + dx, c.Y + dy);
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r || !Map.InBounds(o) || Belief.Forbidden(o)) continue;
                        best = o;
                        break;
                    }
            if (best is Cell b2) c = b2;
        }
        if (c == Rover.Pos)
        {
            AddLog("info", $"El rover ya está en {c}: no hay desplazamiento que evaluar");
            return;
        }
        if (Charging) AddLog("alerta", $"Se interrumpe la recarga ({Rover.BatteryPct:0} %) por orden del operador");
        Charging = false;
        _goingRefuge = false;
        _resumeTarget = null;
        OperatorTarget = c;
        var site = Map.Sites.FirstOrDefault(x => x.Cell == c && !x.Visited);
        SetTarget(c, site != null ? $"{site.Name} · {site.Kind}" : Map.Refuges.Contains(c) ? "refugio" : "destino del operador");
        HasDestination = true;
        DestReport = EvaluateDestination(c, requested);
        var r0 = DestReport;
        AddLog(r0.Verdict is DestVerdict.Viable ? "ok" : r0.Verdict is DestVerdict.Arriesgado or DestVerdict.SoloIda ? "alerta" : "veto",
            $"Nuevo destino ({by}) {c}: {r0.VerdictText} · ruta est. {r0.PathM:0} m / {r0.Steps} pasos · {r0.EnergyWh:0.0} Wh ida + {r0.ReturnWh:0.0} Wh regreso · ETA {r0.EtaS / 60:0.0} min · riesgo máx {r0.RiskMax:0.00}"
            + (r0.Notes.Count > 0 ? " · " + string.Join(" · ", r0.Notes.Take(3)) : ""));
        Route = new List<Cell> { Rover.Pos };
        RouteIdx = 0;
        _needRoute = true;
        _replanTrigger = $"nuevo destino del operador {c}";
    }

    /// <summary>
    /// Evaluación previa del destino con lo que el rover CREE del mapa: ruta estimada de bajo riesgo, energía de ida
    /// y de regreso al refugio (con carga térmica y margen ×1,25), riesgo, incertidumbre, terreno y clima en destino.
    /// </summary>
    public DestinationReport EvaluateDestination(Cell c, Cell requested)
    {
        var r = new DestinationReport { Cell = c, Requested = requested, Adjusted = c != requested, Step = StepCount, Name = TargetName };
        int i = Belief.Idx(c);
        r.StraightM = Rover.Pos.Dist(c) * Map.CellM;
        r.SlopeEst = Belief.SlopeEst[i];
        r.ElevEst = Map.BaseElevation + Belief.HeightEst[i];
        r.TempNow = Env.TempAt(c);
        r.PressureBar = Env.PressureAt(c);
        r.Soil = Belief.Observed[i] ? WorldMap.SoilName(Map.SoilAt(c)) : "desconocido (no observado)";
        r.KnownRisk = Belief.Risk(c);
        if (r.Adjusted) r.Notes.Add($"la celda pedida {requested} está prohibida (barranco/obstáculo confirmado): destino ajustado");
        var path = Cost.Shortest(Rover.Pos, c, 3f);
        if (path == null)
        {
            r.Verdict = DestVerdict.Inalcanzable;
            r.Notes.Add("no hay ruta conocida que evite las celdas prohibidas");
            return r;
        }
        float wh = 0, t = 0, riskSum = 0, rmax = 0;
        int unknown = 0;
        for (int k = 1; k < path.Count; k++)
        {
            wh += Cost.StepWh(path[k - 1], path[k]);
            t += Cost.StepTimeS(path[k - 1], path[k]);
            r.PathM += Cost.StepM(path[k - 1], path[k]);
            float rk = Belief.Risk(path[k]);
            riskSum += rk;
            rmax = MathF.Max(rmax, rk);
            if (!Belief.Observed[Belief.Idx(path[k])]) unknown++;
        }
        r.Steps = path.Count - 1;
        r.EnergyWh = wh * 1.25f;
        r.EtaS = t;
        r.RiskMean = riskSum / Math.Max(1, r.Steps);
        r.RiskMax = rmax;
        r.UnknownFrac = (float)unknown / Math.Max(1, r.Steps);
        r.Path = path;
        r.ReturnWh = float.PositiveInfinity;
        foreach (var rf in Map.Refuges)
        {
            if (rf == c) { r.ReturnWh = 0; break; }
            var back = Cost.Shortest(c, rf, 1.5f);
            if (back == null) continue;
            float bw = 0;
            for (int k = 1; k < back.Count; k++) bw += Cost.StepWh(back[k - 1], back[k]);
            r.ReturnWh = MathF.Min(r.ReturnWh, bw * 1.25f);
        }
        r.BatteryAtArrivalPct = 100f * (Rover.BatteryWh - r.EnergyWh) / Rover.CapacityWh;
        r.BatteryAfterReturnPct = float.IsFinite(r.ReturnWh) ? 100f * (Rover.BatteryWh - r.EnergyWh - r.ReturnWh) / Rover.CapacityWh : float.NegativeInfinity;
        // notas de contexto
        if (r.UnknownFrac > 0.3f) r.Notes.Add($"{r.UnknownFrac:P0} de la ruta sin observar: la RNA la evaluará en marcha");
        if (r.SlopeEst > 20) r.Notes.Add($"pendiente estimada {r.SlopeEst:0}° en destino");
        float margin = MathF.Min(r.TempNow - Planet.HardwareMinC, Planet.HardwareMaxC - r.TempNow);
        if (r.TempNow < -20) r.Notes.Add($"destino a {r.TempNow:0} °C: más calefacción");
        if (r.TempNow > 60 && Planet.Style != TerrainStyle.Venus) r.Notes.Add($"destino a {r.TempNow:0} °C: riesgo térmico");
        if (Planet.AirDensity > 0 && Env.WindGust > Planet.WindLimit * 0.7f) r.Notes.Add($"ráfagas al {100 * Env.WindGust / Planet.WindLimit:0} % del límite");
        if (Planet.HasRain && Env.RainMmH > 1) r.Notes.Add($"lluvia {Env.RainMmH:0.0} mm/h: suelo húmedo, más patinaje");
        if (Env.DustOpacity > 0.4f) r.Notes.Add("polvo denso: visibilidad y carga solar reducidas");
        if (Env.IsDay)
        {
            float toSunset = ((0.75f - Env.DayPhase + 1f) % 1f) * Env.DayLengthS;
            if (toSunset < r.EtaS) r.Notes.Add($"anochece en {toSunset / 60:0} min, antes de llegar (sin carga solar)");
        }
        else r.Notes.Add("de noche: sin carga solar, cámaras con más ruido");
        if (Map.Sites.Any(x => x.Cell == c && !x.Visited)) r.Notes.Add("sitio científico: se tomará muestra al llegar");
        if (Map.Refuges.Contains(c)) r.Notes.Add("refugio: recarga 600 W y relocalización");
        r.Verdict = r.EnergyWh > Rover.BatteryWh ? DestVerdict.EnergiaInsuficiente
                  : !float.IsFinite(r.ReturnWh) || Rover.BatteryWh - r.EnergyWh - r.ReturnWh < ReserveWh ? DestVerdict.SoloIda
                  : r.RiskMax > 0.6f || r.UnknownFrac > 0.6f || r.SlopeEst > 25 || margin < 10 ? DestVerdict.Arriesgado
                  : DestVerdict.Viable;
        return r;
    }

    public void RequestMissionPlan()
    {
        _needMissionPlan = true;
        _needRoute = true;
        _replanTrigger = "cambio a misión autónoma";
    }

    public void RequestReplan(string why)
    {
        _needRoute = true;
        _replanTrigger = why;
    }

    // ================================================================== paso principal
    public void Step()
    {
        if (Finished) return;
        var sw = Stopwatch.StartNew();
        StepCount++;
        LastMoved = LastSlip = LastCollision = false;
        LastFrom = Rover.Pos;

        // --- 0) intención provisional: orden del operador o siguiente celda de la ruta vigente
        bool click = Config.ClickMode;
        if (PendingDestination is { } pd && pd.Release <= StepCount)
        {
            PendingDestination = null;
            ApplyDestination(pd.Cell, "orden diferida por el enlace");
        }
        bool autonomous = !click || HasDestination;
        Cell? proposal = null;
        bool atRefugeNow = Map.Refuges.Contains(Rover.Pos);
        if (atRefugeNow && !Charging && !HasDestination && Rover.BatteryPct < 50 && (State == MissionState.Refugio || _goingRefuge || Rover.BatteryPct < 35
            || (Plan != null && Plan.Empty && Map.Sites.Any(x => !x.Visited))))
        {
            Charging = true;
            AddLog("alerta", $"En refugio con {Rover.BatteryPct:0} % de batería: recarga hasta 90 % antes de continuar");
        }
        if (Charging && Rover.BatteryPct >= 90)
        {
            Charging = false;
            _goingRefuge = false;
            _replanTrigger = "recarga completa";
            if (!click)
            {
                _needMissionPlan = true;
                AddLog("ok", "Recarga completa: se replanifica la misión científica");
            }
            else if (_resumeTarget is Cell rt)
            {
                _resumeTarget = null;
                AddLog("ok", $"Recarga completa: se reanuda el destino del operador {rt}");
                ApplyDestination(rt, "reanudación tras recarga");
                autonomous = true;
            }
            else AddLog("ok", "Recarga completa: esperando un destino del operador");
        }
        if (PendingSample || Charging)
        {
            // acción completa sin desplazamiento (muestreo o recarga)
        }
        else if (autonomous && !_needMissionPlan && !_needRoute && Route.Count > 1 && RouteIdx < Route.Count - 1 && Route.Contains(Rover.Pos))
        {
            proposal = Route[RouteIdx + 1];
        }

        Cell HeadingOf(Cell? prop) => prop is Cell pp && pp != Rover.Pos ? pp - Rover.Pos : Rover.Dir;
        Cell heading = HeadingOf(proposal);
        if (proposal is Cell pr && !(Heli ? Map.InGrid(pr) : Map.InBounds(pr)))
        {
            AddLog("veto", $"S-05 · movimiento fuera del mapa hacia {pr}: rechazado");
            proposal = null;
            heading = Rover.Dir;
        }

        // --- 1) observar y validar sensores
        var s = Reading = Sensors.Observe(Map, Env, Rover, heading);
        foreach (var w in s.Warnings.Distinct()) AddLog("fallo", w);

        // --- 2) RNA: percepción de las celdas visibles → creencias
        var cond = new Conditions
        {
            VisibilityM = s.Ok("vis") ? s.V("vis") : 200f,
            Night = !Env.IsDay,
            Vibration = s.Ok("accel") ? s.V("accel") : 0.3f,
            RadiationMsvH = s.Ok("rad") ? s.V("rad") : Planet.RadiationMsvH,
            TempExtC = s.Ok("t_ext") ? s.V("t_ext") : Planet.TempMeanC,
        };
        Perceive(cond);

        // --- 2b) AG si corresponde: inicio, objetivo alcanzado, ruta bloqueada por lo recién percibido o evento grave
        if (autonomous && !PendingSample && !Charging)
        {
            if (_needMissionPlan && !click) PlanMission();
            bool blocked = !Heli && proposal is Cell nx && (Belief.Forbidden(nx) || MathF.Max(Belief.P[Belief.Idx(nx)][1], Belief.P[Belief.Idx(nx)][2]) > 0.8f);
            if (blocked) _replanTrigger = $"ruta bloqueada en {proposal}";
            if (_needRoute || blocked || Route.Count == 0 || RouteIdx >= Route.Count - 1 || !Route.Contains(Rover.Pos)) PlanRoute(_replanTrigger);
            proposal = Route.Count > 1 && RouteIdx < Route.Count - 1 ? Route[RouteIdx + 1] : null;
            var nh = HeadingOf(proposal);
            if (nh != heading)
            {
                heading = nh;
                s = Reading = Sensors.Observe(Map, Env, Rover, heading);   // re-apuntar los sensores frontales
            }
        }

        FrontCell = proposal;
        if (proposal is Cell front)
        {
            FrontFeatures = FeatureModel.Extract(Map, Rover.Pos, front, cond, _rngSense);
            if (UseRna)
            {
                FrontActivations = Rna!.ForwardRaw(FrontFeatures);
                FrontProbs = FrontActivations[^1];
            }
            else
            {
                FrontActivations = null;
                FrontProbs = FeatureModel.RulePredict(FrontFeatures);
            }
            Belief.Update(front, FrontProbs, Rover.Pos.Dist(front) * WorldMap.RefCellM, StepCount);
            // sensor determinista de borde: confirma barrancos adyacentes (veto de seguridad)
            if (s.Ok("lidar_drop") && s.V("lidar_drop") > WorldMap.CliffDropM)
            {
                if (!Belief.ConfirmedCliff[Belief.Idx(front)]) AddLog("veto", $"Sensor de borde: caída de {s.V("lidar_drop"):0.0} m en {front} → zona prohibida");
                Belief.ConfirmCliff(front);
                _cliffsAvoided.Add(front);
            }
            if (s.Ok("prox_f") && s.V("prox_f") < 2.6f && Map.ClassAt(front) == TerrainClass.Obstaculo)
                Belief.ConfirmObstacle(front);
        }
        else
        {
            FrontProbs = new[] { 1f, 0f, 0f };
            FrontActivations = null;
        }

        // --- 3) difuso
        float pHaz = FrontProbs[1] + FrontProbs[2];
        float distRisk = DistanceToRisk(s, heading);
        float slopeAhead = s.Ok("lidar_grad") ? MathF.Abs(MathF.Atan(s.V("lidar_grad")) * 180f / MathF.PI) : s.Ok("incl_pitch") ? MathF.Abs(s.V("incl_pitch")) : 10f;
        if (Rover.Probe) slopeAhead *= 0.3f;   // en sonda la "pendiente" es cizalla de la capa de nubes
        float windPct = Planet.AirDensity <= 0 ? 0 : 100f * (s.Ok("gust") ? s.V("gust") : Env.WindGust) / MathF.Max(0.1f, Heli ? HeliModel.GustLimitMs : Planet.WindLimit);
        if (Heli)
        {
            // difuso del helicóptero: distancia al relieve que obliga a trepar, gradiente de vuelo y riesgo = margen de potencia
            UpdateHeliAir();
            distRisk = HeliTerrainClearance(heading);
            slopeAhead = HeliClimbDeg = HeliClimbAhead(heading);
            pHaz = MathX.Clamp01((1.3f - HeliMargin) / 0.4f);
        }
        var fin = new Dictionary<string, float>
        {
            ["distancia"] = distRisk, ["bateria"] = Rover.BatteryPct, ["pendiente"] = slopeAhead,
            ["temperatura"] = s.Ok("t_int") ? s.V("t_int") : Rover.TempInt, ["viento"] = windPct, ["riesgo"] = proposal == null ? 0 : pHaz,
        };
        float fSpeed = 0.6f, fCaution = 0.5f;
        if (Config.UseFuzzy)
        {
            LastFuzzy = Fuzzy.Infer(fin);
            fSpeed = LastFuzzy.Crisp["velocidad"];
            fCaution = LastFuzzy.Crisp["cautela"];
        }
        else
        {
            LastFuzzy = null;
        }

        // --- 4) supervisor
        UpdateRefugeEstimate();
        bool atRefuge = Map.Refuges.Contains(Rover.Pos);
        float tip = TipIndex(slopeAhead, windPct);
        var si = new SupervisorInput
        {
            Sensors = s, From = Rover.Pos, HasProposal = proposal != null, Proposed = proposal ?? Rover.Pos,
            RnaProbs = FrontProbs, RnaConfidence = FrontProbs.Max(), RnaEnabled = UseRna, FuzzySpeed = fSpeed, FuzzyCaution = fCaution,
            BatteryWh = Rover.BatteryWh, ReserveWh = ReserveWh, EnergyToRefugeWh = EnergyToRefuge, RefugeReachable = RefugeReachable, AtRefuge = atRefuge,
            TempInt = s.Ok("t_int") ? s.V("t_int") : Rover.TempInt, HwMin = Planet.HardwareMinC, HwMax = Planet.HardwareMaxC,
            WindPct = windPct, TipIndex = tip,
            // residuo sísmico: aceleración medida menos la vibración esperada por rugosidad/velocidad (y turbulencia en sonda)
            QuakeAccel = s.Ok("accel") ? MathF.Max(0, s.V("accel") - Map.Rough[Map.Idx(Rover.Pos)] * (0.2f + Rover.LastSpeed * 1.6f)
                                                     - (Rover.Probe ? (s.Ok("gust") ? s.V("gust") : Env.WindGust) / 60f : 0) - 0.15f) : 0,
            Daylight = Env.IsDay, SolarW = Rover.SolarW,
            ProposedForbidden = proposal is Cell pf2 && Map.InBounds(pf2) && Belief.Forbidden(pf2), ManualCommand = click,
            Heli = Heli, PowerMargin = Heli ? HeliMargin : 9f, ClimbDeg = HeliClimbDeg, RainMmH = Env.RainMmH, VisM = s.Ok("vis") ? s.V("vis") : 0,
        };
        var d = LastDecision = SafetySupervisor.Decide(si);
        if (Charging && d.Code is "S-20" or "S-21" or "S-11" or "S-30" or "S-31")
        {
            d.Action = RoverAction.Recargar;
            d.Code = "S-41";
            d.Reason = $"En refugio: recargando ({Rover.BatteryPct:0} % → 90 %)";
            d.Explanation = $"{d.Code} · {d.Reason}";
        }
        if (PendingSample && d.Action is RoverAction.Esperar or RoverAction.Avanzar && d.Code is "S-20" or "S-21" or "S-30" or "S-31")
        {
            d.Action = RoverAction.Muestrear;
            d.Code = "S-40";
            d.Reason = FireMode ? (MainWaterL > 0 && !WaterSources.Contains(Rover.Pos) ? "Sobre el foco: descarga de 1 000 L de agua" : "En el punto de agua: cargar el balde")
                     : !Rescue ? "En el sitio científico: tomar y analizar muestra"
                     : Medical.Any(m => m.Cell == Rover.Pos) && OnBoard > 0 ? "En el punto médico: entregar a las personas rescatadas"
                     : "Junto a la persona atrapada: la tripulación la sube a bordo";
            d.Explanation = $"{d.Code} · {d.Reason}";
        }

        // --- 5) ejecutar
        if (click && !HasDestination && d.Code == "S-20")
        {
            d.Reason = Charging ? "Recargando" : "Sin destino asignado: observando y esperando (haga clic en el terreno)";
            d.Explanation = $"{d.Code} · {d.Reason}";
        }
        Execute(d, proposal, click);
        UpdateVictims(LastStepSeconds);
        UpdateFleet(LastStepSeconds);
        UpdateFire(LastStepSeconds);
        if (FireMode && StepCount - _firePlanStep >= 12) _needMissionPlan = true;   // el fuego cambia: reasignar seguido
        EvaluateDroneDecision();
        UpdateDrone(LastStepSeconds);

        // --- 6) replanificación si el supervisor vetó (el AG corre en el mismo ciclo, el rover queda detenido)
        if (d.Veto)
        {
            Vetoes++;
            if (proposal is Cell vc && Map.InBounds(vc) && d.Code is "S-07" or "S-08" or "S-09") Belief.Veto(vc);
            AddLog("veto", $"{d.Code} · {d.Reason}");
            if (autonomous) PlanRoute($"veto {d.Code}");
        }
        if (d.GoRefuge && !_goingRefuge && !Finished)
        {
            _goingRefuge = true;
            if (click)
            {
                if (HasDestination && OperatorTarget is Cell ot && !Map.Refuges.Contains(ot)) _resumeTarget = ot;
                HasDestination = true;   // el rover se dirige solo al refugio
                AddLog("alerta", "Reserva de energía comprometida: se suspende el destino del operador y se va al refugio a recargar" + (_resumeTarget != null ? " (se reanudará después)" : ""));
            }
            else AddLog("alerta", "Reserva de energía comprometida: se abandona el plan científico y se va al refugio");
            Plan = null;
            SetTarget(NearestRefuge, "refugio (energía)");
            _needRoute = true;
            _replanTrigger = "energía insuficiente";
        }

        // --- 7) estado de la misión
        UpdateState(d);
        sw.Stop();
        float ms = (float)sw.Elapsed.TotalMilliseconds;
        DecisionMsTotal += ms;
        RecordTrace(d, ms);
        if (StepCount >= Config.MaxSteps && !Finished) Fail("límite de pasos de la misión alcanzado");
    }

    // ================================================================== percepción
    private void Perceive(Conditions cond)
    {
        float rangeCells = MathX.Clamp(cond.VisibilityM / Map.CellM, 1.5f, 4.2f);
        if (cond.Night) rangeCells = MathF.Min(rangeCells, 3.2f);   // LIDAR activo: sigue viendo de noche, cámara no
        int r = (int)MathF.Ceiling(rangeCells);
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            var c = new Cell(Rover.Pos.X + dx, Rover.Pos.Y + dy);
            if (!Map.InBounds(c) || Rover.Pos.Dist(c) > rangeCells) continue;
            var f = FeatureModel.Extract(Map, Rover.Pos, c, cond, _rngSense);
            var p = UseRna ? Rna!.Predict(f) : FeatureModel.RulePredict(f);
            Belief.Update(c, p, Rover.Pos.Dist(c) * WorldMap.RefCellM, StepCount);
        }
        Belief.ConfirmFree(Rover.Pos);
    }

    private float DistanceToRisk(SensorReading s, Cell heading)
    {
        float d = s.Ok("prox_f") ? s.V("prox_f") : s["prox_f"].Status == SensorStatus.Saturado ? 0f : 5f;
        for (int k = 1; k <= 4; k++)
        {
            var c = Rover.Pos + new Cell(heading.X * k, heading.Y * k);
            if (!Map.InBounds(c)) break;
            if (Belief.Risk(c) > 0.6f)
            {
                d = MathF.Min(d, (k - 0.5f) * Map.CellM * (heading.IsDiagonal ? 1.414f : 1));
                break;
            }
        }
        return MathX.Clamp(d, 0, 20);
    }

    private float TipIndex(float slopeDeg, float windPct)
    {
        float wind = Planet.AirDensity <= 0 ? 0 : 0.5f * Planet.AirDensity * 1.2f * 2f * Env.WindGust * Env.WindGust;   // N
        float windTerm = wind * 1.0f / MathF.Max(1, Rover.MassKg * Planet.Gravity * 0.8f);
        if (Rover.Probe) return windPct / 130f;   // aerostato: sin vuelco por pendiente
        return slopeDeg / 34f + windTerm;
    }

    // ================================================================== planificación
    private void PlanMission()
    {
        _needMissionPlan = false;
        if (FireMode)
        {
            PlanFire();   // decide si el H-1 cambia de objetivo (solo entonces se recalcula la ruta)
            return;
        }
        if (Rescue)
        {
            PlanRescue();
            _needRoute = true;
            return;
        }
        if (Config.UseGa)
        {
            Plan = new MissionGA(Cost, _rngGa.Fork((ulong)StepCount + 1)).Run(Rover.Pos, ReserveWh, Config.ScienceWeight);
            AddLog("ag", $"AG plan de misión (viajero+mochila, {Plan.Generations} gen, {Plan.Ms:0} ms): {Plan.Note}");
        }
        else
        {
            // sin AG: vecino más cercano sobre todos los sitios, sin mochila
            Plan = new MissionPlan { Note = "Sin AG: orden por vecino más cercano, todos los sitios", MassOk = true, EnergyOk = true };
            var left = Map.Sites.Where(x => !x.Visited).ToList();
            var cur = Rover.Pos;
            while (left.Count > 0)
            {
                var nx = left.OrderBy(x => x.Cell.Dist(cur)).First();
                Plan.Order.Add(nx);
                left.Remove(nx);
                cur = nx.Cell;
            }
            Plan.FinalRefuge = Map.Refuges.OrderBy(rf => rf.Dist(cur)).First();
            Plan.Empty = Plan.Order.Count == 0;
            AddLog("ag", Plan.Note);
        }
        ChooseNextTarget();
        _needRoute = true;
    }

    private void ChooseNextTarget()
    {
        if (FireMode)
        {
            _needMissionPlan = true;
            return;
        }
        if (Rescue)
        {
            ChooseRescueTarget();
            return;
        }
        var next = Plan?.Order.FirstOrDefault(x => !x.Visited);
        if (next != null) SetTarget(next.Cell, $"{next.Name} · {next.Kind}");
        else SetTarget(Plan?.FinalRefuge ?? NearestRefuge, "refugio (fin de misión)");
    }

    private void SetTarget(Cell c, string name)
    {
        Target = c;
        TargetName = name;
    }

    private void PlanRoute(string trigger)
    {
        _needRoute = false;
        if (Rover.Pos == Target)
        {
            Route = new List<Cell> { Rover.Pos };
            RouteIdx = 0;
            return;
        }
        Replans++;
        if (Config.UseGa)
        {
            var ga = new RouteGA(Cost, _rngGa.Fork((ulong)StepCount * 31 + (ulong)Replans), Config.Ga);
            var res = ga.Run(Rover.Pos, Target, ReserveWh, trigger);
            LastGa = res;
            GaHistory.Add(res);
            if (GaHistory.Count > 12) GaHistory.RemoveAt(0);
            GaMsTotal += res.Ms;
            if (res.Best != null)
            {
                Route = res.Best.Path;
                RouteIdx = 0;
                if (Config.ClickMode && HasDestination && OperatorTarget == Target) DestReport = EvaluateDestination(Target, DestReport?.Requested ?? Target);
                AddLog("ag", $"AG ruta → {TargetName} [{trigger}]: {res.Generations} gen{(res.EarlyStop ? " (parada temprana)" : "")}, {res.Ms:0} ms · " +
                             string.Join(" · ", res.Candidates.Select(c => $"{c.Label} f={c.Fitness:0.00}{(c.Feasible ? "" : " ✗")}")) + $" → elegida {res.Best.Label}");
            }
            else
            {
                Route = new List<Cell> { Rover.Pos };
                RouteIdx = 0;
                AddLog("veto", $"AG: {res.Note}");
                HandleNoRoute();
            }
        }
        else
        {
            var path = Cost.Shortest(Rover.Pos, Target, 0f);
            LastGa = null;
            if (path != null)
            {
                Route = path;
                RouteIdx = 0;
                AddLog("ag", $"Sin AG: ruta de menor distancia ({path.Count - 1} pasos) [{trigger}]");
            }
            else
            {
                Route = new List<Cell> { Rover.Pos };
                RouteIdx = 0;
                HandleNoRoute();
            }
        }
    }

    private void HandleNoRoute()
    {
        if (FireMode)
        {
            AddLog("alerta", $"Sin ruta conocida hasta {TargetName}: se reasigna");
            _needMissionPlan = true;
            return;
        }
        if (Rescue)
        {
            var vv = Victims.FirstOrDefault(v => v.Cell == Target && v.Active && !v.OnBoard);
            if (vv != null)
            {
                vv.Unreachable = true;
                AddLog("alerta", $"{vv.Name}: sin ruta conocida hasta la persona; se replanifica el rescate");
            }
            _needMissionPlan = true;
            return;
        }
        if (Config.ClickMode && !_goingRefuge)
        {
            AddLog("veto", $"Destino {Target} inalcanzable con la información actual: el rover se detiene y espera otra orden");
            HasDestination = false;
            TargetName = "sin destino";
            return;
        }
        // objetivo inalcanzable en el mapa estimado: descartar el sitio o, si era el refugio, esperar
        var site = Map.Sites.FirstOrDefault(x => x.Cell == Target && !x.Visited);
        if (site != null && Plan != null)
        {
            AddLog("alerta", $"{site.Name} inalcanzable con la información actual: se descarta y se replanifica la misión");
            Plan.Order.Remove(site);
            ChooseNextTarget();
            _needRoute = true;
            _replanTrigger = "sitio inalcanzable";
        }
        else
        {
            AddLog("alerta", "No existe ruta conocida al refugio: se espera y se vuelve a medir (sin prometer rescate)");
        }
    }

    // ================================================================== ejecución y física
    private void Execute(Decision d, Cell? proposal, bool click)
    {
        float dt;
        bool moved = false;
        float driveJ = 0;
        float moveM = 0;
        bool recharge = false, safe = false;
        switch (d.Action)
        {
            case RoverAction.Avanzar when proposal is Cell to && Heli:
            {
                Rover.Dir = to - Rover.Pos;
                (dt, driveJ, moveM) = HeliFly(to, d.Speed);
                Rover.LastSpeed = d.Speed;
                moved = true;
                Rover.Pos = to;
                RouteIdx = NextRouteIdx(to);
                Trail.Add(to);
                DistanceM += moveM;
                Rover.OdometerM += moveM;
                Rover.EstX = Rover.TrueX;   // GNSS a bordo: sin deriva de odometría
                Rover.EstY = Rover.TrueY;
                break;
            }
            case RoverAction.Avanzar when proposal is Cell to:
            {
                var dir = to - Rover.Pos;
                Rover.Dir = dir;
                float v = MathF.Max(0.05f, d.Speed) * Rover.VmaxMs;
                moveM = dir.IsDiagonal ? Map.CellM * 1.41421f : Map.CellM;
                dt = moveM / v;
                driveJ = DriveEnergy(Rover.Pos, to, moveM, v);
                Rover.LastSpeed = d.Speed;
                var truth = Map.ClassAt(to);
                float realDrop = (Map.Height(Rover.Pos) - Map.Height(to)) * Map.DropScale;
                if (truth == TerrainClass.Barranco && realDrop < WorldMap.CliffDropM * 0.6f && Map.Slope[Map.Idx(to)] < 40f)
                {
                    // borde suave de un cráter/barranco: no hay caída, pero el fondo no es transitable → atasco y retroceso
                    SafetyViolations++;
                    Rover.Integrity = MathF.Max(0, Rover.Integrity - 15f);
                    Belief.ConfirmCliff(to);
                    LastCollision = true;
                    AddLog("veto", $"El rover entra en terreno no transitable ({WorldMap.HazardName(Map.HazardAt(to))}) en {to}: atasco y retroceso, integridad −15 %");
                    _needRoute = true;
                    _replanTrigger = "atasco en terreno no transitable";
                    break;
                }
                if (truth == TerrainClass.Barranco)
                {
                    SafetyViolations++;
                    Falls++;
                    Rover.Pos = to;
                    Trail.Add(to);
                    Fail($"caída en {WorldMap.HazardName(Map.HazardAt(to))} en {to}: el sistema no detectó el precipicio");
                    break;
                }
                if (truth == TerrainClass.Obstaculo || (!Map.ProbeMode && Map.Slope[Map.Idx(to)] > WorldMap.MaxSlopeDeg + 10))
                {
                    SafetyViolations++;
                    Rover.Collisions++;
                    LastCollision = true;
                    Rover.Integrity = MathF.Max(0, Rover.Integrity - 30f);
                    Belief.ConfirmObstacle(to);
                    AddLog("veto", $"¡COLISIÓN con {WorldMap.HazardName(Map.HazardAt(to))} en {to}! Integridad −30 %");
                    _needRoute = true;
                    _replanTrigger = "colisión";
                    break;
                }
                float slipP = WorldMap.SlipChance(Map.SoilAt(to)) * (1 + Env.SoilMoisture * 2) + MathF.Max(0, MathF.Abs(Map.DirSlope(Rover.Pos, to)) - 18) / 60f;
                if (_rngPhys.Chance(slipP * (0.5f + d.Speed)))
                {
                    LastSlip = true;
                    Rover.Slips++;
                    AddLog("alerta", $"Patinaje en {WorldMap.SoilName(Map.SoilAt(to))}: el rover no avanza (energía gastada)");
                    OdometryNoise(moveM * 0.3f, true);
                    break;
                }
                moved = true;
                Rover.Pos = to;
                RouteIdx = NextRouteIdx(to);
                Trail.Add(to);
                DistanceM += moveM;
                Rover.OdometerM += moveM;
                OdometryNoise(moveM, false);   // usa el rumbo nuevo (Rover.Dir ya actualizado)
                if (Rover.Probe) ProbeDrift();
                break;
            }
            case RoverAction.Muestrear when FireMode:
                dt = DoFireAction();
                if (Heli) driveJ = HeliWaitJ(dt);
                break;
            case RoverAction.Muestrear when Rescue:
                dt = DoRescueAction();
                if (Heli) driveJ = HeliWaitJ(dt);
                break;
            case RoverAction.Muestrear:
                dt = 90f;
                if (Heli)
                {
                    driveJ = HeliSampleJ(ref dt);
                    AddLog("info", $"Helicóptero en {Rover.Pos}: {HeliLzNote}");
                }
                TakeSample();
                break;
            case RoverAction.Recargar:
                dt = 120f;
                recharge = true;
                if (Heli) { HeliLanded = true; HeliAglM = 0; HeliPowerW = HeliModel.GroundIdleW * 0.3f; }
                break;
            case RoverAction.ModoSeguro:
                dt = 60f;
                safe = true;
                break;
            default:
                dt = 20f;
                Rover.LastSpeed = 0;
                if (Heli) driveJ = HeliWaitJ(dt);
                break;
        }
        if (Heli) UpdateHeliAir();
        if (!moved && d.Action != RoverAction.Avanzar) Rover.LastSpeed = 0;
        LastMoved = moved;
        LastStepSeconds = dt;
        PhysicsUpdate(dt, driveJ, recharge, safe, moved, moveM);

        // llegada
        if (moved && Rescue && Rover.Pos == Target &&
            (Victims.Any(v => v.Cell == Rover.Pos && v.Active && !v.OnBoard) || Medical.Any(m => m.Cell == Rover.Pos) && OnBoard > 0))
            PendingSample = true;   // rescatar o entregar en el siguiente paso
        if (moved && FireMode && Rover.Pos == Target) PendingSample = true;   // descargar o cargar agua
        if (moved && Rover.Pos == Target)
        {
            var site = Map.Sites.FirstOrDefault(x => x.Cell == Rover.Pos && !x.Visited);
            if (site != null) PendingSample = true;
        }
        if (moved && !PendingSample)
        {
            var site = Map.Sites.FirstOrDefault(x => x.Cell == Rover.Pos && !x.Visited);
            if (site != null && click) PendingSample = true;   // destino por clic: muestrear al pisar un sitio
        }
        if (moved && click && HasDestination && Rover.Pos == Target)
        {
            HasDestination = false;
            AddLog("ok", $"Destino alcanzado: {TargetName} {Target} · batería {Rover.BatteryPct:0} % · {Map.Elevation(Rover.Pos):0} m · {WorldMap.SoilName(Map.SoilAt(Rover.Pos))}");
            TargetName = PendingSample ? TargetName : "sin destino";
        }
        if (moved && Map.Refuges.Contains(Rover.Pos))
        {
            Rover.EstX = Rover.TrueX;
            Rover.EstY = Rover.TrueY;
            AddLog("ok", $"Refugio {Rover.Pos}: relocalización (error de odometría → 0 m) y carga disponible");
        }
    }

    /// <summary>Índice de la celda alcanzada en la ruta, buscando hacia adelante (una ruta podría repetir celdas).</summary>
    private int NextRouteIdx(Cell to)
    {
        if (Route.Count == 0) return RouteIdx;
        int k = Route.IndexOf(to, Math.Clamp(RouteIdx + 1, 0, Route.Count - 1));
        if (k < 0) k = Route.IndexOf(to);
        return k >= 0 ? k : RouteIdx;
    }

    private float DriveEnergy(Cell a, Cell b, float d, float v)
    {
        float g = Planet.Gravity;
        float theta = MathF.Atan2(Map.Height(b) - Map.Height(a), d);
        float crr = WorldMap.RollingCoeff(Map.SoilAt(b)) * (1 + Env.SoilMoisture * 0.8f);
        float rolling = Rover.MassKg * g * crr * MathF.Cos(theta);
        float grade = Rover.MassKg * g * MathF.Sin(theta);
        // viento: componente en contra del avance
        float windDirRad = Env.WindDirDeg * MathF.PI / 180f;
        var dir = b - a;
        float headRad = MathF.Atan2(dir.X, -dir.Y);
        float headwind = Env.WindSpeed * MathF.Cos(windDirRad - headRad + MathF.PI);   // viento "desde" la dirección
        float vRel = v + headwind;
        float drag = 0.5f * Planet.AirDensity * 1.2f * 2f * vRel * MathF.Abs(vRel);
        float force = MathF.Max(rolling * 0.2f, rolling + grade + drag);
        if (Rover.Probe) force = 0.5f * Planet.AirDensity * 0.8f * 3f * v * v + 30f;   // empuje del aerostato
        return force * d / Rover.Efficiency;
    }

    private void OdometryNoise(float meters, bool slip)
    {
        float sigma = meters * (0.01f + (slip ? 0.08f : 0) + WorldMap.SlipChance(Map.SoilAt(Rover.Pos)) * 0.2f);
        float h = Rover.HeadingDeg * MathF.PI / 180f;
        float dx = slip ? 0 : MathF.Sin(h) * meters, dy = slip ? 0 : -MathF.Cos(h) * meters;
        // la estimación integra el desplazamiento medido con ruido: el error acumulado es la deriva de odometría
        Rover.EstX += dx + _rngPhys.Gauss(0, sigma);
        Rover.EstY += dy + _rngPhys.Gauss(0, sigma);
    }

    private void ProbeDrift()
    {
        float pct = Env.WindGust / MathF.Max(1, Planet.WindLimit);
        if (!_rngPhys.Chance(MathX.Clamp01((pct - 0.5f) * 0.5f))) return;   // SUPUESTO: los propulsores compensan parte de la ráfaga
        float r = Env.WindDirDeg * MathF.PI / 180f;
        var push = new Cell(-(int)MathF.Round(MathF.Sin(r)), (int)MathF.Round(MathF.Cos(r)));   // hacia donde sopla
        var to = Rover.Pos + push;
        if (push == new Cell(0, 0) || !Map.InBounds(to)) return;
        if (Map.ClassAt(to) != TerrainClass.Libre)
        {
            Rover.Integrity = MathF.Max(0, Rover.Integrity - 4f);
            AddLog("alerta", $"Ráfaga empuja la sonda contra {WorldMap.HazardName(Map.HazardAt(to))}: los propulsores la frenan, turbulencia, integridad −4 %");
            return;
        }
        Rover.Pos = to;
        Trail.Add(to);
        AddLog("alerta", $"Ráfaga de {Env.WindGust:0} m/s: desviación de trayectoria a {to}; se replanifica");
        _needRoute = true;
        _replanTrigger = "desviación por viento";
    }

    private void TakeSample()
    {
        PendingSample = false;
        var site = Map.Sites.FirstOrDefault(x => x.Cell == Rover.Pos && !x.Visited);
        if (site == null) return;
        if (Rover.SampleKg + site.MassKg > Rover.SampleCapKg + 1e-3f)
        {
            AddLog("alerta", $"{site.Name}: bodega llena ({Rover.SampleKg:0.0}/{Rover.SampleCapKg:0} kg) — solo análisis in situ, sin muestra");
        }
        else Rover.SampleKg += site.MassKg;
        site.Visited = true;
        Rover.Visited.Add(site.Id);
        Rover.Science += site.Value;
        Rover.BatteryWh = MathF.Max(0, Rover.BatteryWh - site.AnalysisWh);
        Samples++;
        Rover.EstX = Rover.TrueX;
        Rover.EstY = Rover.TrueY;
        AddLog("ok", $"Muestra en {site.Name} ({site.Kind}): valor {site.Value:0.00}, {site.MassKg:0.0} kg, análisis {site.AnalysisWh:0.0} Wh");
        if (site.Anomaly)
            AddLog("evento", $"{site.Name}: SEÑAL ESPECTRAL ANÓMALA (biofirma simulada). Se archiva para análisis en la Tierra — NO confirma vida.");
        _needMissionPlan = !Config.ClickMode;
        _needRoute = true;
        _replanTrigger = $"objetivo {site.Name} completado";
        if (Config.ClickMode && !HasDestination) TargetName = "sin destino";
    }

    private void PhysicsUpdate(float dt, float driveJ, bool recharge, bool safe, bool moved, float moveM)
    {
        var p = Planet;
        float tExt = Env.TempAt(Rover.Pos);
        // térmica: modelo de primer orden con calefactor y refrigeración activa (SUPUESTOS)
        float setpoint = safe ? -30f : 5f;   // hibernación: consigna de supervivencia para ahorrar energía
        float heater = MathX.Clamp((setpoint - Rover.TempInt) * 15f, 0, 150f);
        float cooler = MathX.Clamp((Rover.TempInt - 40f) * 15f, 0, 180f);
        float coolEff = tExt < Rover.TempInt + 20 ? 1f : 0.2f;   // los radiadores no disipan hacia un ambiente más caliente
        const float cTh = 15000f;   // J/K
        float driveW = dt > 0 ? driveJ / dt : 0;
        float rtgHeat = Rover.RtgW * 1.5f;   // calor residual del generador de radioisótopos (sonda)
        float dT = ((tExt + 15f - Rover.TempInt) / p.ThermalTauS + (heater * 0.95f + rtgHeat - cooler * coolEff + driveW * (Heli ? 0.002f : 0.04f)) / cTh) * dt;   // el rotor disipa al aire, no en la electrónica
        Rover.TempInt += dT;
        Rover.HeaterW = heater;
        Rover.CoolerW = cooler;

        float solar = Env.SolarIrradiance() * Rover.PanelM2 * Rover.PanelEff * (1 - Rover.PanelDust) * (recharge ? 1.25f : 1f);
        bool atRefuge = Map.Refuges.Contains(Rover.Pos);
        // helipuerto: carga rápida; en emergencias (rescate e incendio) repostaje en caliente (~10 min)
        float charger = (atRefuge ? (Heli ? (Rescue || FireMode ? 60000f : 9000f) : 600f) : 0f) + Rover.RtgW;
        float baseW = safe ? 15f : Rover.BaseW;
        float loadJ = (baseW + heater + cooler) * dt + driveJ;
        float gainJ = (solar + charger) * dt;
        Rover.SolarW = solar;
        Rover.DriveW = driveW;
        Rover.LoadW = loadJ / MathF.Max(1, dt);
        Rover.BatteryWh = MathX.Clamp(Rover.BatteryWh + (gainJ - loadJ) / 3600f, 0, Rover.CapacityWh);

        // desgaste
        float wear = (moved ? Map.Rough[Map.Idx(Rover.Pos)] * 0.04f * (0.5f + Rover.LastSpeed) : 0) + Env.QuakeAccel * 0.15f;
        Rover.MechWear += wear;
        Rover.Corrosion += p.CorrosionPerStep;
        Rover.Integrity = MathF.Max(0, Rover.Integrity - wear - p.CorrosionPerStep);
        Rover.PanelDust = MathX.Clamp01(Rover.PanelDust + Env.DustOpacity * 0.0004f * dt / 20f - (Env.WindGust > 15 ? 0.002f : 0));
        Rover.DoseMsv += Env.RadiationAt(Rover.Pos) * dt / 3600f;

        // fallo espontáneo de sensor (radiación / polvo), muy poco probable
        if (_rngEvents.Chance(0.0003f * (1 + Rover.DoseMsv) * (1 + Env.DustOpacity)))
        {
            string id = SensorSuite.FaultCandidates[_rngEvents.Int(0, SensorSuite.FaultCandidates.Length)];
            Sensors.InjectFault(id, FaultMode.Ruido);
            Env.Add(EventKind.FalloSensor, 0.4f, $"{id}: ruido excesivo (espontáneo)");
            AddLog("fallo", $"Fallo espontáneo: {id} con ruido excesivo");
        }

        Env.Step(dt);
        MoveAlien();
        AutoEvents();
        if (Rover.BatteryWh <= 0.001f) Fail("batería agotada (0 %)");
        if (Rover.Integrity <= 0) Fail("integridad mecánica 0 % (daño crítico)");
    }

    private void AutoEvents()
    {
        // remoción en masa (Tierra) cuando la lluvia diaria supera el periodo de retorno de 2 años
        if (Planet.HasRain && Env.Rain24h > Map.Geo.RainT(2) && !Env.Has(EventKind.Remocion) && _rngEvents.Chance(0.03f))
            Inject(EventKind.Remocion, "lluvia acumulada sobre el umbral T=2 años");
        // sismo espontáneo que altera el mapa
        if (Env.Get(EventKind.Sismo) is { } q && q.Remaining == 5 && q.Detail == "sismo espontáneo")
        {
            var ch = Map.QuakeAlter(_rngEvents, q.Severity);
            Belief.Forget(Rover.Pos, 4);
            AddLog("evento", $"Sismo espontáneo ({q.Severity * 3.2f:0.0} m/s²): {ch.Count} caídas de roca; se olvida la zona cercana y se replanifica");
            _needRoute = true;
            _replanTrigger = "sismo";
        }
    }

    private void MoveAlien()
    {
        if (AlienPos is not Cell a) return;
        if (!Env.Has(EventKind.Alien))
        {
            if (Map.HazardAt(a) == Hazard.Alien) Map.Hazards[Map.Idx(a)] = Hazard.None;
            AlienPos = null;
            Map.Version++;
            AddLog("evento", "El objeto no identificado se aleja del área (narrativa ficticia)");
            return;
        }
        var options = Cell.Dirs8.Select(d => a + d).Where(c => Map.InBounds(c) && Map.Hazards[Map.Idx(c)] == Hazard.None && c.Chebyshev(Rover.Pos) >= 2 && !Map.Refuges.Contains(c)).ToList();
        if (options.Count == 0 || !_rngEvents.Chance(0.6f)) return;
        // se mueve de forma errática pero tiende a cruzarse con la ruta del rover
        var next = Route.Count > RouteIdx + 3 && _rngEvents.Chance(0.4f)
            ? options.OrderBy(c => c.Dist(Route[Math.Min(Route.Count - 1, RouteIdx + 3)])).First()
            : options[_rngEvents.Int(0, options.Count)];
        Map.Hazards[Map.Idx(a)] = Hazard.None;
        Map.Hazards[Map.Idx(next)] = Hazard.Alien;
        Belief.Forget(a, 0);
        AlienPos = next;
        Map.Version++;
    }

    private void UpdateRefugeEstimate()
    {
        float best = float.MaxValue;
        Cell bestR = Map.Refuges[0];
        foreach (var r in Map.Refuges)
        {
            var path = Cost.Shortest(Rover.Pos, r, 1.5f);
            if (path == null) continue;
            float wh = 0, t = 0;
            for (int i = 1; i < path.Count; i++)
            {
                wh += Cost.StepWh(path[i - 1], path[i]);
                t += Cost.StepTimeS(path[i - 1], path[i]);
            }
            // calefacción/refrigeración durante el trayecto y margen de incertidumbre del mapa estimado (×1,25)
            wh = (wh + (Rover.HeaterW + Rover.CoolerW) * t / 3600f) * 1.25f;
            if (wh < best)
            {
                best = wh;
                bestR = r;
            }
        }
        RefugeReachable = best < float.MaxValue;
        EnergyToRefuge = RefugeReachable ? best : float.PositiveInfinity;
        NearestRefuge = bestR;
    }

    private void UpdateState(Decision d)
    {
        if (Finished) return;
        if (Config.ClickMode)
        {
            if (Map.Sites.All(x => x.Visited) && Map.Sites.Count > 0 && Map.Refuges.Contains(Rover.Pos) && !HasDestination && !PendingSample && !Charging)
            {
                State = MissionState.Exito;
                EndReason = $"todas las muestras ({Samples}, valor {Rover.Science:0.00}) y retorno seguro al refugio";
                AddLog("ok", $"ÉXITO: {EndReason}");
                return;
            }
        }
        bool planDone = Plan == null ? Target == NearestRefuge || Map.Refuges.Contains(Target) : Plan.Order.All(x => x.Visited);
        if (FireOver(out bool fireOk, out string fireWhy))
        {
            State = fireOk ? MissionState.Exito : MissionState.Fracaso;
            EndReason = fireWhy + (Fleet.Count > 0 ? $" · {FleetSummary()}" : "");
            AddLog(fireOk ? "ok" : "fallo", $"{(fireOk ? "ÉXITO" : "FRACASO")}: {EndReason}");
            return;
        }
        if (Rescue && RescueDone)
        {
            State = Saved > 0 ? MissionState.Exito : MissionState.Fracaso;
            EndReason = $"rescate: {Saved} de {Victims.Count} personas entregadas con vida" + (LostCount > 0 ? $", {LostCount} no sobrevivieron" : "") +
                        (Victims.Any(v => v.Unreachable) ? $", {Victims.Count(v => v.Unreachable)} inalcanzables" : "") + (Fleet.Count > 0 ? $" · {FleetSummary()}" : "");
            AddLog(Saved > 0 ? "ok" : "fallo", $"{(Saved > 0 ? "ÉXITO" : "FRACASO")}: {EndReason}");
            return;
        }
        if (!Config.ClickMode && !Rescue && !FireMode && Map.Refuges.Contains(Rover.Pos) && planDone && Map.Refuges.Contains(Target) && StepCount > 1 && !PendingSample && !Charging)
        {
            State = MissionState.Exito;
            int left = Map.Sites.Count(x => !x.Visited);
            EndReason = Samples == 0 ? "retorno seguro al refugio (misión sin muestras alcanzables)"
                      : $"{Samples} muestras (valor {Rover.Science:0.00}) y retorno seguro" + (left > 0 ? $"; {left} sitios descartados por energía/alcance" : "");
            AddLog("ok", $"ÉXITO: {EndReason}");
            return;
        }
        if (Planet.Style == TerrainStyle.Venus && Rover.Integrity < 20)
        {
            AddLog("alerta", "Venus: límite de exposición alcanzado por corrosión — se aborta la misión (modo seguro permanente)");
            Fail("límite de exposición en Venus (corrosión)");
            return;
        }
        var prev = State;
        State = d.EnterSafeMode || d.Action == RoverAction.ModoSeguro ? MissionState.ModoSeguro
              : Charging || d.GoRefuge || (Plan == null && Map.Refuges.Contains(Target) && TargetName.Contains("energía")) ? MissionState.Refugio
              : d.Veto || (LastGa != null && LastGa.Trigger.Length > 0 && Replans > 0 && Log.Count > 0 && Log[^1].Kind == "ag" && Log[^1].Step == StepCount) ? MissionState.Replanificacion
              : d.Alert || Env.Events.Any(e => e.Kind is EventKind.Sismo or EventKind.TormentaPolvo or EventKind.Tormenta or EventKind.Meteorito or EventKind.FalloSensor) ? MissionState.Alerta
              : MissionState.Exploracion;
        if (State != prev) AddLog(State is MissionState.ModoSeguro or MissionState.Refugio ? "alerta" : "info", $"Estado: {StateName(prev)} → {StateName(State)}");
    }

    private void Fail(string reason)
    {
        if (Finished) return;
        State = MissionState.Fracaso;
        EndReason = reason;
        AddLog("fallo", $"FRACASO: {reason}. Postmortem en la traza (paso {StepCount}).");
    }

    /// <summary>Nombre del mundo para el HUD y los informes (escenario GeoClick o cuerpo).</summary>
    public string WorldName => Map.IsScenario ? $"Tierra · GeoClick {Map.Geo.Id}" : Planet.Name;

    private string[] ScenarioNotes()
    {
        var g = Map.Geo;
        return new[]
        {
            $"Elevación {g.ElevMin:0}–{g.ElevMax:0} m (mediana {g.ElevMedian:0}); pendiente mediana {g.SlopeMedian:0.0}° · {g.Km2Over25:0.0} km² sobre 25° (DEM GLO-30).",
            $"Cobertura dominante: {string.Join(", ", g.Cover.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key.ToLowerInvariant()} {kv.Value:0.0} km²"))} (ESA WorldCover).",
            $"Lluvia normal {g.AnnualRainMm:0} mm/año; máx. diaria {g.MaxDailyMm:0.#} mm ({g.MaxDailyDate}); T=10 años {g.RainT(10):0.#} mm/día.",
            $"Temperatura media {g.TempMeanC:0.0} °C; {g.FrostDaysYear:0.#} heladas/año · cicatrices {g.ScarsHa:0.#} ha · área quemada {g.BurnedHa:0.#} ha.",
            Heli ? $"El área mide {Map.N * Map.CellM / 1000f:0.0} km de lado: el helicóptero la cruza en ~{Map.N * Map.CellM / HeliModel.CruiseMs / 60f:0} min." : $"Vehículo: rover terrestre de campo (2 m/s, 6 kWh) — el área mide {Map.N * Map.CellM / 1000f:0.0} km de lado.",
        };
    }

    public static string StateName(MissionState s) => s switch
    {
        MissionState.Configuracion => "CONFIGURACIÓN", MissionState.Exploracion => "EXPLORACIÓN", MissionState.Alerta => "ALERTA",
        MissionState.Replanificacion => "REPLANIFICACIÓN", MissionState.Refugio => "REFUGIO", MissionState.ModoSeguro => "MODO SEGURO",
        MissionState.Exito => "ÉXITO", MissionState.Fracaso => "FRACASO", _ => s.ToString(),
    };

    // ================================================================== eventos inyectables
    public void Inject(EventKind kind, string why = "operador")
    {
        var p = Planet;
        switch (kind)
        {
            case EventKind.Sismo:
            {
                Env.Add(EventKind.Sismo, 0.85f, why);
                var ch = Map.QuakeAlter(_rngEvents, 0.85f);
                Belief.Forget(Rover.Pos, 5);
                AddLog("evento", $"SISMO inyectado ({(p.Style == TerrainStyle.Marte ? "marsquake" : "temblor")}): picos de aceleración, {ch.Count} caídas de roca; se olvidan las celdas cercanas");
                RequestReplan("sismo");
                break;
            }
            case EventKind.Tormenta:
            case EventKind.TormentaPolvo:
                if (p.Style == TerrainStyle.Marte || (p.Style != TerrainStyle.Tierra && kind == EventKind.TormentaPolvo))
                {
                    Env.Add(EventKind.TormentaPolvo, 0.9f, why);
                    AddLog("evento", "TORMENTA DE POLVO: baja la visibilidad y la producción solar; se reduce el avance");
                }
                else if (p.Style == TerrainStyle.Tierra)
                {
                    Env.Add(EventKind.Tormenta, 0.9f, $"lluvia intensa (≈ T=10 años: {Map.Geo.RainT(10)} mm/día)");
                    AddLog("evento", $"TORMENTA DE LLUVIA: intensidad creciente; referencia GeoClick T=10 años = {Map.Geo.RainT(10)} mm en un día");
                }
                else if (p.Style == TerrainStyle.Mercurio)
                {
                    Env.Add(EventKind.TormentaSolar, 0.9f, why);
                    AddLog("evento", "EVENTO DE PARTÍCULAS SOLARES: dosis ×25 (Mercurio no tiene atmósfera que proteja)");
                }
                else
                {
                    Env.Add(EventKind.Rafaga, 1f, why);
                    AddLog("evento", "TORMENTA ATMOSFÉRICA: vientos y turbulencia muy por encima de lo normal");
                }
                break;
            case EventKind.Meteorito:
            {
                var center = Route.Count > RouteIdx + 4 ? Route[Math.Min(Route.Count - 1, RouteIdx + 4)] : Rover.Pos + new Cell(_rngEvents.Int(-5, 6), _rngEvents.Int(-5, 6));
                center = Map.Clamp(center);
                if (center.Chebyshev(Rover.Pos) < 3) center = Map.Clamp(Rover.Pos + new Cell(4, -3));
                var ch = Map.Impact(center, _rngEvents);
                Env.Add(EventKind.Meteorito, 0.7f, why, -2, center);
                Env.Add(EventKind.Sismo, 0.35f, "onda del impacto", 3);
                Belief.Forget(center, 3);
                AddLog("evento", $"IMPACTO DE METEORITO en {center}: cráter nuevo ({ch.Count} celdas alteradas), polvo y vibración; replanificación evitando la zona");
                RequestReplan("impacto de meteorito");
                break;
            }
            case EventKind.FalloSensor:
            {
                string id = SensorSuite.FaultCandidates[_faultCycle % 3];
                var mode = new[] { FaultMode.NaN, FaultMode.Saturacion, FaultMode.Atascado, FaultMode.Contradiccion }[(_faultCycle / 3) % 4];
                _faultCycle++;
                Sensors.InjectFault(id, mode);
                Env.Add(EventKind.FalloSensor, 0.7f, $"{id}: {mode}");
                AddLog("fallo", $"FALLO DE SENSOR inyectado: {id} → {mode}. Validación y degradación segura activas");
                break;
            }
            case EventKind.Noche:
                if (Env.ForcedNight)
                {
                    Env.Remove(EventKind.Noche);
                    AddLog("evento", "Noche forzada desactivada");
                }
                else
                {
                    Env.Add(EventKind.Noche, 1f, why);
                    AddLog("evento", "NOCHE FORZADA: sin carga solar, más error óptico, frío");
                }
                break;
            case EventKind.BajaConectividad:
                Env.Add(EventKind.BajaConectividad, 0.8f, why);
                AddLog("evento", "BAJA CONECTIVIDAD: las órdenes externas llegan con retraso; el rover opera con autonomía local");
                break;
            case EventKind.Rafaga:
                Env.Add(EventKind.Rafaga, 0.9f, why);
                AddLog("evento", $"RÁFAGA: viento hasta ≈{p.WindGust * 1.3f:0} m/s{(Rover.Probe ? " (desviación de trayectoria)" : "")}");
                break;
            case EventKind.Biofirma:
            {
                var site = Map.Sites.Where(x => !x.Visited).OrderBy(x => x.Cell.Dist(Rover.Pos)).FirstOrDefault();
                if (site != null) site.Anomaly = true;
                Env.Add(EventKind.Biofirma, 0.6f, site?.Name ?? "");
                AddLog("evento", site == null ? "Señal anómala sin sitio asociado (ficción)" :
                    $"SEÑAL BIOLÓGICA ANÓMALA SIMULADA cerca de {site.Name}: lecturas atípicas; muestra opcional. No se afirma vida.");
                break;
            }
            case EventKind.Alien:
            {
                var c = Map.Clamp(Rover.Pos + new Cell(5, -2));
                for (int k = 0; k < 20 && Map.Hazards[Map.Idx(c)] != Hazard.None; k++) c = Map.Clamp(c + Cell.Dirs8[k % 8]);
                if (AlienPos is Cell old) Map.Hazards[Map.Idx(old)] = Hazard.None;
                Map.Hazards[Map.Idx(c)] = Hazard.Alien;
                AlienPos = c;
                Map.Version++;
                Env.Add(EventKind.Alien, 0.5f, "objeto dinámico");
                AddLog("evento", $"OBJETO NO IDENTIFICADO en {c} (evento narrativo FICTICIO): obstáculo dinámico, se evita");
                RequestReplan("obstáculo dinámico");
                break;
            }
            case EventKind.BateriaBaja:
                Rover.BatteryWh = Rover.CapacityWh * 0.12f;
                AddLog("evento", "BATERÍA AL 12 % (caso CA-03): se prioriza un refugio alcanzable o se declara que no existe alternativa segura");
                _needMissionPlan = true;
                RequestReplan("batería baja");
                break;
            case EventKind.Remocion:
            {
                var steep = Enumerable.Range(0, Map.N * Map.N).Select(i => new Cell(i % Map.N, i / Map.N))
                    .Where(c => c.Dist(Rover.Pos) < 10 && Map.Slope[Map.Idx(c)] > 18).OrderByDescending(c => Map.Slope[Map.Idx(c)]).FirstOrDefault();
                var ch = Map.Landslide(steep == default ? Rover.Pos : steep, _rngEvents);
                Env.Add(EventKind.Remocion, 0.7f, why);
                AddLog("evento", $"REMOCIÓN EN MASA ({why}): {ch.Count} celdas con detritos en laderas > 18° (GeoClick registró {Map.Geo.ScarsHa} ha de cicatrices 2018–2026)");
                RequestReplan("remoción en masa");
                break;
            }
            case EventKind.TormentaSolar:
                Env.Add(EventKind.TormentaSolar, 0.9f, why);
                AddLog("evento", "TORMENTA SOLAR: dosis de radiación ×25");
                break;
        }
    }

    public void RepairSensors()
    {
        foreach (var id in Sensors.Faults.Keys.ToList()) Sensors.InjectFault(id, FaultMode.None);
        Env.Remove(EventKind.FalloSensor);
        AddLog("ok", "Sensores restablecidos (reinicio de subsistemas)");
        if (State == MissionState.ModoSeguro) State = MissionState.Exploracion;
    }

    /// <summary>Obstáculo imprevisto colocado por el operador: el rover NO lo sabe hasta que lo percibe.</summary>
    public void EditMap(Cell c, string what)
    {
        if (!Map.InBounds(c) || c == Rover.Pos) return;
        if (what == "cliff") Map.AddCliff(c);
        else if (what == "rock") Map.AddRock(c);
        else Map.ClearHazard(c);
        AddLog("evento", what switch
        {
            "cliff" => $"Operador agrega un {(Map.ProbeMode ? "descendente" : "barranco")} en {c} (el rover aún no lo sabe)",
            "rock" => $"Operador agrega un obstáculo en {c}",
            _ => $"Operador despeja la celda {c}",
        });
    }

    // ================================================================== traza y exportación
    private void RecordTrace(Decision d, float ms)
    {
        var s = Reading!;
        string Ev() => string.Join("|", Env.Events.Select(e => e.Kind.ToString()));
        var row = new TraceRow
        {
            Step = StepCount, TimeS = Env.TimeS, State = StateName(State), Action = SafetySupervisor.ActionName(d.Action), Code = d.Code, Reason = d.Reason,
            Row = Rover.Pos.Y, Col = Rover.Pos.X, TargetRow = Target.Y, TargetCol = Target.X, Speed = d.Action == RoverAction.Avanzar ? d.Speed : 0,
            BatteryPct = Rover.BatteryPct, BatteryWh = Rover.BatteryWh, TempExt = Env.TempAt(Rover.Pos), TempInt = Rover.TempInt,
            Wind = Env.WindSpeed, Gust = Env.WindGust, Rain = Env.RainMmH, Visibility = Env.Visibility, Slope = Map.Slope[Map.Idx(Rover.Pos)],
            Pressure = Env.PressureAt(Rover.Pos), Radiation = Env.RadiationAt(Rover.Pos), Integrity = Rover.Integrity,
            PLibre = FrontProbs[0], PObst = FrontProbs[1], PBarr = FrontProbs[2], Confidence = FrontProbs.Max(),
            FuzzySpeed = LastFuzzy?.Crisp["velocidad"] ?? 0.6f, FuzzyCaution = LastFuzzy?.Crisp["cautela"] ?? 0.5f,
            TopRules = LastFuzzy == null ? "(sin difuso)" : string.Join(" ", LastFuzzy.Fired.Take(3).Select(f => $"{f.Rule.Id}:{f.Strength:0.00}")),
            GaBest = LastGa?.Best?.Fitness ?? float.NaN, Route = LastGa?.Best?.Label ?? (Config.UseGa ? "-" : "menor-dist"),
            DecisionMs = ms, Events = Ev(),
        };
        string cls = NeuralPerception.ClassNames[Array.IndexOf(FrontProbs, FrontProbs.Max())];
        row.Explanation = $"{SafetySupervisor.ActionName(d.Action)} {(d.Action == RoverAction.Avanzar ? d.Speed.ToString("0.00") : "")} | " +
                          (FrontCell != null ? $"{(UseRna ? "RNA" : "regla")} {cls} {FrontProbs.Max():0.00} | " : "") +
                          (LastFuzzy != null ? $"DIF v={row.FuzzySpeed:0.00} {row.TopRules} | " : "") +
                          (LastGa?.Best != null ? $"AG ruta {LastGa.Best.Label} f={LastGa.Best.Fitness:0.00} | " : "") + d.Explanation;
        Trace.Add(row);
        if (d.Action != RoverAction.Avanzar || StepCount % 5 == 0)
        {
            if (d.Action is RoverAction.Esperar && d.Code == "S-20") { }
            else AddLog(d.Veto ? "veto" : d.Action == RoverAction.Avanzar ? "info" : "alerta", row.Explanation);
        }
    }

    public string TraceCsv()
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.AppendLine("paso,t_s,estado,accion,codigo,motivo,fila,col,obj_fila,obj_col,velocidad,bateria_pct,bateria_wh,t_ext,t_int,viento,rafaga,lluvia_mmh,visibilidad_m,pendiente,presion_bar,radiacion_msvh,integridad,p_libre,p_obst,p_barr,confianza,dif_vel,dif_cautela,reglas,ag_fitness,ruta,ms_decision,eventos,explicacion");
        string Q(string s) => "\"" + s.Replace("\"", "'") + "\"";
        foreach (var r in Trace)
            sb.AppendLine(string.Join(",", new[]
            {
                r.Step.ToString(ci), r.TimeS.ToString("0.0", ci), Q(r.State), Q(r.Action), r.Code, Q(r.Reason), r.Row.ToString(ci), r.Col.ToString(ci),
                r.TargetRow.ToString(ci), r.TargetCol.ToString(ci), r.Speed.ToString("0.000", ci), r.BatteryPct.ToString("0.00", ci), r.BatteryWh.ToString("0.00", ci),
                r.TempExt.ToString("0.0", ci), r.TempInt.ToString("0.0", ci), r.Wind.ToString("0.0", ci), r.Gust.ToString("0.0", ci), r.Rain.ToString("0.00", ci),
                r.Visibility.ToString("0", ci), r.Slope.ToString("0.0", ci), r.Pressure.ToString("0.00000", ci), r.Radiation.ToString("0.00000", ci),
                r.Integrity.ToString("0.0", ci), r.PLibre.ToString("0.000", ci), r.PObst.ToString("0.000", ci), r.PBarr.ToString("0.000", ci), r.Confidence.ToString("0.000", ci),
                r.FuzzySpeed.ToString("0.000", ci), r.FuzzyCaution.ToString("0.000", ci), Q(r.TopRules), float.IsNaN(r.GaBest) ? "" : r.GaBest.ToString("0.0000", ci), r.Route,
                r.DecisionMs.ToString("0.00", ci), Q(r.Events), Q(r.Explanation),
            }));
        return sb.ToString();
    }

    public Dictionary<string, object> Metrics() => new()
    {
        ["exito"] = State == MissionState.Exito,
        ["estado_final"] = StateName(State),
        ["motivo_final"] = EndReason,
        ["pasos"] = StepCount,
        ["tiempo_mision_s"] = MathF.Round(Env.TimeS),
        ["colisiones"] = Rover.Collisions,
        ["caidas"] = Falls,
        ["barrancos_evitados"] = _cliffsAvoided.Count,
        ["vetos"] = Vetoes,
        ["replanificaciones"] = Replans,
        ["violaciones_seguridad"] = SafetyViolations,
        ["energia_restante_pct"] = MathF.Round(Rover.BatteryPct, 2),
        ["distancia_m"] = MathF.Round(DistanceM, 1),
        ["ciencia"] = MathF.Round(Rover.Science, 3),
        ["muestras"] = Samples,
        ["patinajes"] = Rover.Slips,
        ["integridad_pct"] = MathF.Round(Rover.Integrity, 1),
        ["ms_ag_total"] = MathF.Round(GaMsTotal, 1),
        ["ms_decision_medio"] = StepCount == 0 ? 0 : MathF.Round(DecisionMsTotal / StepCount, 3),
        ["despliegues_dron"] = Drone.Deployments,
        ["rescatadas"] = Saved,
        ["perdidas"] = LostCount,
        ["victimas"] = Victims.Count,
        ["objetivos_dron"] = Drone.ObjectivesFound,
        ["flota"] = FleetSize,
        ["hectareas_quemadas"] = MathF.Round(BurnedHa, 2),
        ["viviendas_perdidas"] = BuildingsLost,
        ["descargas"] = FireDrops + Fleet.Sum(u => u.Drops),
        ["celdas_dron"] = Drone.CellsScanned,
    };

    public string ResultJson()
    {
        var rep = Rna?.Report;
        var obj = new Dictionary<string, object?>
        {
            ["version"] = Version,
            ["fecha"] = "2026-11-07",
            ["configuracion"] = new Dictionary<string, object>
            {
                ["planeta"] = WorldName, ["escenario"] = Map.IsScenario ? Map.Geo.Id : "procedural", ["modo"] = Config.Probe ? "sonda" : "rover", ["semilla"] = Config.Seed, ["dificultad"] = Config.Difficulty,
                ["bateria_inicial_pct"] = Config.BatteryPct, ["prioridad_ciencia"] = Config.ScienceWeight, ["mes"] = Config.Month,
                ["rna"] = Config.UseRna, ["difuso"] = Config.UseFuzzy, ["ag"] = Config.UseGa, ["dron"] = Config.UseDrone, ["politica"] = Config.AblationName, ["modo"] = Config.ClickMode ? "destino por clic" : "autónomo",
                ["ag_parametros"] = new Dictionary<string, object>
                {
                    ["poblacion"] = Config.Ga.Population, ["generaciones"] = Config.Ga.Generations, ["puntos_de_paso"] = Config.Ga.Waypoints,
                    ["torneo"] = Config.Ga.Tournament, ["elitismo"] = Config.Ga.Elite, ["p_cruce"] = Config.Ga.CrossoverP, ["p_mutacion"] = Config.Ga.MutationP,
                    ["w_ciencia"] = Config.Ga.WScience, ["w_distancia"] = Config.Ga.WDistance, ["w_energia"] = Config.Ga.WEnergy, ["w_riesgo"] = Config.Ga.WRisk, ["w_tiempo"] = Config.Ga.WTime,
                },
            },
            ["metricas"] = Metrics(),
            ["rna"] = rep == null ? null : new Dictionary<string, object>
            {
                ["arquitectura"] = "11-32-16-3 ReLU softmax, Adam", ["mapas_entrenamiento"] = rep.TrainMaps, ["mapas_validacion"] = rep.ValMaps, ["mapas_prueba"] = rep.TestMaps,
                ["n_train"] = rep.TrainN, ["n_val"] = rep.ValN, ["n_test"] = rep.TestN, ["mejor_epoca"] = rep.BestEpoch,
                ["acc_test"] = rep.Test!.Accuracy, ["macro_f1_test"] = rep.Test.MacroF1, ["recall_barranco_test"] = rep.Test.Recall[2], ["ece_test"] = rep.Test.Ece, ["baseline_mayoritaria"] = rep.Test.Baseline,
            },
            ["plan"] = Plan == null ? null : new Dictionary<string, object>
            {
                ["orden"] = Plan.Order.Select(x => x.Name).ToArray(), ["valor"] = Plan.Value, ["masa_kg"] = Plan.MassKg, ["energia_wh"] = Plan.EnergyWh, ["nota"] = Plan.Note,
            },
            ["sitios"] = Map.Sites.Select(x => new Dictionary<string, object> { ["id"] = x.Name, ["tipo"] = x.Kind, ["fila"] = x.Cell.Y, ["col"] = x.Cell.X, ["valor"] = x.Value, ["masa_kg"] = x.MassKg, ["visitado"] = x.Visited, ["senal_anomala_simulada"] = x.Anomaly }).ToArray(),
            ["bitacora"] = Log.Select(l => $"T+{l.Step:0000} [{l.Kind}] {l.Text}").ToArray(),
            ["supuestos"] = new[]
            {
                "Celda = 5 m; pendiente máxima de rodadura 30°; precipicio = salto > 5 m.",
                "Rover 900 kg, 0,5 m/s, batería 320 Wh, panel 2,2 m² × 24 %, consumo base 45 W (sonda: 350 kg, 2,5 m/s, 420 Wh).",
                "Día comprimido (SimDayMinutes) para que el ciclo día/noche ocurra en la misión.",
                "Modelo térmico de primer orden con calefactor 150 W y refrigeración 180 W.",
                "Valores de daño, energía, radiación y fallos son hipótesis pedagógicas, no especificaciones NASA.",
            },
        };
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
