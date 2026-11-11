using System;
using AresX.AI;
using AresX.Core;

namespace AresX.Sim;

/// <summary>
/// Helicóptero no tripulado de exploración (solo Tierra). Modelo de potencia por teoría de cantidad de movimiento:
/// la potencia de vuelo estacionario crece con la masa y cae con la densidad del aire (altura y temperatura), y la
/// potencia disponible del motor también cae con la densidad: en cordillera alta y días calurosos el margen se
/// estrecha. Todos los parámetros son SUPUESTOS pedagógicos de un helicóptero eléctrico-híbrido de ~300 kg.
/// </summary>
public static class HeliModel
{
    public const float MassKg = 300f, RotorR = 3.2f, FigureOfMerit = 0.68f, FlatPlateM2 = 0.6f;
    public const float PowerAvailSeaLevelW = 46000f;   // motor + baterías de apoyo al nivel del mar
    public const float CruiseMs = 25f, CruiseAglM = 90f, GustLimitMs = 15f, MinVisM = 1500f, RainLimitMmH = 8f;
    public const float GroundIdleW = 1500f, AvionicsW = 300f, MaxClimbMs = 6f, LandSlopeDeg = 12f;
    public static float DiskArea => MathF.PI * RotorR * RotorR;

    /// <summary>Densidad del aire (kg/m³) desde presión (bar) y temperatura (°C).</summary>
    public static float Density(float pressureBar, float tempC) => 1.225f * (pressureBar / 1.01325f) * 288.15f / (273.15f + tempC);

    /// <summary>Potencia de vuelo estacionario (W): (m g)^1,5 / √(2 ρ A) / FM.</summary>
    public static float HoverW(float rho, float g = 9.81f)
    {
        float t = MassKg * g;
        return MathF.Pow(t, 1.5f) / MathF.Sqrt(2f * rho * DiskArea) / FigureOfMerit;
    }

    /// <summary>Potencia en avance (W): inducida (cae con la velocidad) + perfil + parásita (ρ f v³ / 2): curva en "bañera".</summary>
    public static float ForwardW(float rho, float v, float g = 9.81f)
    {
        float ph = HoverW(rho, g);
        float vh = MathF.Sqrt(MassKg * g / (2f * rho * DiskArea));
        float induced = ph * 0.88f * vh / MathF.Sqrt(v * v + vh * vh);
        float profile = ph * 0.12f * (1f + 3f * (v / 200f) * (v / 200f));
        float parasite = 0.5f * rho * FlatPlateM2 * v * v * v;
        return induced + profile + parasite;
    }

    /// <summary>Potencia disponible (W): el motor pierde potencia con la densidad (σ^0,85).</summary>
    public static float AvailW(float rho) => PowerAvailSeaLevelW * MathF.Pow(rho / 1.225f, 0.85f);

    public static float Margin(float rho) => AvailW(rho) / HoverW(rho);
}

public sealed partial class Mission
{
    public bool Heli => Config.Heli;
    /// <summary>Límite de ráfagas del vehículo en uso (m/s).</summary>
    public float WindLimitMs => Heli ? HeliModel.GustLimitMs : Planet.WindLimit;
    public float HeliAglM;            // altura sobre el terreno (m); 0 = en tierra
    public bool HeliLanded = true;
    public float HeliRho = 1.2f, HeliPowerW, HeliMargin = 1.5f, HeliClimbDeg;
    public string HeliLzNote = "";

    /// <summary>Parámetros del vehículo (antes de fijar la batería inicial).</summary>
    private void InitHeliParams()
    {
        if (!Heli) return;
        Rover.Heli = true;
        Rover.MassKg = HeliModel.MassKg;
        Rover.VmaxMs = HeliModel.CruiseMs;
        Rover.CapacityWh = 20000f;   // energía embarcada equivalente (SUPUESTO: híbrido eléctrico)
        Rover.BaseW = HeliModel.AvionicsW;
        Rover.PanelM2 = 0;
        Rover.SampleCapKg = 8f;
        Rover.Efficiency = 0.85f;
    }

    private void InitHeli()
    {
        if (!Heli) return;
        UpdateHeliAir();
        Cost.Heli = true;
        Cost.HeliWh = HeliStepWh;
    }

    /// <summary>Densidad, potencia y margen en la posición actual.</summary>
    private void UpdateHeliAir()
    {
        HeliRho = HeliModel.Density(Env.PressureAt(Rover.Pos), Env.TempAt(Rover.Pos));
        HeliMargin = HeliModel.Margin(HeliRho);
        Rover.HeliRho = HeliRho;
        Rover.HeliMargin = HeliMargin;
        Rover.HeliAglM = HeliAglM;
        Rover.HeliPowerW = HeliPowerW;
    }

    /// <summary>Energía estimada (Wh) de un tramo de vuelo a → b para el AG (sobre el relieve conocido del informe).</summary>
    private float HeliStepWh(Cell a, Cell b)
    {
        float d = Cost.StepM(a, b);
        float v = HeliModel.CruiseMs * 0.85f;
        float hA = Map.Elevation(a), hB = Map.Elevation(b);
        float rho = HeliModel.Density(1.01325f * MathF.Exp(-hB / 1000f / Planet.ScaleHeightKm), Map.Geo.MonthlyTempC[(Env.Month - 1) % 12] - 0.0065f * (hB - Map.Geo.ElevMedian));
        float t = d / v;
        float climbJ = MathF.Max(0, hB - hA) * HeliModel.MassKg * Planet.Gravity / 0.75f;
        return (HeliModel.ForwardW(rho, v) * t + climbJ + HeliModel.AvionicsW * t) / 3600f;
    }

    /// <summary>Gradiente del relieve en la dirección de vuelo (°): el helicóptero sigue el terreno a 90 m.</summary>
    private float HeliClimbAhead(Cell heading)
    {
        float best = 0;
        for (int k = 1; k <= 3; k++)
        {
            var c = Map.Clamp(Rover.Pos + new Cell(heading.X * k, heading.Y * k));
            float dh = Map.Elevation(c) - Map.Elevation(Rover.Pos);
            float dist = k * Map.CellM * (heading.IsDiagonal ? 1.414f : 1);
            best = MathF.Max(best, MathF.Atan2(dh, dist) * 180f / MathF.PI);
        }
        return best;
    }

    /// <summary>Distancia (m, 0..20 en la escala difusa) al primer relieve que obliga a trepar más de lo que permite la tasa de ascenso.</summary>
    private float HeliTerrainClearance(Cell heading)
    {
        for (int k = 1; k <= 4; k++)
        {
            var c = Map.Clamp(Rover.Pos + new Cell(heading.X * k, heading.Y * k));
            float dist = k * Map.CellM * (heading.IsDiagonal ? 1.414f : 1);
            float need = Map.Elevation(c) - Map.Elevation(Rover.Pos);   // vuela a altura constante sobre el terreno: debe trepar lo que sube el relieve
            float canClimb = HeliModel.MaxClimbMs * dist / HeliModel.CruiseMs;
            if (need > canClimb) return MathX.Clamp((k - 1) * 5f, 0, 20);
        }
        return 20f;
    }

    /// <summary>Zona de aterrizaje: pendiente real &lt; 12°, sin agua, árboles, edificios, rocas ni detritos.</summary>
    public bool Landable(Cell c) => Map.InBounds(c) && Map.Hazards[Map.Idx(c)] == Hazard.None && Map.Slope[Map.Idx(c)] < HeliModel.LandSlopeDeg;

    /// <summary>Paso de vuelo: sin colisiones con el suelo, energía de la curva de potencia y ascenso al seguir el relieve.</summary>
    private (float Dt, float J, float MoveM) HeliFly(Cell to, float speedFrac)
    {
        var dir = to - Rover.Pos;
        float moveM = dir.IsDiagonal ? Map.CellM * 1.41421f : Map.CellM;
        float v = MathF.Max(3f, speedFrac * HeliModel.CruiseMs);
        float dt = moveM / v;
        float rho = HeliModel.Density(Env.PressureAt(to), Env.TempAt(to));
        // viento: el componente de frente aumenta la potencia parásita
        float windDirRad = Env.WindDirDeg * MathF.PI / 180f;
        float headRad = MathF.Atan2(dir.X, -dir.Y);
        float headwind = Env.WindSpeed * MathF.Cos(windDirRad - headRad + MathF.PI);
        float pw = HeliModel.ForwardW(rho, MathF.Max(1f, v + headwind * 0.6f));
        float takeoffJ = 0;
        if (HeliLanded)
        {
            takeoffJ = HeliModel.HoverW(rho) * 25f;   // despegue y ascenso a la altura de crucero
            HeliLanded = false;
            AddLog("info", $"Despegue: densidad {rho:0.000} kg/m³ · potencia estacionaria {HeliModel.HoverW(rho) / 1000:0.0} kW de {HeliModel.AvailW(rho) / 1000:0.0} kW (margen {HeliModel.Margin(rho):0.00})");
        }
        float climb = MathF.Max(0, Map.Elevation(to) - Map.Elevation(Rover.Pos));
        float climbJ = climb * HeliModel.MassKg * Planet.Gravity / 0.75f;
        HeliAglM = HeliModel.CruiseAglM;
        HeliPowerW = pw;
        return (dt, pw * dt + climbJ + takeoffJ, moveM);
    }

    /// <summary>Esperar: en tierra si la celda es apta (motor en ralentí), si no, vuelo estacionario.</summary>
    private float HeliWaitJ(float dt)
    {
        if (HeliLanded) { HeliPowerW = HeliModel.GroundIdleW; return HeliModel.GroundIdleW * dt; }
        if (Landable(Rover.Pos))
        {
            HeliLanded = true;
            HeliAglM = 0;
            AddLog("info", $"Aterrizaje de espera en {Rover.Pos} (pendiente {Map.Slope[Map.Idx(Rover.Pos)]:0}°, {WorldMap.SoilName(Map.SoilAt(Rover.Pos))})");
            HeliPowerW = HeliModel.GroundIdleW;
            return HeliModel.HoverW(HeliRho) * 20f + HeliModel.GroundIdleW * dt;
        }
        HeliPowerW = HeliModel.HoverW(HeliRho);
        return HeliPowerW * dt;
    }

    /// <summary>Toma de muestra: aterriza si la zona es apta; si no, vuelo estacionario con cabrestante (más energía).</summary>
    private float HeliSampleJ(ref float dt)
    {
        var lzP = Belief.P[Belief.Idx(Rover.Pos)];
        if (Landable(Rover.Pos))
        {
            HeliLzNote = $"aterrizaje en zona apta (pendiente {Map.Slope[Map.Idx(Rover.Pos)]:0}°, RNA libre {lzP[0]:0.00})";
            float j = (HeliLanded ? 0 : HeliModel.HoverW(HeliRho) * 30f) + HeliModel.GroundIdleW * dt;
            HeliLanded = true;
            HeliAglM = 0;
            HeliPowerW = HeliModel.GroundIdleW;
            return j;
        }
        dt = 180f;
        HeliLanded = false;
        HeliAglM = 15f;
        HeliPowerW = HeliModel.HoverW(HeliRho);
        HeliLzNote = $"zona NO apta ({WorldMap.HazardName(Map.HazardAt(Rover.Pos))}, pendiente {Map.Slope[Map.Idx(Rover.Pos)]:0}°): muestreo con cabrestante en vuelo estacionario";
        return HeliPowerW * dt;
    }

    /// <summary>Notas de vuelo para la bitácora inicial.</summary>
    private string[] HeliNotes()
    {
        float rho = HeliRho;
        return new[]
        {
            $"Helicóptero no tripulado (SUPUESTO): {HeliModel.MassKg:0} kg, rotor de {HeliModel.RotorR * 2:0.0} m, crucero {HeliModel.CruiseMs:0} m/s a {HeliModel.CruiseAglM:0} m sobre el terreno, 20 kWh.",
            $"En la base: densidad {rho:0.000} kg/m³ → estacionario {HeliModel.HoverW(rho) / 1000:0.0} kW, disponible {HeliModel.AvailW(rho) / 1000:0.0} kW (margen {HeliModel.Margin(rho):0.00}). Más altura o más calor = menos margen.",
            $"Límites: ráfagas {HeliModel.GustLimitMs:0} m/s · visibilidad {HeliModel.MinVisM:0} m · lluvia {HeliModel.RainLimitMmH:0} mm/h · aterrizaje en pendiente < {HeliModel.LandSlopeDeg:0}° sin obstáculos.",
        };
    }
}
