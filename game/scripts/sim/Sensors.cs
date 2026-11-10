using System;
using System.Collections.Generic;
using System.Linq;
using AresX.Core;

namespace AresX.Sim;

/// <summary>Estado físico del rover (o de la sonda). Valores de diseño = SUPUESTOS de simulación.</summary>
public sealed class RoverState
{
    public Cell Pos;
    public Cell Dir = new(1, 0);           // última dirección de marcha (8 direcciones)
    public float CapacityWh = 320f;        // SUPUESTO
    public float BatteryWh;
    public float TempInt = 20f;
    public float HeaterW, CoolerW, SolarW, DriveW, LoadW, BaseW = 45f, RtgW;
    public float Integrity = 100f;
    public float MechWear, Corrosion, PanelDust, DoseMsv;
    public float OdometerM;
    public float EstX, EstY;               // posición estimada por odometría (m)
    public float SampleKg, SampleCapKg = 5f;
    public float Science;                  // valor científico acumulado
    public readonly List<int> Visited = new();
    public float MassKg = 900f, VmaxMs = 0.5f, PanelM2 = 2.2f, PanelEff = 0.24f, Efficiency = 0.6f;
    public float LastSpeed;
    public int Collisions, Slips;
    public bool Probe;

    public float BatteryPct => CapacityWh <= 0 ? 0 : 100f * BatteryWh / CapacityWh;
    public float TrueX => (Pos.X + 0.5f) * WorldMap.CellM;
    public float TrueY => (Pos.Y + 0.5f) * WorldMap.CellM;
    public float PosErrorM => MathF.Sqrt((EstX - TrueX) * (EstX - TrueX) + (EstY - TrueY) * (EstY - TrueY));
    public Cell EstCell => new((int)MathF.Floor(EstX / WorldMap.CellM), (int)MathF.Floor(EstY / WorldMap.CellM));
    public float HeadingDeg => Cell.Heading(Dir);
}

public enum SensorStatus { OK, Aviso, Critico, Ruido, Saturado, NaN, Atascado, Contradiccion, NoAplica }

public enum FaultMode { None, NaN, Saturacion, Atascado, Contradiccion, Ruido }

/// <summary>Un canal de medida con su unidad, rango del sensor, umbrales y estado de validación.</summary>
public sealed class Channel
{
    public string Id = "", Name = "", Group = "", Unit = "", Fmt = "0.0";
    public float Value, Truth;
    public float Min, Max;                 // rango físico del sensor
    public float WarnLo = float.NegativeInfinity, WarnHi = float.PositiveInfinity;
    public float CritLo = float.NegativeInfinity, CritHi = float.PositiveInfinity;
    public SensorStatus Status = SensorStatus.OK;
    public bool CriticalNav;               // sensor crítico de navegación
    public bool Applicable = true;
    public string Note = "";

    public bool Valid => Applicable && Status is SensorStatus.OK or SensorStatus.Aviso or SensorStatus.Critico or SensorStatus.Ruido;
    public string Text => !Applicable ? "n/a" : float.IsNaN(Value) ? "NaN" : float.IsInfinity(Value) ? "∞" : Value.ToString(Fmt) + (Unit == "" ? "" : " " + Unit);
}

public sealed class SensorReading
{
    public readonly Dictionary<string, Channel> Ch = new();
    public readonly List<string> Order = new();
    public int NavValid, NavTotal;
    public bool Contradiction;
    public readonly List<string> Warnings = new();

    public Channel this[string id] => Ch[id];
    public float V(string id) => Ch[id].Value;
    public bool Ok(string id) => Ch[id].Valid && MathX.Finite(Ch[id].Value);

    public void Add(Channel c)
    {
        Ch[c.Id] = c;
        Order.Add(c.Id);
    }
}

/// <summary>
/// Suite de sensores con ruido gaussiano, dependencia de la visibilidad y fallos inyectables (NaN, saturación,
/// atasco, contradicción, ruido excesivo). La validación marca cada canal y resume la salud de la navegación.
/// </summary>
public sealed class SensorSuite
{
    private readonly Rng _rng;
    public readonly Dictionary<string, FaultMode> Faults = new();
    private readonly Dictionary<string, Queue<float>> _history = new();
    private readonly Dictionary<string, float> _stuck = new();

    public static readonly string[] NavSensors = { "prox_f", "lidar_drop", "incl_pitch" };
    public static readonly string[] FaultCandidates = { "prox_f", "lidar_drop", "incl_pitch", "prox_l", "prox_r", "t_ext", "accel", "rad", "cam_alb" };

    public SensorSuite(Rng rng) => _rng = rng;

    public void InjectFault(string id, FaultMode mode)
    {
        if (mode == FaultMode.None) Faults.Remove(id);
        else Faults[id] = mode;
        _stuck.Remove(id);
    }

    /// <summary>Distancia (m) al primer obstáculo o borde de barranco en la dirección d (alcance 20 m).</summary>
    public static float RayDistance(WorldMap map, Cell from, Cell d, out TerrainClass hit)
    {
        hit = TerrainClass.Libre;
        float stepM = d.IsDiagonal ? WorldMap.CellM * 1.41421f : WorldMap.CellM;
        for (int k = 1; k <= 4; k++)
        {
            var c = new Cell(from.X + d.X * k, from.Y + d.Y * k);
            if (!map.InBounds(c))
                return MathF.Min(20f, k * stepM - stepM * 0.5f);   // borde del mapa: se detecta como límite
            var cls = map.ClassAt(c);
            if (cls != TerrainClass.Libre)
            {
                hit = cls;
                return MathF.Max(0f, k * stepM - stepM * 0.5f);
            }
        }
        return 20f;
    }

    public static Cell Rotate(Cell d, int steps)
    {
        int i = Array.IndexOf(Cell.Dirs8, d);
        if (i < 0) i = 0;
        return Cell.Dirs8[((i + steps) % 8 + 8) % 8];
    }

    public SensorReading Observe(WorldMap map, SimEnvironment env, RoverState r, Cell heading)
    {
        var s = new SensorReading();
        var p = map.Planet;
        float vis = env.Visibility;
        float visF = MathX.Clamp01(vis / 3000f);
        bool probe = map.ProbeMode;
        Cell pos = r.Pos, ahead = map.Clamp(pos + heading);

        Channel C(string id, string name, string group, string unit, float truth, float sigma, float min, float max, string fmt = "0.0")
        {
            float v = truth + _rng.Gauss(0, sigma);
            var c = new Channel { Id = id, Name = name, Group = group, Unit = unit, Truth = truth, Value = MathX.Clamp(v, min, max), Min = min, Max = max, Fmt = fmt };
            s.Add(c);
            return c;
        }

        // ---- energía y potencia
        C("bat_pct", "Batería", "Energía", "%", r.BatteryPct, 0.1f, 0, 100).With(15, 101, 6, 101);
        C("bat_wh", "Energía almacenada", "Energía", "Wh", r.BatteryWh, 0.2f, 0, r.CapacityWh, "0");
        C("solar_w", "Potencia solar", "Energía", "W", r.SolarW, 0.5f, 0, 2000, "0");
        C("load_w", "Consumo total", "Energía", "W", r.LoadW, 1f, 0, 3000, "0");

        // ---- térmico
        float tExt = env.TempAt(pos);
        C("t_ext", "Temp. exterior", "Térmico", "°C", tExt, 0.4f, -273, 600).With(-150, 300, -200, 470);
        C("t_int", "Temp. interior (electrónica)", "Térmico", "°C", r.TempInt, 0.2f, -273, 600)
            .With(p.HardwareMinC + 15, p.HardwareMaxC - 15, p.HardwareMinC, p.HardwareMaxC);

        // ---- atmósfera
        float pres = env.PressureAt(pos);
        var pc = C("pressure", "Presión atmosférica", "Atmósfera", pres < 0.05f ? "Pa" : "bar", pres < 0.05f ? pres * 1e5f : pres,
            pres < 0.05f ? pres * 1e5f * 0.004f + 1e-6f : pres * 0.004f, 0, pres < 0.05f ? 5000 : 200, pres < 0.05f ? (pres < 1e-6f ? "0.0E+0" : "0") : "0.000");
        if (p.Style == TerrainStyle.Mercurio) pc.Note = "exosfera";
        bool airless = p.AirDensity <= 0;
        C("o2", "O₂ ambiental", "Atmósfera", "%", p.O2Pct, p.O2Pct * 0.01f + 0.001f, 0, 100, "0.00").Applicable = !airless && !probe;
        C("co2", "CO₂ ambiental", "Atmósfera", "%", p.CO2Pct, p.CO2Pct * 0.005f + 0.001f, 0, 100, "0.000").Applicable = !airless && !probe;
        C("so2", "SO₂", "Atmósfera", "ppm", p.SO2Ppm, p.SO2Ppm * 0.03f + 0.01f, 0, 1000, "0").Applicable = !airless && !probe;
        C("h2", "H₂ / He", "Atmósfera", "%", p.H2Pct, 0.3f, 0, 100, "0.0").Applicable = probe;
        C("wind", "Viento medio", "Atmósfera", "m/s", env.WindSpeed, 0.3f, 0, 800).With(-1, p.WindLimit * 0.6f, -1, p.WindLimit).Applicable = !airless;
        C("gust", "Ráfaga", "Atmósfera", "m/s", env.WindGust, 0.4f, 0, 900).With(-1, p.WindLimit * 0.7f, -1, p.WindLimit * 1.1f).Applicable = !airless;
        C("wind_dir", "Dirección viento", "Atmósfera", "°", env.WindDirDeg, 3f, 0, 360, "0").Applicable = !airless;
        C("rain", "Lluvia (pluviómetro)", "Atmósfera", "mm/h", env.RainMmH, 0.05f, 0, 200).With(-1, 8, -1, 25).Applicable = p.HasRain;
        C("rain24", "Lluvia 24 h equiv.", "Atmósfera", "mm", env.Rain24h, 0.2f, 0, 400).With(-1, 49.2f, -1, 80.1f).Applicable = p.HasRain;
        C("soil_h", "Humedad del suelo", "Atmósfera", "%", env.SoilMoisture * 100, 1f, 0, 100, "0").With(-1, 60, -1, 85).Applicable = p.HasRain;
        C("vis", "Visibilidad", "Atmósfera", "m", vis, vis * 0.03f, 0, 25000, "0").With(800, 1e9f, 150, 1e9f);
        C("part", "Partículas / opacidad", "Atmósfera", "", env.Particles, 0.01f, 0, 1, "0.00").With(-1, 0.45f, -1, 0.75f);

        // ---- terreno
        Cell left = Rotate(heading, -2), right = Rotate(heading, 2);
        float pitch = (map.DirSlope(pos, ahead) - map.DirSlope(pos, map.Clamp(pos - heading))) / 2f;
        float roll = (map.DirSlope(pos, map.Clamp(pos + right)) - map.DirSlope(pos, map.Clamp(pos + left))) / 2f;
        C("incl_pitch", "Inclinómetro cabeceo", "Terreno", "°", pitch, 0.3f, -45, 45).With(-20, 20, -WorldMap.MaxSlopeDeg, WorldMap.MaxSlopeDeg).CriticalNav = true;
        C("incl_roll", "Inclinómetro alabeo", "Terreno", "°", roll, 0.3f, -45, 45).With(-15, 15, -25, 25);
        float drop = map.Height(pos) - map.Height(ahead);
        C("lidar_drop", probe ? "Cizalla vertical (sonda)" : "LIDAR borde (caída)", "Terreno", "m", drop, 0.08f, -30, 30).With(-1e9f, 2.5f, -1e9f, WorldMap.CliffDropM).CriticalNav = true;
        float grad = (map.Height(map.Clamp(pos + new Cell(heading.X * 2, heading.Y * 2))) - map.Height(pos)) / (WorldMap.CellM * 2 * (heading.IsDiagonal ? 1.414f : 1));
        C("lidar_grad", "LIDAR gradiente", "Terreno", "m/m", grad, 0.02f, -5, 5, "0.00");
        C("alt", probe ? "Altitud sobre 1 bar" : "Altímetro", "Terreno", "m", probe ? map.Height(pos) * 40 : map.Elevation(pos), 0.3f, -1e5f, 1e5f, "0");
        C("rough", "Rugosidad (textura)", "Terreno", "", map.Rough[map.Idx(pos)], 0.03f, 0, 1, "0.00").With(-1, 0.6f, -1, 0.85f);

        // ---- proximidad
        float pf = RayDistance(map, pos, heading, out _), pl = RayDistance(map, pos, left, out _), pr = RayDistance(map, pos, right, out _);
        float proxSigma = 0.15f + 0.4f * (1 - visF);
        C("prox_f", "Proximidad frontal", "Proximidad", "m", pf, proxSigma, 0, 20).With(4, 1e9f, 1.5f, 1e9f).CriticalNav = true;
        C("prox_l", "Proximidad izquierda", "Proximidad", "m", pl, proxSigma, 0, 20).With(3, 1e9f, 1f, 1e9f);
        C("prox_r", "Proximidad derecha", "Proximidad", "m", pr, proxSigma, 0, 20).With(3, 1e9f, 1f, 1e9f);

        // ---- dinámica y radiación
        float vib = map.Rough[map.Idx(pos)] * (0.2f + r.LastSpeed * 1.6f) + env.QuakeAccel + (probe ? env.WindGust / 60f : 0);
        C("accel", probe ? "Acelerómetro (turbulencia)" : "Acelerómetro / sismómetro", "Dinámica", "m/s²", vib, 0.05f, 0, 30, "0.00").With(-1, 1.2f, -1, 2.5f);
        float rad = env.RadiationAt(pos);
        C("rad", "Dosis de radiación", "Radiación", "mSv/h", rad, rad * 0.05f, 0, 1000, rad < 0.01f ? "0.0000" : "0.000").With(-1, 0.5f, -1, 5f);

        // ---- cámara multiespectral
        float dark = env.IsDay ? 0f : 0.5f;
        var site = map.Sites.FirstOrDefault(x => x.Cell.Chebyshev(pos) <= 2);
        float anomaly = site != null && site.Anomaly ? 0.7f + _rng.Gauss(0, 0.08f) : MathF.Abs(_rng.Gauss(0, 0.08f));
        C("cam_alb", "Cámara: albedo frontal", "Cámara", "", map.Albedo[map.Idx(ahead)] * (1 - dark), 0.02f + (1 - visF) * 0.1f, 0, 1, "0.00");
        C("cam_anom", "Cámara: índice espectral anómalo", "Cámara", "", MathX.Clamp01(anomaly), 0.01f, 0, 1, "0.00").With(-1, 0.4f, -1, 0.65f);

        // ---- navegación y sistema
        C("odo", "Odómetro", "Navegación", "m", r.OdometerM, 0.01f, 0, 1e6f, "0");
        C("compass", "Brújula (rumbo)", "Navegación", "°", r.HeadingDeg, 1.5f, 0, 360, "0").Applicable = p.Style is TerrainStyle.Tierra or TerrainStyle.Gaseoso;
        C("pos_err", "Error de posición (odometría)", "Navegación", "m", r.PosErrorM, 0.0f, 0, 1000).With(-1, 4, -1, 9);
        C("integrity", "Integridad mecánica", "Sistema", "%", r.Integrity, 0.05f, 0, 100, "0.0").With(50, 101, 25, 101);
        C("link_lat", "Latencia de enlace (luz, ida)", "Sistema", "min", p.LightTimeMin, 0, 0, 1000, "0.0");
        C("link_q", "Calidad de enlace", "Sistema", "%", env.LinkQuality * 100, 1f, 0, 100, "0").With(50, 101, 25, 101);

        if (!s["compass"].Applicable) s["compass"].Note = "sin campo magnético global: rumbo por seguimiento solar/estelar";
        ApplyFaults(s, r);
        Validate(s);
        return s;
    }

    private void ApplyFaults(SensorReading s, RoverState r)
    {
        foreach (var (id, mode) in Faults)
        {
            if (!s.Ch.TryGetValue(id, out var c)) continue;
            switch (mode)
            {
                case FaultMode.NaN: c.Value = float.NaN; break;
                case FaultMode.Saturacion: c.Value = id.StartsWith("prox") ? 0f : c.Max; break;
                case FaultMode.Ruido: c.Value = MathX.Clamp(c.Value + _rng.Gauss(0, (c.Max - c.Min) * 0.15f), c.Min, c.Max); break;
                case FaultMode.Atascado:
                    if (!_stuck.ContainsKey(id)) _stuck[id] = c.Value;
                    c.Value = _stuck[id];
                    break;
                case FaultMode.Contradiccion:
                    c.Value = id == "lidar_drop" ? 9f : id.StartsWith("prox") ? 20f : c.Max * 0.9f;
                    break;
            }
        }
    }

    /// <summary>Validación: NaN/∞, saturación, atasco, contradicción entre sensores y umbrales de aviso/crítico.</summary>
    private void Validate(SensorReading s)
    {
        foreach (var c in s.Ch.Values)
        {
            if (!c.Applicable) { c.Status = SensorStatus.NoAplica; continue; }
            if (!MathX.Finite(c.Value)) { c.Status = SensorStatus.NaN; s.Warnings.Add($"{c.Name}: lectura inválida (NaN/∞)"); continue; }
            if (Faults.TryGetValue(c.Id, out var f) && f == FaultMode.Saturacion) { c.Status = SensorStatus.Saturado; s.Warnings.Add($"{c.Name}: saturado en {c.Text}"); continue; }
            if (Faults.TryGetValue(c.Id, out f) && f == FaultMode.Ruido) { c.Status = SensorStatus.Ruido; continue; }
            // atasco: mismo valor exacto 4 lecturas seguidas en un sensor que normalmente tiene ruido
            var h = _history.TryGetValue(c.Id, out var q) ? q : _history[c.Id] = new Queue<float>();
            h.Enqueue(c.Value);
            while (h.Count > 4) h.Dequeue();
            if (h.Count == 4 && h.All(v => v == c.Value) && c.Id is "prox_f" or "prox_l" or "prox_r" or "lidar_drop" or "incl_pitch" or "t_ext" or "accel" && c.Value is > 0.01f and < 19.99f)
            {
                c.Status = SensorStatus.Atascado;
                s.Warnings.Add($"{c.Name}: valor congelado ({c.Text})");
                continue;
            }
            c.Status = c.Value <= c.CritLo || c.Value >= c.CritHi ? SensorStatus.Critico
                     : c.Value <= c.WarnLo || c.Value >= c.WarnHi ? SensorStatus.Aviso : SensorStatus.OK;
        }
        // contradicción: el LIDAR de borde ve un precipicio justo delante pero la proximidad frontal dice "libre lejos"
        if (s.Ok("lidar_drop") && s.Ok("prox_f") && s.V("lidar_drop") > WorldMap.CliffDropM && s.V("prox_f") > 10f)
        {
            s.Contradiction = true;
            s["lidar_drop"].Status = SensorStatus.Contradiccion;
            s["prox_f"].Status = SensorStatus.Contradiccion;
            s.Warnings.Add("Contradicción LIDAR-borde vs proximidad frontal: se asume el peor caso");
        }
        s.NavTotal = NavSensors.Length;
        s.NavValid = NavSensors.Count(id => s.Ch[id].Valid);
    }
}

internal static class ChannelExt
{
    public static Channel With(this Channel c, float warnLo, float warnHi, float critLo, float critHi)
    {
        c.WarnLo = warnLo;
        c.WarnHi = warnHi;
        c.CritLo = critLo;
        c.CritHi = critHi;
        return c;
    }
}
