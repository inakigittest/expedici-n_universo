using System;
using System.Linq;
using AresX.AI;
using AresX.Sim;
using Godot;

namespace AresX.UI;

/// <summary>
/// Ficha flotante que sigue al vehículo en pantalla (exploratoria): la decisión del paso con su ícono (mover, esperar,
/// analizar, recargar…), batería, temperatura interior, ráfaga frente al límite y lo que la RNA ve delante. En el
/// helicóptero agrega altura y margen de potencia. Se posiciona sobre el vehículo con suavidad y se mantiene dentro del
/// área central libre. Sin mucho texto: íconos, barras y números.
/// </summary>
public partial class VehicleCard : Control
{
    public Mission? Mission;
    public Vector3 WorldPos;            // punto del vehículo (la ficha va encima)
    public float LiftUnits = 3.5f;      // altura sobre el punto (unidades de mundo)
    public Rect2 Area = new(0, 0, 1280, 720);
    private Vector2 _pos;
    private bool _placed;
    private const float W = 176;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Size = new Vector2(W, 96);
    }

    public override void _Process(double delta)
    {
        var m = Mission;
        var cam = GetViewport()?.GetCamera3D();
        if (m == null || cam == null || !IsVisibleInTree()) return;
        Size = new Vector2(W, (m.Heli ? 112 : 96) + (m.Rescue ? 15 : 0));
        var world = WorldPos + Vector3.Up * LiftUnits;
        if (cam.IsPositionBehind(world)) return;
        Vector2 sp = cam.UnprojectPosition(world);
        // encima del vehículo, con una pequeña separación; dentro del área central
        var goal = sp + new Vector2(28, -Size.Y - 18);
        goal.X = Mathf.Clamp(goal.X, Area.Position.X + 4, Area.End.X - Size.X - 4);
        goal.Y = Mathf.Clamp(goal.Y, Area.Position.Y + 76, Area.End.Y - Size.Y - 8);
        _pos = _placed ? _pos.Lerp(goal, 1 - MathF.Exp(-10f * (float)delta)) : goal;
        _placed = true;
        Position = _pos;
        _anchor = sp - _pos;
        QueueRedraw();
    }

    private Vector2 _anchor;

    public override void _Draw()
    {
        var m = Mission;
        if (m == null) return;
        var d = m.LastDecision;
        bool heli = m.Heli;
        float h = Size.Y;
        // conector hacia el vehículo
        var anchor = _anchor;
        var near = new Vector2(Mathf.Clamp(anchor.X, 8, W - 8), anchor.Y > h ? h : anchor.Y < 0 ? 0 : anchor.Y);
        DrawLine(near, anchor, new Color(1, 1, 1, 0.45f), 1.2f, true);
        DrawCircle(anchor, 3, new Color(1, 1, 1, 0.7f));
        var bg = new StyleBoxFlat { BgColor = new Color(0.03f, 0.05f, 0.08f, 0.88f), BorderColor = UiTheme.Border, CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6 };
        bg.SetBorderWidthAll(1);
        DrawStyleBox(bg, new Rect2(Vector2.Zero, Size));

        // decisión
        var col = d == null ? UiTheme.Dim : d.Veto ? UiTheme.Crit : d.Action == RoverAction.Avanzar ? (d.Alert ? UiTheme.Warn : UiTheme.Ok) : UiTheme.Warn;
        var act = d?.Action ?? RoverAction.Esperar;
        DrawCircle(new Vector2(18, 18), 13, new Color(col, 0.2f));
        Icons.Draw(this, Icons.Action(act), new Vector2(18, 18), 16, col);
        string verb = act == RoverAction.Muestrear && m.Rescue ? (m.Medical.Exists(x => x.Cell == m.Rover.Pos) && m.OnBoard > 0 ? "ENTREGAR" : "RESCATAR") : Icons.Verb(act);
        if (act == RoverAction.Muestrear && m.Rescue) Icons.Draw(this, verb == "ENTREGAR" ? "cross" : "person", new Vector2(18, 18), 16, col);
        Gfx.Text(this, new Vector2(36, 5), verb + (act == RoverAction.Avanzar && d != null ? $" {d.Speed * m.Rover.VmaxMs:0.#} m/s" : ""), 12, col, W - 40);
        Gfx.Text(this, new Vector2(36, 20), d?.Code ?? "", 9, UiTheme.Dim, W - 40);

        float y = 38;
        void Row(string icon, Color ic, float frac, Color bar, string val)
        {
            Icons.Draw(this, icon, new Vector2(14, y + 6), 12, ic);
            if (frac >= 0) Gfx.Bar(this, new Rect2(26, y + 3, W - 26 - 52, 7), Mathf.Clamp(frac, 0, 1), bar);
            Gfx.Text(this, new Vector2(0, y), val, 10, Colors.White, W - 6, HorizontalAlignment.Right);
            y += 15;
        }
        float bat = m.Rover.BatteryPct;
        var bc = bat < 15 ? UiTheme.Crit : bat < 35 ? UiTheme.Warn : UiTheme.Ok;
        Row("battery", bc, bat / 100f, bc, $"{bat:0} %");
        float t = m.Rover.TempInt;
        float tMargin = MathF.Min(t - m.Planet.HardwareMinC, m.Planet.HardwareMaxC - t);
        var tc = tMargin < 10 ? UiTheme.Crit : tMargin < 20 ? UiTheme.Warn : UiTheme.Info;
        Row("thermo", tc, (t - m.Planet.HardwareMinC) / MathF.Max(1, m.Planet.HardwareMaxC - m.Planet.HardwareMinC), tc, $"{t:0} °C");
        float wind = m.Planet.AirDensity > 0 ? m.Env.WindGust / MathF.Max(1, m.WindLimitMs) : 0;
        var wc = wind > 1 ? UiTheme.Crit : wind > 0.75f ? UiTheme.Warn : UiTheme.Info;
        Row("wind", wc, wind, wc, m.Planet.AirDensity > 0 ? $"{m.Env.WindGust:0}/{m.WindLimitMs:0}" : "—");
        if (m.Rescue)
        {
            var oc = m.OnBoard >= m.Capacity ? UiTheme.Warn : UiTheme.Ok;
            Row("person", oc, m.OnBoard / (float)m.Capacity, oc, $"{m.OnBoard}/{m.Capacity} a bordo");
        }
        if (heli)
        {
            var mc = m.HeliMargin < 1 ? UiTheme.Crit : m.HeliMargin < 1.12f ? UiTheme.Warn : UiTheme.Ok;
            Row("gauge", mc, (m.HeliMargin - 0.8f) / 1.0f, mc, $"{m.HeliMargin:0.00}");
        }
        // lo que ve la RNA delante (punto de color) y altura en el helicóptero
        if (m.FrontCell != null)
        {
            var p = m.FrontProbs;
            int k = Array.IndexOf(p, p.Max());
            Color[] cc = { new("5ad87a"), new("ff9a3a"), new("ff4a4a") };
            Icons.Draw(this, "eye", new Vector2(14, y + 6), 12, UiTheme.Rna);
            for (int i = 0; i < 3; i++) DrawRect(new Rect2(26 + i * 22, y + 2, 18, 9), new Color(cc[i], 0.25f + 0.75f * p[i]));
            Gfx.Text(this, new Vector2(0, y), heli ? $"{m.HeliAglM:0} m" : $"{p[k] * 100:0} %", 10, Colors.White, W - 6, HorizontalAlignment.Right);
        }
    }
}
