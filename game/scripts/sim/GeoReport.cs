using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace AresX.Sim;

/// <summary>
/// Datos de un informe GeoClick (Copernicus DEM GLO-30, ESA WorldCover, Sentinel-2, ERA5): clima, pendientes,
/// cobertura y, cuando el escenario viene de un informe convertido, las grillas a escala real (celdas de 30 m).
/// <see cref="Santiago"/> es la calibración del terreno procedural de la Tierra (consulta 40e9efb1).
/// </summary>
public sealed class GeoReport
{
    public string Id = "40e9efb1", Period = "", SentinelDate = "", MaxDailyDate = "", SatelliteFile = "";
    public float AreaKm2 = 8.2f, Lat = -33.42f, Lon = -70.62f;
    public bool HasCoords = true, SyntheticSatellite;
    public float ElevMin = 525, ElevMax = 880, ElevMedian = 616, SlopeMedian = 6.7f, Km2Over25 = 1.3f;
    public float WaterKm2, ScarsHa = 0.8f, BurnedHa;
    public int Floods;
    public float AnnualRainMm = 488f, MaxDailyMm = 129.9f, TrendMmDecade = -61.5f, TempMeanC = 14.7f, TempTrendC = 0.52f, FrostDaysYear = 17.8f;

    /// <summary>Distribución de pendientes (fracción del área): 0–5, 5–15, 15–25, 25–35, &gt;35°.</summary>
    public float[] SlopeBins = { 0.43f, 0.23f, 0.19f, 0.15f, 0.01f };
    public static readonly string[] SlopeLabels = { "0–5°", "5–15°", "15–25°", "25–35°", ">35°" };

    /// <summary>Cobertura ESA WorldCover en km² por clase (nombre del informe).</summary>
    public Dictionary<string, float> Cover = new()
    {
        ["Urbano"] = 4.2f, ["Bosque"] = 2.49f, ["Matorral"] = 1.07f, ["Suelo desnudo"] = 0.23f, ["Pastizal"] = 0.19f, ["Agua"] = 0.02f, ["Cultivos"] = 0.01f,
    };

    /// <summary>Precipitación media mensual (mm), Ene..Dic.</summary>
    public float[] MonthlyRainMm = { 4.1f, 4.4f, 8.5f, 23.7f, 65.3f, 102.6f, 92.3f, 88.5f, 58.1f, 34.2f, 14.2f, 5.3f };

    /// <summary>Temperatura media mensual (°C), Ene..Dic (serie ERA5 de la figura del informe).</summary>
    public float[] MonthlyTempC = { 22.0f, 22.1f, 20.2f, 16.7f, 13.3f, 10.9f, 10.8f, 11.4f, 13.2f, 15.8f, 18.5f, 21.0f };

    /// <summary>Periodos de retorno de Gumbel: (años, lluvia en un día mm, en tres días mm).</summary>
    public (int Years, float Mm, float Mm3)[] ReturnPeriods =
        { (2, 49.2f, 78.2f), (5, 67.8f, 112.9f), (10, 80.1f, 136f), (25, 95.7f, 165f), (50, 107.2f, 186.6f), (100, 118.7f, 208f) };

    // ---- grillas a escala (solo escenarios convertidos)
    public int N;
    public float CellM = 5f;
    public float WidthM, HeightM;           // caja de datos real
    public float[]? Elev;                   // m sobre el nivel del mar, N×N (fila = sur→norte invertido como la imagen)
    public byte[]? CoverGrid, SlopeGrid, Marks;   // 255 = fuera del polígono · marcas: 1 cicatriz, 2 quemada
    public string[] SoilNames = Array.Empty<string>();

    public bool IsScenario => Elev != null;
    public string Consulta => $"GeoClick {Id} · {AreaKm2:0.0} km²" + (HasCoords ? $" · {Coords}" : "");
    public string Coords => $"{MathF.Abs(Lat):0.00}° {(Lat < 0 ? "S" : "N")} {MathF.Abs(Lon):0.00}° {(Lon < 0 ? "O" : "E")}";
    public string DominantCover => Cover.Count == 0 ? "" : Cover.OrderByDescending(kv => kv.Value).First().Key;

    public string Title
    {
        get
        {
            string relief = SlopeMedian > 20 ? "montaña" : SlopeMedian > 8 ? "lomaje" : "valle";
            return $"{Id} · {AreaKm2:0.#} km² · {relief} · {DominantCover.ToLowerInvariant()}";
        }
    }

    /// <summary>Calibración por defecto del terreno procedural de la Tierra (GeoClick 40e9efb1, Santiago).</summary>
    public static readonly GeoReport Santiago = new();

    /// <summary>Periodo de retorno aproximado (años) de una lluvia diaria, interpolando la tabla de Gumbel.</summary>
    public float ReturnYears(float dailyMm)
    {
        var rp = ReturnPeriods;
        if (rp.Length == 0) return 0;
        if (dailyMm < rp[0].Mm) return dailyMm / rp[0].Mm * rp[0].Years;
        for (int i = 1; i < rp.Length; i++)
            if (dailyMm <= rp[i].Mm)
            {
                var (y0, m0, _) = rp[i - 1];
                var (y1, m1, _) = rp[i];
                return y0 + (y1 - y0) * (dailyMm - m0) / (m1 - m0);
            }
        var last = rp[^1];
        return last.Years + (dailyMm - last.Mm) * 5f;
    }

    public float RainT(int years) => ReturnPeriods.FirstOrDefault(r => r.Years == years).Mm is var v && v > 0 ? v : ReturnPeriods.Length > 0 ? ReturnPeriods[^1].Mm : 80;

    // ------------------------------------------------------------------ carga del JSON del conversor
    public static GeoReport FromJson(string json, bool withGrids = true)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        float F(string k, float d = 0) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : d;
        string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        float[]? Arr(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(e => e.GetSingle()).ToArray() : null;
        var g = new GeoReport
        {
            Id = S("id"), Period = S("periodo"), SentinelDate = S("fecha_sentinel"), MaxDailyDate = S("max_diaria_fecha"),
            SatelliteFile = S("satelite"), AreaKm2 = F("area_km2"), ElevMin = F("elev_min"), ElevMax = F("elev_max"),
            ElevMedian = F("elev_mediana"), SlopeMedian = F("pend_mediana"), Km2Over25 = F("km2_sobre_25"), WaterKm2 = F("agua_km2"),
            ScarsHa = F("cicatrices_ha"), BurnedHa = F("quemadas_ha"), Floods = (int)F("inundaciones"), AnnualRainMm = F("lluvia_anual_mm", 500),
            MaxDailyMm = F("max_diaria_mm", 100), TrendMmDecade = F("tendencia_mm_decada"), TempMeanC = F("temp_media_c", 14),
            TempTrendC = F("temp_tendencia_c"), FrostDaysYear = F("heladas_anio"), N = (int)F("n"), CellM = F("celda_m", 30),
            WidthM = F("ancho_datos_m"), HeightM = F("alto_datos_m"),
            SyntheticSatellite = r.TryGetProperty("satelite_sintetico", out var ss) && ss.ValueKind == JsonValueKind.True,
        };
        if (r.TryGetProperty("lat", out var la) && la.ValueKind == JsonValueKind.Number)
        {
            g.Lat = la.GetSingle();
            g.Lon = F("lon");
        }
        else g.HasCoords = false;
        if (Arr("pend_clases") is { Length: 5 } pc) g.SlopeBins = pc;
        if (Arr("lluvia_mensual_mm") is { Length: 12 } lm) g.MonthlyRainMm = lm;
        if (Arr("temp_mensual_c") is { Length: 12 } tm) g.MonthlyTempC = tm;
        else
        {
            // sin serie mensual: media anual ± oscilación estacional (SUPUESTO, hemisferio sur: máximo en enero)
            float amp = 5.5f;
            g.MonthlyTempC = Enumerable.Range(0, 12).Select(m => g.TempMeanC + amp * MathF.Cos(m / 12f * MathF.Tau)).ToArray();
        }
        if (r.TryGetProperty("cobertura", out var cov) && cov.ValueKind == JsonValueKind.Object)
            g.Cover = cov.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetSingle());
        if (r.TryGetProperty("retorno", out var rp) && rp.ValueKind == JsonValueKind.Array)
            g.ReturnPeriods = rp.EnumerateArray().Select(e => (e[0].GetInt32(), e[1].GetSingle(), e[2].GetSingle())).ToArray();
        if (r.TryGetProperty("suelos", out var su))
            g.SoilNames = su.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        if (withGrids && g.N > 0)
        {
            int n = g.N;
            var raw = Inflate(S("elev_dm"));
            g.Elev = new float[n * n];
            for (int i = 0; i < n * n; i++) g.Elev[i] = g.ElevMin + BitConverter.ToUInt16(raw, i * 2) / 10f;
            g.CoverGrid = Inflate(S("cobertura_grid"));
            g.SlopeGrid = Inflate(S("pendiente_grid"));
            g.Marks = Inflate(S("marcas_grid"));
        }
        return g;
    }

    private static byte[] Inflate(string b64)
    {
        var data = Convert.FromBase64String(b64);
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Suelo del juego para la clase de cobertura de una celda.</summary>
    public Soil SoilOf(byte cover) => cover >= SoilNames.Length ? Soil.SueloDesnudo : SoilNames[cover] switch
    {
        "Bosque" => Soil.Bosque, "Matorral" => Soil.Matorral, "Pastizal" => Soil.Pastizal, "Cultivo" => Soil.Cultivo,
        "Urbano" => Soil.Urbano, "SueloDesnudo" => Soil.SueloDesnudo, "Hielo" => Soil.Hielo, "Agua" => Soil.Agua, "Barro" => Soil.Barro,
        _ => Soil.SueloDesnudo,
    };
}
