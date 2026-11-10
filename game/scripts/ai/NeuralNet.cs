using System;
using System.Collections.Generic;
using System.Linq;
using AresX.Core;

namespace AresX.AI;

/// <summary>
/// Perceptrón multicapa implementado desde cero: capas densas con ReLU, salida softmax, pérdida de entropía cruzada y
/// optimizador Adam por mini-lotes. Arquitectura usada: 11 → 32 → 16 → 3 (libre / obstáculo / barranco).
/// </summary>
public sealed class Mlp
{
    public readonly int[] Sizes;
    public float[][] W, B;
    private readonly float[][] _mW, _vW, _mB, _vB;
    private int _t;

    public Mlp(int[] sizes, Rng rng)
    {
        Sizes = sizes;
        int L = sizes.Length - 1;
        W = new float[L][];
        B = new float[L][];
        _mW = new float[L][];
        _vW = new float[L][];
        _mB = new float[L][];
        _vB = new float[L][];
        for (int l = 0; l < L; l++)
        {
            int fin = sizes[l], fout = sizes[l + 1];
            W[l] = new float[fin * fout];
            B[l] = new float[fout];
            float std = MathF.Sqrt(2f / fin);   // inicialización de He (ReLU)
            for (int i = 0; i < W[l].Length; i++) W[l][i] = rng.Gauss(0, std);
            _mW[l] = new float[W[l].Length];
            _vW[l] = new float[W[l].Length];
            _mB[l] = new float[fout];
            _vB[l] = new float[fout];
        }
    }

    public Mlp Clone()
    {
        var m = (Mlp)MemberwiseClone();
        m.W = W.Select(a => (float[])a.Clone()).ToArray();
        m.B = B.Select(a => (float[])a.Clone()).ToArray();
        return m;
    }

    /// <summary>Propagación hacia delante. acts[0] = entrada, acts[l] = activación de la capa l (softmax en la última).</summary>
    public float[][] Forward(float[] x)
    {
        int L = Sizes.Length - 1;
        var acts = new float[L + 1][];
        acts[0] = x;
        for (int l = 0; l < L; l++)
        {
            int fin = Sizes[l], fout = Sizes[l + 1];
            var a = new float[fout];
            var w = W[l];
            var prev = acts[l];
            for (int o = 0; o < fout; o++)
            {
                float s = B[l][o];
                int off = o * fin;
                for (int i = 0; i < fin; i++) s += w[off + i] * prev[i];
                a[o] = l < L - 1 ? MathF.Max(0, s) : s;
            }
            if (l == L - 1) Softmax(a);
            acts[l + 1] = a;
        }
        return acts;
    }

    public float[] Predict(float[] x) => Forward(x)[^1];

    private static void Softmax(float[] z)
    {
        float m = z.Max(), sum = 0;
        for (int i = 0; i < z.Length; i++)
        {
            z[i] = MathF.Exp(z[i] - m);
            sum += z[i];
        }
        for (int i = 0; i < z.Length; i++) z[i] /= sum;
    }

    /// <summary>Un paso de Adam sobre un mini-lote. Devuelve la pérdida media del lote.</summary>
    public float TrainBatch(IReadOnlyList<float[]> xs, IReadOnlyList<int> ys, float lr, float l2 = 1e-4f)
    {
        int L = Sizes.Length - 1;
        var gW = new float[L][];
        var gB = new float[L][];
        for (int l = 0; l < L; l++)
        {
            gW[l] = new float[W[l].Length];
            gB[l] = new float[B[l].Length];
        }
        float loss = 0;
        for (int n = 0; n < xs.Count; n++)
        {
            var acts = Forward(xs[n]);
            var p = acts[L];
            loss += -MathF.Log(MathF.Max(1e-7f, p[ys[n]]));
            // gradiente softmax + entropía cruzada: p − y
            var delta = (float[])p.Clone();
            delta[ys[n]] -= 1f;
            for (int l = L - 1; l >= 0; l--)
            {
                int fin = Sizes[l], fout = Sizes[l + 1];
                var prev = acts[l];
                var prevDelta = l > 0 ? new float[fin] : null;
                for (int o = 0; o < fout; o++)
                {
                    float d = delta[o];
                    if (d == 0) continue;
                    gB[l][o] += d;
                    int off = o * fin;
                    for (int i = 0; i < fin; i++)
                    {
                        gW[l][off + i] += d * prev[i];
                        if (prevDelta != null) prevDelta[i] += d * W[l][off + i];
                    }
                }
                if (prevDelta != null)
                {
                    for (int i = 0; i < fin; i++)
                        if (prev[i] <= 0) prevDelta[i] = 0;   // derivada de ReLU
                    delta = prevDelta;
                }
            }
        }
        _t++;
        const float b1 = 0.9f, b2 = 0.999f, eps = 1e-8f;
        float c1 = 1 - MathF.Pow(b1, _t), c2 = 1 - MathF.Pow(b2, _t);
        float inv = 1f / xs.Count;
        for (int l = 0; l < L; l++)
        {
            for (int i = 0; i < W[l].Length; i++)
            {
                float g = gW[l][i] * inv + l2 * W[l][i];
                _mW[l][i] = b1 * _mW[l][i] + (1 - b1) * g;
                _vW[l][i] = b2 * _vW[l][i] + (1 - b2) * g * g;
                W[l][i] -= lr * (_mW[l][i] / c1) / (MathF.Sqrt(_vW[l][i] / c2) + eps);
            }
            for (int i = 0; i < B[l].Length; i++)
            {
                float g = gB[l][i] * inv;
                _mB[l][i] = b1 * _mB[l][i] + (1 - b1) * g;
                _vB[l][i] = b2 * _vB[l][i] + (1 - b2) * g * g;
                B[l][i] -= lr * (_mB[l][i] / c1) / (MathF.Sqrt(_vB[l][i] / c2) + eps);
            }
        }
        return loss * inv;
    }
}

/// <summary>Métricas de clasificación sobre un conjunto: matriz de confusión, precisión, recall, F1, ECE.</summary>
public sealed class ClassMetrics
{
    public readonly int K;
    public readonly int[,] Confusion;   // [verdad, predicción]
    public float Accuracy, MacroF1, Ece, Loss, Baseline;
    public float[] Precision, Recall, F1;
    public int Count;

    public ClassMetrics(int k)
    {
        K = k;
        Confusion = new int[k, k];
        Precision = new float[k];
        Recall = new float[k];
        F1 = new float[k];
    }

    public static ClassMetrics Evaluate(Func<float[], float[]> predict, IReadOnlyList<float[]> xs, IReadOnlyList<int> ys, int k)
    {
        var m = new ClassMetrics(k) { Count = xs.Count };
        const int bins = 10;
        var binConf = new float[bins];
        var binAcc = new float[bins];
        var binN = new int[bins];
        float loss = 0;
        var classCount = new int[k];
        for (int n = 0; n < xs.Count; n++)
        {
            var p = predict(xs[n]);
            int pred = Array.IndexOf(p, p.Max());
            m.Confusion[ys[n], pred]++;
            classCount[ys[n]]++;
            loss += -MathF.Log(MathF.Max(1e-7f, p[ys[n]]));
            float conf = p[pred];
            int b = Math.Min(bins - 1, (int)(conf * bins));
            binConf[b] += conf;
            binAcc[b] += pred == ys[n] ? 1 : 0;
            binN[b]++;
        }
        int correct = 0;
        for (int c = 0; c < k; c++)
        {
            correct += m.Confusion[c, c];
            int predC = 0, trueC = 0;
            for (int j = 0; j < k; j++)
            {
                predC += m.Confusion[j, c];
                trueC += m.Confusion[c, j];
            }
            m.Precision[c] = predC == 0 ? 0 : (float)m.Confusion[c, c] / predC;
            m.Recall[c] = trueC == 0 ? 0 : (float)m.Confusion[c, c] / trueC;
            m.F1[c] = m.Precision[c] + m.Recall[c] == 0 ? 0 : 2 * m.Precision[c] * m.Recall[c] / (m.Precision[c] + m.Recall[c]);
        }
        m.Accuracy = xs.Count == 0 ? 0 : (float)correct / xs.Count;
        m.MacroF1 = m.F1.Average();
        m.Loss = xs.Count == 0 ? 0 : loss / xs.Count;
        float ece = 0;
        for (int b = 0; b < bins; b++)
            if (binN[b] > 0)
                ece += (float)binN[b] / xs.Count * MathF.Abs(binAcc[b] / binN[b] - binConf[b] / binN[b]);
        m.Ece = ece;
        m.Baseline = xs.Count == 0 ? 0 : (float)classCount.Max() / xs.Count;   // clasificador de clase mayoritaria
        return m;
    }
}
