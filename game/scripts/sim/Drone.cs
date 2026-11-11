using System;
using System.Collections.Generic;
using System.Linq;
using AresX.AI;
using AresX.Core;

namespace AresX.Sim;

public enum DroneMode { Acoplado, Volando, Escaneando, Regresando }

/// <summary>Resultado del análisis aéreo de una zona.</summary>
public sealed class DroneScan
{
    public Cell Center;
    public int Step, Cells, Rocks, Cliffs, Steep, NewlyObserved;
    public float MeanSlope, MeanRough, Traversable, RockDensity;
    public string? NewSite;
    public string Summary => $"{Cells} celdas · rocas {Rocks} ({RockDensity:P0}) · barrancos {Cliffs} · pendiente media {MeanSlope:0}° · rugosidad {MeanRough:0.00} · transitable {Traversable:P0}" +
                             (NewSite != null ? $" · NUEVO OBJETIVO {NewSite}" : "");
}

/// <summary>
/// Dron explorador que transporta el rover: vuela a una zona, la escanea desde el aire (la RNA analiza las lecturas
/// y el LIDAR aéreo confirma barrancos y rocas), propone objetivos científicos y vuelve a acoplarse para recargar.
/// Solo vuela donde hay atmósfera para sus rotores (Tierra, Marte). Valores de diseño = SUPUESTOS de simulación.
/// </summary>
public sealed class DroneState
{
    public bool Available;
    public string Unavailable = "";
    public DroneMode Mode = DroneMode.Acoplado;
    public float X, Y;                    // posición en celdas (continua)
    public Cell Target;
    public float BatteryPct = 100f;
    public float EnduranceS, SpeedMs, WindLimit;
    public float ScanRadius = 5f, ScanTimeS = 60f, ScanLeftS;
    public const float AltitudeM = 25f;
    public int Deployments, CellsScanned, ObjectivesFound;
    public DroneScan? LastScan;
    public float Convenience;              // salida difusa de la decisión de despliegue
    public FuzzyResult? LastFuzzy;
    public string LastDecision = "";
    public bool Auto = true;
    public readonly List<Cell> Track = new();

    public float CellM = WorldMap.RefCellM;
    public float SpeedCellsPerS => SpeedMs / CellM;
    public bool Flying => Mode != DroneMode.Acoplado;
    public static string ModeName(DroneMode m) => m switch
    {
        DroneMode.Acoplado => "ACOPLADO", DroneMode.Volando => "EN VUELO", DroneMode.Escaneando => "ESCANEANDO", _ => "REGRESANDO",
    };
}

/// <summary>Decisión difusa de despliegue: incertidumbre de la ruta, viento y batería del dron → conveniencia.</summary>
public static class DroneFuzzy
{
    public static FuzzySystem Build()
    {
        var fs = new FuzzySystem();
        fs.Inputs["incertidumbre"] = new FuzzyVar { Id = "incertidumbre", Name = "Incertidumbre de la ruta", Min = 0, Max = 1 }
            .Set("baja", new Trap(0, 0, 0.15f, 0.35f)).Set("media", new Tri(0.2f, 0.45f, 0.7f)).Set("alta", new Trap(0.55f, 0.75f, 1, 1));
        fs.Inputs["viento"] = new FuzzyVar { Id = "viento", Name = "Viento / límite del dron", Unit = "%", Min = 0, Max = 120 }
            .Set("calma", new Trap(0, 0, 25, 45)).Set("moderado", new Tri(30, 55, 80)).Set("fuerte", new Trap(65, 85, 120, 120));
        fs.Inputs["bateria"] = new FuzzyVar { Id = "bateria", Name = "Batería del dron", Unit = "%", Min = 0, Max = 100 }
            .Set("baja", new Trap(0, 0, 30, 45)).Set("media", new Tri(35, 60, 85)).Set("alta", new Trap(70, 90, 100, 100));
        fs.Outputs["conveniencia"] = new FuzzyVar { Id = "conveniencia", Name = "Conveniencia de desplegar", Min = 0, Max = 1 }
            .Set("baja", new Trap(0, 0, 0.2f, 0.4f)).Set("media", new Tri(0.3f, 0.5f, 0.7f)).Set("alta", new Trap(0.6f, 0.8f, 1, 1));
        fs.FallbackValue["conveniencia"] = 0f;
        void R(string id, string why, (string, string)[] c, params (string, string)[] t) => fs.Rules.Add(new FuzzyRule { Id = id, If = c, Then = t, Justification = why });
        R("D1", "Mucho terreno desconocido, aire calmo y dron cargado: el reconocimiento aéreo rinde al máximo.",
            new[] { ("incertidumbre", "alta"), ("viento", "calma"), ("bateria", "alta") }, ("conveniencia", "alta"));
        R("D2", "Incertidumbre media con aire calmo: un vistazo aéreo puede evitar replanificaciones.",
            new[] { ("incertidumbre", "media"), ("viento", "calma") }, ("conveniencia", "media"));
        R("D3", "Si el rover ya conoce el camino, volar no aporta.", new[] { ("incertidumbre", "baja") }, ("conveniencia", "baja"));
        R("D4", "Viento fuerte: riesgo de perder el dron.", new[] { ("viento", "fuerte") }, ("conveniencia", "baja"));
        R("D5", "Batería baja: el dron no alcanzaría a volver.", new[] { ("bateria", "baja") }, ("conveniencia", "baja"));
        R("D6", "Mucha incertidumbre con viento moderado: vale la pena, con cautela.",
            new[] { ("incertidumbre", "alta"), ("viento", "moderado") }, ("conveniencia", "media"));
        R("D7", "Mucha incertidumbre y batería a medias: un vuelo corto.", new[] { ("incertidumbre", "alta"), ("bateria", "media") }, ("conveniencia", "media"));
        R("D8", "Incertidumbre media con viento moderado: mejor seguir explorando por tierra.",
            new[] { ("incertidumbre", "media"), ("viento", "moderado") }, ("conveniencia", "baja"));
        return fs;
    }
}

public sealed partial class Mission
{
    public readonly DroneState Drone = new();
    private readonly FuzzySystem _droneFuzzy = DroneFuzzy.Build();
    private Rng _rngDrone = null!;
    public int DroneScansDone => Drone.Deployments;

    private void InitDrone(ulong seed)
    {
        _rngDrone = new Rng(seed * 7 + 991);
        var d = Drone;
        d.CellM = Map.CellM;
        d.X = Rover.Pos.X;
        d.Y = Rover.Pos.Y;
        if (!Config.UseDrone) { d.Available = false; d.Unavailable = "desactivado en la configuración (ablación)"; return; }
        if (Config.Probe) { d.Available = false; d.Unavailable = "la sonda atmosférica no transporta dron"; return; }
        if (Config.Heli) { d.Available = false; d.Unavailable = "el helicóptero ya hace el reconocimiento aéreo"; return; }
        switch (Planet.Style)
        {
            case TerrainStyle.Tierra:
                d.Available = true; d.EnduranceS = 900; d.SpeedMs = 4f; d.WindLimit = 12f; break;
            case TerrainStyle.Marte:
                d.Available = true; d.EnduranceS = 240; d.SpeedMs = 3f; d.WindLimit = 14f; break;   // estilo Ingenuity (SUPUESTO)
            case TerrainStyle.Mercurio:
                d.Available = false; d.Unavailable = "sin atmósfera: los rotores no tienen aire que empujar"; break;
            case TerrainStyle.Venus:
                d.Available = false; d.Unavailable = "464 °C y 92 bar: fuera de la tolerancia del dron"; break;
            default:
                d.Available = false; d.Unavailable = "no disponible en este cuerpo"; break;
        }
    }

    /// <summary>Restricciones duras para despegar (las evalúa el supervisor del dron).</summary>
    public bool DroneCanDeploy(out string reason)
    {
        var d = Drone;
        reason = "";
        if (!d.Available) { reason = d.Unavailable; return false; }
        if (Finished) { reason = "misión terminada"; return false; }
        if (d.Flying) { reason = "ya está en vuelo"; return false; }
        if (d.BatteryPct < 40) { reason = $"batería del dron {d.BatteryPct:0} % (< 40 %)"; return false; }
        if (Env.WindGust > d.WindLimit) { reason = $"ráfagas {Env.WindGust:0} m/s sobre el límite del dron ({d.WindLimit:0} m/s)"; return false; }
        if (Planet.HasRain && Env.RainMmH > 2) { reason = $"lluvia {Env.RainMmH:0.0} mm/h"; return false; }
        if (Env.DustOpacity > 0.5f) { reason = $"polvo denso ({Env.DustOpacity:0.00})"; return false; }
        if (Env.LinkQuality < 0.3f) { reason = "enlace degradado: no se puede controlar el vuelo"; return false; }
        if (State == MissionState.ModoSeguro) { reason = "rover en modo seguro"; return false; }
        return true;
    }

    /// <summary>Incertidumbre del tramo próximo: fracción no observada y riesgo dudoso en la ruta y alrededor.</summary>
    public float RouteUncertainty(out Cell focus)
    {
        focus = HasDestination || !Config.ClickMode ? Target : Rover.Pos;
        var cells = new List<Cell>();
        if (Route.Count > 1)
            for (int i = RouteIdx + 1; i < Math.Min(Route.Count, RouteIdx + 14); i++) cells.Add(Route[i]);
        else if (HasDestination || !Config.ClickMode)
        {
            var dir = Target - Rover.Pos;
            float len = MathF.Max(1, dir.Length);
            for (int k = 1; k <= 10; k++) cells.Add(Map.Clamp(new Cell(Rover.Pos.X + (int)MathF.Round(dir.X / len * k), Rover.Pos.Y + (int)MathF.Round(dir.Y / len * k))));
        }
        if (cells.Count == 0) return 0;
        focus = cells[Math.Min(cells.Count - 1, 7)];
        int n = 0;
        float unc = 0;
        foreach (var c in cells)
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                var o = new Cell(c.X + dx, c.Y + dy);
                if (!Map.InBounds(o)) continue;
                n++;
                int i = Belief.Idx(o);
                if (!Belief.Observed[i]) unc += 1f;
                else
                {
                    float r = Belief.Risk(o);
                    unc += 4f * r * (1 - r);   // máxima duda con riesgo ≈ 0,5
                }
            }
        return n == 0 ? 0 : MathX.Clamp01(unc / n);
    }

    /// <summary>Decisión difusa de despliegue (se calcula cada paso para mostrarla en el HUD).</summary>
    private void EvaluateDroneDecision()
    {
        var d = Drone;
        if (!d.Available) return;
        float unc = RouteUncertainty(out _);
        d.LastFuzzy = _droneFuzzy.Infer(new Dictionary<string, float>
        {
            ["incertidumbre"] = unc, ["viento"] = 100f * Env.WindGust / MathF.Max(1, d.WindLimit), ["bateria"] = d.BatteryPct,
        });
        d.Convenience = d.LastFuzzy.Crisp["conveniencia"];
    }

    /// <summary>Despliega el dron hacia una celda (o hacia la zona más incierta de la ruta).</summary>
    public bool DeployDrone(Cell? target, string by)
    {
        if (!DroneCanDeploy(out var why))
        {
            Drone.LastDecision = $"No despega: {why}";
            if (by != "auto") AddLog("alerta", $"Dron: no despega ({why})");
            return false;
        }
        var d = Drone;
        RouteUncertainty(out var focus);
        d.Target = Map.Clamp(target ?? focus);
        // alcance: ida + escaneo + vuelta con 30 % de margen
        float distM = new Cell((int)d.X, (int)d.Y).Dist(d.Target) * Map.CellM;
        float needS = (2 * distM / d.SpeedMs + d.ScanTimeS) * 1.3f;
        if (needS / d.EnduranceS * 100f > d.BatteryPct)
        {
            float maxDist = MathF.Max(1, (d.BatteryPct / 100f * d.EnduranceS / 1.3f - d.ScanTimeS) * d.SpeedMs / 2 / Map.CellM);
            var dir = d.Target - Rover.Pos;
            float k = MathF.Min(1, maxDist / MathF.Max(1, dir.Length));
            d.Target = Map.Clamp(new Cell(Rover.Pos.X + (int)(dir.X * k), Rover.Pos.Y + (int)(dir.Y * k)));
            AddLog("alerta", $"Dron: objetivo recortado a {d.Target} por autonomía ({d.BatteryPct:0} % de batería)");
        }
        d.Mode = DroneMode.Volando;
        d.X = Rover.Pos.X;
        d.Y = Rover.Pos.Y;
        d.Track.Clear();
        d.Deployments++;
        d.LastDecision = $"Despegue ({(by == "auto" ? $"automático · conveniencia {d.Convenience:0.00}" : "orden del operador")}) → {d.Target}";
        AddLog("evento", $"DRON despega hacia {d.Target} ({(by == "auto" ? $"decisión difusa: conveniencia {d.Convenience:0.00}" : "orden del operador")}) · batería {d.BatteryPct:0} %");
        return true;
    }

    /// <summary>Orden del operador: regreso inmediato al rover.</summary>
    public bool RecallDrone()
    {
        if (!Drone.Flying || Drone.Mode == DroneMode.Regresando) return false;
        Drone.Mode = DroneMode.Regresando;
        Drone.LastDecision = "Regreso ordenado por el operador";
        AddLog("info", "Dron: regreso ordenado por el operador");
        return true;
    }

    /// <summary>Avanza el dron dt segundos: vuelo, escaneo, regreso, batería y recarga.</summary>
    private void UpdateDrone(float dt)
    {
        var d = Drone;
        if (!d.Available) return;
        if (!d.Flying)
        {
            d.X = Rover.Pos.X;
            d.Y = Rover.Pos.Y;
            d.BatteryPct = MathF.Min(100, d.BatteryPct + dt / 600f * 100f);   // recarga acoplado: 10 min
            if (d.Auto && Config.UseDrone && !Finished && d.Convenience >= 0.6f && DroneCanDeploy(out _))
                DeployDrone(null, "auto");
            return;
        }
        float windPct = Env.WindGust / MathF.Max(1, d.WindLimit);
        d.BatteryPct = MathF.Max(0, d.BatteryPct - dt / d.EnduranceS * 100f * (1 + 0.5f * windPct));
        // abortar si el viento supera el límite o la batería no alcanza para volver
        float backS = new Cell((int)d.X, (int)d.Y).Dist(Rover.Pos) * Map.CellM / d.SpeedMs;
        if (d.Mode != DroneMode.Regresando && (Env.WindGust > d.WindLimit * 1.1f || d.BatteryPct / 100f * d.EnduranceS < backS * 1.3f + 15))
        {
            d.Mode = DroneMode.Regresando;
            AddLog("alerta", Env.WindGust > d.WindLimit * 1.1f ? $"Dron: ráfaga de {Env.WindGust:0} m/s, regreso anticipado" : "Dron: reserva de batería justa para volver, regreso anticipado");
        }
        float travel = d.SpeedCellsPerS * dt;
        switch (d.Mode)
        {
            case DroneMode.Volando:
                if (MoveDrone(d.Target.X, d.Target.Y, ref travel))
                {
                    d.Mode = DroneMode.Escaneando;
                    d.ScanLeftS = d.ScanTimeS;
                }
                break;
            case DroneMode.Escaneando:
                d.ScanLeftS -= dt;
                if (d.ScanLeftS <= 0)
                {
                    ScanArea(d.Target);
                    d.Mode = DroneMode.Regresando;
                }
                break;
            case DroneMode.Regresando:
                if (MoveDrone(Rover.Pos.X, Rover.Pos.Y, ref travel))
                {
                    d.Mode = DroneMode.Acoplado;
                    AddLog("ok", $"Dron acoplado al rover · batería {d.BatteryPct:0} % · recargando");
                }
                break;
        }
        if (d.BatteryPct <= 0)
        {
            d.Mode = DroneMode.Acoplado;
            d.Available = false;
            d.Unavailable = "perdido: batería agotada en vuelo";
            AddLog("fallo", "Dron PERDIDO: batería agotada en vuelo");
        }
    }

    /// <summary>Mueve el dron hacia (tx, ty); escanea una franja estrecha bajo su trayectoria. True si llegó.</summary>
    private bool MoveDrone(float tx, float ty, ref float travel)
    {
        var d = Drone;
        float dx = tx - d.X, dy = ty - d.Y, dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist <= travel || dist < 0.05f)
        {
            d.X = tx;
            d.Y = ty;
            travel -= dist;
            return true;
        }
        int samples = (int)MathF.Ceiling(travel);
        for (int k = 1; k <= samples; k++)
        {
            float t = MathF.Min(1, k / (float)samples) * travel / dist;
            var c = Map.Clamp(new Cell((int)MathF.Round(d.X + dx * t), (int)MathF.Round(d.Y + dy * t)));
            if (d.Track.Count == 0 || d.Track[^1] != c) d.Track.Add(c);
            ObserveFromAir(c, 1, null);
        }
        d.X += dx / dist * travel;
        d.Y += dy / dist * travel;
        travel = 0;
        return false;
    }

    /// <summary>Observación aérea: la RNA analiza las lecturas (vista cercana, poca bruma) y el LIDAR aéreo confirma.</summary>
    private void ObserveFromAir(Cell center, int radius, DroneScan? scan)
    {
        var cond = new Conditions
        {
            VisibilityM = MathF.Max(Env.Visibility, 4000f), Night = !Env.IsDay, Vibration = 0.2f,
            RadiationMsvH = Env.RadiationAt(center), TempExtC = Env.TempAt(center),
        };
        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            var c = new Cell(center.X + dx, center.Y + dy);
            if (!Map.InBounds(c) || dx * dx + dy * dy > radius * radius + 1) continue;
            int i = Belief.Idx(c);
            bool wasObserved = Belief.Observed[i];
            var from = Map.Clamp(c + new Cell(1, 0));
            if (from == c) from = Map.Clamp(c + new Cell(-1, 0));
            var f = FeatureModel.Extract(Map, from, c, cond, _rngDrone);
            var p = UseRna ? Rna!.Predict(f) : FeatureModel.RulePredict(f);
            Belief.Update(c, p, 3f, StepCount);
            float drop = Map.MaxDrop(c) * Map.DropScale + _rngDrone.Gauss(0, 0.3f);
            float echo = Map.ObstacleH[i] + _rngDrone.Gauss(0, 0.15f);
            if (drop > WorldMap.CliffDropM * 1.2f && Map.ClassAt(c) == TerrainClass.Barranco) Belief.ConfirmCliff(c);
            else if (echo > 0.7f && Map.ClassAt(c) == TerrainClass.Obstaculo) Belief.ConfirmObstacle(c);
            if (scan == null) continue;
            scan.Cells++;
            if (!wasObserved) scan.NewlyObserved++;
            var cls = Map.ClassAt(c);
            if (cls == TerrainClass.Obstaculo) scan.Rocks++;
            if (cls == TerrainClass.Barranco) scan.Cliffs++;
            if (Map.Slope[i] > WorldMap.MaxSlopeDeg) scan.Steep++;
            scan.MeanSlope += Map.Slope[i];
            scan.MeanRough += Map.Rough[i];
            if (Map.Passable(c)) scan.Traversable += 1;
            Drone.CellsScanned++;
        }
    }

    /// <summary>Escaneo completo de la zona objetivo: análisis del terreno rocoso y posible objetivo científico nuevo.</summary>
    private void ScanArea(Cell center)
    {
        var d = Drone;
        var scan = new DroneScan { Center = center, Step = StepCount };
        ObserveFromAir(center, (int)d.ScanRadius, scan);
        if (scan.Cells > 0)
        {
            scan.MeanSlope /= scan.Cells;
            scan.MeanRough /= scan.Cells;
            scan.Traversable /= scan.Cells;
            scan.RockDensity = (float)scan.Rocks / scan.Cells;
        }
        // objetivo nuevo: el afloramiento más interesante (rugoso, rocoso, transitable) lejos de otros sitios
        int r = (int)d.ScanRadius;
        Cell? best = null;
        float bestScore = 0.55f;
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            var c = new Cell(center.X + dx, center.Y + dy);
            if (!Map.InBounds(c) || !Map.Passable(c) || Map.Refuges.Contains(c) || Map.Sites.Any(s => s.Cell.Dist(c) < 6) || c.Dist(Rover.Pos) < 3) continue;
            int i = Map.Idx(c);
            bool rocky = Map.Soils[i] is Soil.Roca or Soil.Basalto or Soil.SueloDesnudo or Soil.Hielo;
            float score = Map.Rough[i] * 0.6f + (rocky ? 0.3f : 0) + Map.MaxDrop(c) * 0.03f + _rngDrone.F() * 0.25f;
            if (score > bestScore) { bestScore = score; best = c; }
        }
        if (best is Cell bc)
        {
            var site = new ScienceSite
            {
                Id = Map.Sites.Count, Cell = bc, Kind = Map.Soils[Map.Idx(bc)] == Soil.Hielo ? "Hielo expuesto (dron)" : "Afloramiento rocoso (dron)",
                Value = MathF.Round(MathX.Clamp(0.35f + bestScore * 0.5f, 0.35f, 0.95f), 2), MassKg = MathF.Round(_rngDrone.Range(0.4f, 1.6f), 1),
                AnalysisWh = MathF.Round(_rngDrone.Range(2f, 5f), 1),
            };
            site.Name = $"S{site.Id + 1}";
            Map.Sites.Add(site);
            Map.Version++;
            d.ObjectivesFound++;
            scan.NewSite = $"{site.Name} {bc} ★{site.Value:0.00}";
            if (!Config.ClickMode) _needMissionPlan = true;
        }
        d.LastScan = scan;
        AddLog("evento", $"DRON escaneó {center}: {scan.Summary}");
        if (scan.NewSite != null) AddLog("ok", $"Dron propone un objetivo nuevo: {scan.NewSite} ({Map.Sites[^1].Kind})");
        _needRoute = _needRoute || Route.Skip(RouteIdx).Any(c => Belief.Forbidden(c));
        if (_needRoute) _replanTrigger = "reconocimiento aéreo del dron";
    }
}
