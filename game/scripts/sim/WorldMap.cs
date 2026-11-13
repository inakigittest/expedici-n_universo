using System;
using System.Collections.Generic;
using System.Linq;
using AresX.Core;

namespace AresX.Sim;

public enum Soil { Regolito, Roca, Arena, Hielo, Basalto, Urbano, Bosque, Matorral, SueloDesnudo, Pastizal, Agua, Barro, Nube, Cultivo, Quemado }

public enum Hazard { None, Rock, Cliff, Water, Crater, Debris, Building, Tree, Storm, Downdraft, Alien }

/// <summary>Clase de verdad del terreno que la RNA debe aprender: libre / obstáculo / barranco.</summary>
public enum TerrainClass { Libre = 0, Obstaculo = 1, Barranco = 2 }

public sealed class ScienceSite
{
    public int Id;
    public Cell Cell;
    public string Name = "", Kind = "";
    public float Value;        // valor científico 0..1
    public float MassKg;       // masa de la muestra (mochila)
    public float AnalysisWh;   // energía del análisis
    public bool Anomaly;       // señal biológica anómala SIMULADA (nunca confirma vida)
    public bool Visited;
}

/// <summary>
/// Mapa de verdad de la misión (lo que el rover NO ve directamente): cuadrícula N×N de celdas de 5 m con elevación,
/// pendiente, suelo, peligros, refugios y sitios científicos. Se genera de forma determinista desde la semilla (RF-02).
/// Un campo de alturas fino (Sub vértices por celda) da forma al relieve 3D.
/// </summary>
public sealed class WorldMap
{
    public const float RefCellM = 5f;       // celda de referencia (m): escala con la que se entrenó la RNA
    public float CellM = RefCellM;          // metros por celda de este mapa (5 m procedural; 30 m en escenarios GeoClick)
    /// <summary>Convierte un desnivel entre celdas vecinas a su equivalente sobre 5 m (rasgos de la RNA, LIDAR de borde).</summary>
    public float DropScale => RefCellM / CellM;
    public GeoReport Geo = GeoReport.Santiago;   // clima y calibración (Tierra)
    public bool[]? Inside;                   // escenarios: celda dentro del polígono analizado
    public CityPlan? City;                   // escenarios: ciudad simulada sobre la mancha urbana
    public const int Sub = 3;               // vértices de relieve por celda (vista 3D)
    public const float MaxSlopeDeg = 30f;   // pendiente máxima de rodadura (SUPUESTO)
    public const float CliffDropM = 5f;     // salto vertical que se considera precipicio

    public readonly int N;
    public readonly PlanetProfile Planet;
    public readonly bool ProbeMode;
    public readonly int Seed;
    public readonly int Difficulty;          // 0 fácil, 1 normal, 2 difícil

    public float[] Fine;                     // (N*Sub+1)²
    public float[] H, Slope, Rough, Albedo, ObstacleH;
    public Soil[] Soils;
    public Hazard[] Hazards;
    public readonly List<ScienceSite> Sites = new();
    public readonly List<Cell> Refuges = new();
    public Cell Start;
    public int Version;                      // sube cuando cambian los peligros (la vista de objetos se reconstruye)
    public int HeightVersion;                // sube cuando cambia el relieve (la malla del terreno se reconstruye)
    public float BaseElevation;              // m (Tierra: 525 m como GeoClick)
    public float[] SlopeHistogram = new float[5];
    public string Calibration = "";

    public int FN => N * Sub + 1;

    /// <summary>Escenario a escala real desde un informe GeoClick convertido.</summary>
    public WorldMap(PlanetProfile planet, GeoReport scenario, int seed, int difficulty)
    {
        Planet = planet;
        Seed = seed;
        Difficulty = Math.Clamp(difficulty, 0, 2);
        Geo = scenario;
        N = scenario.N;
        CellM = scenario.CellM;
        Fine = new float[FN * FN];
        H = new float[N * N];
        Slope = new float[N * N];
        Rough = new float[N * N];
        Albedo = new float[N * N];
        ObstacleH = new float[N * N];
        Soils = new Soil[N * N];
        Hazards = new Hazard[N * N];
        GenerateFromScenario(scenario);
    }

    public bool IsScenario => Inside != null;

    public WorldMap(PlanetProfile planet, bool probe, int seed, int difficulty, int n = 40)
    {
        Planet = planet;
        ProbeMode = probe;
        Seed = seed;
        Difficulty = Math.Clamp(difficulty, 0, 2);
        N = n;
        Fine = new float[FN * FN];
        H = new float[N * N];
        Slope = new float[N * N];
        Rough = new float[N * N];
        Albedo = new float[N * N];
        ObstacleH = new float[N * N];
        Soils = new Soil[N * N];
        Hazards = new Hazard[N * N];
        Generate();
    }

    // ------------------------------------------------------------------ acceso
    public bool InGrid(Cell c) => c.X >= 0 && c.Y >= 0 && c.X < N && c.Y < N;
    /// <summary>Dentro del mapa jugable (en escenarios: dentro del polígono analizado).</summary>
    public bool InBounds(Cell c) => InGrid(c) && (Inside == null || Inside[c.Y * N + c.X]);
    public int Idx(Cell c) => c.Y * N + c.X;
    public float Height(Cell c) => H[Idx(Clamp(c))];
    public Cell Clamp(Cell c) => new(Math.Clamp(c.X, 0, N - 1), Math.Clamp(c.Y, 0, N - 1));
    public Hazard HazardAt(Cell c) => InBounds(c) ? Hazards[Idx(c)] : Hazard.None;
    public Soil SoilAt(Cell c) => Soils[Idx(Clamp(c))];
    public float FineAt(int i, int j) => Fine[Math.Clamp(j, 0, FN - 1) * FN + Math.Clamp(i, 0, FN - 1)];

    public static TerrainClass ClassOf(Hazard h) => h switch
    {
        Hazard.Rock or Hazard.Water or Hazard.Debris or Hazard.Building or Hazard.Tree or Hazard.Storm or Hazard.Alien => TerrainClass.Obstaculo,
        Hazard.Cliff or Hazard.Crater or Hazard.Downdraft => TerrainClass.Barranco,
        _ => TerrainClass.Libre,
    };

    public TerrainClass ClassAt(Cell c) => InBounds(c) ? ClassOf(Hazards[Idx(c)]) : TerrainClass.Obstaculo;

    /// <summary>Se puede atravesar (verdad): sin obstáculo, sin barranco y con pendiente rodable.</summary>
    public bool Passable(Cell c) => InBounds(c) && ClassAt(c) == TerrainClass.Libre && (ProbeMode || Slope[Idx(c)] <= MaxSlopeDeg);

    /// <summary>Pendiente direccional de a → b en grados (positiva = subida).</summary>
    public float DirSlope(Cell a, Cell b)
    {
        float d = (b - a).IsDiagonal ? CellM * 1.41421f : CellM;
        return MathF.Atan2(Height(b) - Height(a), d) * 180f / MathF.PI;
    }

    /// <summary>Mayor caída hacia cualquier vecina (m): borde de precipicio.</summary>
    public float MaxDrop(Cell c)
    {
        float h = Height(c), drop = 0;
        foreach (var d in Cell.Dirs8)
        {
            var o = c + d;
            if (InBounds(o))
                drop = MathF.Max(drop, h - Height(o));
        }
        return drop;
    }

    public float Elevation(Cell c) => BaseElevation + Height(c);

    /// <summary>Hash FNV-1a estable entre ejecuciones (string.GetHashCode está aleatorizado en .NET).</summary>
    public static ulong StableHash(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char ch in s)
        {
            h ^= ch;
            h *= 1099511628211UL;
        }
        return h;
    }

    // ------------------------------------------------------------------ generación
    private void Generate()
    {
        var rng = new Rng((ulong)Seed * 7919UL + StableHash(Planet.Id) * 31UL + (ProbeMode ? 17UL : 0UL));
        var noise = new Noise2D(rng.Fork(1));
        var noise2 = new Noise2D(rng.Fork(2));
        var noise3 = new Noise2D(rng.Fork(3));
        int fn = FN;
        float inv = 1f / (fn - 1);
        BaseElevation = Planet.Style == TerrainStyle.Tierra ? Geo.ElevMin : 0;

        // 1) relieve base
        for (int j = 0; j < fn; j++)
        for (int i = 0; i < fn; i++)
        {
            float u = i * inv, v = j * inv;
            float h = Planet.Style switch
            {
                TerrainStyle.Marte => noise.Fbm(u * 3.2f, v * 3.2f, 5) * 9f + noise2.Fbm(u * 9f, v * 9f, 3) * 1.6f,
                TerrainStyle.Mercurio => noise.Fbm(u * 4f, v * 4f, 5) * 6f + noise2.Fbm(u * 14f, v * 14f, 3) * 1.4f,
                TerrainStyle.Venus => noise.Fbm(u * 2.4f, v * 2.4f, 4) * 5f + MathF.Pow(noise2.Ridge(u * 3f, v * 5f), 6) * 4f,
                TerrainStyle.Tierra => EarthHeight(u, v, noise, noise2),
                _ => noise.Fbm(u * 2f + 0.3f, v * 2.6f, 4) * 4f + MathF.Sin(u * 9f + noise2.Fbm(u * 2, v * 2) * 3f) * 1.2f,
            };
            Fine[j * fn + i] = h;
        }

        // 2) rasgos: cañones, cráteres, escarpes, quebradas, ríos
        switch (Planet.Style)
        {
            case TerrainStyle.Marte:
                CarveCanyon(noise3, 0.955f, 13f, segmented: true);
                for (int k = 0; k < 3 + Difficulty; k++)
                    Crater(rng, rng.Range(2.2f, 3.8f), 6f);
                break;
            case TerrainStyle.Mercurio:
                for (int k = 0; k < 7 + Difficulty * 2; k++)
                    Crater(rng, rng.Range(1.6f, 4.2f), rng.Range(5f, 8f));
                break;
            case TerrainStyle.Venus:
                CarveCanyon(noise3, 0.975f, 9f, segmented: true);
                Crater(rng, 3.5f, 6f);
                break;
            case TerrainStyle.Tierra:
                CalibrateEarthSlopes();
                CarveCanyon(noise3, 0.965f, 7f, segmented: true, onlySteep: true);
                break;
        }

        RecomputeCells();

        // 3) suelo, rugosidad, albedo y peligros
        AssignSoilsAndHazards(rng.Fork(4), noise2);
        PlacePoints(rng.Fork(5));
        EnsureConnectivity();
        RecomputeCells();
        ComputeSlopeHistogram();
        Version++;
    }

    private float EarthHeight(float u, float v, Noise2D n1, Noise2D n2)
    {
        // Cerro (relieve alto al oeste/noroeste) que cae hacia la planicie urbana del este, como en la figura 2 del
        // informe GeoClick; la escala vertical se calibra después con la pendiente mediana.
        float hill = MathF.Exp(-((u - 0.18f) * (u - 0.18f) * 5f + (v - 0.45f) * (v - 0.45f) * 3f));
        float ridge = MathF.Pow(n1.Ridge(u * 2.2f, v * 2.2f), 3) * 0.35f;
        return hill * 1.0f + ridge * (0.3f + hill) + n2.Fbm(u * 6f, v * 6f, 3) * 0.06f - u * 0.12f;
    }

    /// <summary>Ajusta la escala vertical para que la pendiente mediana coincida con la de GeoClick (6,7°).</summary>
    private void CalibrateEarthSlopes()
    {
        var baseF = (float[])Fine.Clone();
        float lo = 1f, hi = 400f, k = 60f;
        for (int it = 0; it < 30; it++)
        {
            k = (lo + hi) / 2;
            for (int i = 0; i < Fine.Length; i++)
                Fine[i] = baseF[i] * k;
            RecomputeCells();
            var s = Slope.OrderBy(x => x).ToArray();
            float median = s[s.Length / 2];
            if (median < Geo.SlopeMedian) lo = k; else hi = k;
        }
        float min = Fine.Min();
        for (int i = 0; i < Fine.Length; i++)
            Fine[i] -= min;
        Calibration = $"Escala vertical ×{k:0.0} → pendiente mediana {Geo.SlopeMedian:0.0}° (objetivo GeoClick)";
    }

    private void CarveCanyon(Noise2D n, float threshold, float depth, bool segmented, bool onlySteep = false)
    {
        int fn = FN;
        float inv = 1f / (fn - 1);
        var gate = new Noise2D(new Rng((ulong)Seed + 991));
        for (int j = 0; j < fn; j++)
        for (int i = 0; i < fn; i++)
        {
            float u = i * inv, v = j * inv;
            float r = n.Ridge(u * 1.6f + 0.2f, v * 1.6f - 0.1f, 2);
            if (r < threshold - 0.04f)
                continue;
            if (segmented && gate.Fbm(u * 3.1f, v * 3.1f, 2) < -0.12f)
                continue;   // pasos naturales: el cañón se interrumpe
            if (onlySteep && Fine[j * fn + i] < 0.35f * Fine.Max())
                continue;
            float t = MathX.Clamp01((r - (threshold - 0.04f)) / 0.04f);
            Fine[j * fn + i] -= depth * (t * t * (3 - 2 * t));
        }
    }

    private void Crater(Rng rng, float radiusCells, float depth)
    {
        int fn = FN;
        float cx = rng.Range(0.15f, 0.85f) * (fn - 1), cy = rng.Range(0.15f, 0.85f) * (fn - 1);
        float r = radiusCells * Sub;
        for (int j = 0; j < fn; j++)
        for (int i = 0; i < fn; i++)
        {
            float d = MathF.Sqrt((i - cx) * (i - cx) + (j - cy) * (j - cy)) / r;
            if (d > 1.6f)
                continue;
            float bowl = d < 1f ? -depth * (1 - d * d) : 0;
            float rim = 2.2f * MathF.Exp(-(d - 1f) * (d - 1f) * 18f);
            Fine[j * fn + i] += bowl + rim;
        }
    }

    /// <summary>Recalcula altura media, pendiente y caídas de cada celda desde el relieve fino.</summary>
    public void RecomputeCells()
    {
        HeightVersion++;
        int fn = FN;
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float s = 0;
            for (int j = 0; j <= Sub; j++)
            for (int i = 0; i <= Sub; i++)
                s += Fine[(y * Sub + j) * fn + x * Sub + i];
            H[y * N + x] = s / ((Sub + 1) * (Sub + 1));
        }
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            var c = new Cell(x, y);
            float gx = (Height(new Cell(x + 1, y)) - Height(new Cell(x - 1, y))) / (2 * CellM);
            float gy = (Height(new Cell(x, y + 1)) - Height(new Cell(x, y - 1))) / (2 * CellM);
            if (x == 0 || x == N - 1) gx *= 2;
            if (y == 0 || y == N - 1) gy *= 2;
            Slope[Idx(c)] = MathF.Atan(MathF.Sqrt(gx * gx + gy * gy)) * 180f / MathF.PI;
        }
    }

    private void AssignSoilsAndHazards(Rng rng, Noise2D n)
    {
        float maxH = H.Max(), minH = H.Min();
        float rockDensity = new[] { 0.03f, 0.055f, 0.09f }[Difficulty];
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            var c = new Cell(x, y);
            int i = Idx(c);
            float hn = MathX.InvLerp(minH, maxH, H[i]);
            float sl = Slope[i];
            float nz = n.Fbm(x * 0.13f, y * 0.13f, 3);
            float drop = MaxDrop(c);
            Soil soil;
            Hazard hz = Hazard.None;
            switch (Planet.Style)
            {
                case TerrainStyle.Marte:
                    soil = sl > 18 ? Soil.Roca : hn < 0.35f && nz > -0.1f ? Soil.Arena : Soil.Regolito;
                    if (Planet.HasIce && hn < 0.3f && nz < -0.35f) soil = Soil.Hielo;
                    break;
                case TerrainStyle.Mercurio:
                    soil = sl > 15 ? Soil.Roca : Soil.Regolito;
                    if (hn < 0.12f && nz < -0.1f) soil = Soil.Hielo;   // fondo de cráter en sombra permanente
                    break;
                case TerrainStyle.Venus:
                    soil = sl > 14 || nz > 0.25f ? Soil.Roca : Soil.Basalto;
                    break;
                case TerrainStyle.Tierra:
                    if (sl < 8 && hn < 0.35f) soil = Soil.Urbano;
                    else if (sl > 14 && nz > -0.25f) soil = Soil.Bosque;
                    else if (sl > 6) soil = nz > 0.3f ? Soil.SueloDesnudo : Soil.Matorral;
                    else soil = nz > 0.2f ? Soil.Pastizal : Soil.Matorral;
                    break;
                default:
                    soil = Soil.Nube;
                    break;
            }

            // barrancos: salto vertical o fondo profundo de cañón / cráter
            if (Planet.Style != TerrainStyle.Gaseoso && (drop > CliffDropM * 1.6f || sl > 42f))
                hz = Planet.Style == TerrainStyle.Mercurio || (Planet.Style == TerrainStyle.Marte && nz < 0) ? Hazard.Crater : Hazard.Cliff;

            float roughBase = soil switch
            {
                Soil.Roca => 0.65f, Soil.Basalto => 0.5f, Soil.Regolito => 0.35f, Soil.Arena => 0.2f, Soil.Hielo => 0.1f,
                Soil.Urbano => 0.12f, Soil.Bosque => 0.55f, Soil.Matorral => 0.45f, Soil.SueloDesnudo => 0.4f, Soil.Pastizal => 0.25f,
                _ => 0.15f,
            };
            Rough[i] = MathX.Clamp01(roughBase + rng.Gauss(0, 0.08f) + sl / 90f);
            Albedo[i] = SoilAlbedo(soil) + rng.Gauss(0, 0.03f);

            if (hz == Hazard.None)
            {
                if (Planet.Style == TerrainStyle.Gaseoso)
                {
                    float storm = n.Fbm(x * 0.21f + 7, y * 0.21f - 3, 3);
                    if (storm > 0.42f - Difficulty * 0.05f) hz = Hazard.Storm;
                    else if (storm < -0.55f + Difficulty * 0.04f) hz = Hazard.Downdraft;
                }
                else if (Planet.Style == TerrainStyle.Tierra)
                {
                    if (soil == Soil.Urbano && rng.Chance(0.07f + Difficulty * 0.03f)) hz = Hazard.Building;
                    else if (soil == Soil.Bosque && rng.Chance(0.12f + Difficulty * 0.04f)) hz = Hazard.Tree;
                    else if (rng.Chance(rockDensity * 0.4f)) hz = Hazard.Rock;
                }
                else if (rng.Chance(rockDensity * (0.6f + Rough[i])))
                {
                    hz = Hazard.Rock;
                }
            }
            Soils[i] = soil;
            Hazards[i] = hz;
            ObstacleH[i] = hz switch
            {
                Hazard.Rock => rng.Range(0.8f, 2.6f), Hazard.Building => rng.Range(4f, 12f), Hazard.Tree => rng.Range(3f, 9f),
                Hazard.Storm => rng.Range(3f, 6f), _ => 0,
            };
        }

        if (Planet.Style == TerrainStyle.Tierra)
            CarveRiver(rng);
        // las corrientes descendentes de los gigantes se modelan como un hundimiento de la capa de nubes (la sonda
        // mide la cizalla vertical igual que el rover mide una caída)
        for (int i = 0; i < N * N; i++)
            if (Hazards[i] == Hazard.Downdraft)
                SetCellHeight(new Cell(i % N, i / N), H[i] - 14f);
        RecomputeCells();
    }

    /// <summary>Cauce diagonal SO→NE (como el río de la figura 8 de GeoClick) con dos puentes.</summary>
    private void CarveRiver(Rng rng)
    {
        float off = rng.Range(-4f, 4f);
        var bridges = new[] { rng.Int(8, 16), rng.Int(24, 32) };
        for (int x = 0; x < N; x++)
        {
            int yc = (int)MathF.Round(N - 1 - (x * 0.9f + 4 + off + MathF.Sin(x * 0.35f) * 2f));
            if (bridges.Any(b => Math.Abs(b - x) <= 0)) continue;
            for (int w = 0; w < 2; w++)
            {
                var c = new Cell(x, yc + w);
                if (!InBounds(c) || x < N * 0.35f) continue;
                int i = Idx(c);
                Hazards[i] = Hazard.Water;
                Soils[i] = Soil.Agua;
                Albedo[i] = 0.08f;
                Rough[i] = 0.05f;
                ObstacleH[i] = 0;
            }
        }
    }

    public static float SoilAlbedo(Soil s) => s switch
    {
        Soil.Hielo => 0.6f, Soil.Arena => 0.32f, Soil.Regolito => 0.22f, Soil.Roca => 0.18f, Soil.Basalto => 0.12f,
        Soil.Urbano => 0.25f, Soil.Bosque => 0.1f, Soil.Matorral => 0.15f, Soil.SueloDesnudo => 0.28f, Soil.Pastizal => 0.2f,
        Soil.Agua => 0.06f, Soil.Barro => 0.1f, Soil.Cultivo => 0.18f, Soil.Quemado => 0.07f, _ => 0.5f,
    };

    /// <summary>Coeficiente de rodadura (SUPUESTO) por suelo.</summary>
    public static float RollingCoeff(Soil s) => s switch
    {
        Soil.Urbano => 0.03f, Soil.Hielo => 0.04f, Soil.Roca => 0.06f, Soil.Basalto => 0.07f, Soil.Regolito => 0.1f,
        Soil.Pastizal => 0.11f, Soil.SueloDesnudo => 0.12f, Soil.Matorral => 0.14f, Soil.Bosque => 0.16f, Soil.Arena => 0.2f,
        Soil.Barro => 0.28f, Soil.Nube => 0.02f, Soil.Cultivo => 0.15f, Soil.Quemado => 0.13f, _ => 0.1f,
    };

    /// <summary>Probabilidad base de patinaje (SUPUESTO).</summary>
    public static float SlipChance(Soil s) => s switch
    {
        Soil.Hielo => 0.12f, Soil.Arena => 0.08f, Soil.Barro => 0.15f, Soil.SueloDesnudo => 0.03f, _ => 0.01f,
    };

    public static string SoilName(Soil s) => s switch
    {
        Soil.SueloDesnudo => "suelo desnudo", Soil.Nube => "capa de nubes", Soil.Cultivo => "cultivos", Soil.Quemado => "suelo quemado", _ => s.ToString().ToLowerInvariant(),
    };

    public static string HazardName(Hazard h) => h switch
    {
        Hazard.None => "—", Hazard.Rock => "roca", Hazard.Cliff => "barranco", Hazard.Water => "agua", Hazard.Crater => "cráter",
        Hazard.Debris => "detritos (remoción)", Hazard.Building => "edificio", Hazard.Tree => "árbol", Hazard.Storm => "celda de tormenta",
        Hazard.Downdraft => "corriente descendente", Hazard.Alien => "objeto no identificado (ficción)", _ => h.ToString(),
    };

    // ------------------------------------------------------------------ puntos de interés
    private static readonly Dictionary<TerrainStyle, string[]> SiteKinds = new()
    {
        [TerrainStyle.Marte] = new[] { "Afloramiento estratificado", "Hielo subsuperficial", "Meteorito férrico", "Sedimento lacustre", "Veta de sulfatos", "Duna de basalto" },
        [TerrainStyle.Mercurio] = new[] { "Hielo en sombra permanente", "Hueco (hollow) brillante", "Eyecta de cráter", "Lava antigua", "Regolito maduro", "Roca de impacto" },
        [TerrainStyle.Venus] = new[] { "Basalto tesserae", "Flujo de lava", "Domo volcánico", "Sedimento de viento", "Roca alterada por SO₂", "Escarpe de falla" },
        [TerrainStyle.Tierra] = new[] { "Cicatriz de remoción", "Suelo de bosque esclerófilo", "Cauce y sedimentos", "Afloramiento andesítico", "Estación de calibración", "Quebrada" },
        [TerrainStyle.Gaseoso] = new[] { "Muestreo de amoníaco", "Nube de agua", "Vórtice menor", "Capa de hidrosulfuro", "Relámpago (radio)", "Haze fotoquímico" },
    };

    private void PlacePoints(Rng rng)
    {
        bool Free(Cell c) => InBounds(c) && Hazards[Idx(c)] == Hazard.None && Slope[Idx(c)] < 14f && c.X > 0 && c.Y > 0 && c.X < N - 1 && c.Y < N - 1;
        Cell FindNear(Cell target, int maxR)
        {
            for (int r = 0; r <= maxR; r++)
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                var c = new Cell(target.X + dx, target.Y + dy);
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && Free(c))
                    return c;
            }
            return Clamp(target);
        }

        Start = FindNear(new Cell(4, N - 5), 10);
        ClearAround(Start, 1);
        Refuges.Add(Start);
        var station = FindNear(new Cell(N - 6, 5), 12);
        ClearAround(station, 1);
        Refuges.Add(station);

        int count = 4 + Difficulty;
        var kinds = SiteKinds[Planet.Style];
        int tries = 0;
        while (Sites.Count < count && tries++ < 4000)
        {
            var c = new Cell(rng.Int(3, N - 3), rng.Int(3, N - 3));
            if (!Free(c) || c.Dist(Start) < 9 || Refuges.Any(r => r.Dist(c) < 5) || Sites.Any(s => s.Cell.Dist(c) < 8))
                continue;
            var site = new ScienceSite
            {
                Id = Sites.Count,
                Cell = c,
                Kind = kinds[Sites.Count % kinds.Length],
                Value = MathF.Round(rng.Range(0.35f, 1f), 2),
                MassKg = MathF.Round(rng.Range(0.4f, 2.2f), 1),
                AnalysisWh = MathF.Round(rng.Range(2f, 6f), 1),
            };
            site.Name = $"S{site.Id + 1}";
            Sites.Add(site);
        }
        if (Sites.Count > 0 && Planet.Style != TerrainStyle.Mercurio && rng.Chance(0.6f))
            Sites[rng.Int(0, Sites.Count)].Anomaly = true;
    }

    private void ClearAround(Cell c, int r)
    {
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            var o = new Cell(c.X + dx, c.Y + dy);
            if (InBounds(o) && ClassOf(Hazards[Idx(o)]) == TerrainClass.Obstaculo && Hazards[Idx(o)] != Hazard.Water)
                Hazards[Idx(o)] = Hazard.None;
        }
    }

    /// <summary>
    /// Garantiza que cada refugio y sitio sea alcanzable: Dijkstra donde las rocas cuestan 6 y los barrancos 60; a lo
    /// largo del camino resultante se retiran rocas y se rellena el barranco como una rampa natural (un "paso").
    /// </summary>
    private void EnsureConnectivity()
    {
        var targets = Refuges.Concat(Sites.Select(s => s.Cell)).ToList();
        foreach (var t in targets)
        {
            if (ReachableTruth(Start, t))
                continue;
            var path = CheapestPath(Start, t);
            foreach (var c in path)
            {
                int i = Idx(c);
                if (Hazards[i] != Hazard.None && Hazards[i] != Hazard.Water)
                {
                    if (ClassOf(Hazards[i]) == TerrainClass.Barranco)
                        FillToNeighbors(c);
                    Hazards[i] = Hazard.None;
                }
                else if (Hazards[i] == Hazard.Water)
                {
                    Hazards[i] = Hazard.None;   // vado / puente
                    Soils[i] = Soil.Urbano;
                }
            }
            RecomputeCells();
            foreach (var c in path)
                if (Slope[Idx(c)] > MaxSlopeDeg)
                    SmoothCell(c);
        }
        RecomputeCells();
    }

    private void FillToNeighbors(Cell c)
    {
        float sum = 0;
        int n = 0;
        foreach (var d in Cell.Dirs8)
        {
            var o = c + d;
            if (InBounds(o) && ClassAt(o) != TerrainClass.Barranco)
            {
                sum += Height(o);
                n++;
            }
        }
        float target = n > 0 ? sum / n : Height(c);
        SetCellHeight(c, target);
    }

    private void SmoothCell(Cell c)
    {
        float sum = 0;
        int n = 0;
        foreach (var d in Cell.Dirs8)
        {
            var o = c + d;
            if (!InBounds(o)) continue;
            sum += Height(o);
            n++;
        }
        SetCellHeight(c, n > 0 ? (sum / n + Height(c)) / 2 : Height(c));
    }

    /// <summary>Lleva el relieve fino de una celda hacia una altura (con borde suavizado).</summary>
    public void SetCellHeight(Cell c, float h, float blend = 1f)
    {
        int fn = FN;
        for (int j = 0; j <= Sub; j++)
        for (int i = 0; i <= Sub; i++)
        {
            int fi = c.X * Sub + i, fj = c.Y * Sub + j;
            if (fi < 0 || fj < 0 || fi >= fn || fj >= fn) continue;
            bool edge = i == 0 || j == 0 || i == Sub || j == Sub;
            float w = edge ? blend * 0.5f : blend;
            Fine[fj * fn + fi] = MathX.Lerp(Fine[fj * fn + fi], h, w);
        }
    }

    public bool ReachableTruth(Cell a, Cell b) => BfsDistance(a, Passable)[Idx(b)] >= 0;

    /// <summary>Distancias BFS (en pasos) desde a sobre las celdas que cumplen ok; −1 = inalcanzable.</summary>
    public int[] BfsDistance(Cell a, Func<Cell, bool> ok)
    {
        var dist = Enumerable.Repeat(-1, N * N).ToArray();
        var q = new Queue<Cell>();
        dist[Idx(a)] = 0;
        q.Enqueue(a);
        while (q.Count > 0)
        {
            var c = q.Dequeue();
            foreach (var d in Cell.Dirs8)
            {
                var o = c + d;
                if (!InBounds(o) || dist[Idx(o)] >= 0 || !ok(o)) continue;
                dist[Idx(o)] = dist[Idx(c)] + 1;
                q.Enqueue(o);
            }
        }
        return dist;
    }

    private List<Cell> CheapestPath(Cell a, Cell b)
    {
        var cost = new float[N * N];
        var prev = new int[N * N];
        Array.Fill(cost, float.MaxValue);
        Array.Fill(prev, -1);
        var pq = new PriorityQueue<Cell, float>();
        cost[Idx(a)] = 0;
        pq.Enqueue(a, 0);
        while (pq.Count > 0)
        {
            var c = pq.Dequeue();
            if (c == b) break;
            foreach (var d in Cell.Dirs8)
            {
                var o = c + d;
                if (!InBounds(o)) continue;
                var cls = ClassAt(o);
                float step = (d.IsDiagonal ? 1.414f : 1f) * (cls == TerrainClass.Barranco ? 60 : cls == TerrainClass.Obstaculo ? 6 : 1)
                             + (Slope[Idx(o)] > MaxSlopeDeg ? 20 : 0);
                if (cost[Idx(c)] + step < cost[Idx(o)])
                {
                    cost[Idx(o)] = cost[Idx(c)] + step;
                    prev[Idx(o)] = Idx(c);
                    pq.Enqueue(o, cost[Idx(o)]);
                }
            }
        }
        var path = new List<Cell>();
        for (int i = Idx(b); i >= 0; i = prev[i])
            path.Add(new Cell(i % N, i / N));
        path.Reverse();
        return path;
    }

    private void ComputeSlopeHistogram()
    {
        var bins = new float[5];
        int total = 0;
        for (int i = 0; i < Slope.Length; i++)
        {
            if (Inside != null && !Inside[i]) continue;
            float s = Slope[i];
            bins[s < 5 ? 0 : s < 15 ? 1 : s < 25 ? 2 : s < 35 ? 3 : 4]++;
            total++;
        }
        for (int i = 0; i < 5; i++)
            bins[i] /= Math.Max(1, total);
        SlopeHistogram = bins;
    }

    // ------------------------------------------------------------------ escenario GeoClick a escala real
    private static readonly Dictionary<Soil, string> ScenarioSiteKind = new()
    {
        [Soil.Bosque] = "Suelo de bosque", [Soil.Matorral] = "Matorral esclerófilo", [Soil.Pastizal] = "Pradera",
        [Soil.Cultivo] = "Suelo agrícola", [Soil.Urbano] = "Estación de calibración urbana", [Soil.SueloDesnudo] = "Afloramiento rocoso",
        [Soil.Quemado] = "Área quemada (regeneración)", [Soil.Barro] = "Humedal", [Soil.Hielo] = "Nieve estacional",
    };

    /// <summary>
    /// Relieve real (DEM GLO-30 decodificado del informe) interpolado al relieve fino; suelos de ESA WorldCover;
    /// cicatrices y áreas quemadas de Sentinel-2. Peligros a escala de 30 m: agua, edificios (manzanas), árboles
    /// (bosque denso), escarpes (&gt; 40°) y detritos en las cicatrices. Fuera del polígono no se puede circular.
    /// </summary>
    private void GenerateFromScenario(GeoReport g)
    {
        var rng = new Rng((ulong)Seed * 7919UL + StableHash(g.Id));
        var noise = new Noise2D(rng.Fork(2));
        int n = N, fn = FN;
        BaseElevation = g.ElevMin;
        var e = g.Elev!;
        for (int j = 0; j < fn; j++)
        for (int i = 0; i < fn; i++)
        {
            float cx = Math.Clamp(i / (float)Sub - 0.5f, 0, n - 1.001f), cy = Math.Clamp(j / (float)Sub - 0.5f, 0, n - 1.001f);
            int x0 = (int)cx, y0 = (int)cy;
            float tx = cx - x0, ty = cy - y0;
            float a = MathX.Lerp(e[y0 * n + x0], e[y0 * n + x0 + 1], tx);
            float b = MathX.Lerp(e[(y0 + 1) * n + x0], e[(y0 + 1) * n + x0 + 1], tx);
            Fine[j * fn + i] = MathX.Lerp(a, b, ty) - g.ElevMin;
        }
        RecomputeCells();
        Inside = new bool[n * n];
        for (int i = 0; i < n * n; i++) Inside[i] = g.CoverGrid![i] != 255;
        float rockDensity = new[] { 0.02f, 0.04f, 0.07f }[Difficulty];
        // suelos primero: la ciudad simulada se genera sobre la mancha urbana de WorldCover
        for (int i = 0; i < n * n; i++)
        {
            if (!Inside[i]) continue;
            var so = g.SoilOf(g.CoverGrid![i]);
            if (((g.Marks?[i] ?? 0) & 2) != 0 && so is not (Soil.Agua or Soil.Urbano)) so = Soil.Quemado;
            Soils[i] = so;
        }
        City = CityPlan.Generate(this, StableHash(g.Id) ^ 0x5eed);
        var coverage = City.CellCoverage(n, CellM, out var cityH);
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int i = y * n + x;
            var c = new Cell(x, y);
            if (!Inside[i])
            {
                Soils[i] = Soil.SueloDesnudo;
                Albedo[i] = 0.12f;
                Rough[i] = 0.3f;
                continue;
            }
            byte marks = g.Marks?[i] ?? 0;
            var soil = g.SoilOf(g.CoverGrid![i]);
            if ((marks & 2) != 0 && soil is not (Soil.Agua or Soil.Urbano)) soil = Soil.Quemado;
            float sl = Slope[i];
            var hz = Hazard.None;
            if (soil == Soil.Agua) hz = Hazard.Water;
            else if (sl > 40f || (g.SlopeGrid?[i] == 4 && MaxDrop(c) * DropScale > CliffDropM * 0.8f)) hz = Hazard.Cliff;   // escarpe
            else if ((marks & 1) != 0 && rng.Chance(0.55f)) hz = Hazard.Debris;
            else if (soil == Soil.Urbano && City.Buildings.Count > 0) hz = !City.Street[i] && coverage[i] >= 0.15f ? Hazard.Building : Hazard.None;   // interior de manzana con edificios
            else if (soil == Soil.Urbano && rng.Chance(0.22f + Difficulty * 0.05f)) hz = Hazard.Building;
            else if (soil == Soil.Bosque && rng.Chance(0.2f + Difficulty * 0.05f + MathF.Max(0, noise.Fbm(x * 0.09f, y * 0.09f, 2)) * 0.3f)) hz = Hazard.Tree;
            else if (soil is Soil.SueloDesnudo or Soil.Matorral && rng.Chance(rockDensity * (0.6f + sl / 20f))) hz = Hazard.Rock;
            float roughBase = soil switch
            {
                Soil.Urbano => 0.12f, Soil.Bosque => 0.55f, Soil.Matorral => 0.45f, Soil.SueloDesnudo => 0.42f, Soil.Pastizal => 0.25f,
                Soil.Cultivo => 0.3f, Soil.Quemado => 0.4f, Soil.Hielo => 0.12f, Soil.Agua => 0.05f, _ => 0.3f,
            };
            Rough[i] = MathX.Clamp01(roughBase + rng.Gauss(0, 0.07f) + sl / 90f + ((marks & 1) != 0 ? 0.2f : 0));
            Albedo[i] = SoilAlbedo(soil) + rng.Gauss(0, 0.02f);
            Soils[i] = soil;
            Hazards[i] = hz;
            ObstacleH[i] = hz switch
            {
                Hazard.Rock => rng.Range(0.8f, 2.6f), Hazard.Building => cityH[i] > 0 ? cityH[i] : rng.Range(6f, 18f), Hazard.Tree => rng.Range(8f, 22f),
                Hazard.Debris => rng.Range(0.8f, 2.2f), _ => 0,
            };
        }

        // base: celda libre y plana más cercana a la esquina suroeste del polígono; estación a ~2/3 del alcance (≤ 70 celdas)
        bool Free(Cell c) => InBounds(c) && Hazards[Idx(c)] == Hazard.None && Slope[Idx(c)] < 10f;
        // componente transitable más grande (en montaña muchas zonas quedan aisladas por pendientes > 30°)
        var comp = new int[n * n];
        Array.Fill(comp, -1);
        int bestComp = -1, bestSize = 0, label = 0;
        for (int i0 = 0; i0 < n * n; i0++)
        {
            var c0 = new Cell(i0 % n, i0 / n);
            if (comp[i0] >= 0 || !Passable(c0)) continue;
            int size = 0;
            var q = new Queue<Cell>();
            q.Enqueue(c0);
            comp[i0] = label;
            while (q.Count > 0)
            {
                var c = q.Dequeue();
                size++;
                foreach (var d in Cell.Dirs8)
                {
                    var o = c + d;
                    if (!InBounds(o) || comp[Idx(o)] >= 0 || !Passable(o)) continue;
                    comp[Idx(o)] = label;
                    q.Enqueue(o);
                }
            }
            if (size > bestSize) { bestSize = size; bestComp = label; }
            label++;
        }
        Cell best = new(n / 2, n / 2);
        float bestScore = float.MaxValue;
        for (int y = 2; y < n - 2; y++)
        for (int x = 2; x < n - 2; x++)
        {
            var c = new Cell(x, y);
            if (!Free(c) || comp[Idx(c)] != bestComp) continue;
            int clear = Cell.Dirs8.Count(d => Free(c + d));
            float score = x + (n - 1 - y) + (8 - clear) * 6 + Slope[Idx(c)];
            if (score < bestScore) { bestScore = score; best = c; }
        }
        Start = best;
        ClearAround(Start, 1);
        Refuges.Add(Start);
        var dist = BfsDistance(Start, Passable);
        int maxD = dist.Max();
        int want = Math.Max(12, Math.Min(maxD * 2 / 3, 70));
        var station = Enumerable.Range(0, n * n).Where(i => dist[i] >= want - 4 && dist[i] <= want + 4 && Free(new Cell(i % n, i / n)))
            .Select(i => new Cell(i % n, i / n)).OrderBy(_ => rng.F()).FirstOrDefault(Start);
        if (station != Start)
        {
            ClearAround(station, 1);
            Refuges.Add(station);
        }

        // sitios científicos con el tipo del suelo real de la celda (preferencia: cicatrices, quemas, riberas)
        int count = 6 + Difficulty * 2;
        int spacing = Math.Max(6, Math.Min(want / 3, 18));
        bool Riverside(Cell c) => Cell.Dirs8.Any(d => HazardAt(c + d) == Hazard.Water);
        var cand = Enumerable.Range(0, n * n).Where(i => dist[i] > 6 && dist[i] < want * 1.4f + 10 && Free(new Cell(i % n, i / n)))
            .Select(i => (i, key: rng.F() - ((g.Marks?[i] ?? 0) != 0 ? 0.35f : 0) - (Riverside(new Cell(i % n, i / n)) ? 0.3f : 0)))
            .OrderBy(t => t.key).Select(t => t.i).ToList();
        foreach (int i in cand)
        {
            if (Sites.Count >= count) break;
            var c = new Cell(i % n, i / n);
            if (Refuges.Any(r => r.Dist(c) < 5) || Sites.Any(s => s.Cell.Dist(c) < spacing)) continue;
            byte marks = g.Marks?[i] ?? 0;
            string kind = (marks & 1) != 0 ? "Cicatriz de aluvión o remoción" : Riverside(c) ? "Ribera y sedimentos" : ScenarioSiteKind.GetValueOrDefault(Soils[i], "Punto de control");
            var site = new ScienceSite
            {
                Id = Sites.Count, Cell = c, Kind = kind,
                Value = MathF.Round(rng.Range(0.35f, 1f), 2), MassKg = MathF.Round(rng.Range(0.4f, 2.2f), 1), AnalysisWh = MathF.Round(rng.Range(2f, 6f), 1),
            };
            site.Name = $"S{site.Id + 1}";
            Sites.Add(site);
        }
        if (Sites.Count > 0 && rng.Chance(0.4f)) Sites[rng.Int(0, Sites.Count)].Anomaly = true;
        EnsureConnectivity();
        RecomputeCells();
        ComputeSlopeHistogram();
        Calibration = $"Escenario a escala real: {n}×{n} celdas de {CellM:0} m ({n * CellM / 1000f:0.0} km de lado) · relieve DEM GLO-30 decodificado del informe {g.Id}";
        Version++;
    }


    // ------------------------------------------------------------------ cambios en tiempo de ejecución
    /// <summary>Crea (o desplaza) un barranco: hunde el relieve de la celda y la marca como precipicio.</summary>
    public void AddCliff(Cell c)
    {
        if (!InBounds(c) || Refuges.Contains(c)) return;
        // −14 m en el centro: con la mezcla de bordes la caída efectiva desde una vecina queda ≈ 7 m (> umbral de 5 m)
        SetCellHeight(c, Height(c) - 14f);
        Hazards[Idx(c)] = ProbeMode ? Hazard.Downdraft : Hazard.Cliff;
        RecomputeCells();
        Version++;
    }

    public void AddRock(Cell c, float height = 1.8f)
    {
        if (!InBounds(c) || Refuges.Contains(c)) return;
        Hazards[Idx(c)] = ProbeMode ? Hazard.Storm : Hazard.Rock;
        ObstacleH[Idx(c)] = height;
        Version++;
    }

    public void ClearHazard(Cell c)
    {
        if (!InBounds(c)) return;
        if (ClassAt(c) == TerrainClass.Barranco)
            FillToNeighbors(c);
        Hazards[Idx(c)] = Hazard.None;
        RecomputeCells();
        Version++;
    }

    /// <summary>Impacto de meteorito: cráter nuevo con fondo de precipicio y eyecta de rocas.</summary>
    public List<Cell> Impact(Cell center, Rng rng)
    {
        var changed = new List<Cell>();
        for (int dy = -2; dy <= 2; dy++)
        for (int dx = -2; dx <= 2; dx++)
        {
            var c = new Cell(center.X + dx, center.Y + dy);
            if (!InBounds(c) || Refuges.Contains(c)) continue;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d <= 1.0f)
            {
                SetCellHeight(c, Height(c) - 13f);
                Hazards[Idx(c)] = Hazard.Crater;
                changed.Add(c);
            }
            else if (d <= 2.3f && rng.Chance(0.45f) && Hazards[Idx(c)] == Hazard.None)
            {
                Hazards[Idx(c)] = Hazard.Rock;
                ObstacleH[Idx(c)] = rng.Range(0.6f, 1.6f);
                changed.Add(c);
            }
        }
        RecomputeCells();
        Version++;
        return changed;
    }

    /// <summary>Remoción en masa (Tierra): las celdas empinadas y saturadas se convierten en detritos.</summary>
    public List<Cell> Landslide(Cell near, Rng rng, int radius = 6)
    {
        var changed = new List<Cell>();
        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            var c = new Cell(near.X + dx, near.Y + dy);
            if (!InBounds(c) || Refuges.Contains(c) || Hazards[Idx(c)] != Hazard.None) continue;
            if (Slope[Idx(c)] > 18f && rng.Chance(0.35f))
            {
                Hazards[Idx(c)] = Hazard.Debris;
                ObstacleH[Idx(c)] = rng.Range(0.8f, 2f);
                Soils[Idx(c)] = Soil.Barro;
                changed.Add(c);
            }
        }
        Version++;
        return changed;
    }

    /// <summary>Sismo: caída de rocas en laderas y agrietamiento de bordes de barranco.</summary>
    public List<Cell> QuakeAlter(Rng rng, float intensity)
    {
        var changed = new List<Cell>();
        int n = (int)(intensity * 6);
        for (int k = 0; k < n * 4 && changed.Count < n; k++)
        {
            var c = new Cell(rng.Int(1, N - 1), rng.Int(1, N - 1));
            if (!InBounds(c) || Hazards[Idx(c)] != Hazard.None || Refuges.Contains(c) || Sites.Any(s => s.Cell == c)) continue;
            if (Slope[Idx(c)] > 12f || MaxDrop(c) * DropScale > 2.5f)
            {
                Hazards[Idx(c)] = Hazard.Rock;
                ObstacleH[Idx(c)] = rng.Range(0.6f, 1.8f);
                changed.Add(c);
            }
        }
        if (changed.Count > 0) Version++;
        return changed;
    }
}
