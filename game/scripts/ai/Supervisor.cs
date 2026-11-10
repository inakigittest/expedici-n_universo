using System;
using System.Collections.Generic;
using System.Linq;
using AresX.Core;
using AresX.Sim;

namespace AresX.AI;

public enum RoverAction { Avanzar, Detener, Esperar, Recargar, Volver, Replanificar, ModoSeguro, Muestrear, Abortar }

public sealed class ConstraintCheck
{
    public string Code = "", Name = "", Detail = "";
    public bool Passed = true;
    public bool Hard;           // restricción dura (veto)
    public bool Applied;        // influyó en la decisión
}

public sealed class Decision
{
    public RoverAction Action = RoverAction.Esperar;
    public float Speed;
    public Cell? Target;
    public string Code = "S-00", Reason = "";
    public readonly List<ConstraintCheck> Checks = new();
    public bool Veto, NeedReplan, Alert, EnterSafeMode, GoRefuge;
    public string Explanation = "";
    public float SpeedFuzzy, SpeedCap = 1f;
}

/// <summary>Todo lo que el supervisor necesita para decidir un paso.</summary>
public sealed class SupervisorInput
{
    public SensorReading Sensors = null!;
    public Cell From, Proposed;
    public bool HasProposal;
    public float[] RnaProbs = { 1, 0, 0 };
    public float RnaConfidence = 1;
    public bool RnaEnabled = true;
    public float FuzzySpeed = 0.5f, FuzzyCaution = 0.5f;
    public float BatteryWh, ReserveWh, EnergyToRefugeWh;
    public bool RefugeReachable = true;
    public bool AtRefuge;
    public float TempInt, HwMin, HwMax;
    public float WindPct;          // viento / límite (%)
    public float TipIndex;         // índice de vuelco (0..1+)
    public float QuakeAccel;
    public bool Daylight;
    public float SolarW;
    public bool ProposedForbidden; // celda prohibida en las creencias
    public bool ManualCommand;
}

/// <summary>
/// Supervisor híbrido. Primero evalúa las prohibiciones físicas en un orden FIJO (determinista ante eventos
/// simultáneos) y después combina las recomendaciones: la velocidad final es la difusa limitada por los topes
/// de seguridad. Cada decisión lleva un código de motivo reproducible.
/// </summary>
public static class SafetySupervisor
{
    public const float CliffVetoP = 0.85f, AlertP = 0.5f, LowConfidence = 0.5f;

    public static Decision Decide(SupervisorInput i)
    {
        var d = new Decision { SpeedFuzzy = i.FuzzySpeed, Target = i.HasProposal ? i.Proposed : null };
        var s = i.Sensors;
        ConstraintCheck Check(string code, string name, bool passed, string detail, bool hard)
        {
            var c = new ConstraintCheck { Code = code, Name = name, Passed = passed, Detail = detail, Hard = hard };
            d.Checks.Add(c);
            return c;
        }
        void Set(RoverAction a, string code, string reason, ConstraintCheck c)
        {
            if (d.Code != "S-00") return;   // ya decidió una restricción de mayor prioridad
            d.Action = a;
            d.Code = code;
            d.Reason = reason;
            c.Applied = true;
        }

        // S-01 salud de sensores críticos de navegación
        var c1 = Check("S-01", "Sensores críticos de navegación", s.NavValid >= 2,
            $"{s.NavValid}/{s.NavTotal} válidos" + (s.Contradiction ? " · contradicción" : ""), true);
        if (s.NavValid == 0)
        {
            d.EnterSafeMode = true;
            Set(RoverAction.ModoSeguro, "S-01", "Todos los sensores de navegación fallan: no se avanza a ciegas", c1);
        }
        else if (s.NavValid < 2) d.SpeedCap = MathF.Min(d.SpeedCap, 0.2f);

        // S-02 batería agotada
        var c2 = Check("S-02", "Batería > 0", i.BatteryWh > 0.01f, $"{i.BatteryWh:0.0} Wh", true);
        if (!c2.Passed) Set(RoverAction.ModoSeguro, "S-02", "Batería agotada: sin movimiento (nunca batería negativa)", c2);

        // S-03 límites térmicos del hardware
        float margin = MathF.Min(i.TempInt - i.HwMin, i.HwMax - i.TempInt);
        var c3 = Check("S-03", "Temperatura interior en tolerancia", margin > 0, $"{i.TempInt:0.0} °C (límites {i.HwMin:0}…{i.HwMax:0})", true);
        if (!c3.Passed)
        {
            d.EnterSafeMode = true;
            Set(RoverAction.ModoSeguro, "S-03", "Temperatura fuera de tolerancia: aislar subsistemas", c3);
        }
        else if (margin < 10) { d.Alert = true; d.SpeedCap = MathF.Min(d.SpeedCap, 0.35f); }

        // S-04 sismo / vibración extrema
        var c4 = Check("S-04", "Sin sismo activo", i.QuakeAccel < 0.9f, $"aceleración {i.QuakeAccel:0.00} m/s²", true);
        if (!c4.Passed) Set(RoverAction.Esperar, "S-04", "Sismo: frenar, esperar y remedir", c4);

        if (i.HasProposal)
        {
            // S-05 dentro del mapa
            var c5 = Check("S-05", "Destino dentro del mapa", i.Proposed.X >= 0 && i.Proposed.Y >= 0, i.Proposed.ToString(), true);
            if (!c5.Passed) Set(RoverAction.Detener, "S-05", "Movimiento fuera del mapa", c5);

            // S-06 barranco confirmado por el sensor determinista de borde
            bool edgeOk = s.Ok("lidar_drop");
            float drop = edgeOk ? s.V("lidar_drop") : float.NaN;
            bool confirmed = (edgeOk && drop > WorldMap.CliffDropM) || s.Contradiction;
            var c6 = Check("S-06", "Sin barranco confirmado delante", !confirmed && !i.ProposedForbidden,
                edgeOk ? $"caída {drop:0.0} m (umbral {WorldMap.CliffDropM:0} m)" + (i.ProposedForbidden ? " · celda prohibida" : "") : "sensor de borde NO válido", true);
            if (!c6.Passed)
            {
                d.Veto = true;
                d.NeedReplan = true;
                Set(RoverAction.Replanificar, "S-06", i.ProposedForbidden && !confirmed ? "Celda prohibida en el mapa de creencias: veto y replanificación"
                    : "Barranco confirmado: detener, zona prohibida y replantear ruta", c6);
            }

            // S-07 RNA: veto conservador con probabilidad muy alta, alerta con probabilidad media
            float pHaz = i.RnaProbs[1] + i.RnaProbs[2];
            var c7 = Check("S-07", i.RnaEnabled ? "Riesgo RNA de la celda siguiente" : "Riesgo (regla sin RNA)", MathF.Max(i.RnaProbs[1], i.RnaProbs[2]) < CliffVetoP,
                $"p(obst) {i.RnaProbs[1]:0.00} · p(barr) {i.RnaProbs[2]:0.00}", true);
            if (!c7.Passed)
            {
                d.Veto = true;
                d.NeedReplan = true;
                Set(RoverAction.Replanificar, "S-07", $"{(i.RnaProbs[2] > i.RnaProbs[1] ? "Barranco" : "Obstáculo")} probable (p ≥ {CliffVetoP:0.00}): veto conservador", c7);
            }
            else if (pHaz >= AlertP)
            {
                d.Alert = true;
                d.SpeedCap = MathF.Min(d.SpeedCap, 0.3f);
            }

            // S-08 colisión inminente (proximidad frontal)
            bool proxOk = s.Ok("prox_f");
            float pf = proxOk ? s.V("prox_f") : 0;
            var c8 = Check("S-08", "Sin colisión inminente", !proxOk || pf >= 1.5f || pf >= 19.9f,
                proxOk ? $"proximidad frontal {pf:0.0} m" : $"proximidad frontal no válida ({s["prox_f"].Status})", true);
            if (!c8.Passed)
            {
                d.Veto = true;
                d.NeedReplan = true;
                Set(RoverAction.Replanificar, "S-08", "Obstáculo a menos de 1,5 m: veto por colisión", c8);
            }
            if (!proxOk && s["prox_f"].Status == SensorStatus.Saturado)
                d.SpeedCap = MathF.Min(d.SpeedCap, 0.1f);   // distancia 0: prioridad al frenado

            // S-09 vuelco / pendiente
            var c9 = Check("S-09", "Índice de vuelco < 1", i.TipIndex < 1f, $"índice {i.TipIndex:0.00}", true);
            if (!c9.Passed)
            {
                d.Veto = true;
                d.NeedReplan = true;
                Set(RoverAction.Replanificar, "S-09", "Pendiente/viento lateral con riesgo de vuelco", c9);
            }
        }

        // S-10 viento
        var c10 = Check("S-10", "Viento bajo el límite operacional", i.WindPct < 100f, $"{i.WindPct:0}% del límite", true);
        if (!c10.Passed) Set(RoverAction.Esperar, "S-10", "Viento sobre el límite: detenerse y esperar", c10);
        else if (i.WindPct > 75) d.SpeedCap = MathF.Min(d.SpeedCap, 0.35f);

        // S-11 reserva de energía para volver al refugio
        bool energyOk = i.AtRefuge || i.BatteryWh - i.EnergyToRefugeWh >= i.ReserveWh;
        var c11 = Check("S-11", "Energía para volver al refugio", energyOk,
            i.RefugeReachable ? $"necesita {i.EnergyToRefugeWh:0.0}+{i.ReserveWh:0} Wh, hay {i.BatteryWh:0.0} Wh" : "refugio inalcanzable en el mapa estimado", false);
        if (!energyOk)
        {
            d.GoRefuge = true;
            if (!i.RefugeReachable || i.BatteryWh < i.EnergyToRefugeWh)
            {
                if (i.Daylight && i.SolarW > 5)
                    Set(RoverAction.Recargar, "S-11", "Refugio inalcanzable con la energía actual: recarga solar in situ (no se promete rescate)", c11);
                else
                    Set(RoverAction.ModoSeguro, "S-11", "Refugio inalcanzable y sin sol: hibernación en modo seguro (se declara inalcanzable)", c11);
            }
        }

        // S-12 confianza baja de la RNA → fusión con sensor determinista y modo conservador
        var c12 = Check("S-12", "Confianza RNA suficiente", !i.RnaEnabled || i.RnaConfidence >= LowConfidence, $"confianza {i.RnaConfidence:0.00}", false);
        if (!c12.Passed)
        {
            d.SpeedCap = MathF.Min(d.SpeedCap, 0.3f);
            d.Alert = true;
            c12.Applied = true;
        }

        // S-13 visibilidad
        bool visOk = s.Ok("vis") && s.V("vis") >= 150;
        var c13 = Check("S-13", "Visibilidad ≥ 150 m", visOk, s.Ok("vis") ? $"{s.V("vis"):0} m" : "no válida", false);
        if (!visOk) { d.SpeedCap = MathF.Min(d.SpeedCap, 0.2f); c13.Applied = true; }

        // combinación final
        if (d.Code == "S-00")
        {
            if (!i.HasProposal)
            {
                d.Action = RoverAction.Esperar;
                d.Code = "S-20";
                d.Reason = "Sin movimiento propuesto: observar y esperar";
            }
            else
            {
                float v = MathF.Min(i.FuzzySpeed, d.SpeedCap);
                d.Speed = v;
                if (v < 0.06f)
                {
                    d.Action = RoverAction.Esperar;
                    d.Code = "S-21";
                    d.Reason = $"Velocidad recomendada {v:0.00} ≈ parar: esperar y remedir";
                }
                else
                {
                    d.Action = RoverAction.Avanzar;
                    d.Code = d.SpeedCap < i.FuzzySpeed - 1e-3f ? "S-30" : "S-31";
                    d.Reason = d.SpeedCap < i.FuzzySpeed - 1e-3f
                        ? $"Avanzar a {v:0.00} (difuso {i.FuzzySpeed:0.00} limitado por seguridad a {d.SpeedCap:0.00})"
                        : $"Avanzar a velocidad difusa {v:0.00}";
                }
            }
        }
        if (d.GoRefuge && d.Action == RoverAction.Avanzar)
        {
            d.Code = "S-11";
            d.Reason = "Reserva de energía comprometida: rumbo al refugio";
            d.Checks.First(c => c.Code == "S-11").Applied = true;
        }
        d.Explanation = $"{d.Code} · {d.Reason}";
        return d;
    }

    public static string ActionName(RoverAction a) => a switch
    {
        RoverAction.Avanzar => "AVANZAR", RoverAction.Detener => "DETENER", RoverAction.Esperar => "ESPERAR", RoverAction.Recargar => "RECARGAR",
        RoverAction.Volver => "VOLVER", RoverAction.Replanificar => "REPLANIFICAR", RoverAction.ModoSeguro => "MODO SEGURO",
        RoverAction.Muestrear => "MUESTREAR", RoverAction.Abortar => "ABORTAR", _ => a.ToString().ToUpperInvariant(),
    };
}
