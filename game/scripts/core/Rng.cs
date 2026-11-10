using System;

namespace AresX.Core;

/// <summary>
/// Generador pseudoaleatorio determinista (SplitMix64). Toda la simulación usa instancias de esta clase con semillas
/// derivadas de la semilla de la misión: misma semilla + misma configuración + mismas órdenes = misma traza (RF-12).
/// No se usa System.Random ni el tiempo del reloj en ninguna decisión.
/// </summary>
public sealed class Rng
{
    private ulong _s;
    private float? _spare;

    public Rng(ulong seed)
    {
        _s = seed * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL;
        NextU();
    }

    public ulong NextU()
    {
        ulong z = _s += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public double NextDouble() => (NextU() >> 11) * (1.0 / 9007199254740992.0);
    public float F() => (float)NextDouble();
    public float Range(float a, float b) => a + (b - a) * F();
    public int Int(int minInclusive, int maxExclusive) =>
        maxExclusive <= minInclusive ? minInclusive : minInclusive + (int)(NextU() % (ulong)(maxExclusive - minInclusive));
    public bool Chance(float p) => F() < p;

    /// <summary>Normal(mu, sigma) por Box-Muller.</summary>
    public float Gauss(float mu = 0f, float sigma = 1f)
    {
        if (_spare is float s)
        {
            _spare = null;
            return mu + sigma * s;
        }
        double u1 = Math.Max(1e-12, NextDouble()), u2 = NextDouble();
        double r = Math.Sqrt(-2.0 * Math.Log(u1));
        _spare = (float)(r * Math.Sin(2 * Math.PI * u2));
        return mu + sigma * (float)(r * Math.Cos(2 * Math.PI * u2));
    }

    /// <summary>Flujo independiente derivado (mundo, sensores, AG… no se contaminan entre sí).</summary>
    public Rng Fork(ulong salt) => new(NextU() ^ (salt * 0xD6E8FEB86659FD93UL));
}

/// <summary>Ruido de valor 2D suavizado con fBm, con tabla de permutación sembrada.</summary>
public sealed class Noise2D
{
    private readonly int[] _perm = new int[512];
    private readonly float[] _val = new float[256];

    public Noise2D(Rng rng)
    {
        var p = new int[256];
        for (int i = 0; i < 256; i++)
        {
            p[i] = i;
            _val[i] = rng.F() * 2f - 1f;
        }
        for (int i = 255; i > 0; i--)
        {
            int j = rng.Int(0, i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }
        for (int i = 0; i < 512; i++)
            _perm[i] = p[i & 255];
    }

    private float Lattice(int x, int y) => _val[_perm[(_perm[x & 255] + y) & 511]];

    private static float Smooth(float t) => t * t * t * (t * (t * 6 - 15) + 10);

    /// <summary>Ruido en [-1, 1].</summary>
    public float Sample(float x, float y)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float tx = Smooth(x - x0), ty = Smooth(y - y0);
        float a = Lattice(x0, y0), b = Lattice(x0 + 1, y0), c = Lattice(x0, y0 + 1), d = Lattice(x0 + 1, y0 + 1);
        return Lerp(Lerp(a, b, tx), Lerp(c, d, tx), ty);
    }

    public float Fbm(float x, float y, int octaves = 4, float lacunarity = 2f, float gain = 0.5f)
    {
        float sum = 0, amp = 1, norm = 0;
        for (int i = 0; i < octaves; i++)
        {
            sum += Sample(x, y) * amp;
            norm += amp;
            amp *= gain;
            x *= lacunarity;
            y *= lacunarity;
        }
        return sum / norm;
    }

    /// <summary>Ruido de crestas en [0, 1]: 1 sobre la línea de cresta (cañones, ríos, quebradas).</summary>
    public float Ridge(float x, float y, int octaves = 3) => 1f - MathF.Abs(Fbm(x, y, octaves));

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>Celda (columna X, fila Y) de la cuadrícula de la misión.</summary>
public readonly record struct Cell(int X, int Y)
{
    public static readonly Cell[] Dirs8 =
    {
        new(1, 0), new(1, 1), new(0, 1), new(-1, 1), new(-1, 0), new(-1, -1), new(0, -1), new(1, -1),
    };

    public static Cell operator +(Cell a, Cell b) => new(a.X + b.X, a.Y + b.Y);
    public static Cell operator -(Cell a, Cell b) => new(a.X - b.X, a.Y - b.Y);
    public float Length => MathF.Sqrt(X * X + Y * Y);
    public int Chebyshev(Cell o) => Math.Max(Math.Abs(X - o.X), Math.Abs(Y - o.Y));
    public float Dist(Cell o) => (this - o).Length;
    public bool IsDiagonal => X != 0 && Y != 0;

    /// <summary>Rumbo de brújula en grados (0 = N = −Y, 90 = E = +X).</summary>
    public static float Heading(Cell d) => (MathF.Atan2(d.X, -d.Y) * 180f / MathF.PI + 360f) % 360f;

    public override string ToString() => $"({Y},{X})";   // (fila, columna) como pide el caso
}

public static class MathX
{
    public static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    public static float Clamp(float v, float a, float b) => v < a ? a : v > b ? b : v;
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    public static float InvLerp(float a, float b, float v) => Math.Abs(b - a) < 1e-9f ? 0 : (v - a) / (b - a);
    public static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    public static string Compass(float deg)
    {
        string[] n = { "N", "NE", "E", "SE", "S", "SO", "O", "NO" };
        return n[(int)MathF.Round(((deg % 360) + 360) % 360 / 45f) % 8];
    }
}
