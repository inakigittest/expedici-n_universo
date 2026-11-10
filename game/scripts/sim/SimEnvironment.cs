using System;
using System.Collections.Generic;
using System.Linq;
using AresX.Core;

namespace AresX.Sim;

public enum EventKind
{
    Sismo, TormentaPolvo, Tormenta, Meteorito, FalloSensor, Noche, BajaConectividad, Rafaga, Biofirma, Alien,
    BateriaBaja, Remocion, TormentaSolar,
}

public sealed class ActiveEvent
{
    public EventKind Kind;
    public int Remaining;     // pasos que le quedan (−1 = permanente hasta que se quite)
    public float Severity;    // 0..1
    public string Detail = "";
    public Cell? Where;
}

/// <summary>
/// Estado ambiental que evoluciona paso a paso: reloj de misión (día comprimido), temperatura, viento con ráfagas,
/// lluvia (climatología GeoClick para la Tierra), polvo y visibilidad, sismos, radiación y enlace. Los eventos
/// inyectados por el operador o aleatorios viven en <see cref="Events"/>.
/// </summary>
public sealed class SimEnvironment
{
    public readonly PlanetProfile P;
    private readonly WorldMap _map;
    private readonly Rng _rng;

    public float TimeS;               // segundos de misión
    public int Sol = 14;              // número de sol/día de la misión (narrativa SOL 014)
    public float DayPhase;            // 0..1 (0,25 = amanecer, 0,5 = mediodía, 0,75 = atardecer)
    public float SunElev;             // −1..1
    public bool ForcedNight;
    public int Month = 11;            // Tierra: mes para la climatología

    public float WindMean, WindSpeed, WindGust, WindDirDeg;
    public float DustOpacity, Visibility, Particles;
    public float RainMmH, Rain24h, SoilMoisture;
    public float QuakeAccel;
    public float RadiationMult = 1f;
    public float LinkQuality = 1f;
    public int CommandDelaySteps;
    public float CloudCover;
    public readonly List<ActiveEvent> Events = new();

    private readonly Queue<(float T, float Mm)> _rainHistory = new();

    public bool IsDay => !ForcedNight && SunElev > 0.02f;
    public float DayLengthS => P.SimDayMinutes * 60f;
    public float LocalHour => DayPhase * 24f;

    public SimEnvironment(PlanetProfile p, WorldMap map, Rng rng, int month)
    {
        P = p;
        _map = map;
        _rng = rng;
        Month = month;
        DayPhase = 0.32f;   // la misión empieza a media mañana
        WindMean = p.WindMean;
        WindSpeed = p.WindMean;
        WindDirDeg = rng.Range(0, 360);
        DustOpacity = p.DustBase;
        SoilMoisture = p.HasRain ? MathX.Clamp01(GeoClick.MonthlyRainMm[(month - 1) % 12] / 120f) : 0;
        UpdateDerived();
    }

    public bool Has(EventKind k) => Events.Any(e => e.Kind == k);
    public ActiveEvent? Get(EventKind k) => Events.FirstOrDefault(e => e.Kind == k);

    /// <summary>Avanza el ambiente dt segundos de misión.</summary>
    public void Step(float dt)
    {
        TimeS += dt;
        float prevPhase = DayPhase;
        DayPhase = (DayPhase + dt / DayLengthS) % 1f;
        if (DayPhase < prevPhase) Sol++;

        // viento: proceso de Ornstein-Uhlenbeck alrededor de la media (más alta con tormenta o ráfaga)
        float mean = P.WindMean;
        if (Has(EventKind.TormentaPolvo)) mean *= 3.2f;
        if (Has(EventKind.Tormenta)) mean *= 2.5f;
        if (Get(EventKind.Rafaga) is { } gustEv) mean = MathF.Max(mean, P.WindGust * (0.9f + gustEv.Severity * 0.6f));
        WindMean = mean;
        float theta = 0.15f, sigma = MathF.Max(0.3f, P.WindGust * 0.12f);
        float k = MathF.Min(1f, dt / 30f);
        WindSpeed = MathF.Max(0, WindSpeed + theta * (mean - WindSpeed) * k * 4 + sigma * MathF.Sqrt(k) * _rng.Gauss());
        WindGust = WindSpeed * (1f + MathF.Abs(_rng.Gauss(0, 0.35f)));
        WindDirDeg = (WindDirDeg + _rng.Gauss(0, 4f) * MathF.Sqrt(k) + 360f) % 360f;

        // polvo / bruma
        float dustTarget = P.DustBase + (Has(EventKind.TormentaPolvo) ? 0.7f : 0) + (Has(EventKind.Meteorito) ? 0.25f : 0);
        DustOpacity = MathX.Lerp(DustOpacity, MathX.Clamp01(dustTarget), MathF.Min(1, dt / 120f));

        // lluvia (solo Tierra): cadena de Markov con probabilidad según la lluvia media del mes (GeoClick)
        if (P.HasRain)
        {
            float monthly = GeoClick.MonthlyRainMm[(Month - 1) % 12];
            var storm = Get(EventKind.Tormenta);
            if (storm != null)
                RainMmH = MathX.Lerp(RainMmH, storm.Severity * 18f + 4f, MathF.Min(1, dt / 90f));
            else
            {
                float pStart = monthly / 4000f * dt / 30f;
                if (RainMmH <= 0.05f && _rng.Chance(pStart)) RainMmH = _rng.Range(0.5f, 3f + monthly / 30f);
                else if (RainMmH > 0) RainMmH = _rng.Chance(0.02f * dt / 30f) ? 0 : MathF.Max(0, RainMmH + _rng.Gauss(0, 0.4f));
            }
            float mm = RainMmH * dt / 3600f;
            _rainHistory.Enqueue((TimeS, mm));
            float window = DayLengthS;   // "24 h" del día comprimido
            while (_rainHistory.Count > 0 && _rainHistory.Peek().T < TimeS - window) _rainHistory.Dequeue();
            // lluvia diaria equivalente: lo acumulado en el día comprimido escalado a 24 h reales
            Rain24h = _rainHistory.Sum(r => r.Mm) * (86400f / window);
            SoilMoisture = MathX.Clamp01(SoilMoisture + mm * (86400f / window) / 80f - dt / 20000f * (IsDay ? 1.5f : 0.6f));
            CloudCover = MathX.Clamp01(RainMmH > 0 ? 0.6f + RainMmH / 30f : CloudCover - dt / 3000f);
        }
        else if (P.Style == TerrainStyle.Venus || P.Style == TerrainStyle.Gaseoso)
        {
            CloudCover = 1f;
        }

        // sismo: aleatorio en Tierra/Marte o inyectado
        if (P.HasQuakes && !Has(EventKind.Sismo) && _rng.Chance(P.QuakeChance * dt / 20f))
            Add(EventKind.Sismo, _rng.Range(0.3f, 0.8f), "sismo espontáneo");
        var q = Get(EventKind.Sismo);
        QuakeAccel = q == null ? 0 : q.Severity * 3.2f * (0.5f + 0.5f * q.Remaining / 6f) * (1f + _rng.Gauss(0, 0.15f));

        RadiationMult = Has(EventKind.TormentaSolar) ? 25f : 1f;
        var link = Get(EventKind.BajaConectividad);
        LinkQuality = link != null ? 0.15f + 0.2f * (1 - link.Severity) : MathX.Clamp01(0.97f - DustOpacity * 0.25f - (P.Style == TerrainStyle.Gaseoso ? 0.1f : 0));
        CommandDelaySteps = link != null ? 3 + (int)(link.Severity * 4) : 0;

        // eventos: consumir duración
        foreach (var e in Events) if (e.Remaining > 0) e.Remaining--;
        Events.RemoveAll(e => e.Remaining == 0);
        UpdateDerived();
    }

    private void UpdateDerived()
    {
        SunElev = MathF.Sin((DayPhase - 0.25f) * MathF.Tau);
        float rainVis = P.HasRain ? 1f / (1f + RainMmH * 0.25f) : 1f;
        float baseVis = P.Style switch
        {
            TerrainStyle.Mercurio => 20000f, TerrainStyle.Venus => 2000f, TerrainStyle.Gaseoso => 1500f, TerrainStyle.Marte => 8000f, _ => 15000f,
        };
        Visibility = baseVis * (1f - 0.92f * DustOpacity) * rainVis;
        Particles = MathX.Clamp01(DustOpacity + (P.HasRain ? RainMmH / 30f : 0));
    }

    public ActiveEvent Add(EventKind kind, float severity, string detail = "", int steps = -2, Cell? where = null)
    {
        int dur = steps != -2 ? steps : kind switch
        {
            EventKind.Sismo => 6, EventKind.TormentaPolvo => 60, EventKind.Tormenta => 50, EventKind.Meteorito => 12,
            EventKind.Rafaga => 8, EventKind.BajaConectividad => 40, EventKind.TormentaSolar => 30, EventKind.Biofirma => 20,
            EventKind.Remocion => 15, EventKind.Noche => -1, EventKind.FalloSensor => -1, EventKind.Alien => 80, _ => 10,
        };
        var existing = Get(kind);
        if (existing != null && kind != EventKind.FalloSensor)
        {
            existing.Severity = MathF.Max(existing.Severity, severity);
            existing.Remaining = dur;
            existing.Detail = detail;
            return existing;
        }
        var e = new ActiveEvent { Kind = kind, Severity = severity, Remaining = dur, Detail = detail, Where = where };
        Events.Add(e);
        if (kind == EventKind.Noche) ForcedNight = true;
        return e;
    }

    public void Remove(EventKind kind)
    {
        Events.RemoveAll(e => e.Kind == kind);
        if (kind == EventKind.Noche) ForcedNight = false;
    }

    // ------------------------------------------------------------------ magnitudes por celda
    /// <summary>Temperatura exterior (°C) en una celda: media + ciclo diario + gradiente térmico con la altura.</summary>
    public float TempAt(Cell c)
    {
        float mean = P.Style == TerrainStyle.Tierra ? GeoClick.MonthlyTempC[(Month - 1) % 12] : P.TempMeanC;
        float amp = P.TempAmpC * (1f - 0.6f * DustOpacity) * (1f - 0.5f * CloudCover * (P.HasRain ? 1 : 0));
        float diurnal = ForcedNight ? -1f : SunElev > 0 ? SunElev : SunElev * 0.8f;
        if (P.Style == TerrainStyle.Mercurio)
            diurnal = SunElev > 0 ? MathF.Pow(SunElev, 0.5f) : -1f;   // sin atmósfera: cambio brusco en el terminador
        float lapse = P.Style switch { TerrainStyle.Tierra => 0.0065f, TerrainStyle.Marte => 0.0025f, TerrainStyle.Venus => 0.008f, _ => 0f };
        float t = mean + amp * diurnal - lapse * _map.Height(c);
        if (P.Style == TerrainStyle.Tierra && RainMmH > 0) t -= 2f;
        return t;
    }

    /// <summary>Irradiancia solar en superficie (W/m²).</summary>
    public float SolarIrradiance()
    {
        if (!IsDay) return 0;
        float atm = P.Style == TerrainStyle.Mercurio ? 1f : MathF.Exp(-DustOpacity * 2.2f);
        return P.SunMultiplier * MathF.Max(0, SunElev) * atm * (1f - 0.7f * CloudCover * (P.HasRain ? 1 : 0));
    }

    /// <summary>Presión (bar) en la celda según la altura de escala.</summary>
    public float PressureAt(Cell c)
    {
        if (P.ScaleHeightKm <= 0) return P.PressureBar;
        float hKm = (_map.Height(c) - 20f) / 1000f;
        if (P.Style == TerrainStyle.Tierra) hKm = (_map.Elevation(c) - GeoClick.ElevMedian) / 1000f;
        return P.PressureBar * MathF.Exp(-hKm / P.ScaleHeightKm);
    }

    public float RadiationAt(Cell c) => P.RadiationMsvH * RadiationMult * (IsDay ? 1.05f : 0.95f);

    /// <summary>Índice de riesgo de remoción en masa (Tierra): humedad del suelo × pendiente.</summary>
    public float LandslideIndex(Cell c) =>
        P.HasRain ? MathX.Clamp01(SoilMoisture * 1.2f * MathX.Clamp01((_map.Slope[_map.Idx(c)] - 15f) / 20f) + Rain24h / 200f) : 0;
}
