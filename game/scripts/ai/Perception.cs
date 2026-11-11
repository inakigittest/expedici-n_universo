using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AresX.Core;
using AresX.Sim;

namespace AresX.AI;

/// <summary>Condiciones de observación (contexto de los sensores) para generar rasgos.</summary>
public struct Conditions
{
    public float VisibilityM;
    public bool Night;
    public float Vibration;     // m/s²
    public float RadiationMsvH;
    public float TempExtC;
}

/// <summary>
/// Modelo de observación: transforma la verdad de una celda en lecturas ruidosas (LIDAR, cámara, eco, térmica…).
/// La etiqueta de entrenamiento es la clase de verdad (libre/obstáculo/barranco); ningún rasgo es la etiqueta: todos
/// pasan por ruido dependiente de la distancia, la visibilidad y la noche (punto 10.1 del caso).
/// </summary>
public static class FeatureModel
{
    public const int NF = 11;

    public static readonly string[] Names =
    {
        "distancia (m)", "caída LIDAR (m)", "pendiente (°)", "eco/altura (m)", "textura", "albedo cámara",
        "contraste térmico (K)", "vibración (m/s²)", "visibilidad (0-1)", "radiación (log)", "temp. ext (norm)",
    };

    public static readonly string[] Short = { "dist", "caída", "pend", "eco", "text", "alb", "térm", "vib", "vis", "rad", "temp" };

    public static float[] Extract(WorldMap map, Cell rover, Cell target, Conditions cond, Rng rng)
    {
        var f = new float[NF];
        float d = rover.Dist(target) * WorldMap.RefCellM;   // distancia en celdas de referencia (escala del entrenamiento)
        float visF = MathX.Clamp01(cond.VisibilityM / 3000f);
        int ti = map.Idx(map.Clamp(target));
        var hz = map.Hazards[ti];
        // celda anterior en la línea de visión (desde donde se mide el borde)
        var dir = target - rover;
        var step = new Cell(Math.Sign(dir.X), Math.Sign(dir.Y));
        var prev = map.Clamp(target - step);
        float edge = (map.Height(prev) - map.Height(target)) * map.DropScale;   // desnivel equivalente sobre 5 m
        float pit = map.MaxDrop(prev) * map.DropScale * 0.25f;
        float lidarSigma = 0.25f + 1.4f * (1 - visF) + 0.04f * d;
        f[0] = d + rng.Gauss(0, 0.2f);
        f[1] = MathF.Max(edge, pit * (WorldMap.ClassOf(hz) == TerrainClass.Barranco ? 1.6f : 0.4f)) + rng.Gauss(0, lidarSigma);
        f[2] = map.Slope[ti] + rng.Gauss(0, 1.8f + 0.08f * d);
        float echo = map.ObstacleH[ti];
        if (hz == Hazard.Water) echo = 0.05f;
        f[3] = MathF.Max(0, echo + rng.Gauss(0, 0.25f + 0.6f * (1 - visF)));
        f[4] = MathX.Clamp01(map.Rough[ti] + (hz == Hazard.Rock || hz == Hazard.Debris ? 0.25f : 0) + rng.Gauss(0, 0.07f + 0.1f * (1 - visF)));
        float alb = map.Albedo[ti] * (WorldMap.ClassOf(hz) == TerrainClass.Barranco ? 0.45f : 1f);   // sombra en el fondo del barranco
        float camSigma = 0.03f + (cond.Night ? 0.12f : 0) + 0.12f * (1 - visF);
        f[5] = MathX.Clamp01(alb * (cond.Night ? 0.35f : 1f) + rng.Gauss(0, camSigma));
        float thermal = hz switch
        {
            Hazard.Rock or Hazard.Debris => 3f, Hazard.Building => 4f, Hazard.Cliff or Hazard.Crater => -3f, Hazard.Water => -2.5f,
            Hazard.Tree => -1.2f, Hazard.Storm => -4f, Hazard.Downdraft => 2.5f, _ => 0f,
        };
        if (cond.Night) thermal *= 1.4f;
        f[6] = thermal + rng.Gauss(0, 1.1f);
        f[7] = cond.Vibration + rng.Gauss(0, 0.05f);
        f[8] = visF;
        f[9] = MathF.Log10(1e-4f + MathF.Max(0, cond.RadiationMsvH));
        f[10] = MathX.Clamp(cond.TempExtC / 200f, -2f, 3f);
        return f;
    }

    /// <summary>Línea base sin RNA (ablación): umbrales fijos sobre los rasgos crudos.</summary>
    public static float[] RulePredict(float[] f)
    {
        float pb = f[1] > 3.5f ? 0.85f : f[1] > 2f ? 0.4f : 0.05f;
        float po = f[3] > 0.7f ? 0.85f : f[3] > 0.4f ? 0.4f : 0.05f;
        float pl = MathF.Max(0.05f, 1 - pb - po);
        float s = pb + po + pl;
        return new[] { pl / s, po / s, pb / s };
    }
}

public sealed class TrainingReport
{
    public readonly List<float> TrainLoss = new(), ValLoss = new(), TrainAcc = new(), ValAcc = new();
    public ClassMetrics? Test, Val;
    public int TrainN, ValN, TestN, BestEpoch, Epochs;
    public int[] TrainMaps = Array.Empty<int>(), ValMaps = Array.Empty<int>(), TestMaps = Array.Empty<int>();
    public float Seconds;
    public float[] Mean = Array.Empty<float>(), Std = Array.Empty<float>();
    public int[] ClassCountsTrain = new int[3];
}

/// <summary>
/// Percepción neuronal: genera el dataset sintético (particionado por MAPAS: entrenamiento, validación y prueba sin
/// fuga), normaliza con estadísticas del entrenamiento, entrena el MLP con detención temprana y evalúa.
/// Se entrena una sola vez por ejecución (semilla fija → mismo modelo).
/// </summary>
public sealed class NeuralPerception
{
    public static NeuralPerception? Shared { get; private set; }
    public static readonly string[] ClassNames = { "libre", "obstáculo", "barranco" };
    public static readonly string[] ProbeClassNames = { "estable", "tormenta", "descendente" };

    public Mlp Net = null!;
    public TrainingReport Report = new();
    public float[] Mean = new float[FeatureModel.NF], Std = new float[FeatureModel.NF];
    public List<float[]> TestX = new();
    public List<int> TestY = new();

    public static readonly string[] PlanetMix = { "marte", "tierra", "mercurio", "venus", "jupiter", "marte", "neptuno", "tierra" };

    public static NeuralPerception TrainShared(Action<string>? progress = null)
    {
        Shared ??= Train(2026, progress);
        return Shared;
    }

    public static void SetShared(NeuralPerception np) => Shared = np;

    // ------------------------------------------------------------------ modelo pre-entrenado (Android: arranque rápido)
    private sealed class ModelDto
    {
        public int Seed { get; set; } = 2026;
        public int[] Sizes { get; set; } = Array.Empty<int>();
        public float[][] W { get; set; } = Array.Empty<float[]>();
        public float[][] B { get; set; } = Array.Empty<float[]>();
        public float[] Mean { get; set; } = Array.Empty<float>();
        public float[] Std { get; set; } = Array.Empty<float>();
        public List<float> TrainLoss { get; set; } = new();
        public List<float> ValLoss { get; set; } = new();
        public List<float> TrainAcc { get; set; } = new();
        public List<float> ValAcc { get; set; } = new();
        public int TrainN { get; set; }
        public int ValN { get; set; }
        public int TestN { get; set; }
        public int BestEpoch { get; set; }
        public int Epochs { get; set; }
        public float Seconds { get; set; }
        public int[] TrainMaps { get; set; } = Array.Empty<int>();
        public int[] ValMaps { get; set; } = Array.Empty<int>();
        public int[] TestMaps { get; set; } = Array.Empty<int>();
        public int[] ClassCountsTrain { get; set; } = Array.Empty<int>();
        public float[][] TestX { get; set; } = Array.Empty<float[]>();
        public int[] TestY { get; set; } = Array.Empty<int>();
    }

    /// <summary>Serializa pesos, normalización, curvas y conjunto de prueba (los float hacen ida y vuelta exactos).</summary>
    public string ToJson()
    {
        var r = Report;
        var dto = new ModelDto
        {
            Sizes = Net.Sizes, W = Net.W, B = Net.B, Mean = Mean, Std = Std, TrainLoss = r.TrainLoss, ValLoss = r.ValLoss,
            TrainAcc = r.TrainAcc, ValAcc = r.ValAcc, TrainN = r.TrainN, ValN = r.ValN, TestN = r.TestN, BestEpoch = r.BestEpoch,
            Epochs = r.Epochs, Seconds = r.Seconds, TrainMaps = r.TrainMaps, ValMaps = r.ValMaps, TestMaps = r.TestMaps,
            ClassCountsTrain = r.ClassCountsTrain, TestX = TestX.ToArray(), TestY = TestY.ToArray(),
        };
        return System.Text.Json.JsonSerializer.Serialize(dto);
    }

    public static NeuralPerception FromJson(string json)
    {
        var dto = System.Text.Json.JsonSerializer.Deserialize<ModelDto>(json) ?? throw new InvalidOperationException("modelo RNA vacío");
        var np = new NeuralPerception();
        var net = new Mlp(dto.Sizes, new Rng(1)) { W = dto.W, B = dto.B };
        np.Net = net;
        np.Mean = dto.Mean;
        np.Std = dto.Std;
        np.TestX = dto.TestX.ToList();
        np.TestY = dto.TestY.ToList();
        var r = np.Report;
        r.TrainLoss.AddRange(dto.TrainLoss);
        r.ValLoss.AddRange(dto.ValLoss);
        r.TrainAcc.AddRange(dto.TrainAcc);
        r.ValAcc.AddRange(dto.ValAcc);
        r.TrainN = dto.TrainN; r.ValN = dto.ValN; r.TestN = dto.TestN; r.BestEpoch = dto.BestEpoch; r.Epochs = dto.Epochs;
        r.Seconds = dto.Seconds; r.TrainMaps = dto.TrainMaps; r.ValMaps = dto.ValMaps; r.TestMaps = dto.TestMaps;
        r.ClassCountsTrain = dto.ClassCountsTrain; r.Mean = dto.Mean; r.Std = dto.Std;
        r.Test = ClassMetrics.Evaluate(x => net.Predict(np.Normalize(x)), np.TestX, np.TestY, 3);
        return np;
    }

    public float[] Normalize(float[] raw)
    {
        var x = new float[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            x[i] = (raw[i] - Mean[i]) / Std[i];
        return x;
    }

    /// <summary>Predicción con activaciones de cada capa (para el laboratorio).</summary>
    public float[][] ForwardRaw(float[] raw) => Net.Forward(Normalize(raw));

    public float[] Predict(float[] raw) => Net.Predict(Normalize(raw));

    private static void Generate(int mapSeed, Rng rng, List<float[]> xs, List<int> ys, int perClass)
    {
        var planet = PlanetProfile.Get(PlanetMix[Math.Abs(mapSeed) % PlanetMix.Length]);
        var map = new WorldMap(planet, !planet.SolidSurface, mapSeed, mapSeed % 3);
        var buckets = new List<(float[] X, int Y)>[3];
        for (int c = 0; c < 3; c++) buckets[c] = new();
        for (int k = 0; k < 220; k++)
        {
            var rover = new Cell(rng.Int(1, map.N - 1), rng.Int(1, map.N - 1));
            if (map.ClassAt(rover) != TerrainClass.Libre) continue;
            var cond = new Conditions
            {
                VisibilityM = MathF.Exp(rng.Range(MathF.Log(80), MathF.Log(16000))),
                Night = rng.Chance(0.35f),
                Vibration = rng.Range(0, 1.6f) + (rng.Chance(0.08f) ? rng.Range(1, 3) : 0),
                RadiationMsvH = MathF.Exp(rng.Range(MathF.Log(1e-4f), MathF.Log(20f))),
                TempExtC = rng.Range(-200, 470),
            };
            for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var t = new Cell(rover.X + dx, rover.Y + dy);
                if (!map.InBounds(t) || rover.Dist(t) > 4.2f) continue;
                int y = (int)map.ClassAt(t);
                if (y == 0 && !rng.Chance(0.12f)) continue;   // submuestreo de la clase mayoritaria
                buckets[y].Add((FeatureModel.Extract(map, rover, t, cond, rng), y));
            }
        }
        foreach (var b in buckets)
        {
            // barajar y recortar por clase (balanceo)
            for (int i = b.Count - 1; i > 0; i--)
            {
                int j = rng.Int(0, i + 1);
                (b[i], b[j]) = (b[j], b[i]);
            }
            foreach (var (x, y) in b.Take(perClass))
            {
                xs.Add(x);
                ys.Add(y);
            }
        }
    }

    public static NeuralPerception Train(int seed, Action<string>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        var np = new NeuralPerception();
        var rng = new Rng((ulong)seed);
        var rep = np.Report;
        rep.TrainMaps = Enumerable.Range(10001, 24).ToArray();
        rep.ValMaps = Enumerable.Range(20001, 6).ToArray();
        rep.TestMaps = Enumerable.Range(30001, 8).ToArray();
        var trX = new List<float[]>(); var trY = new List<int>();
        var vaX = new List<float[]>(); var vaY = new List<int>();
        var teX = new List<float[]>(); var teY = new List<int>();
        progress?.Invoke("Generando dataset sintético (24 mapas de entrenamiento)…");
        foreach (int s in rep.TrainMaps) Generate(s, rng.Fork((ulong)s), trX, trY, 320);
        progress?.Invoke("Generando validación (6 mapas) y prueba (8 mapas)…");
        foreach (int s in rep.ValMaps) Generate(s, rng.Fork((ulong)s), vaX, vaY, 200);
        foreach (int s in rep.TestMaps) Generate(s, rng.Fork((ulong)s), teX, teY, 200);
        rep.TrainN = trX.Count;
        rep.ValN = vaX.Count;
        rep.TestN = teX.Count;
        foreach (int y in trY) rep.ClassCountsTrain[y]++;

        // normalización z-score con estadísticas SOLO del entrenamiento
        for (int i = 0; i < FeatureModel.NF; i++)
        {
            float mean = trX.Average(x => x[i]);
            float var = trX.Average(x => (x[i] - mean) * (x[i] - mean));
            np.Mean[i] = mean;
            np.Std[i] = MathF.Max(1e-3f, MathF.Sqrt(var));
        }
        rep.Mean = np.Mean;
        rep.Std = np.Std;
        var trN = trX.Select(np.Normalize).ToList();
        var vaN = vaX.Select(np.Normalize).ToList();
        var teN = teX.Select(np.Normalize).ToList();

        var net = new Mlp(new[] { FeatureModel.NF, 32, 16, 3 }, rng.Fork(77));
        Mlp best = net.Clone();
        float bestVal = float.MaxValue;
        int patience = 0, epochs = 45;
        var idx = Enumerable.Range(0, trN.Count).ToArray();
        for (int ep = 0; ep < epochs; ep++)
        {
            for (int i = idx.Length - 1; i > 0; i--)
            {
                int j = rng.Int(0, i + 1);
                (idx[i], idx[j]) = (idx[j], idx[i]);
            }
            float lr = 0.004f * (ep < 25 ? 1f : 0.5f);
            for (int b = 0; b < idx.Length; b += 64)
            {
                var bx = new List<float[]>();
                var by = new List<int>();
                for (int k = b; k < Math.Min(idx.Length, b + 64); k++)
                {
                    bx.Add(trN[idx[k]]);
                    by.Add(trY[idx[k]]);
                }
                net.TrainBatch(bx, by, lr);
            }
            var mt = ClassMetrics.Evaluate(net.Predict, trN, trY, 3);
            var mv = ClassMetrics.Evaluate(net.Predict, vaN, vaY, 3);
            rep.TrainLoss.Add(mt.Loss);
            rep.TrainAcc.Add(mt.Accuracy);
            rep.ValLoss.Add(mv.Loss);
            rep.ValAcc.Add(mv.Accuracy);
            rep.Epochs = ep + 1;
            progress?.Invoke($"Época {ep + 1}/{epochs} · pérdida {mt.Loss:0.000} · val {mv.Loss:0.000} · acc val {mv.Accuracy:P1}");
            if (mv.Loss < bestVal - 1e-4f)
            {
                bestVal = mv.Loss;
                best = net.Clone();
                rep.BestEpoch = ep + 1;
                patience = 0;
            }
            else if (++patience >= 8)
            {
                progress?.Invoke($"Detención temprana en la época {ep + 1} (mejor: {rep.BestEpoch})");
                break;
            }
        }
        np.Net = best;
        rep.Val = ClassMetrics.Evaluate(best.Predict, vaN, vaY, 3);
        rep.Test = ClassMetrics.Evaluate(best.Predict, teN, teY, 3);
        np.TestX = teX;
        np.TestY = teY;
        rep.Seconds = (float)sw.Elapsed.TotalSeconds;
        progress?.Invoke($"RNA lista: acc prueba {rep.Test.Accuracy:P1}, macro-F1 {rep.Test.MacroF1:0.000}, recall barranco {rep.Test.Recall[2]:P1}");
        return np;
    }
}

/// <summary>
/// Creencias del rover sobre el mapa: lo que sabe de cada celda (probabilidades fusionadas de la RNA, riesgo,
/// barrancos confirmados por el sensor de borde, obstáculos confirmados). El AG planifica sobre ESTO, no sobre la verdad.
/// </summary>
public sealed class BeliefMap
{
    public readonly int N;
    public readonly float[][] P;          // probabilidades fusionadas [libre, obstáculo, barranco]
    public readonly float[] PriorRisk, SlopeEst, HeightEst;
    public readonly bool[] Observed, ConfirmedCliff, ConfirmedObstacle, Vetoed;
    public readonly int[] LastSeen;
    public int Version;

    public BeliefMap(WorldMap map, Rng rng)
    {
        N = map.N;
        P = new float[N * N][];
        PriorRisk = new float[N * N];
        SlopeEst = new float[N * N];
        HeightEst = new float[N * N];
        Observed = new bool[N * N];
        ConfirmedCliff = new bool[N * N];
        ConfirmedObstacle = new bool[N * N];
        Vetoed = new bool[N * N];
        LastSeen = new int[N * N];
        // prior orbital: pendiente gruesa (bloques 2×2 con ruido); sin conocimiento de rocas ni barrancos pequeños
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            int i = y * N + x;
            var c = new Cell(x, y);
            var block = new Cell(x / 2 * 2, y / 2 * 2);
            float s = (map.Slope[map.Idx(block)] + map.Slope[map.Idx(map.Clamp(block + new Cell(1, 1)))]) / 2 + rng.Gauss(0, 3f);
            SlopeEst[i] = MathF.Max(0, s);
            HeightEst[i] = (map.Height(block) + map.Height(map.Clamp(block + new Cell(1, 1)))) / 2 + rng.Gauss(0, 0.6f);
            float r = map.ProbeMode ? 0.1f : 0.08f + MathX.Clamp01((s - 12f) / 30f) * 0.5f;
            PriorRisk[i] = r;
            P[i] = new[] { 1 - r, r * 0.5f, r * 0.5f };
            LastSeen[i] = -1;
        }
        if (map.IsScenario) ApplyGeoClickPrior(map, rng);
    }

    /// <summary>
    /// Escenario GeoClick: el informe ES el conocimiento orbital previo. El DEM de 30 m da pendiente y altura reales;
    /// ESA WorldCover marca el agua (prohibida desde el inicio) y sube el riesgo a priori de manzanas urbanas, bosque
    /// denso y cicatrices de remoción. Lo que el informe no resuelve (rocas, árboles o edificios puntuales) lo descubre la RNA.
    /// </summary>
    private void ApplyGeoClickPrior(WorldMap map, Rng rng)
    {
        var g = map.Geo;
        for (int i = 0; i < N * N; i++)
        {
            SlopeEst[i] = MathF.Max(0, map.Slope[i] + rng.Gauss(0, 1f));
            HeightEst[i] = map.H[i] + rng.Gauss(0, 0.5f);
            var soil = map.Soils[i];
            float obst = soil switch { Soil.Urbano => 0.3f, Soil.Bosque => 0.32f, Soil.Agua => 0.95f, _ => 0.06f };
            if (((g.Marks?[i] ?? 0) & 1) != 0) obst = MathF.Max(obst, 0.3f);
            float cliff = MathX.Clamp01((SlopeEst[i] - 25f) / 25f) * 0.6f;
            float free = MathF.Max(0.02f, 1 - obst - cliff);
            float sum = free + obst + cliff;
            P[i] = new[] { free / sum, obst / sum, cliff / sum };
            PriorRisk[i] = (obst + cliff) / sum;
            if (soil == Soil.Agua && map.Hazards[i] == Hazard.Water) ConfirmedObstacle[i] = true;   // masas de agua del informe
        }
    }

    public int Idx(Cell c) => c.Y * N + c.X;
    public bool Forbidden(Cell c) => ConfirmedCliff[Idx(c)] || ConfirmedObstacle[Idx(c)] || Vetoed[Idx(c)];

    /// <summary>Memoria de vetos del supervisor: la celda vetada queda prohibida para el AG hasta un evento que la reabra.</summary>
    public void Veto(Cell c)
    {
        Vetoed[Idx(c)] = true;
        Version++;
    }
    public float Risk(Cell c) => Forbidden(c) ? 1f : MathX.Clamp01(P[Idx(c)][1] + P[Idx(c)][2]);

    /// <summary>Fusión: media ponderada exponencial; pesa más una observación cercana y segura.</summary>
    public void Update(Cell c, float[] probs, float distM, int step)
    {
        int i = Idx(c);
        float conf = probs.Max();
        float alpha = MathX.Clamp(0.65f * (1f - distM / 30f) * (0.4f + 0.6f * conf), 0.1f, 0.75f);
        for (int k = 0; k < 3; k++)
            P[i][k] = MathX.Lerp(P[i][k], probs[k], alpha);
        Observed[i] = true;
        LastSeen[i] = step;
        if (P[i][1] > 0.88f && distM <= 11f) ConfirmedObstacle[i] = true;
        Version++;
    }

    public void ConfirmCliff(Cell c)
    {
        ConfirmedCliff[Idx(c)] = true;
        P[Idx(c)] = new[] { 0f, 0f, 1f };
        Version++;
    }

    public void ConfirmObstacle(Cell c)
    {
        ConfirmedObstacle[Idx(c)] = true;
        P[Idx(c)] = new[] { 0f, 1f, 0f };
        Version++;
    }

    public void ConfirmFree(Cell c)
    {
        int i = Idx(c);
        ConfirmedCliff[i] = ConfirmedObstacle[i] = Vetoed[i] = false;
        P[i] = new[] { 1f, 0f, 0f };
        Observed[i] = true;
        Version++;
    }

    /// <summary>Tras un evento grave (sismo, impacto) se pierde confianza en lo observado de la zona.</summary>
    public void Forget(Cell center, int radius)
    {
        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            var c = new Cell(center.X + dx, center.Y + dy);
            if (c.X < 0 || c.Y < 0 || c.X >= N || c.Y >= N) continue;
            int i = Idx(c);
            if (!ConfirmedCliff[i])
            {
                float r = MathF.Max(PriorRisk[i], 0.25f);
                P[i] = new[] { 1 - r, r * 0.5f, r * 0.5f };
                ConfirmedObstacle[i] = false;
                Vetoed[i] = false;
                Observed[i] = false;
            }
        }
        Version++;
    }
}
