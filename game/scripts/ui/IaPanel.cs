using System;
using System.Collections.Generic;
using System.Linq;
using AresX.AI;
using AresX.Core;
using AresX.Sim;
using Godot;

namespace AresX.UI;

/// <summary>
/// Panel vertical "IA en vivo": las figuras principales de los tres laboratorios y del supervisor, apiladas en una
/// columna y escaladas a su ancho. Clic en una sección = abrir su laboratorio completo; el detalle va en la descripción
/// emergente.
///   RNA     · fila de entrada (rasgos con nombre) → normalización z → activaciones de la red → probabilidades → acción posterior.
///   DIFUSO  · curva agregada de velocidad y centroide, historia de las salidas, y las 16 reglas con su fuerza y su salida
///             crisp individual (velocidad y cautela) frente al centroide final.
///   AG      · fitness por generación, mapa de riesgo con las 5 mejores rutas de cada generación (se reproduce la evolución
///             de cada corrida nueva) y candidatas A/B/C.
///   SUPERV. · ícono de la acción, código y una luz por restricción (verde cumple, ámbar limita, rojo veta).
/// </summary>
public partial class IaPanel : Control
{
    public Mission? Mission;
    public Action<string>? OpenWindow;
    private const float Head = 18, RuleH = 11, SupH = 96;
    private static readonly (string Id, string Icon, string Title, string Window)[] Sections =
    {
        ("rna", "rna", "RNA", "lab_rna"), ("fuzzy", "fuzzy", "DIFUSO", "lab_fuzzy"), ("ga", "ga", "AG", "lab_ga"), ("sup", "shield", "SUPERVISOR", "dec"),
    };

    private static readonly Color Neg = new("e07050");
    private static readonly Color[] ClassCol = { new("5ad87a"), new("ff9a3a"), new("ff4a4a") };
    private static readonly string[] ClassShort = { "libre", "obstác.", "barranco" };

    // estado de animación
    private float[][]? _disp;                 // activaciones mostradas (suavizadas hacia las reales)
    private float _pulse;
    private FuzzyResult? _lastFz;
    private readonly List<(float V, float C)> _fzHist = new();
    private GaResult? _lastGa;
    private float _gaT;
    private ImageTexture? _riskTex;
    private int _riskVer = -1;
    private float _riskAge = 99;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(0, 900);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
    }

    // ------------------------------------------------------------------ disposición
    private float SectionH(string id, float w) => id switch
    {
        "rna" => Head + 12 + 152 + 42 + 18 + 4,
        "fuzzy" => Head + 46 + 26 + 13 + RuleH * (Mission?.Fuzzy.Rules.Count ?? 16) + 6,
        "ga" => Head + 40 + 6 + MathF.Max(80, w - 18) + 6 + 3 * 10 + 4,
        _ => SupH,
    };

    private float SectionTop(int k, float w)
    {
        float y = 0;
        for (int i = 0; i < k; i++) y += SectionH(Sections[i].Id, w);
        return y;
    }

    private float RescueH => Mission?.Rescue == true ? 26 + Mission.Victims.Count * 16 : 0;

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree()) return;
        float dt = (float)delta;
        float h = SectionTop(Sections.Length, Size.X) + RescueH;
        if (MathF.Abs(CustomMinimumSize.Y - h) > 0.5f) CustomMinimumSize = new Vector2(0, h);
        var m = Mission;
        if (m != null)
        {
            Animate(m, dt);
            _riskAge += dt;
        }
        QueueRedraw();
    }

    private void Animate(Mission m, float dt)
    {
        // RNA: las activaciones mostradas se acercan a las reales (las neuronas "se encienden" en cada paso)
        var a = m.FrontActivations;
        if (a != null)
        {
            if (_disp == null || _disp.Length != a.Length || _disp.Where((l, i) => l.Length != a[i].Length).Any())
                _disp = a.Select(l => new float[l.Length]).ToArray();
            float k = 1 - MathF.Exp(-dt * 7);
            bool changed = false;
            for (int l = 0; l < a.Length; l++)
                for (int i = 0; i < a[l].Length; i++)
                {
                    float d = a[l][i] - _disp[l][i];
                    if (MathF.Abs(d) > 0.05f) changed = true;
                    _disp[l][i] += d * k;
                }
            if (changed) _pulse = 1;
        }
        _pulse = MathF.Max(0, _pulse - dt * 1.6f);
        // difuso: historia de las salidas crisp, una muestra por inferencia
        if (m.LastFuzzy != null && m.LastFuzzy != _lastFz)
        {
            _lastFz = m.LastFuzzy;
            _fzHist.Add((_lastFz.Crisp["velocidad"], _lastFz.Crisp["cautela"]));
            if (_fzHist.Count > 80) _fzHist.RemoveAt(0);
        }
        // AG: reproducir la evolución de cada corrida nueva
        if (m.LastGa != _lastGa)
        {
            _lastGa = m.LastGa;
            _gaT = 0;
        }
        _gaT += dt;
    }

    private int SectionAt(Vector2 p)
    {
        float y = 0;
        for (int i = 0; i < Sections.Length; i++)
        {
            y += SectionH(Sections[i].Id, Size.X);
            if (p.Y < y) return i;
        }
        return Sections.Length - 1;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
        {
            OpenWindow?.Invoke(Sections[SectionAt(mb.Position)].Window);
            AcceptEvent();
        }
    }

    public override string _GetTooltip(Vector2 at)
    {
        var m = Mission;
        if (m == null) return "";
        return Sections[SectionAt(at)].Id switch
        {
            "rna" => "RNA (VER): la celda que el vehículo evalúa delante.\nFila de entrada (11 rasgos con su valor) → normalización z (barra: + turquesa, − naranjo) → activaciones de las capas ocultas (32 y 16 ReLU; brillo = activación, líneas = mayores contribuciones w·a) → probabilidades softmax → acción posterior del supervisor.\n\n" +
                     string.Join("\n", Enumerable.Range(0, FeatureModel.NF).Select(i => $"{FeatureModel.Short[i]} = {FeatureModel.Names[i]}")) + "\n\nClic: laboratorio RNA completo.",
            "fuzzy" => "Lógica difusa (AJUSTAR): curva agregada de velocidad y su centroide (línea blanca); medidores de velocidad y cautela; historia de las salidas.\nTabla: las 16 reglas. Barra = fuerza de disparo (AND = mín). Columnas vel/caut: salida crisp que daría la regla sola (centroide de su consecuente recortado); la línea blanca es el centroide final.\nClic: laboratorio difuso completo." +
                       (m.LastFuzzy != null ? "\n\n" + string.Join("\n", m.LastFuzzy.Fired.OrderByDescending(f => f.Strength).Take(5).Select(f => $"{f.Rule.Id} {f.Strength:0.00}: {f.Rule.Text}")) : ""),
            "ga" => "Algoritmo genético (ELEGIR): fitness por generación (verde mejor, violeta media).\nMapa: riesgo estimado (RNA + sensores) y las 5 mejores rutas de cada generación; al llegar una corrida nueva se reproduce su evolución. Blanco = vehículo, anillo = meta, ámbar = sitios.\nAbajo: candidatas A/B/C (la elegida en verde).\nClic: laboratorio genético completo." +
                    (m.LastGa != null ? $"\n\nÚltima corrida: {m.LastGa.Trigger} · {m.LastGa.Generations} gen · {m.LastGa.Ms:0} ms" : ""),
            _ => "Supervisor (DECIDIR): acción final y una luz por restricción de seguridad.\nClic: panel de decisión con el detalle." +
                 (m.LastDecision != null ? $"\n\n{m.LastDecision.Code} · {m.LastDecision.Reason}\n" + string.Join("\n", m.LastDecision.Checks.Where(c => !c.Passed || c.Applied).Select(c => $"{(c.Passed ? "!" : "✗")} {c.Code} {c.Name}: {c.Detail}")) : ""),
        };
    }

    public override void _Draw()
    {
        var m = Mission;
        if (m == null) return;
        float w = Size.X, y0 = 0;
        for (int i = 0; i < Sections.Length; i++)
        {
            var sec = Sections[i];
            float sh = SectionH(sec.Id, w);
            var col = sec.Id switch { "rna" => UiTheme.Rna, "fuzzy" => UiTheme.Fuzzy, "ga" => UiTheme.Ga, _ => UiTheme.Sup };
            DrawRect(new Rect2(2, y0 + 2, w - 4, sh - 4), new Color(col, 0.06f));
            DrawRect(new Rect2(2, y0 + 2, 3, sh - 4), col);
            Icons.Draw(this, sec.Icon, new Vector2(16, y0 + 12), 14, col);
            Gfx.Text(this, new Vector2(28, y0 + 4), sec.Title, 10, col);
            var r = new Rect2(10, y0 + Head + 2, w - 18, sh - Head - 6);
            switch (sec.Id)
            {
                case "rna": DrawRna(m, r, y0); break;
                case "fuzzy": DrawFuzzy(m, r, y0); break;
                case "ga": DrawGa(m, r, y0); break;
                default: DrawSup(m, r); break;
            }
            y0 += sh;
        }
        if (m.Rescue) DrawRescue(m, y0, w);
    }

    // ------------------------------------------------------------------ RNA
    private void DrawRna(Mission m, Rect2 r, float y0)
    {
        var p = m.FrontCell != null ? m.FrontProbs : new[] { 1f, 0f, 0f };
        int k = Array.IndexOf(p, p.Max());
        // cabecera: clase y confianza
        var hc = new Vector2(r.End.X - 8, y0 + 10);
        DrawArc(hc, 7, 0, MathF.Tau, 20, new Color(1, 1, 1, 0.12f), 2.5f, true);
        DrawArc(hc, 7, -MathF.PI / 2, -MathF.PI / 2 + MathF.Tau * p[k], 20, ClassCol[k], 2.5f, true);
        Gfx.Text(this, new Vector2(60, y0 + 4), m.FrontCell != null ? $"{NeuralPerception.ClassNames[k]} {p[k] * 100:0}%" : "sin celda", 10, m.FrontCell != null ? ClassCol[k] : UiTheme.Dim, r.End.X - 80, HorizontalAlignment.Right);

        var rna = m.Rna;
        float x0 = r.Position.X, ty = r.Position.Y;
        // columnas del flujo: entrada | z | oculta 1 | oculta 2 | salida
        float xName = x0, xZ = x0 + 60, zW = 26, xIn = x0 + 94, xOut = r.End.X - 30;
        float xH1 = xIn + (xOut - xIn) * 0.36f, xH2 = xIn + (xOut - xIn) * 0.70f;
        var dim = new Color(UiTheme.Dim, 0.9f);
        Gfx.Text(this, new Vector2(xName, ty), "entrada", 8, dim);
        Gfx.Text(this, new Vector2(xZ - 2, ty), "norm z", 8, dim, zW + 6, HorizontalAlignment.Center);
        Gfx.Text(this, new Vector2(xH1 - 30, ty), "activaciones", 8, dim, xH2 - xH1 + 60, HorizontalAlignment.Center);
        Gfx.Text(this, new Vector2(xOut - 12, ty), "p", 8, dim, 24, HorizontalAlignment.Center);
        var net = new Rect2(x0, ty + 12, r.Size.X, 152);

        if (rna == null || m.FrontActivations == null || _disp == null)
        {
            Icons.Draw(this, "veto", net.GetCenter(), 22, UiTheme.Dim);
            Gfx.Text(this, net.GetCenter() + new Vector2(-80, 16), rna == null ? "RNA desactivada (regla de respaldo)" : "esperando celda a evaluar", 9, UiTheme.Dim, 160, HorizontalAlignment.Center);
        }
        else
        {
            int[] sizes = rna.Net.Sizes;
            float[] xs = sizes.Length == 4 ? new[] { xIn, xH1, xH2, xOut } : Enumerable.Range(0, sizes.Length).Select(l => xIn + (xOut - xIn) * l / (sizes.Length - 1f)).ToArray();
            var pos = new Vector2[sizes.Length][];
            for (int l = 0; l < sizes.Length; l++)
            {
                pos[l] = new Vector2[sizes[l]];
                float span = l == sizes.Length - 1 ? net.Size.Y * 0.5f : net.Size.Y - 4;
                float top = net.Position.Y + (net.Size.Y - span) / 2;
                for (int i = 0; i < sizes[l]; i++) pos[l][i] = new Vector2(xs[l], top + span * (i + 0.5f) / sizes[l]);
            }
            // conexiones: las de mayor contribución |w·a| por capa
            for (int l = 0; l < sizes.Length - 1; l++)
            {
                var wl = rna.Net.W[l];
                int fin = sizes[l];
                var contrib = new List<(int O, int I, float C)>(sizes[l + 1] * fin);
                for (int o = 0; o < sizes[l + 1]; o++)
                    for (int i = 0; i < fin; i++)
                        contrib.Add((o, i, wl[o * fin + i] * _disp[l][i]));
                foreach (var (o, i, c) in contrib.OrderByDescending(q => MathF.Abs(q.C)).Take(l == sizes.Length - 2 ? 24 : 40))
                    DrawLine(pos[l][i], pos[l + 1][o], new Color(c > 0 ? UiTheme.Rna : Neg, Mathf.Clamp(MathF.Abs(c) / 2f, 0.06f, 0.75f)), 1, true);
            }
            // neuronas
            for (int l = 0; l < sizes.Length; l++)
            {
                float maxA = _disp[l].Select(MathF.Abs).DefaultIfEmpty(1).Max();
                bool last = l == sizes.Length - 1;
                float rad = l == 0 ? 4f : last ? 6f : sizes[l] > 20 ? 2.3f : 3.2f;
                for (int i = 0; i < sizes[l]; i++)
                {
                    float a = _disp[l][i];
                    float t = l == 0 ? Mathf.Clamp(MathF.Abs(a) / 3f, 0, 1) : last ? a : maxA > 0 ? a / maxA : 0;
                    var col = last ? ClassCol[Math.Min(i, 2)] : l == 0 && a < 0 ? Neg : UiTheme.Rna;
                    if (!last && l > 0 && t > 0.6f) DrawCircle(pos[l][i], rad + 2.5f * (0.4f + _pulse), new Color(col, 0.18f * t));
                    DrawCircle(pos[l][i], rad, new Color(0.08f, 0.1f, 0.14f));
                    DrawCircle(pos[l][i], rad, new Color(col, 0.12f + 0.88f * Mathf.Clamp(t, 0, 1)));
                    if (last)
                    {
                        if (i == k) DrawArc(pos[l][i], rad + 2.5f, 0, MathF.Tau, 16, ClassCol[i], 1.5f, true);
                        Gfx.Text(this, pos[l][i] + new Vector2(9, -6), $"{a * 100:0}", 9, ClassCol[Math.Min(i, 2)], 22);
                    }
                }
            }
            // fila de entrada (nombre + valor) y normalización z, alineadas con las neuronas de entrada
            for (int i = 0; i < FeatureModel.NF && i < sizes[0]; i++)
            {
                var pp = pos[0][i];
                float z = (m.FrontFeatures[i] - rna.Mean[i]) / rna.Std[i];
                Gfx.Text(this, new Vector2(xName, pp.Y - 6), FeatureModel.Short[i], 9, UiTheme.Text, 32);
                Gfx.Text(this, new Vector2(xName + 26, pp.Y - 6), Fmt(m.FrontFeatures[i]), 8, UiTheme.Dim, 32, HorizontalAlignment.Right);
                Gfx.SignedBar(this, new Rect2(xZ, pp.Y - 3, zW, 6), z, 3, new Color(UiTheme.Rna, 0.9f), Neg);
                DrawLine(new Vector2(xZ + zW + 1, pp.Y), new Vector2(pp.X - 4, pp.Y), new Color(1, 1, 1, 0.08f), 1);
            }
        }

        // probabilidades
        float by = net.End.Y + 4;
        for (int i = 0; i < 3; i++)
        {
            float y = by + i * 13;
            DrawCircle(new Vector2(x0 + 4, y + 5), 3.5f, ClassCol[i]);
            Gfx.Text(this, new Vector2(x0 + 11, y - 1), ClassShort[i], 9, i == k ? ClassCol[i] : UiTheme.Dim, 50);
            Gfx.Bar(this, new Rect2(x0 + 60, y + 2, r.Size.X - 60 - 30, 7), p[i], ClassCol[i]);
            Gfx.Text(this, new Vector2(x0, y - 1), $"{p[i] * 100:0}%", 9, Colors.White, r.Size.X, HorizontalAlignment.Right);
        }
        // acción posterior (misma lógica que el laboratorio y el supervisor)
        float ay = by + 40;
        string icon; string txt; Color ac;
        if (m.FrontCell == null) { icon = "wait"; txt = "sin evaluación en este paso"; ac = UiTheme.Dim; }
        else if (p[2] >= SafetySupervisor.CliffVetoP || p[1] >= SafetySupervisor.CliffVetoP) { icon = "veto"; txt = "VETO S-07 · replanificar"; ac = UiTheme.Crit; }
        else if (p[1] + p[2] >= SafetySupervisor.AlertP) { icon = "warn"; txt = "ALERTA · velocidad ≤ 0,30"; ac = UiTheme.Warn; }
        else if (p.Max() < SafetySupervisor.LowConfidence) { icon = "safe"; txt = "confianza baja · conservador"; ac = UiTheme.Warn; }
        else { icon = "move"; txt = "avance normal · riesgo bajo"; ac = UiTheme.Ok; }
        DrawRect(new Rect2(x0 - 2, ay - 1, r.Size.X + 4, 16), new Color(ac, 0.12f));
        Icons.Draw(this, icon, new Vector2(x0 + 7, ay + 7), 11, ac);
        Gfx.Text(this, new Vector2(x0 + 18, ay + 1), "" + txt, 9, ac, r.Size.X - 20);
    }

    private static string Fmt(float v) => MathF.Abs(v) >= 100 ? $"{v:0}" : MathF.Abs(v) >= 10 ? $"{v:0.#}" : $"{v:0.0#}";

    // ------------------------------------------------------------------ difuso
    private void DrawFuzzy(Mission m, Rect2 r, float y0)
    {
        var fz = m.LastFuzzy;
        if (fz == null)
        {
            Icons.Draw(this, "veto", r.Position + new Vector2(20, 24), 18, UiTheme.Dim);
            return;
        }
        float vC = fz.Crisp["velocidad"], cC = fz.Crisp["cautela"];
        Gfx.Text(this, new Vector2(60, y0 + 4), $"{fz.Fired.Count}/{m.Fuzzy.Rules.Count} reglas", 9, UiTheme.Dim, r.End.X - 60, HorizontalAlignment.Right);
        // curva agregada de velocidad + centroide
        var cr = new Rect2(r.Position, new Vector2(r.Size.X - 64, 42));
        Gfx.Frame(this, cr);
        var agg = fz.Aggregated["velocidad"];
        var pts = new List<Vector2> { new(cr.Position.X, cr.End.Y) };
        for (int i = 0; i < agg.Length; i++) pts.Add(new Vector2(cr.Position.X + cr.Size.X * i / (agg.Length - 1), cr.End.Y - cr.Size.Y * agg[i]));
        pts.Add(cr.End);
        if (agg.Any(a => a > 0)) DrawColoredPolygon(pts.ToArray(), new Color(UiTheme.Fuzzy, 0.4f));
        float vx = cr.Position.X + cr.Size.X * vC;
        DrawLine(new Vector2(vx, cr.Position.Y), new Vector2(vx, cr.End.Y), Colors.White, 2);
        Gfx.Text(this, cr.Position + new Vector2(3, 1), "velocidad", 8, UiTheme.Dim);
        Gauge(new Vector2(r.End.X - 46, r.Position.Y + 15), vC, UiTheme.Fuzzy, "move");
        Gauge(new Vector2(r.End.X - 14, r.Position.Y + 15), cC, UiTheme.Warn, "safe");
        // historia de las salidas crisp
        var hr = new Rect2(r.Position.X, r.Position.Y + 46, r.Size.X, 22);
        Gfx.Frame(this, hr);
        if (_fzHist.Count > 1)
        {
            Gfx.Series(this, hr, _fzHist.Select(h => h.V).ToList(), 0, 1, UiTheme.Fuzzy, 1.4f);
            Gfx.Series(this, hr, _fzHist.Select(h => h.C).ToList(), 0, 1, new Color(UiTheme.Warn, 0.7f), 1.1f);
        }
        // tabla de las 16 reglas
        float ty = r.Position.Y + 72;
        float xId = r.Position.X, xBar = xId + 24, barW = 34, xV = xBar + barW + 22, colW = (r.End.X - xV - 6) / 2, xC = xV + colW + 6;
        var dim = new Color(UiTheme.Dim, 0.9f);
        Gfx.Text(this, new Vector2(xId, ty - 1), "regla", 8, dim);
        Gfx.Text(this, new Vector2(xBar, ty - 1), "fuerza", 8, dim, barW + 20);
        Gfx.Text(this, new Vector2(xV, ty - 1), "vel crisp", 8, UiTheme.Fuzzy, colW, HorizontalAlignment.Center);
        Gfx.Text(this, new Vector2(xC, ty - 1), "caut crisp", 8, UiTheme.Warn, colW, HorizontalAlignment.Center);
        float ry = ty + 13;
        var outs = m.Fuzzy.Outputs;
        // centroide final en ambas columnas (línea blanca a lo largo de la tabla)
        float tableH = RuleH * m.Fuzzy.Rules.Count;
        foreach (var rule in m.Fuzzy.Rules)
        {
            float st = 0;
            foreach (var f in fz.Fired) if (f.Rule == rule) { st = f.Strength; break; }
            bool on = st > 0.005f;
            if (on) DrawRect(new Rect2(xId - 2, ry, r.Size.X + 4, RuleH - 1), new Color(UiTheme.Fuzzy, 0.05f + 0.18f * st));
            Gfx.Text(this, new Vector2(xId, ry - 1), rule.Id, 8, on ? UiTheme.Text : UiTheme.Dim);
            Gfx.Bar(this, new Rect2(xBar, ry + 2, barW, 6), st, UiTheme.Fuzzy);
            Gfx.Text(this, new Vector2(xBar + barW + 2, ry - 1), on ? $"{st:0.00}" : "", 8, Colors.White, 22);
            Track(new Rect2(xV, ry + 4, colW, 1), rule, outs["velocidad"], st, UiTheme.Fuzzy);
            Track(new Rect2(xC, ry + 4, colW, 1), rule, outs["cautela"], st, UiTheme.Warn);
            ry += RuleH;
        }
        float tTop = ty + 12, tBot = ty + 13 + tableH;
        DrawLine(new Vector2(xV + colW * vC, tTop), new Vector2(xV + colW * vC, tBot), new Color(1, 1, 1, 0.7f), 1);
        DrawLine(new Vector2(xC + colW * cC, tTop), new Vector2(xC + colW * cC, tBot), new Color(1, 1, 1, 0.7f), 1);
    }

    /// <summary>Pista 0..1 con la salida crisp que daría la regla sola (centroide de su consecuente recortado por la fuerza).</summary>
    private void Track(Rect2 tr, FuzzyRule rule, FuzzyVar ov, float st, Color col)
    {
        DrawLine(tr.Position, new Vector2(tr.End.X, tr.Position.Y), new Color(1, 1, 1, 0.1f), 1);
        string? set = null;
        foreach (var (v, s) in rule.Then) if (v == ov.Id) { set = s; break; }
        if (set == null) return;
        var fn = ov[set].Fn;
        float clip = st > 0.005f ? st : 1;   // sin disparo: posición de referencia del consecuente (gris)
        float num = 0, den = 0;
        for (int i = 0; i <= 40; i++)
        {
            float x = ov.Min + (ov.Max - ov.Min) * i / 40f;
            float mu = MathF.Min(clip, fn.Mu(x));
            num += x * mu;
            den += mu;
        }
        float c = den > 1e-6f ? (num / den - ov.Min) / (ov.Max - ov.Min) : 0;
        var p = new Vector2(tr.Position.X + tr.Size.X * c, tr.Position.Y);
        if (st > 0.005f)
        {
            DrawLine(new Vector2(tr.Position.X, tr.Position.Y), p, new Color(col, 0.25f + 0.4f * st), 3);
            DrawCircle(p, 2.2f + 1.8f * st, col);
        }
        else DrawCircle(p, 1.8f, new Color(UiTheme.Dim, 0.45f));
    }

    private void Gauge(Vector2 c, float v, Color col, string icon)
    {
        DrawArc(c, 12, MathF.PI * 0.75f, MathF.PI * 2.25f, 20, new Color(1, 1, 1, 0.12f), 3, true);
        DrawArc(c, 12, MathF.PI * 0.75f, MathF.PI * (0.75f + 1.5f * Mathf.Clamp(v, 0, 1)), 20, col, 3, true);
        Icons.Draw(this, icon, c, 10, col);
        Gfx.Text(this, c + new Vector2(-16, 13), $"{v:0.00}", 9, Colors.White, 32, HorizontalAlignment.Center);
    }

    // ------------------------------------------------------------------ genético
    private const float GenStepS = 0.12f;

    private void DrawGa(Mission m, Rect2 r, float y0)
    {
        var ga = m.LastGa;
        float side = MathF.Max(80, r.Size.X);
        var cr = new Rect2(r.Position, new Vector2(r.Size.X, 38));
        var mr = new Rect2(r.Position.X, cr.End.Y + 6, side, side);
        int gi = 0;
        if (ga != null && ga.History.Count > 0)
        {
            gi = Math.Min(ga.History.Count - 1, (int)(_gaT / GenStepS));
            Gfx.Text(this, new Vector2(60, y0 + 4), $"gen {gi}/{ga.History.Count - 1} · {ga.Trigger}", 9, UiTheme.Dim, r.End.X - 60, HorizontalAlignment.Right);
        }
        else Gfx.Text(this, new Vector2(60, y0 + 4), m.Config.UseGa ? "sin corridas" : "AG desactivado", 9, UiTheme.Dim, r.End.X - 60, HorizontalAlignment.Right);

        // fitness por generación
        Gfx.Frame(this, cr);
        if (ga != null && ga.History.Count >= 2)
        {
            float lo = ga.History.Min(h => h.Mean), hi = ga.History.Max(h => h.Best);
            if (hi - lo < 1e-4f) hi = lo + 1e-4f;
            Vector2 P(int i, float v) => new(cr.Position.X + cr.Size.X * i / (ga.History.Count - 1), cr.End.Y - cr.Size.Y * Mathf.Clamp((v - lo) / (hi - lo), 0, 1));
            DrawPolyline(ga.History.Select((h, i) => P(i, h.Mean)).ToArray(), new Color(UiTheme.Ga, 0.7f), 1.2f, true);
            DrawPolyline(ga.History.Select((h, i) => P(i, h.Best)).ToArray(), UiTheme.Ok, 1.8f, true);
            float gx = cr.Position.X + cr.Size.X * gi / (ga.History.Count - 1);
            DrawLine(new Vector2(gx, cr.Position.Y), new Vector2(gx, cr.End.Y), new Color(1, 1, 1, 0.5f), 1);
            Gfx.Text(this, cr.Position + new Vector2(3, 1), "fitness", 8, UiTheme.Dim);
        }

        // mapa de rutas evaluadas
        DrawRouteMap(m, ga, gi, mr);

        // candidatas
        float cy = mr.End.Y + 6;
        if (ga == null || ga.Candidates.Count == 0) return;
        float fmin = ga.Candidates.Min(c => c.Fitness), fmax = ga.Candidates.Max(c => c.Fitness);
        for (int i = 0; i < ga.Candidates.Count; i++)
        {
            var c = ga.Candidates[i];
            bool chosen = ga.Best != null && c.Label == ga.Best.Label;
            float y = cy + i * 10;
            var cc = View.OverlayView.CandidateColors[i % 3];
            Gfx.Text(this, new Vector2(r.Position.X, y - 3), c.Label, 9, chosen ? UiTheme.Ok : cc);
            float t = fmax - fmin < 1e-5f ? 1 : 0.25f + 0.75f * (c.Fitness - fmin) / (fmax - fmin);
            DrawRect(new Rect2(r.Position.X + 12, y, (r.Size.X - 48) * t, 6), chosen ? UiTheme.Ok : new Color(cc, 0.6f));
            Gfx.Text(this, new Vector2(r.Position.X, y - 3), $"{c.Fitness:0.00}", 8, chosen ? UiTheme.Ok : UiTheme.Dim, r.Size.X, HorizontalAlignment.Right);
        }
    }

    private void DrawRouteMap(Mission m, GaResult? ga, int gi, Rect2 mr)
    {
        int n = m.Map.N;
        EnsureRiskTexture(m);
        // recorte: caja de las rutas + vehículo + meta, cuadrada, con margen (los escenarios grandes se ven de cerca)
        int x0 = m.Rover.Pos.X, x1 = x0, yA = m.Rover.Pos.Y, yB = yA;
        void Inc(Cell c) { x0 = Math.Min(x0, c.X); x1 = Math.Max(x1, c.X); yA = Math.Min(yA, c.Y); yB = Math.Max(yB, c.Y); }
        GenerationSnapshot? snap = ga != null && ga.History.Count > 0 ? ga.History[gi] : null;
        if (ga != null) { Inc(ga.Start); Inc(ga.Goal); }
        if (snap != null) foreach (var c in snap.Top) foreach (var q in c.Path) Inc(q);
        int span = Math.Max(Math.Max(x1 - x0, yB - yA) + 8, Math.Min(n, 28));
        span = Math.Min(span, n);
        float cxm = (x0 + x1 + 1) / 2f, cym = (yA + yB + 1) / 2f;
        float ox = Mathf.Clamp(cxm - span / 2f, 0, n - span), oy = Mathf.Clamp(cym - span / 2f, 0, n - span);
        float cp = mr.Size.X / span;
        Vector2 Px(Cell c) => mr.Position + new Vector2((c.X + 0.5f - ox) * cp, (c.Y + 0.5f - oy) * cp);

        if (_riskTex != null) DrawTextureRectRegion(_riskTex, mr, new Rect2(ox, oy, span, span));
        else DrawRect(mr, new Color(0.12f, 0.14f, 0.18f));
        DrawRect(mr, new Color(1, 1, 1, 0.15f), false, 1);
        // escala
        float km = span * m.Map.CellM / 1000f;
        Gfx.Text(this, mr.Position + new Vector2(3, mr.Size.Y - 13), km >= 1 ? $"{km:0.#} km" : $"{span * m.Map.CellM:0} m", 8, new Color(1, 1, 1, 0.75f));
        // sitios
        foreach (var s in m.Map.Sites)
        {
            if (s.Cell.X < ox || s.Cell.X >= ox + span || s.Cell.Y < oy || s.Cell.Y >= oy + span) continue;
            DrawCircle(Px(s.Cell), MathF.Max(2, cp * 0.45f), s.Visited ? new Color(UiTheme.Dim, 0.7f) : new Color("ffb347"));
        }
        // rutas: top-5 de la generación mostrada (la mejor encima)
        if (snap != null)
            for (int i = snap.Top.Count - 1; i >= 0; i--)
            {
                var path = snap.Top[i].Path;
                if (path.Count < 2) continue;
                bool best = i == 0;
                var col = new Color(View.OverlayView.CandidateColors[i % 3], best ? 1f : 0.55f);
                if (best && gi == ga!.History.Count - 1) col = UiTheme.Ok;
                DrawPolyline(path.Select(Px).ToArray(), col, best ? 2.4f : 1.2f, true);
            }
        if (ga != null)
        {
            var g = Px(ga.Goal);
            DrawArc(g, MathF.Max(4, cp * 0.8f), 0, MathF.Tau, 16, Colors.White, 1.5f, true);
            DrawCircle(g, 1.8f, Colors.White);
        }
        var rp = Px(m.Rover.Pos);
        DrawCircle(rp, 4.2f, new Color(0, 0, 0, 0.6f));
        DrawCircle(rp, 3f, Colors.White);
        if (ga == null) Gfx.Text(this, mr.Position + new Vector2(0, mr.Size.Y / 2 - 6), m.Config.UseGa ? "esperando corrida del AG" : "ablación: ruta de menor distancia", 9, UiTheme.Dim, mr.Size.X, HorizontalAlignment.Center);
    }

    /// <summary>Textura del mapa de riesgo estimado (una celda = un píxel), regenerada cuando cambia la creencia.</summary>
    private void EnsureRiskTexture(Mission m)
    {
        var b = m.Belief;
        if (_riskTex != null && (b.Version == _riskVer || _riskAge < 0.5f)) return;
        int n = b.N;
        var data = new byte[n * n * 4];
        var inside = m.Map.Inside;
        for (int i = 0; i < n * n; i++)
        {
            float rr, gg, bb;
            if (inside != null && !inside[i]) { rr = 0.05f; gg = 0.06f; bb = 0.08f; }
            else if (b.ConfirmedCliff[i]) { rr = 0.75f; gg = 0.09f; bb = 0.09f; }
            else if (b.ConfirmedObstacle[i] || b.Vetoed[i]) { rr = 0.85f; gg = 0.32f; bb = 0.12f; }
            else
            {
                float k = Math.Clamp(b.P[i][1] + b.P[i][2], 0, 1);
                float o = b.Observed[i] ? 1f : 0.72f;
                rr = (0.18f + (1.0f - 0.18f) * k) * o;
                gg = (0.22f + (0.42f - 0.22f) * k) * o;
                bb = (0.27f + (0.16f - 0.27f) * k) * o;
            }
            data[i * 4] = (byte)(rr * 255);
            data[i * 4 + 1] = (byte)(gg * 255);
            data[i * 4 + 2] = (byte)(bb * 255);
            data[i * 4 + 3] = 255;
        }
        var img = Image.CreateFromData(n, n, false, Image.Format.Rgba8, data);
        if (_riskTex == null || _riskTex.GetWidth() != n) _riskTex = ImageTexture.CreateFromImage(img);
        else _riskTex.Update(img);
        _riskVer = b.Version;
        _riskAge = 0;
    }

    // ------------------------------------------------------------------ supervisor
    private void DrawSup(Mission m, Rect2 r)
    {
        var d = m.LastDecision;
        if (d == null) return;
        var col = d.Veto ? UiTheme.Crit : d.Action == RoverAction.Avanzar ? (d.Alert ? UiTheme.Warn : UiTheme.Ok) : UiTheme.Warn;
        var c = r.Position + new Vector2(20, 18);
        DrawCircle(c, 18, new Color(col, 0.18f));
        Icons.Draw(this, Icons.Action(d.Action), c, 22, col);
        Gfx.Text(this, r.Position + new Vector2(46, 2), Icons.Verb(d.Action) + (d.Action == RoverAction.Avanzar ? $" {d.Speed:0.00}" : ""), 12, col, r.Size.X - 46);
        Gfx.Text(this, r.Position + new Vector2(46, 18), d.Code + (d.Veto ? " · VETO" : ""), 10, UiTheme.Dim, r.Size.X - 46);
        int n = d.Checks.Count, perRow = Math.Max(1, (int)((r.Size.X - 4) / 14));
        for (int i = 0; i < n; i++)
        {
            var ch = d.Checks[i];
            var lc = !ch.Passed ? UiTheme.Crit : ch.Applied ? UiTheme.Warn : UiTheme.Ok;
            DrawCircle(r.Position + new Vector2(6 + (i % perRow) * 14, 48 + (i / perRow) * 14), 4.5f, lc);
        }
    }

    // ------------------------------------------------------------------ rescate
    /// <summary>Rescate: una fila por persona (estado, supervivencia restante y urgencia difusa).</summary>
    private void DrawRescue(Mission m, float y0, float w)
    {
        var col = new Color("ff6a60");
        DrawRect(new Rect2(2, y0 + 2, w - 4, 22 + m.Victims.Count * 16), new Color(col, 0.06f));
        DrawRect(new Rect2(2, y0 + 2, 3, 22 + m.Victims.Count * 16), col);
        Icons.Draw(this, "person", new Vector2(16, y0 + 12), 14, col);
        Gfx.Text(this, new Vector2(28, y0 + 4), $"RESCATE  {m.Saved}/{m.Victims.Count} · a bordo {m.OnBoard}/{m.Capacity}", 10, col, w - 32);
        float y = y0 + 24;
        foreach (var v in m.Victims)
        {
            string icon = v.Delivered ? "cross" : v.OnBoard ? "person" : v.Lost ? "veto" : v.Unreachable ? "warn" : "target";
            var ic = v.Delivered ? UiTheme.Ok : v.OnBoard ? UiTheme.Info : v.Lost || v.Unreachable ? UiTheme.Dim : col;
            Icons.Draw(this, icon, new Vector2(16, y + 6), 11, ic);
            Gfx.Text(this, new Vector2(26, y - 1), v.Name, 10, ic);
            float pct = v.SurvivalMaxS > 0 ? v.SurvivalS / v.SurvivalMaxS : 0;
            var bc = pct < 0.3f ? UiTheme.Crit : pct < 0.6f ? UiTheme.Warn : UiTheme.Ok;
            if (!v.Delivered && !v.Lost) Gfx.Bar(this, new Rect2(52, y + 3, w - 52 - 56, 7), pct, bc);
            Gfx.Text(this, new Vector2(0, y - 1), v.Delivered ? "salvada" : v.Lost ? "—" : $"{v.SurvivalS / 60:0}′", 9, Colors.White, w - 30, HorizontalAlignment.Right);
            if (v.Active) DrawCircle(new Vector2(w - 18, y + 6), 4.5f, new Color(1f, 0.35f, 0.25f, 0.25f + 0.75f * v.Urgency));
            y += 16;
        }
    }
}
