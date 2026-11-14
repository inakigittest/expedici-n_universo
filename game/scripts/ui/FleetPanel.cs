using System;
using System.Linq;
using AresX.Core;
using AresX.Sim;
using Godot;

namespace AresX.UI;

/// <summary>
/// Ventana FLOTA: una fila por vehículo (ícono, nombre, estado, carga o agua, recorrido) y, en incendio, los focos
/// ordenados por su prioridad difusa (amenaza, tamaño, viento) con el área en llamas y quemada. Abajo, el último
/// mensaje de radio entre vehículos.
/// </summary>
public partial class FleetPanel : Control
{
    public Mission? Mission;
    private const float RowH = 30;

    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree() || Mission == null) return;
        var m = Mission;
        float h = 20 + RowH * m.FleetSize + (m.FireMode ? 46 + 15 * Math.Min(6, m.Fires.Count) : 0) + 34;
        if (MathF.Abs(CustomMinimumSize.Y - h) > 0.5f) CustomMinimumSize = new Vector2(0, h);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var m = Mission;
        if (m == null) return;
        float w = Size.X, y = 2;
        Gfx.Header(this, new Vector2(2, y), w - 4, m.FireMode ? $"FLOTA · {m.FleetSize} VEHÍCULOS · INCENDIO" : $"FLOTA · {m.FleetSize} VEHÍCULOS · RESCATE", UiTheme.Accent);
        y += 20;
        // H-1 (líder)
        var d = m.LastDecision;
        string lead = d == null ? "" : d.Action == AI.RoverAction.Avanzar ? $"rumbo a {m.TargetName}" : Icons.Verb(d.Action).ToLowerInvariant() + " · " + m.TargetName;
        Row(y, true, "H-1", "líder · IA híbrida", lead, m.Rescue ? m.OnBoard / (float)m.Capacity : m.MainWaterL / 1000f,
            m.Rescue ? $"{m.OnBoard}/{m.Capacity}" : $"{m.MainWaterL:0} L", UiTheme.Rna);
        y += RowH;
        foreach (var u in m.Fleet)
        {
            float frac = m.Rescue ? u.Aboard.Count / (float)u.Capacity : u.WaterL / MathF.Max(1, u.WaterCapL);
            Row(y, u.Heli, u.Name, u.Heli ? "helicóptero" : "carro · 6 bomberos", u.Status, frac,
                m.Rescue ? $"{u.Aboard.Count}/{u.Capacity}" : $"{u.WaterL:0} L", u.Heli ? UiTheme.Info : new Color("ff7a3a"), u.DistanceM);
            y += RowH;
        }
        if (m.FireMode)
        {
            y += 4;
            Gfx.Header(this, new Vector2(2, y), w - 4, $"FOCOS · {m.Burning.Count * m.Map.CellM * m.Map.CellM / 10000f:0.0} ha en llamas · {m.BurnedHa:0.0} ha quemadas · {m.BuildingsLost} viviendas", UiTheme.Crit);
            y += 20;
            Gfx.Text(this, new Vector2(8, y), "foco", 9, UiTheme.Dim);
            Gfx.Text(this, new Vector2(48, y), "prioridad difusa", 9, UiTheme.Dim);
            Gfx.Text(this, new Vector2(0, y), "ha · km a zona poblada", 9, UiTheme.Dim, w - 8, HorizontalAlignment.Right);
            y += 14;
            foreach (var f in m.Fires.Take(6))
            {
                var pc = f.Priority > 0.66f ? UiTheme.Crit : f.Priority > 0.4f ? UiTheme.Warn : UiTheme.Ok;
                Icons.Draw(this, "warn", new Vector2(14, y + 6), 10, pc);
                Gfx.Text(this, new Vector2(24, y - 1), f.Name, 10, UiTheme.Text);
                Gfx.Bar(this, new Rect2(48, y + 3, w - 48 - 96, 7), f.Priority, pc);
                Gfx.Text(this, new Vector2(0, y - 1), $"{f.AreaHa:0.0} · {f.ThreatKm:0.0}", 10, Colors.White, w - 8, HorizontalAlignment.Right);
                y += 15;
            }
        }
        // último mensaje de radio
        var radio = m.Log.LastOrDefault(l => l.Kind == "radio");
        if (radio != null)
        {
            y += 4;
            bool fresh = m.StepCount - radio.Step <= 3;
            Icons.Draw(this, "wind", new Vector2(12, y + 8), 12, fresh ? UiTheme.Ok : UiTheme.Dim);
            Gfx.Text(this, new Vector2(24, y), radio.Text.Replace("📻 ", ""), 10, fresh ? UiTheme.Ok : UiTheme.Dim, w - 28);
        }
    }

    private void Row(float y, bool heli, string name, string kind, string status, float frac, string val, Color col, float distM = -1)
    {
        float w = Size.X;
        DrawRect(new Rect2(2, y + 1, w - 4, RowH - 3), new Color(col, 0.06f));
        DrawRect(new Rect2(2, y + 1, 3, RowH - 3), col);
        if (heli) Icons.Draw(this, "drone", new Vector2(18, y + 13), 15, col);
        else
        {
            // camión: caja + cabina + ruedas
            DrawRect(new Rect2(9, y + 7, 12, 8), col);
            DrawRect(new Rect2(21, y + 9, 6, 6), col);
            DrawCircle(new Vector2(13, y + 17), 2.2f, col);
            DrawCircle(new Vector2(24, y + 17), 2.2f, col);
        }
        Gfx.Text(this, new Vector2(34, y + 1), name, 12, col);
        Gfx.Text(this, new Vector2(70, y + 3), kind + (distM >= 0 ? $" · {distM / 1000:0.0} km" : ""), 9, UiTheme.Dim, w - 150);
        Gfx.Text(this, new Vector2(34, y + 15), status, 10, UiTheme.Text, w - 34 - 70);
        Gfx.Bar(this, new Rect2(w - 66, y + 6, 58, 6), frac, col);
        Gfx.Text(this, new Vector2(w - 70, y + 14), val, 9, Colors.White, 64, HorizontalAlignment.Right);
    }
}
