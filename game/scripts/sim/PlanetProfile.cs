using System.Collections.Generic;

namespace AresX.Sim;

public enum TerrainStyle { Mercurio, Venus, Tierra, Marte, Gaseoso }

/// <summary>
/// Perfil de un cuerpo del sistema solar. Los valores "de referencia" son aproximaciones de NASA Planetary Fact Sheet /
/// NASA Science (no son cotas de diseño). Todo lo marcado como SUPUESTO es un parámetro pedagógico de la simulación.
/// La Tierra está calibrada con la consulta GeoClick 40e9efb1 (Santiago, Cerro San Cristóbal, 8,2 km²).
/// </summary>
public sealed class PlanetProfile
{
    public string Id = "", Name = "", Summary = "";
    public bool SolidSurface = true;
    public TerrainStyle Style;

    // ---- de referencia (aprox.)
    public float Gravity;              // m/s²
    public float DayHours;             // día solar real (h)
    public float TempMeanC, TempAmpC;  // media y semiamplitud día/noche (°C)
    public float PressureBar;          // superficie (rocosos) o nivel 1 bar (gigantes)
    public float ScaleHeightKm;        // altura de escala de la presión
    public float AirDensity;           // kg/m³ en superficie
    public float O2Pct, CO2Pct, SO2Ppm, N2Pct, H2Pct, HePct, CH4Pct, ArPct;
    public float WindMean, WindGust;   // m/s (superficie o capa de la sonda)
    public float SolarTopWm2;          // irradiancia solar en el tope de la atmósfera (W/m²)
    public float SurfaceSolarFactor;   // fracción que llega a la superficie con cielo despejado
    public float RadiationMsvH;        // dosis en superficie (SUPUESTO calibrado con datos públicos)
    public float LightTimeMin;         // latencia de enlace con la Tierra, ida (minutos, media)

    // ---- SUPUESTOS de simulación
    public float SimDayMinutes = 60;   // día comprimido: un día del cuerpo dura esto en tiempo de misión
    public float DustBase;             // opacidad de polvo/bruma base 0..1
    public float HardwareMinC = -40, HardwareMaxC = 85;  // tolerancia térmica interna
    public float ThermalTauS = 9000;   // constante de tiempo de aislamiento (s)
    public float CorrosionPerStep;     // pérdida de integridad por paso (Venus)
    public float WindLimit;            // límite operacional de viento (m/s)
    public bool HasRain, HasDustStorms, HasQuakes, HasIce;
    public float QuakeChance;          // por paso
    public string SkyDay = "8fc3ea", SkyNight = "0a0e1a", Fog = "", SunColor = "fff4e0";
    public string[] Notes = System.Array.Empty<string>();

    public float SunMultiplier => SolarTopWm2 * SurfaceSolarFactor;

    public static readonly List<PlanetProfile> All = new()
    {
        new PlanetProfile
        {
            Id = "mercurio", Name = "Mercurio", Style = TerrainStyle.Mercurio,
            Summary = "Vacío casi total; contraste térmico extremo entre día y noche. Rover hipotético.",
            Gravity = 3.70f, DayHours = 4222.6f, TempMeanC = 125, TempAmpC = 305, PressureBar = 5e-15f, ScaleHeightKm = 0,
            AirDensity = 0, O2Pct = 0, CO2Pct = 0, WindMean = 0, WindGust = 0,
            SolarTopWm2 = 9082, SurfaceSolarFactor = 1f, RadiationMsvH = 0.30f, LightTimeMin = 7.5f,
            SimDayMinutes = 120, DustBase = 0.02f, HardwareMinC = -40, HardwareMaxC = 85, ThermalTauS = 14000,
            WindLimit = 99, HasQuakes = false, HasIce = true,
            SkyDay = "05060a", SkyNight = "000000", SunColor = "ffffff",
            Notes = new[] { "Exosfera: los sensores de gas leen bajo el límite de detección.", "Día solar ≈ 176 días terrestres; comprimido a 120 min (SUPUESTO)." },
        },
        new PlanetProfile
        {
            Id = "venus", Name = "Venus", Style = TerrainStyle.Venus,
            Summary = "≈464 °C, ≈92 bar, nubes de ácido sulfúrico. Solo rover especial hipotético y misión breve.",
            Gravity = 8.87f, DayHours = 2802f, TempMeanC = 464, TempAmpC = 2, PressureBar = 92f, ScaleHeightKm = 15.9f,
            AirDensity = 65f, CO2Pct = 96.5f, N2Pct = 3.5f, SO2Ppm = 150, WindMean = 0.6f, WindGust = 2f,
            SolarTopWm2 = 2601, SurfaceSolarFactor = 0.01f, RadiationMsvH = 0.0001f, LightTimeMin = 6f,
            SimDayMinutes = 240, DustBase = 0.35f, HardwareMinC = -40, HardwareMaxC = 500, ThermalTauS = 20000,
            CorrosionPerStep = 0.12f, WindLimit = 4,
            SkyDay = "d9a441", SkyNight = "4a2e10", Fog = "e0b060", SunColor = "ffd890",
            Notes = new[] { "Electrónica de SiC hipotética (límite interno 500 °C, SUPUESTO).", "La lluvia ácida se evapora antes del suelo (virga): no se simula en superficie.", "Corrosión: la integridad baja en cada paso → límite de exposición." },
        },
        new PlanetProfile
        {
            Id = "tierra", Name = "Tierra · GeoClick Santiago", Style = TerrainStyle.Tierra,
            Summary = "Banco de calibración: lluvia, sismos y remociones en masa. Relieve y clima de la consulta GeoClick 40e9efb1.",
            Gravity = 9.81f, DayHours = 24, TempMeanC = 14.7f, TempAmpC = 7.5f, PressureBar = 0.946f, ScaleHeightKm = 8.5f,
            AirDensity = 1.12f, O2Pct = 20.95f, CO2Pct = 0.042f, N2Pct = 78.08f, ArPct = 0.93f, WindMean = 3f, WindGust = 9f,
            SolarTopWm2 = 1361, SurfaceSolarFactor = 0.75f, RadiationMsvH = 0.0001f, LightTimeMin = 0f,
            SimDayMinutes = 60, DustBase = 0.06f, HardwareMinC = -40, HardwareMaxC = 85, ThermalTauS = 9000,
            WindLimit = 22, HasRain = true, HasQuakes = true, QuakeChance = 0.002f,
            SkyDay = "8fc3ea", SkyNight = "0a1020", SunColor = "fff4e0",
            Notes = new[] { "Elevación 525–880 m; pendiente mediana 6,7° (GeoClick).", "Lluvia normal 488 mm/año; máx. diaria 129,9 mm (04-07-1984).", "Temperatura media 14,7 °C; 17,8 heladas/año." },
        },
        new PlanetProfile
        {
            Id = "marte", Name = "Marte", Style = TerrainStyle.Marte,
            Summary = "Frío, baja presión, CO₂ y polvo. Rover principal: barrancos, tormentas de polvo, hielo, noche.",
            Gravity = 3.71f, DayHours = 24.66f, TempMeanC = -60, TempAmpC = 45, PressureBar = 0.0061f, ScaleHeightKm = 11.1f,
            AirDensity = 0.020f, O2Pct = 0.16f, CO2Pct = 95.1f, N2Pct = 2.6f, ArPct = 1.9f, WindMean = 6f, WindGust = 18f,
            SolarTopWm2 = 589, SurfaceSolarFactor = 0.85f, RadiationMsvH = 0.028f, LightTimeMin = 12.5f,
            SimDayMinutes = 60, DustBase = 0.12f, HardwareMinC = -40, HardwareMaxC = 85, ThermalTauS = 9000,
            WindLimit = 30, HasDustStorms = true, HasQuakes = true, QuakeChance = 0.0015f, HasIce = true,
            SkyDay = "c9906a", SkyNight = "140c10", Fog = "c08860", SunColor = "ffe2c0",
            Notes = new[] { "O₂ ambiental ≈ 0,16 % (dato de referencia); el rover no respira oxígeno.", "Dosis ≈ 0,67 mSv/día (MSL-RAD) → 0,028 mSv/h." },
        },
        Giant("jupiter", "Júpiter", "Gigante gaseoso sin suelo accesible estable. Sonda atmosférica: turbulencia y presión creciente.",
              24.79f, 9.93f, -108, 27f, 0.16f, 90f, 10f, 0, 120, 180, 50.5f, 10f, 43f, "c8a27a", "3a2a20"),
        Giant("saturno", "Saturno", "Gigante gaseoso. Sonda atmosférica: vientos extremos, presión y comunicaciones.",
              10.44f, 10.7f, -139, 59.5f, 0.19f, 96.3f, 3.25f, 0.45f, 250, 500, 14.9f, 0.5f, 80f, "e2c98e", "2a2414"),
        Giant("urano", "Urano", "Gigante helado sin suelo de rodadura. Sonda atmosférica: frío, atmósfera y energía.",
              8.69f, 17.24f, -195, 27.7f, 0.42f, 82.5f, 15.2f, 2.3f, 100, 250, 3.7f, 0.05f, 160f, "a8dce0", "102428"),
        Giant("neptuno", "Neptuno", "Gigante helado. Sonda atmosférica: frío extremo, vientos y latencia.",
              11.15f, 16.11f, -201, 19.7f, 0.45f, 80f, 19f, 1.5f, 300, 600, 1.5f, 0.05f, 250f, "4870d8", "0a1430"),
    };

    private static PlanetProfile Giant(string id, string name, string summary, float g, float dayH, float t1bar,
        float scaleH, float rho, float h2, float he, float ch4, float wind, float gust, float solar, float rad,
        float lightMin, string sky, string skyNight) => new()
    {
        Id = id, Name = name, Summary = summary, SolidSurface = false, Style = TerrainStyle.Gaseoso,
        Gravity = g, DayHours = dayH, TempMeanC = t1bar, TempAmpC = 3, PressureBar = 1f, ScaleHeightKm = scaleH,
        AirDensity = rho, H2Pct = h2, HePct = he, CH4Pct = ch4, WindMean = wind, WindGust = gust,
        SolarTopWm2 = solar, SurfaceSolarFactor = 0.6f, RadiationMsvH = rad, LightTimeMin = lightMin,
        SimDayMinutes = 40, DustBase = 0.25f, HardwareMinC = -230, HardwareMaxC = 85, ThermalTauS = 9000,
        WindLimit = gust * 1.1f,
        SkyDay = sky, SkyNight = skyNight, Fog = sky, SunColor = "fff8e8",
        Notes = new[] { "Sin superficie sólida: el modo rover de ruedas está bloqueado (CA-07).", "Mapa = capa de nubes a ~1 bar; obstáculo = celda de tormenta; 'barranco' = corriente descendente (SUPUESTO).", "La sonda deriva con las ráfagas (desviación de trayectoria)." },
    };

    public static PlanetProfile Get(string id) => All.Find(p => p.Id == id) ?? All[3];
}

/// <summary>
/// Calibración terrestre tomada de la consulta GeoClick 40e9efb1 (07-11-2026). Se usa para generar el relieve, la
/// cobertura de suelo y el régimen de lluvias del perfil Tierra.
/// </summary>
public static class GeoClick
{
    public const string Consulta = "GeoClick 40e9efb1 · 8,2 km² · Santiago (33,42° S 70,62° O)";
    public const float ElevMin = 525, ElevMax = 880, ElevMedian = 616, SlopeMedian = 6.7f;

    /// <summary>Distribución de pendientes (fracción del área): 0–5, 5–15, 15–25, 25–35, &gt;35°.</summary>
    public static readonly float[] SlopeBins = { 0.43f, 0.23f, 0.19f, 0.15f, 0.01f };
    public static readonly string[] SlopeLabels = { "0–5°", "5–15°", "15–25°", "25–35°", ">35°" };

    /// <summary>Cobertura ESA WorldCover: urbano, bosque, matorral, suelo desnudo, pastizal, agua, cultivos.</summary>
    public static readonly float[] Cover = { 0.51f, 0.30f, 0.13f, 0.03f, 0.02f, 0.003f, 0.001f };

    /// <summary>Precipitación media mensual (mm), Ene..Dic (ERA5 1981–2026).</summary>
    public static readonly float[] MonthlyRainMm = { 4.1f, 4.4f, 8.5f, 23.7f, 65.3f, 102.6f, 92.3f, 88.5f, 58.1f, 34.2f, 14.2f, 5.3f };

    /// <summary>Temperatura media mensual aproximada (°C), Ene..Dic (figura 10 del informe).</summary>
    public static readonly float[] MonthlyTempC = { 22.0f, 21.3f, 19.0f, 15.5f, 12.0f, 10.0f, 9.6f, 10.8f, 12.8f, 15.6f, 18.4f, 21.0f };

    /// <summary>Periodos de retorno de Gumbel: (años, lluvia en un día mm).</summary>
    public static readonly (int Years, float Mm)[] ReturnPeriods = { (2, 49.2f), (5, 67.8f), (10, 80.1f), (25, 95.7f), (50, 107.2f), (100, 118.7f) };

    public const float AnnualRainMm = 488f, MaxDailyMm = 129.9f, FrostDaysYear = 17.8f, ScarsHa = 0.8f;

    public static string MonthName(int m) =>
        new[] { "Ene", "Feb", "Mar", "Abr", "May", "Jun", "Jul", "Ago", "Sep", "Oct", "Nov", "Dic" }[(m - 1 + 12) % 12];

    /// <summary>Periodo de retorno aproximado (años) de una lluvia diaria, interpolando la tabla de Gumbel.</summary>
    public static float ReturnYears(float dailyMm)
    {
        if (dailyMm < ReturnPeriods[0].Mm)
            return dailyMm / ReturnPeriods[0].Mm * 2f;
        for (int i = 1; i < ReturnPeriods.Length; i++)
            if (dailyMm <= ReturnPeriods[i].Mm)
            {
                var (y0, m0) = ReturnPeriods[i - 1];
                var (y1, m1) = ReturnPeriods[i];
                return y0 + (y1 - y0) * (dailyMm - m0) / (m1 - m0);
            }
        return 100f + (dailyMm - 118.7f) * 5f;
    }
}
