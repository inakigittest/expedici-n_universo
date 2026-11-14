using System;
using System.Collections.Generic;
using System.Linq;
using AresX.AI;
using AresX.Core;
using AresX.Sim;
using AresX.View;
using Godot;

namespace AresX.UI;

/// <summary>
/// HUD táctico de cabina (estética propia, inspirada en los HUD de anime de mechas: marcos biselados, tipografía
/// condensada, naranjo/verde/rojo, celdas hexagonales y franjas de peligro). Todo lo que muestra es dato real del juego:
///   * cinta de rumbo con la marca del objetivo; cintas de velocidad y altura; retícula hexagonal con horizonte y
///     escalera de cabeceo proyectados desde la cámara;
///   * marcadores en el mundo: objetivo, ruta próxima, personas por rescatar y puntos médicos (flecha al borde si no se ven);
///   * MONITOR IA: tres ondas (RNA, difuso, AG) cuya amplitud y ritmo salen de la confianza, la velocidad difusa y la
///     mejora del fitness;
///   * bloque de decisión con el verbo de la acción y una celda hexagonal por restricción del supervisor;
///   * sistemas en barras segmentadas y estado de la tripulación; franja de peligro ante veto o alerta.
/// Nivel: cabina = completo; sobre el vehículo = rumbo, marcadores, decisión y alertas; libre = marcadores y alertas.
/// </summary>
public partial class CockpitHud : Control
{
    public Mission? Mission;
    public CamMode Mode = CamMode.Tactica;

    private static readonly Color O = new("ff7a1a"), Amber = new("ffb02e"), G = new("46ff86"), R = new("ff2b2b"), Pu = new("b07aff"), W = new("ffe8d2");
    private static readonly Color Bg = new(0.02f, 0.01f, 0f, 0.55f);
    private static FontVariation? _cond, _condBold;
    private static Font Cond => _cond ??= new FontVariation { BaseFont = ThemeDB.FallbackFont, VariationTransform = new Transform2D(new Vector2(0.8f, 0), new Vector2(0, 1), Vector2.Zero) };
    private static Font CondBold => _condBold ??= new FontVariation { BaseFont = ThemeDB.FallbackFont, VariationTransform = new Transform2D(new Vector2(0.74f, 0), new Vector2(0, 1), Vector2.Zero), VariationEmbolden = 0.9f };

    private float _t, _speedDisp, _altDisp, _alertT;
    private readonly float[] _waveAmp = new float[3];
    private GaResult? _lastGa;
    private float _gaPulse;
    private string _lastCode = "";

    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;

    public override void _Process(double delta)
    {
        Visible = Mode != CamMode.Tactica && Mission != null;
        if (!Visible) return;
        float dt = (float)delta;
        _t += dt;
        var m = Mission!;
        var d = m.LastDecision;
        float speed = d?.Action == RoverAction.Avanzar ? d.Speed * m.Rover.VmaxMs : 0;
        _speedDisp = Mathf.Lerp(_speedDisp, speed, 1 - MathF.Exp(-3f * dt));
        float alt = m.Heli ? m.HeliAglM : 0;
        _altDisp = Mathf.Lerp(_altDisp, alt, 1 - MathF.Exp(-2f * dt));
        // amplitudes del monitor IA
        float rnaConf = m.FrontCell != null ? m.FrontProbs.Max() : 0.2f;
        float fz = m.LastFuzzy?.Crisp["velocidad"] ?? 0;
        if (m.LastGa != _lastGa) { _lastGa = m.LastGa; _gaPulse = 1; }
        _gaPulse = MathF.Max(0.15f, _gaPulse - dt * 0.35f);
        float[] goal = { rnaConf, 0.2f + 0.8f * fz, _gaPulse };
        for (int i = 0; i < 3; i++) _waveAmp[i] = Mathf.Lerp(_waveAmp[i], goal[i], 1 - MathF.Exp(-4f * dt));
        if (d != null && d.Veto && d.Code != _lastCode) _alertT = 3.5f;
        _lastCode = d?.Code ?? "";
        _alertT = MathF.Max(0, _alertT - dt);
        QueueRedraw();
    }

    /// <summary>Área libre del centro, sin la línea de ambiente de arriba ni la del objetivo de abajo.</summary>
    private Rect2 Area
    {
        get
        {
            var c = Platform.IsMobile ? new Rect2(0, 34, Size.X - 140, Size.Y - 92) : WindowManager.Center;
            if (c.Size.X < 200 || c.Size.Y < 200) c = new Rect2(Vector2.Zero, Size);
            return new Rect2(c.Position.X, c.Position.Y + 64, c.Size.X, MathF.Max(200, c.Size.Y - 64 - 40));
        }
    }

    // ------------------------------------------------------------------ primitivas de estilo
    private void Txt(Vector2 p, string s, int size, Color c, float w = -1, HorizontalAlignment al = HorizontalAlignment.Left, bool bold = false) =>
        DrawString(bold ? CondBold : Cond, p + new Vector2(0, size), s, al, w, size, c);

    /// <summary>Caja con esquinas biseladas (arriba-izquierda y abajo-derecha).</summary>
    private void Chamfer(Rect2 r, Color line, Color? fill = null, float cut = 8, float width = 1.5f)
    {
        var pts = new[]
        {
            r.Position + new Vector2(cut, 0), new Vector2(r.End.X, r.Position.Y), new Vector2(r.End.X, r.End.Y - cut),
            r.End - new Vector2(cut, 0), new Vector2(r.Position.X, r.End.Y), r.Position + new Vector2(0, cut), r.Position + new Vector2(cut, 0),
        };
        if (fill is Color f) DrawColoredPolygon(pts[..6], f);
        DrawPolyline(pts, line, width, true);
    }

    private void Hex(Vector2 c, float r, Color col, bool fill)
    {
        var pts = new Vector2[7];
        for (int i = 0; i < 7; i++) { float a = MathF.PI / 3 * i + MathF.PI / 6; pts[i] = c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r; }
        if (fill) DrawColoredPolygon(pts[..6], col);
        else DrawPolyline(pts, col, 1.4f, true);
    }

    private void Stripes(Rect2 r, Color a, Color b, float phase)
    {
        DrawRect(r, b);
        float sw = 14;
        for (float x = r.Position.X - r.Size.Y - sw * 2 + (phase % (sw * 2)); x < r.End.X; x += sw * 2)
        {
            var poly = new[]
            {
                new Vector2(x, r.End.Y), new Vector2(x + sw, r.End.Y), new Vector2(x + sw + r.Size.Y, r.Position.Y), new Vector2(x + r.Size.Y, r.Position.Y),
            };
            for (int i = 0; i < 4; i++) poly[i].X = Mathf.Clamp(poly[i].X, r.Position.X, r.End.X);
            DrawColoredPolygon(poly, a);
        }
    }

    private static float HeadingOf(Vector3 v) => Mathf.PosMod(Mathf.RadToDeg(MathF.Atan2(v.X, -v.Z)), 360f);

    // ------------------------------------------------------------------ dibujo
    public override void _Draw()
    {
        var m = Mission;
        var cam = GetViewport()?.GetCamera3D();
        if (m == null || cam == null) return;
        var a = Area;
        bool full = Mode == CamMode.Cabina;
        var fwd = -cam.GlobalBasis.Z;
        float hdg = HeadingOf(fwd);

        if (full) DrawFrame(a);
        DrawMarkers(m, cam, a);
        if (Mode != CamMode.Libre) DrawHeadingTape(m, cam, a, hdg);
        if (full)
        {
            DrawReticle(cam, a);
            DrawTapes(m, a);
            DrawMonitor(m, a);
            DrawSystems(m, a);
        }
        if (Mode != CamMode.Libre) DrawDecision(m, a, full);
        DrawAlerts(m, a);
    }

    private void DrawFrame(Rect2 a)
    {
        // líneas de barrido y esquinas biseladas
        for (float y = a.Position.Y; y < a.End.Y; y += 3) DrawLine(new Vector2(a.Position.X, y), new Vector2(a.End.X, y), new Color(0, 0, 0, 0.07f), 1);
        float L = MathF.Min(90, a.Size.X * 0.12f), c = 14, ins = 10;
        var r = a.Grow(-ins);
        foreach (var (corner, sx, sy) in new[] { (r.Position, 1f, 1f), (new Vector2(r.End.X, r.Position.Y), -1f, 1f), (new Vector2(r.Position.X, r.End.Y), 1f, -1f), (r.End, -1f, -1f) })
        {
            Vector2 P(float x, float y) => corner + new Vector2(x * sx, y * sy);
            DrawPolyline(new[] { P(0, L), P(0, c), P(c, 0), P(L, 0) }, new Color(O, 0.85f), 2f, true);
            DrawPolyline(new[] { P(5, L * 0.7f), P(5, c + 3), P(c + 3, 5), P(L * 0.7f, 5) }, new Color(O, 0.35f), 1f, true);
        }
        // rótulo de cabina
        var m = Mission!;
        var box = new Rect2(r.Position + new Vector2(18, 10), new Vector2(170, 40));
        Chamfer(box, O, Bg);
        Txt(box.Position + new Vector2(8, 1), "CABINA", 20, O, -1, HorizontalAlignment.Left, true);
        Txt(box.Position + new Vector2(8, 23), (m.Heli ? "HELICÓPTERO" : m.Map.ProbeMode ? "SONDA" : "ROVER") + $" · T+{m.StepCount:0000}", 11, Amber);
        DrawRect(new Rect2(box.End.X + 4, box.Position.Y, 6, box.Size.Y), new Color(O, 0.7f));
    }

    private void DrawHeadingTape(Mission m, Camera3D cam, Rect2 a, float hdg)
    {
        float w = MathF.Min(520, a.Size.X * 0.5f), cx = a.GetCenter().X, y = a.Position.Y + 6;
        var tr = new Rect2(cx - w / 2, y, w, 26);
        DrawRect(tr, Bg);
        DrawLine(new Vector2(tr.Position.X, tr.End.Y), tr.End, new Color(O, 0.8f), 1.5f);
        float span = 90f;   // grados visibles
        for (int deg = -60; deg <= 420; deg += 5)
        {
            float diff = Mathf.Wrap(deg - hdg, -180f, 180f);
            if (MathF.Abs(diff) > span / 2) continue;
            float x = cx + diff / span * w;
            bool major = deg % 15 == 0;
            DrawLine(new Vector2(x, tr.End.Y), new Vector2(x, tr.End.Y - (major ? 9 : 4)), new Color(O, major ? 0.9f : 0.5f), 1.2f);
            if (deg % 30 == 0 && deg >= 0 && deg < 360)
            {
                string lab = deg switch { 0 => "N", 90 => "E", 180 => "S", 270 => "O", _ => $"{deg / 10:00}" };
                Txt(new Vector2(x - 14, tr.Position.Y + 1), lab, 12, deg % 90 == 0 ? W : Amber, 28, HorizontalAlignment.Center, deg % 90 == 0);
            }
        }
        // objetivo sobre la cinta
        if (TargetCell(m) is Cell tc)
        {
            float b = HeadingOf(Grid.CellCenter(tc) - cam.GlobalPosition);
            float diff = Mathf.Wrap(b - hdg, -180f, 180f);
            float x = cx + Mathf.Clamp(diff / span, -0.5f, 0.5f) * w;
            var p = new Vector2(x, tr.End.Y + 6);
            DrawColoredPolygon(new[] { p + new Vector2(0, -6), p + new Vector2(5, 0), p + new Vector2(0, 6), p + new Vector2(-5, 0) }, G);
        }
        // rumbo actual
        var hb = new Rect2(cx - 26, tr.End.Y + 2, 52, 20);
        Chamfer(hb, O, new Color(0, 0, 0, 0.75f), 5);
        Txt(hb.Position + new Vector2(0, 1), $"{hdg:000}", 15, W, hb.Size.X, HorizontalAlignment.Center, true);
    }

    private void DrawReticle(Camera3D cam, Rect2 a)
    {
        var c = a.GetCenter();
        float rot = _t * 0.4f;
        // hexágono con huecos y marcas
        for (int i = 0; i < 6; i++)
        {
            float a0 = MathF.PI / 3 * i + rot, a1 = a0 + MathF.PI / 3 * 0.62f;
            DrawLine(c + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * 30, c + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * 30, new Color(O, 0.9f), 1.6f, true);
        }
        foreach (var dir in new[] { Vector2.Left, Vector2.Right, Vector2.Up, Vector2.Down })
            DrawLine(c + dir * 40, c + dir * 54, new Color(O, 0.8f), 1.6f);
        DrawCircle(c, 2.2f, W);
        // horizonte y escalera de cabeceo proyectados (líneas a ±10° y ±20°)
        var f = -cam.GlobalBasis.Z;
        var flat = new Vector3(f.X, 0, f.Z);
        if (flat.LengthSquared() < 1e-4f) return;
        flat = flat.Normalized();
        foreach (int deg in new[] { -20, -10, 0, 10, 20 })
        {
            float ang = Mathf.DegToRad(deg);
            var wp = cam.GlobalPosition + (flat * MathF.Cos(ang) + Vector3.Up * MathF.Sin(ang)) * 500f;
            if (cam.IsPositionBehind(wp)) continue;
            var sp = cam.UnprojectPosition(wp);
            if (!a.HasPoint(sp)) continue;
            float half = deg == 0 ? 150 : 46, gap = deg == 0 ? 66 : 20;
            var col = new Color(G, deg == 0 ? 0.8f : 0.5f);
            if (deg < 0)
            {
                for (float x = gap; x < half; x += 10)
                {
                    DrawLine(sp + new Vector2(x, 0), sp + new Vector2(MathF.Min(x + 5, half), 0), col, 1.2f);
                    DrawLine(sp - new Vector2(x, 0), sp - new Vector2(MathF.Min(x + 5, half), 0), col, 1.2f);
                }
            }
            else
            {
                DrawLine(sp + new Vector2(gap, 0), sp + new Vector2(half, 0), col, deg == 0 ? 1.6f : 1.2f);
                DrawLine(sp - new Vector2(gap, 0), sp - new Vector2(half, 0), col, deg == 0 ? 1.6f : 1.2f);
            }
            if (deg != 0) Txt(sp + new Vector2(half + 4, -7), $"{deg:+0;-0}", 10, col);
        }
    }

    private void DrawTapes(Mission m, Rect2 a)
    {
        var c = a.GetCenter();
        float off = MathF.Min(300, a.Size.X * 0.3f), h = MathF.Min(260, a.Size.Y * 0.42f);
        Tape(new Vector2(c.X - off, c.Y), h, _speedDisp, 1f, "VEL", "m/s", true);
        float elev = m.Map.Elevation(m.Rover.Pos);
        if (m.Heli) Tape(new Vector2(c.X + off, c.Y), h, _altDisp, 10f, "ALT", "m s/t", false, $"ELEV {elev:0} m");
        else Tape(new Vector2(c.X + off, c.Y), h, elev, 10f, "ELEV", "m", false, $"PEND {m.Map.Slope[m.Map.Idx(m.Rover.Pos)]:0}°");
    }

    /// <summary>Cinta vertical con marcas que se desplazan con el valor y su lectura en una caja biselada.</summary>
    private void Tape(Vector2 c, float h, float v, float step, string name, string unit, bool left, string? extra = null)
    {
        float w = 46, px = h / (step * 8);
        var r = new Rect2(c.X - w / 2, c.Y - h / 2, w, h);
        DrawRect(r, new Color(0, 0, 0, 0.28f));
        float edge = left ? r.End.X : r.Position.X, dir = left ? -1 : 1;
        DrawLine(new Vector2(edge, r.Position.Y), new Vector2(edge, r.End.Y), new Color(O, 0.8f), 1.5f);
        float first = MathF.Floor((v - h / 2 / px) / step) * step;
        for (float k = first; k <= v + h / 2 / px; k += step)
        {
            float y = c.Y - (k - v) * px;
            if (y < r.Position.Y || y > r.End.Y) continue;
            bool major = MathF.Abs(k / step % 2) < 0.01f;
            DrawLine(new Vector2(edge, y), new Vector2(edge + dir * (major ? 12 : 6), y), new Color(O, major ? 0.9f : 0.5f), 1.2f);
            if (major && k >= 0) Txt(new Vector2(left ? r.Position.X - 2 : edge + 14, y - 7), $"{k:0}", 10, Amber, w - 14, left ? HorizontalAlignment.Left : HorizontalAlignment.Left);
        }
        var box = new Rect2(c.X - 34, c.Y - 13, 68, 26);
        Chamfer(box, O, new Color(0, 0, 0, 0.8f), 6);
        Txt(box.Position + new Vector2(0, 2), v >= 100 ? $"{v:0}" : $"{v:0.0}", 17, W, box.Size.X, HorizontalAlignment.Center, true);
        Txt(new Vector2(r.Position.X - 10, r.Position.Y - 30), name, 14, O, w + 20, HorizontalAlignment.Center, true);
        Txt(new Vector2(r.Position.X - 10, r.Position.Y - 15), unit, 10, Amber, w + 20, HorizontalAlignment.Center);
        if (extra != null) Txt(new Vector2(r.Position.X - 30, r.End.Y + 4), extra, 10, Amber, w + 60, HorizontalAlignment.Center);
    }

    // ------------------------------------------------------------------ marcadores en el mundo
    private static Cell? TargetCell(Mission m) =>
        m.Config.ClickMode ? (m.HasDestination ? m.Target : null) : m.TargetName != "sin destino" ? m.Target : null;

    private void DrawMarkers(Mission m, Camera3D cam, Rect2 a)
    {
        // ruta próxima: chevrones sobre el terreno
        int shown = 0;
        foreach (var c in m.Route)
        {
            if (shown++ > 24) break;
            var wp = Grid.CellCenter(c) + Vector3.Up * 0.4f;
            if (cam.IsPositionBehind(wp)) continue;
            var sp = cam.UnprojectPosition(wp);
            if (!a.HasPoint(sp)) continue;
            float s = Mathf.Clamp(260f / MathF.Max(1, cam.GlobalPosition.DistanceTo(wp)), 2.5f, 7f);
            DrawPolyline(new[] { sp + new Vector2(-s, -s * 0.5f), sp + new Vector2(0, s * 0.4f), sp + new Vector2(s, -s * 0.5f) }, new Color(G, 0.75f), 1.5f, true);
        }
        if (m.Rescue)
        {
            foreach (var v in m.Victims)
            {
                if (!v.Active || v.OnBoard) continue;
                float pct = v.SurvivalMaxS > 0 ? v.SurvivalS / v.SurvivalMaxS : 0;
                Marker(cam, a, Grid.CellCenter(v.Cell) + Vector3.Up * 1.5f, pct < 0.3f ? R : Amber, $"{v.Name} · {v.SurvivalS / 60:0}′", 0);
            }
            foreach (var mp in m.Medical) Marker(cam, a, Grid.CellCenter(mp.Cell) + Vector3.Up * 1.5f, new Color("ff6a60"), "✚ " + mp.Name, 2);
        }
        if (TargetCell(m) is Cell t)
        {
            var wp = Grid.CellCenter(t) + Vector3.Up * 1.5f;
            float km = m.Rover.Pos.Dist(t) * m.Map.CellM / 1000f;
            Marker(cam, a, wp, G, $"OBJ · {m.TargetName} · {(km >= 1 ? $"{km:0.0} km" : $"{km * 1000:0} m")}", 1);
        }
    }

    /// <summary>Marcador: 0 triángulo, 1 rombo con corchetes, 2 cruz. Fuera de cuadro: flecha en el borde.</summary>
    private void Marker(Camera3D cam, Rect2 a, Vector3 wp, Color col, string label, int kind)
    {
        var inner = a.Grow(-26);
        bool behind = cam.IsPositionBehind(wp);
        var sp = cam.UnprojectPosition(wp);
        if (behind) sp = inner.GetCenter() - (sp - inner.GetCenter()) * 1000f;
        if (!behind && inner.HasPoint(sp))
        {
            float pulse = 0.6f + 0.4f * MathF.Sin(_t * 5f);
            switch (kind)
            {
                case 1:
                    DrawPolyline(new[] { sp + new Vector2(0, -10), sp + new Vector2(10, 0), sp + new Vector2(0, 10), sp + new Vector2(-10, 0), sp + new Vector2(0, -10) }, col, 1.8f, true);
                    foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
                        DrawPolyline(new[] { sp + new Vector2(sx * 18, sy * 10), sp + new Vector2(sx * 18, sy * 18), sp + new Vector2(sx * 10, sy * 18) }, new Color(col, pulse), 1.4f);
                    break;
                case 2:
                    DrawRect(new Rect2(sp - new Vector2(2.5f, 8), new Vector2(5, 16)), col);
                    DrawRect(new Rect2(sp - new Vector2(8, 2.5f), new Vector2(16, 5)), col);
                    break;
                default:
                    DrawColoredPolygon(new[] { sp + new Vector2(0, 8), sp + new Vector2(-7, -5), sp + new Vector2(7, -5) }, new Color(col, pulse));
                    break;
            }
            DrawRect(new Rect2(sp + new Vector2(14, -22), new Vector2(Cond.GetStringSize(label, HorizontalAlignment.Left, -1, 11).X + 8, 15)), new Color(0, 0, 0, 0.55f));
            Txt(sp + new Vector2(18, -22), label, 11, col);
            return;
        }
        // flecha en el borde del área
        var cc = inner.GetCenter();
        var dir = (sp - cc).Normalized();
        if (!dir.IsFinite() || dir.LengthSquared() < 0.5f) return;
        float tx = dir.X != 0 ? (dir.X > 0 ? inner.End.X - cc.X : inner.Position.X - cc.X) / dir.X : float.MaxValue;
        float ty = dir.Y != 0 ? (dir.Y > 0 ? inner.End.Y - cc.Y : inner.Position.Y - cc.Y) / dir.Y : float.MaxValue;
        var e = cc + dir * MathF.Min(tx, ty);
        var n = new Vector2(-dir.Y, dir.X);
        DrawColoredPolygon(new[] { e + dir * 10, e - dir * 6 + n * 7, e - dir * 6 - n * 7 }, new Color(col, 0.85f));
    }

    // ------------------------------------------------------------------ monitor IA, decisión y sistemas
    private void DrawMonitor(Mission m, Rect2 a)
    {
        var box = new Rect2(a.Position.X + 26, a.End.Y - 136, MathF.Min(270, a.Size.X * 0.26f), 124);
        Chamfer(box, O, Bg, 10);
        Txt(box.Position + new Vector2(10, 3), "MONITOR IA", 13, O, -1, HorizontalAlignment.Left, true);
        string[] names = { "RNA", "DIF", "AG" };
        Color[] cols = { UiTheme.Rna, Amber, Pu };
        float[] vals =
        {
            m.FrontCell != null ? m.FrontProbs.Max() : 0, m.LastFuzzy?.Crisp["velocidad"] ?? 0,
            m.LastGa?.Best?.Fitness ?? 0,
        };
        float gx = box.Position.X + 40, gw = box.Size.X - 50;
        for (int i = 0; i < 3; i++)
        {
            float cy = box.Position.Y + 38 + i * 30;
            DrawLine(new Vector2(gx, cy), new Vector2(gx + gw, cy), new Color(O, 0.18f), 1);
            Txt(new Vector2(box.Position.X + 8, cy - 8), names[i], 12, cols[i], -1, HorizontalAlignment.Left, true);
            var pts = new Vector2[48];
            float amp = 3 + 10 * _waveAmp[i], freq = 1.4f + i * 0.7f + 2f * _waveAmp[i];
            for (int k = 0; k < pts.Length; k++)
            {
                float x = k / (pts.Length - 1f);
                float yv = MathF.Sin(x * 12f * (1 + i * 0.35f) - _t * freq * 2) * 0.7f + MathF.Sin(x * 31f - _t * freq * 3.1f) * 0.3f;
                pts[k] = new Vector2(gx + gw * x, cy - yv * amp);
            }
            DrawPolyline(pts, cols[i], 1.4f, true);
            Txt(new Vector2(gx, cy + 2), i == 2 ? $"{vals[i]:0.00}" : $"{vals[i] * 100:0}%", 9, W, gw, HorizontalAlignment.Right);
        }
    }

    private void DrawDecision(Mission m, Rect2 a, bool full)
    {
        var d = m.LastDecision;
        if (d == null) return;
        var col = d.Veto ? R : d.Action == RoverAction.Avanzar ? (d.Alert ? Amber : G) : Amber;
        var act = d.Action;
        string verb = act == RoverAction.Muestrear && m.Rescue ? (m.Medical.Exists(x => x.Cell == m.Rover.Pos) ? "ENTREGAR" : "RESCATAR") : Icons.Verb(act);
        float w = full ? 300 : 250, cx = a.GetCenter().X;
        var box = new Rect2(cx - w / 2, a.End.Y - (full ? 92 : 80), w, full ? 84 : 72);
        Chamfer(box, col, Bg, 12, 2f);
        Icons.Draw(this, Icons.Action(act), box.Position + new Vector2(28, 26), 26, col);
        Txt(box.Position + new Vector2(52, 2), verb, full ? 32 : 26, col, w - 60, HorizontalAlignment.Left, true);
        Txt(box.Position + new Vector2(54, full ? 40 : 33), d.Code + (d.Veto ? " · VETO" : "") + (act == RoverAction.Avanzar ? $" · {d.Speed:0.00}" : ""), 12, W, w - 60);
        // una celda hexagonal por restricción del supervisor
        int n = d.Checks.Count;
        float hx = box.Position.X + 14, hy = box.End.Y - 12, step = MathF.Min(18, (w - 28) / MathF.Max(1, n));
        for (int i = 0; i < n; i++)
        {
            var ch = d.Checks[i];
            var hc = !ch.Passed ? R : ch.Applied ? Amber : new Color(G, 0.75f);
            Hex(new Vector2(hx + i * step + 7, hy), 7, new Color(hc, 0.3f), true);
            Hex(new Vector2(hx + i * step + 7, hy), 7, hc, false);
        }
    }

    private void DrawSystems(Mission m, Rect2 a)
    {
        float w = MathF.Min(260, a.Size.X * 0.25f);
        var box = new Rect2(a.End.X - w - 26, a.End.Y - 136, w, 124);
        Chamfer(box, O, Bg, 10);
        Txt(box.Position + new Vector2(10, 3), "SISTEMAS", 13, O, -1, HorizontalAlignment.Left, true);
        float y = box.Position.Y + 24;
        void Seg(string name, float frac, string val, Color col)
        {
            Txt(new Vector2(box.Position.X + 10, y - 1), name, 11, Amber);
            int segs = 16;
            float sx = box.Position.X + 58, sw = (box.Size.X - 58 - 56) / segs;
            for (int i = 0; i < segs; i++)
            {
                bool on = i < MathF.Round(Mathf.Clamp(frac, 0, 1) * segs);
                DrawRect(new Rect2(sx + i * sw, y + 2, sw - 2, 9), on ? col : new Color(col, 0.12f));
            }
            Txt(new Vector2(box.Position.X, y - 1), val, 11, W, box.Size.X - 8, HorizontalAlignment.Right);
            y += 17;
        }
        float bat = m.Rover.BatteryPct;
        Seg("BATERÍA", bat / 100f, $"{bat:0}%", bat < 15 ? R : bat < 35 ? Amber : G);
        float t = m.Rover.TempInt;
        float tf = (t - m.Planet.HardwareMinC) / MathF.Max(1, m.Planet.HardwareMaxC - m.Planet.HardwareMinC);
        Seg("TEMP", tf, $"{t:0}°C", tf > 0.85f || tf < 0.1f ? R : G);
        if (m.Heli) Seg("POTENCIA", (m.HeliMargin - 0.8f) / 1.0f, $"{m.HeliMargin:0.00}", m.HeliMargin < 1 ? R : m.HeliMargin < 1.12f ? Amber : G);
        float wind = m.Planet.AirDensity > 0 ? m.Env.WindGust / MathF.Max(1, m.WindLimitMs) : 0;
        Seg("VIENTO", wind, m.Planet.AirDensity > 0 ? $"{m.Env.WindGust:0}/{m.WindLimitMs:0}" : "—", wind > 1 ? R : wind > 0.75f ? Amber : G);
        if (m.Rescue) Seg("A BORDO", m.OnBoard / (float)m.Capacity, $"{m.OnBoard}/{m.Capacity}", m.OnBoard >= m.Capacity ? Amber : G);
        // tripulación
        bool outside = CrewView.Task != "";
        var crewCol = outside ? (MathF.Sin(_t * 7f) > 0 ? Amber : O) : new Color(G, 0.8f);
        Txt(new Vector2(box.Position.X + 10, box.End.Y - 18), outside ? CrewView.Task : "TRIPULACIÓN A BORDO", 11, crewCol, box.Size.X - 16, HorizontalAlignment.Left, true);
    }

    private void DrawAlerts(Mission m, Rect2 a)
    {
        var d = m.LastDecision;
        bool veto = _alertT > 0 && d != null;
        bool crit = m.State is MissionState.ModoSeguro or MissionState.Fracaso;
        bool crew = CrewView.Task != "" && Mode != CamMode.Cabina;
        if (!veto && !crit && !crew) return;
        float cx = a.GetCenter().X, w = MathF.Min(460, a.Size.X * 0.5f);
        var band = new Rect2(cx - w / 2, a.Position.Y + 62, w, 34);
        bool blink = MathF.Sin(_t * 9f) > -0.3f;
        if (veto || crit)
        {
            var col = R;
            Stripes(new Rect2(band.Position.X, band.Position.Y, 40, band.Size.Y), new Color(col, 0.9f), new Color(0, 0, 0, 0.7f), _t * 30);
            Stripes(new Rect2(band.End.X - 40, band.Position.Y, 40, band.Size.Y), new Color(col, 0.9f), new Color(0, 0, 0, 0.7f), _t * 30);
            var mid = new Rect2(band.Position.X + 44, band.Position.Y, band.Size.X - 88, band.Size.Y);
            DrawRect(mid, new Color(0, 0, 0, 0.75f));
            DrawRect(mid, new Color(col, blink ? 0.95f : 0.4f), false, 2);
            string txt = veto ? $"VETO · {d!.Code}" : m.State == MissionState.Fracaso ? "MISIÓN PERDIDA" : "MODO SEGURO";
            Txt(mid.Position + new Vector2(0, 1), txt, 24, blink ? col : new Color(col, 0.5f), mid.Size.X, HorizontalAlignment.Center, true);
            if (veto) DrawRect(a.Grow(-3), new Color(R, 0.35f * _alertT / 3.5f), false, 4);
        }
        else
        {
            var mid = new Rect2(band.Position.X + 10, band.Position.Y, band.Size.X - 20, 38);
            Chamfer(mid, Amber, new Color(0, 0, 0, 0.7f), 7);
            Txt(mid.Position + new Vector2(0, 1), CrewView.Task, 15, blink ? Amber : O, mid.Size.X, HorizontalAlignment.Center, true);
            Txt(mid.Position + new Vector2(0, 20), "EL VEHÍCULO ESPERA EL ABORDAJE", 11, W, mid.Size.X, HorizontalAlignment.Center);
        }
    }
}
