using System;
using System.Collections.Generic;
using System.Linq;
using AresX.Core;

namespace AresX.Sim;

/// <summary>Tipos de edificio (los 16 bocetos del proyecto de mechas, tools/buildings25d.py).</summary>
public enum BuildingType
{
    Casa, Pareadas, Bloque, TorreResidencial, TorreOficinas, Mall, Galpon, Bencinera, Iglesia, Colegio, Hospital, Metro,
    Estacionamiento, Historico, Estanque, Autopista,
}

public sealed class CityBuilding
{
    public BuildingType Type;
    public float X, Z;          // centro en metros del mapa (origen en la esquina de la celda 0,0)
    public float YawDeg;        // giro (la fachada principal mira a la calle)
    public int Seed;
    public float HeightM;
}

/// <summary>
/// CIUDAD SIMULADA sobre la mancha urbana de ESA WorldCover de un escenario (no es la ciudad real: reproduce su
/// distribución). Barrios de ~1,2 km con orientación propia; avenidas cada 400 m y calles cada 100 m; manzanas
/// subdivididas en parcelas según la zonificación (torres y comercio en el centro denso, departamentos alrededor,
/// casas en la periferia, galpones junto a cultivos y suelo desnudo); equipamiento por superficie (hospitales con
/// helipuerto, colegios, iglesias, bencineras, malls, estanques). Determinista por escenario.
/// En la simulación: las celdas de 30 m con calle son transitables; las cubiertas por edificios, obstáculos.
/// </summary>
public sealed class CityPlan
{
    public readonly List<CityBuilding> Buildings = new();
    public readonly List<int> Hospitals = new();        // índices en Buildings
    public bool[] Street = Array.Empty<bool>();          // por celda
    public float UrbanKm2;

    public static float Height(BuildingType t) => t switch
    {
        BuildingType.Casa => 6, BuildingType.Pareadas => 8, BuildingType.Bloque => 11, BuildingType.TorreResidencial => 65,
        BuildingType.TorreOficinas => 103, BuildingType.Mall => 17, BuildingType.Galpon => 22, BuildingType.Bencinera => 13,
        BuildingType.Iglesia => 34, BuildingType.Colegio => 7, BuildingType.Hospital => 33, BuildingType.Metro => 18,
        BuildingType.Estacionamiento => 14, BuildingType.Historico => 19, BuildingType.Estanque => 40, _ => 12,
    };

    /// <summary>Huella (ancho x fondo, m) del tipo; +fondo = hacia atrás de la fachada.</summary>
    public static (float W, float D) Footprint(BuildingType t) => t switch
    {
        BuildingType.Casa => (16, 18), BuildingType.Pareadas => (17, 11), BuildingType.Bloque => (33, 15),
        BuildingType.TorreResidencial => (22, 24), BuildingType.TorreOficinas => (40, 35), BuildingType.Mall => (92, 82),
        BuildingType.Galpon => (62, 32), BuildingType.Bencinera => (30, 36), BuildingType.Iglesia => (30, 60),
        BuildingType.Colegio => (46, 42), BuildingType.Hospital => (50, 32), BuildingType.Metro => (20, 90),
        BuildingType.Estacionamiento => (46, 32), BuildingType.Historico => (32, 22), BuildingType.Estanque => (36, 14),
        _ => (20, 20),
    };

    public static string Name(BuildingType t) => t switch
    {
        BuildingType.Casa => "casa", BuildingType.Pareadas => "casas pareadas", BuildingType.Bloque => "bloque de departamentos",
        BuildingType.TorreResidencial => "torre residencial", BuildingType.TorreOficinas => "torre de oficinas", BuildingType.Mall => "centro comercial",
        BuildingType.Galpon => "galpón industrial", BuildingType.Bencinera => "bencinera", BuildingType.Iglesia => "iglesia",
        BuildingType.Colegio => "colegio", BuildingType.Hospital => "hospital", BuildingType.Metro => "estación de metro",
        BuildingType.Estacionamiento => "estacionamiento", BuildingType.Historico => "edificio histórico", BuildingType.Estanque => "estanque de agua",
        _ => t.ToString(),
    };

    /// <summary>Genera la ciudad sobre las celdas urbanas del mapa (antes de los peligros del resto del terreno).</summary>
    public static CityPlan Generate(WorldMap map, ulong seed)
    {
        var plan = new CityPlan();
        int n = map.N;
        float cm = map.CellM;
        var urban = new bool[n * n];
        for (int i = 0; i < n * n; i++) urban[i] = map.Inside![i] && map.Soils[i] == Soil.Urbano;
        int urbanCells = urban.Count(u => u);
        plan.UrbanKm2 = urbanCells * cm * cm / 1e6f;
        plan.Street = new bool[n * n];
        if (urbanCells < 12) return plan;
        var rng = new Rng(seed);

        // densidad urbana (fracción urbana en ~300 m) → zonificación
        int rad = Math.Max(2, (int)(300 / cm));
        var dens = BoxMean(urban, n, rad);
        var industrialNear = BoxMean(Enumerable.Range(0, n * n).Select(i => map.Inside![i] && map.Soils[i] is Soil.Cultivo or Soil.SueloDesnudo).ToArray(), n, Math.Max(1, (int)(120 / cm)));

        // barrios de ~1,2 km con su orientación de calles
        const float District = 1200f;
        int dCount = (int)Math.Ceiling(n * cm / District);
        var angle = new float[dCount * dCount];
        for (int i = 0; i < angle.Length; i++) angle[i] = rng.Range(0f, 90f);

        // celdas de calle: en el marco girado de su barrio, cerca de una línea de calle (cada 100 m) o avenida (400 m)
        bool OnStreet(float mx, float mz, out bool avenue)
        {
            int dx = Math.Clamp((int)(mx / District), 0, dCount - 1), dz = Math.Clamp((int)(mz / District), 0, dCount - 1);
            float a = angle[dz * dCount + dx] * MathF.PI / 180f;
            float u = mx * MathF.Cos(a) + mz * MathF.Sin(a), v = -mx * MathF.Sin(a) + mz * MathF.Cos(a);
            float du = MathF.Abs(u - MathF.Round(u / 100f) * 100f), dv = MathF.Abs(v - MathF.Round(v / 100f) * 100f);
            float au = MathF.Abs(u - MathF.Round(u / 400f) * 400f), av = MathF.Abs(v - MathF.Round(v / 400f) * 400f);
            avenue = au < 14 || av < 14;
            return du < 7 || dv < 7 || avenue;
        }
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int i = y * n + x;
            if (!urban[i]) continue;
            // una celda de 30 m es "calle" si alguna de sus 9 submuestras cae en una calle (la red queda conectada)
            bool st = false;
            for (int sy = 0; sy < 3 && !st; sy++)
            for (int sx = 0; sx < 3 && !st; sx++)
                st = OnStreet((x + (sx + 0.5f) / 3) * cm, (y + (sy + 0.5f) / 3) * cm, out _);
            plan.Street[i] = st;
        }

        // parcelas: recorre cada manzana del marco girado de cada barrio en pasos de la huella del tipo zonificado
        var occupied = new HashSet<long>();
        bool Free(float mx, float mz, float w, float d, float yawDeg)
        {
            // las cuatro esquinas y el centro deben caer en suelo urbano, fuera de la calle y sin otro edificio
            float a = yawDeg * MathF.PI / 180f, c = MathF.Cos(a), s = MathF.Sin(a);
            foreach (var (ox, oz) in new[] { (0f, 0f), (-w / 2, -d / 2), (w / 2, -d / 2), (-w / 2, d / 2), (w / 2, d / 2) })
            {
                float px = mx + ox * c - oz * s, pz = mz + ox * s + oz * c;
                int cx = (int)(px / cm), cz = (int)(pz / cm);
                if (cx < 0 || cz < 0 || cx >= n || cz >= n || !urban[cz * n + cx]) return false;
                if (OnStreet(px, pz, out _)) return false;
                if (occupied.Contains(((long)(px / 6)) << 20 | (long)(pz / 6))) return false;
            }
            return true;
        }
        void Mark(float mx, float mz, float w, float d, float yawDeg)
        {
            float a = yawDeg * MathF.PI / 180f, c = MathF.Cos(a), s = MathF.Sin(a);
            for (float ox = -w / 2; ox <= w / 2; ox += 5)
            for (float oz = -d / 2; oz <= d / 2; oz += 5)
            {
                float px = mx + ox * c - oz * s, pz = mz + ox * s + oz * c;
                occupied.Add(((long)(px / 6)) << 20 | (long)(pz / 6));
            }
        }
        void Add(BuildingType t, float mx, float mz, float yaw)
        {
            var (w, d) = Footprint(t);
            Mark(mx, mz, w, d, yaw);
            plan.Buildings.Add(new CityBuilding { Type = t, X = mx, Z = mz, YawDeg = yaw, Seed = rng.Int(0, 1 << 30), HeightM = Height(t) });
        }

        // equipamiento primero (necesita parcelas grandes): cantidad por superficie urbana
        float km2 = plan.UrbanKm2;
        var special = new List<BuildingType>();
        for (int k = 0; k < Math.Max(1, (int)MathF.Round(km2 / 5f)); k++) special.Add(BuildingType.Hospital);
        for (int k = 0; k < (int)MathF.Round(km2 * 0.8f); k++) special.Add(BuildingType.Colegio);
        for (int k = 0; k < (int)MathF.Round(km2 * 0.5f); k++) special.Add(BuildingType.Iglesia);
        for (int k = 0; k < (int)MathF.Round(km2 * 0.8f); k++) special.Add(BuildingType.Bencinera);
        for (int k = 0; k < (int)MathF.Round(km2 / 6f); k++) special.Add(BuildingType.Mall);
        for (int k = 0; k < (int)MathF.Round(km2 / 4f); k++) special.Add(BuildingType.Estanque);
        for (int k = 0; k < (int)MathF.Round(km2 / 3f); k++) special.Add(BuildingType.Estacionamiento);
        for (int k = 0; k < (int)MathF.Round(km2 / 4f); k++) special.Add(BuildingType.Historico);
        var urbanIdx = Enumerable.Range(0, n * n).Where(i => urban[i] && !plan.Street[i]).ToList();
        foreach (var t in special)
        {
            var (w, d) = Footprint(t);
            for (int tries = 0; tries < 200; tries++)
            {
                int i = urbanIdx[rng.Int(0, urbanIdx.Count)];
                // los grandes prefieren el centro denso; las bencineras, junto a avenidas
                if (t is BuildingType.Mall or BuildingType.Hospital && dens[i] < 0.55f && tries < 150) continue;
                float mx = (i % n + 0.5f) * cm, mz = (i / n + 0.5f) * cm;
                float yaw = BlockYaw(mx, mz) + (rng.Chance(0.5f) ? 0 : 180);
                if (!Free(mx, mz, w, d, yaw)) continue;
                if (t == BuildingType.Hospital) plan.Hospitals.Add(plan.Buildings.Count);
                Add(t, mx, mz, yaw);
                break;
            }
        }

        float BlockYaw(float mx, float mz)
        {
            int dx = Math.Clamp((int)(mx / District), 0, dCount - 1), dz = Math.Clamp((int)(mz / District), 0, dCount - 1);
            return angle[dz * dCount + dx];
        }

        // centroide y radio de la mancha urbana (para el núcleo de torres)
        float ucx = 0, ucz = 0;
        foreach (int i in Enumerable.Range(0, n * n).Where(i => urban[i])) { ucx += i % n; ucz += i / n; }
        ucx /= urbanCells; ucz /= urbanCells;
        float urad = MathF.Max(4, MathF.Sqrt(urbanCells / MathF.PI) * 1.1f);
        // zonificación por percentiles de (densidad + centralidad): 6 % núcleo de torres, 20 % anillo de bloques
        var zscore = new float[n * n];
        var ranked = new List<float>();
        for (int i = 0; i < n * n; i++)
        {
            if (!urban[i]) continue;
            float core0 = 1f - MathX.Clamp01(MathF.Sqrt((i % n - ucx) * (i % n - ucx) + (i / n - ucz) * (i / n - ucz)) / urad);
            zscore[i] = 0.55f * dens[i] + 0.45f * core0;
            ranked.Add(zscore[i]);
        }
        ranked.Sort();
        float pTower = ranked[(int)(ranked.Count * 0.94f)], pBlock = ranked[(int)(ranked.Count * 0.74f)];

        // relleno: barrido por barrio en el marco girado, con paso = huella del tipo de la zona
        for (int dz = 0; dz < dCount; dz++)
        for (int dx = 0; dx < dCount; dx++)
        {
            float a = angle[dz * dCount + dx], ar = a * MathF.PI / 180f, c = MathF.Cos(ar), s = MathF.Sin(ar);
            float cx0 = (dx + 0.5f) * District, cz0 = (dz + 0.5f) * District;
            for (float v = -District; v < District; v += 0)
            {
                float rowStep = 22;
                for (float u = -District; u < District; u += 0)
                {
                    float mx = cx0 + u * c - v * s, mz = cz0 + u * s + v * c;
                    int gx = (int)(mx / cm), gz = (int)(mz / cm);
                    float step = 18;
                    if (gx >= 0 && gz >= 0 && gx < n && gz < n && (int)(mx / District) == dx && (int)(mz / District) == dz && urban[gz * n + gx])
                    {
                        int gi = gz * n + gx;
                        float de = dens[gi];
                        // núcleo (percentil 94 de densidad + centralidad) → torres; anillo (p74) → bloques; periferia → casas
                        float zs = zscore[gi];
                        float r01 = rng.F();
                        BuildingType t = industrialNear[gi] > 0.25f && de < 0.7f && r01 < 0.55f ? BuildingType.Galpon
                            : zs >= pTower ? (r01 < 0.25f ? BuildingType.TorreOficinas : r01 < 0.6f ? BuildingType.TorreResidencial : r01 < 0.9f ? BuildingType.Bloque : BuildingType.Pareadas)
                            : zs >= pBlock ? (r01 < 0.06f ? BuildingType.TorreResidencial : r01 < 0.45f ? BuildingType.Bloque : r01 < 0.75f ? BuildingType.Pareadas : BuildingType.Casa)
                            : r01 < 0.65f ? BuildingType.Casa : r01 < 0.95f ? BuildingType.Pareadas : BuildingType.Bloque;
                        var (w, d) = Footprint(t);
                        float yaw = a + (MathF.Abs(v - MathF.Round(v / 100f) * 100f) < 50 ? 0 : 180);
                        if (Free(mx, mz, w, d, yaw) && rng.Chance(0.92f))
                        {
                            Add(t, mx, mz, yaw);
                            step = w + 3;
                            rowStep = MathF.Max(rowStep, d + 4);
                        }
                        else step = 8;
                    }
                    else step = 30;
                    u += step;
                }
                v += rowStep;
            }
        }
        return plan;
    }

    private static float[] BoxMean(bool[] m, int n, int r)
    {
        var integ = new int[(n + 1) * (n + 1)];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
            integ[(y + 1) * (n + 1) + x + 1] = (m[y * n + x] ? 1 : 0) + integ[y * (n + 1) + x + 1] + integ[(y + 1) * (n + 1) + x] - integ[y * (n + 1) + x];
        var o = new float[n * n];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int x0 = Math.Max(0, x - r), y0 = Math.Max(0, y - r), x1 = Math.Min(n, x + r + 1), y1 = Math.Min(n, y + r + 1);
            int s = integ[y1 * (n + 1) + x1] - integ[y0 * (n + 1) + x1] - integ[y1 * (n + 1) + x0] + integ[y0 * (n + 1) + x0];
            o[y * n + x] = s / (float)((x1 - x0) * (y1 - y0));
        }
        return o;
    }

    /// <summary>Cubre las celdas del mapa: fracción de cada celda ocupada por edificios (muestreo 3×3 por celda).</summary>
    public float[] CellCoverage(int n, float cm, out float[] height)
    {
        var cov = new float[n * n];
        height = new float[n * n];
        foreach (var b in Buildings)
        {
            var (w, d) = Footprint(b.Type);
            float a = b.YawDeg * MathF.PI / 180f, c = MathF.Cos(a), s = MathF.Sin(a);
            for (float ox = -w / 2 + 2.5f; ox < w / 2; ox += 5)
            for (float oz = -d / 2 + 2.5f; oz < d / 2; oz += 5)
            {
                float px = b.X + ox * c - oz * s, pz = b.Z + ox * s + oz * c;
                int cx = (int)(px / cm), cz = (int)(pz / cm);
                if (cx < 0 || cz < 0 || cx >= n || cz >= n) continue;
                cov[cz * n + cx] += 25f / (cm * cm);
                height[cz * n + cx] = MathF.Max(height[cz * n + cx], b.HeightM);
            }
        }
        return cov;
    }
}
