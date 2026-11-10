using System;
using System.Collections.Generic;
using System.Linq;
using AresX.Core;

namespace AresX.AI;

/// <summary>Función de membresía.</summary>
public abstract class MembershipFn
{
    public abstract float Mu(float x);
    public abstract string Describe();
}

public sealed class Tri : MembershipFn
{
    public readonly float A, B, C;
    public Tri(float a, float b, float c) { A = a; B = b; C = c; }
    public override float Mu(float x) =>
        x <= A || x >= C ? (x == B ? 1 : 0) : x < B ? (x - A) / MathF.Max(1e-6f, B - A) : (C - x) / MathF.Max(1e-6f, C - B);
    public override string Describe() => $"triangular({A:0.##}, {B:0.##}, {C:0.##})";
}

public sealed class Trap : MembershipFn
{
    public readonly float A, B, C, D;
    public Trap(float a, float b, float c, float d) { A = a; B = b; C = c; D = d; }
    public override float Mu(float x)
    {
        if (x >= B && x <= C) return 1;
        if (x <= A || x >= D) return 0;
        return x < B ? (x - A) / MathF.Max(1e-6f, B - A) : (D - x) / MathF.Max(1e-6f, D - C);
    }
    public override string Describe() => $"trapezoidal({A:0.##}, {B:0.##}, {C:0.##}, {D:0.##})";
}

public sealed class Gauss : MembershipFn
{
    public readonly float M, S;
    public Gauss(float m, float s) { M = m; S = s; }
    public override float Mu(float x) => MathF.Exp(-(x - M) * (x - M) / (2 * S * S));
    public override string Describe() => $"gaussiana(μ={M:0.##}, σ={S:0.##})";
}

public sealed class FuzzySet
{
    public string Label = "";
    public MembershipFn Fn = null!;
}

public sealed class FuzzyVar
{
    public string Id = "", Name = "", Unit = "";
    public float Min, Max;
    public readonly List<FuzzySet> Sets = new();

    public FuzzyVar Set(string label, MembershipFn fn)
    {
        Sets.Add(new FuzzySet { Label = label, Fn = fn });
        return this;
    }

    public FuzzySet this[string label] => Sets.First(s => s.Label == label);
}

public sealed class FuzzyRule
{
    public string Id = "", Justification = "";
    public (string Var, string Set)[] If = Array.Empty<(string, string)>();
    public (string Var, string Set)[] Then = Array.Empty<(string, string)>();

    public string Text =>
        "SI " + string.Join(" Y ", If.Select(a => $"{a.Var} es {a.Set}")) + " ENTONCES " + string.Join(", ", Then.Select(c => $"{c.Var} {c.Set}"));
}

public sealed class FuzzyResult
{
    public readonly Dictionary<string, float> Inputs = new();
    public readonly Dictionary<string, Dictionary<string, float>> Degrees = new();   // var → set → μ
    public readonly List<(FuzzyRule Rule, float Strength)> Fired = new();
    public readonly Dictionary<string, float[]> Aggregated = new();                    // salida → curva muestreada
    public readonly Dictionary<string, float> Crisp = new();
    public bool Fallback;
    public string FallbackNote = "";
    public const int Samples = 101;
}

/// <summary>
/// Sistema difuso Mamdani: fusificación, AND = mínimo, implicación = mínimo (recorte), agregación = máximo y
/// desfusificación por centroide sobre 101 muestras del universo. Si ninguna regla se activa para una salida se usa
/// una salida de respaldo documentada (velocidad 0 = parar; cautela 1 = máxima).
/// </summary>
public sealed class FuzzySystem
{
    public readonly Dictionary<string, FuzzyVar> Inputs = new(), Outputs = new();
    public readonly List<FuzzyRule> Rules = new();
    public readonly Dictionary<string, float> FallbackValue = new();

    public FuzzyResult Infer(IReadOnlyDictionary<string, float> x)
    {
        var res = new FuzzyResult();
        foreach (var (id, v) in Inputs)
        {
            float val = x.TryGetValue(id, out var xv) && MathX.Finite(xv) ? MathX.Clamp(xv, v.Min, v.Max) : v.Min;
            res.Inputs[id] = val;
            res.Degrees[id] = v.Sets.ToDictionary(s => s.Label, s => s.Fn.Mu(val));
        }
        foreach (var r in Rules)
        {
            float w = r.If.Min(a => res.Degrees[a.Var][a.Set]);
            if (w > 0) res.Fired.Add((r, w));
        }
        res.Fired.Sort((a, b) => b.Strength.CompareTo(a.Strength));
        foreach (var (oid, ov) in Outputs)
        {
            var agg = new float[FuzzyResult.Samples];
            foreach (var (rule, w) in res.Fired)
                foreach (var (cv, cs) in rule.Then)
                {
                    if (cv != oid) continue;
                    var fn = ov[cs].Fn;
                    for (int i = 0; i < agg.Length; i++)
                    {
                        float xv = ov.Min + (ov.Max - ov.Min) * i / (agg.Length - 1);
                        agg[i] = MathF.Max(agg[i], MathF.Min(w, fn.Mu(xv)));
                    }
                }
            res.Aggregated[oid] = agg;
            float num = 0, den = 0;
            for (int i = 0; i < agg.Length; i++)
            {
                float xv = ov.Min + (ov.Max - ov.Min) * i / (agg.Length - 1);
                num += xv * agg[i];
                den += agg[i];
            }
            if (den < 1e-6f)
            {
                res.Fallback = true;
                res.Crisp[oid] = FallbackValue.GetValueOrDefault(oid, ov.Min);
                res.FallbackNote = $"Ninguna regla activa para {ov.Name}: salida de respaldo = {res.Crisp[oid]:0.00}";
            }
            else res.Crisp[oid] = num / den;
        }
        return res;
    }
}

/// <summary>Controlador difuso del rover: velocidad (0–1) y nivel de cautela (0–1).</summary>
public static class RoverFuzzy
{
    public static FuzzySystem Build()
    {
        var fs = new FuzzySystem();
        void In(FuzzyVar v) => fs.Inputs[v.Id] = v;
        In(new FuzzyVar { Id = "distancia", Name = "Distancia a riesgo", Unit = "m", Min = 0, Max = 20 }
            .Set("cerca", new Trap(0, 0, 2, 6)).Set("media", new Tri(3, 8, 13)).Set("lejos", new Trap(10, 15, 20, 20)));
        In(new FuzzyVar { Id = "bateria", Name = "Batería", Unit = "%", Min = 0, Max = 100 }
            .Set("critica", new Trap(0, 0, 8, 18)).Set("baja", new Tri(10, 25, 45)).Set("alta", new Trap(35, 60, 100, 100)));
        In(new FuzzyVar { Id = "pendiente", Name = "Pendiente", Unit = "°", Min = 0, Max = 45 }
            .Set("plana", new Trap(0, 0, 5, 12)).Set("media", new Tri(8, 17, 26)).Set("fuerte", new Trap(20, 30, 45, 45)));
        In(new FuzzyVar { Id = "temperatura", Name = "Temp. interior", Unit = "°C", Min = -60, Max = 100 }
            .Set("fria", new Trap(-60, -60, -25, -5)).Set("nominal", new Trap(-15, 0, 40, 55)).Set("caliente", new Trap(45, 65, 100, 100)));
        In(new FuzzyVar { Id = "viento", Name = "Viento / límite", Unit = "%", Min = 0, Max = 120 }
            .Set("calma", new Trap(0, 0, 20, 40)).Set("moderado", new Tri(25, 50, 75)).Set("fuerte", new Trap(60, 85, 120, 120)));
        In(new FuzzyVar { Id = "riesgo", Name = "Riesgo RNA", Unit = "", Min = 0, Max = 1 }
            .Set("bajo", new Trap(0, 0, 0.2f, 0.4f)).Set("medio", new Tri(0.25f, 0.5f, 0.75f)).Set("alto", new Trap(0.6f, 0.8f, 1, 1)));
        fs.Outputs["velocidad"] = new FuzzyVar { Id = "velocidad", Name = "Velocidad", Unit = "", Min = 0, Max = 1 }
            .Set("parar", new Trap(0, 0, 0.05f, 0.15f)).Set("lenta", new Tri(0.1f, 0.3f, 0.5f))
            .Set("moderada", new Tri(0.4f, 0.6f, 0.8f)).Set("rapida", new Trap(0.7f, 0.9f, 1, 1));
        fs.Outputs["cautela"] = new FuzzyVar { Id = "cautela", Name = "Cautela", Unit = "", Min = 0, Max = 1 }
            .Set("baja", new Trap(0, 0, 0.2f, 0.4f)).Set("media", new Tri(0.3f, 0.5f, 0.7f)).Set("alta", new Trap(0.6f, 0.8f, 1, 1));
        fs.FallbackValue["velocidad"] = 0f;
        fs.FallbackValue["cautela"] = 1f;

        void R(string id, string why, (string, string)[] cond, params (string, string)[] then) =>
            fs.Rules.Add(new FuzzyRule { Id = id, If = cond, Then = then, Justification = why });
        R("R01", "Un peligro probable a pocos metros exige detenerse antes de entrar.",
            new[] { ("distancia", "cerca"), ("riesgo", "alto") }, ("velocidad", "parar"), ("cautela", "alta"));
        R("R02", "Con batería crítica se conserva energía y se evita cualquier maniobra arriesgada.",
            new[] { ("bateria", "critica") }, ("velocidad", "lenta"), ("cautela", "alta"));
        R("R03", "Pendiente fuerte con riesgo a media distancia: avanzar despacio para poder frenar.",
            new[] { ("pendiente", "fuerte"), ("distancia", "media") }, ("velocidad", "lenta"), ("cautela", "alta"));
        R("R04", "Terreno ideal (batería alta, plano, sin peligros cerca): aprovechar para avanzar rápido.",
            new[] { ("bateria", "alta"), ("pendiente", "plana"), ("distancia", "lejos") }, ("velocidad", "rapida"), ("cautela", "baja"));
        R("R05", "Si la RNA ve un peligro probable, aunque esté lejos, se reduce la velocidad.",
            new[] { ("riesgo", "alto") }, ("velocidad", "lenta"), ("cautela", "alta"));
        R("R06", "Riesgo incierto: velocidad intermedia y atención media.",
            new[] { ("riesgo", "medio") }, ("velocidad", "moderada"), ("cautela", "media"));
        R("R07", "El viento fuerte desestabiliza y empuja: marcha lenta.",
            new[] { ("viento", "fuerte") }, ("velocidad", "lenta"), ("cautela", "alta"));
        R("R08", "Electrónica caliente: menos potencia en motores = menos calor disipado.",
            new[] { ("temperatura", "caliente") }, ("velocidad", "lenta"), ("cautela", "alta"));
        R("R09", "Frío con batería baja: la calefacción consume; ahorrar energía en tracción.",
            new[] { ("temperatura", "fria"), ("bateria", "baja") }, ("velocidad", "lenta"), ("cautela", "media"));
        R("R10", "Pendiente media sin riesgo detectado: marcha de crucero.",
            new[] { ("pendiente", "media"), ("riesgo", "bajo") }, ("velocidad", "moderada"), ("cautela", "media"));
        R("R11", "Peligro de probabilidad media muy cerca: lento y atento.",
            new[] { ("distancia", "cerca"), ("riesgo", "medio") }, ("velocidad", "lenta"), ("cautela", "alta"));
        R("R12", "Batería baja sin peligros: crucero eficiente (ni muy lento por el consumo base ni muy rápido).",
            new[] { ("bateria", "baja"), ("distancia", "lejos"), ("riesgo", "bajo") }, ("velocidad", "moderada"), ("cautela", "media"));
        R("R13", "Viento moderado en pendiente media aumenta el riesgo de vuelco.",
            new[] { ("viento", "moderado"), ("pendiente", "media") }, ("velocidad", "lenta"), ("cautela", "media"));
        R("R14", "Condiciones nominales y despejadas: velocidad alta.",
            new[] { ("temperatura", "nominal"), ("riesgo", "bajo"), ("distancia", "lejos") }, ("velocidad", "rapida"), ("cautela", "baja"));
        R("R15", "Pendiente fuerte siempre implica marcha lenta (riesgo de vuelco y patinaje).",
            new[] { ("pendiente", "fuerte") }, ("velocidad", "lenta"), ("cautela", "alta"));
        R("R16", "Peligro a media distancia en terreno plano: moderar y vigilar.",
            new[] { ("distancia", "media"), ("riesgo", "bajo"), ("pendiente", "plana") }, ("velocidad", "moderada"), ("cautela", "media"));
        return fs;
    }
}
