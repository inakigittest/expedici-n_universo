using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using AresX.Sim;
using Godot;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace AresX.Core;

/// <summary>Resultado de importar un informe: escenario guardado y validación frente a las cifras del informe.</summary>
public sealed class GeoImportResult
{
    public bool Ok;
    public string Id = "", Error = "", JsonPath = "";
    public float AreaReportKm2, AreaRebuiltKm2, WidthM, HeightM, SlopeMedianCalc, SlopeMedianReport;
    public int N;
    public float CellM = 30;
    public bool HasCoords, HasTemps, SyntheticSatellite;
    public readonly List<string> Layers = new(), Notes = new();

    public string Summary => !Ok ? $"No se pudo importar: {Error}" :
        $"Informe {Id}: {N}×{N} celdas de {CellM:0} m ({WidthM / 1000:0.0} × {HeightM / 1000:0.0} km) · área reconstruida {AreaRebuiltKm2:0.0} km² " +
        $"de {AreaReportKm2:0.0} km² del informe ({(AreaRebuiltKm2 / MathF.Max(0.01f, AreaReportKm2) - 1) * 100:+0.0;-0.0} %) · pendiente mediana " +
        $"{SlopeMedianCalc:0.0}° (informe {SlopeMedianReport:0.0}°) · capas: {string.Join(", ", Layers)}" +
        (HasCoords ? "" : " · sin coordenadas: escala por barra gráfica") + (HasTemps ? "" : " · temperatura mensual estimada") +
        (SyntheticSatellite ? " · imagen satelital vacía: textura sintética" : "") + (Notes.Count > 0 ? " · " + string.Join(" · ", Notes) : "");
}

/// <summary>
/// Importa un informe PDF de GeoClick dentro del juego (port del conversor tools/geoclick_escenario.py) y lo guarda
/// como escenario en user://escenarios: decodifica la elevación (viridis sobre el rango del informe), la cobertura
/// ESA WorldCover, las pendientes, cicatrices y quemas y la imagen Sentinel-2; el tamaño real sale de las marcas de
/// coordenadas de las figuras o de la barra de escala. Todas las capas comparten los límites (mismas coordenadas
/// normalizadas de imagen). Lector de PDF: PdfPig (Apache 2.0).
/// </summary>
public static class GeoClickPdf
{
    public const string UserDir = "user://escenarios/";
    public const int MaxCells = 360;
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    private static readonly (string Name, int R, int G, int B, string Soil)[] Esa =
    {
        ("Bosque", 0, 100, 0, "Bosque"), ("Matorral", 255, 187, 34, "Matorral"), ("Pastizal", 255, 255, 76, "Pastizal"),
        ("Cultivos", 240, 150, 255, "Cultivo"), ("Urbano", 250, 0, 0, "Urbano"), ("Suelo desnudo", 180, 180, 180, "SueloDesnudo"),
        ("Nieve y hielo", 240, 240, 240, "Hielo"), ("Agua", 0, 100, 200, "Agua"), ("Humedal", 0, 150, 160, "Barro"),
        ("Manglar", 0, 207, 117, "Bosque"), ("Musgo y liquen", 250, 230, 160, "Pastizal"),
    };
    private static readonly string[] Soils = { "Bosque", "Matorral", "Pastizal", "Cultivo", "Urbano", "SueloDesnudo", "Hielo", "Agua", "Barro" };
    private static readonly (int R, int G, int B)[] SlopeColors = { (255, 247, 188), (254, 196, 79), (254, 153, 41), (217, 95, 14), (153, 52, 4) };
    private static readonly int[][] Natural =
    {
        new[] { 46, 74, 38 }, new[] { 128, 116, 72 }, new[] { 150, 152, 92 }, new[] { 168, 156, 104 }, new[] { 150, 146, 142 },
        new[] { 172, 162, 146 }, new[] { 232, 236, 242 }, new[] { 44, 74, 104 }, new[] { 66, 94, 84 },
    };
    private const string ViridisHex = "44015444025645045745055946075a46085c460a5d460b5e470d60470e6147106347116447136548146748166848176948186a481a6c481b6d481c6e481d6f481f70482071482173482374482475482576482677482878482979472a7a472c7a472d7b472e7c472f7d46307e46327e46337f463480453581453781453882443983443a83443b84433d84433e85423f854240864241864142874144874045884046883f47883f48893e49893e4a893e4c8a3d4d8a3d4e8a3c4f8a3c508b3b518b3b528b3a538b3a548c39558c39568c38588c38598c375a8c375b8d365c8d365d8d355e8d355f8d34608d34618d33628d33638d32648e32658e31668e31678e31688e30698e306a8e2f6b8e2f6c8e2e6d8e2e6e8e2e6f8e2d708e2d718e2c718e2c728e2c738e2b748e2b758e2a768e2a778e2a788e29798e297a8e297b8e287c8e287d8e277e8e277f8e27808e26818e26828e26828e25838e25848e25858e24868e24878e23888e23898e238a8d228b8d228c8d228d8d218e8d218f8d21908d21918c20928c20928c20938c1f948c1f958b1f968b1f978b1f988b1f998a1f9a8a1e9b8a1e9c891e9d891f9e891f9f881fa0881fa1881fa1871fa28720a38620a48621a58521a68522a78522a88423a98324aa8325ab8225ac8226ad8127ad8128ae8029af7f2ab07f2cb17e2db27d2eb37c2fb47c31b57b32b67a34b67935b77937b87838b9773aba763bbb753dbc743fbc7340bd7242be7144bf7046c06f48c16e4ac16d4cc26c4ec36b50c46a52c56954c56856c66758c7655ac8645cc8635ec96260ca6063cb5f65cb5e67cc5c69cd5b6ccd5a6ece5870cf5773d05675d05477d1537ad1517cd2507fd34e81d34d84d44b86d54989d5488bd6468ed64590d74393d74195d84098d83e9bd93c9dd93ba0da39a2da37a5db36a8db34aadc32addc30b0dd2fb2dd2db5de2bb8de29bade28bddf26c0df25c2df23c5e021c8e020cae11fcde11dd0e11cd2e21bd5e21ad8e219dae319dde318dfe318e2e418e5e419e7e419eae51aece51befe51cf1e51df4e61ef6e620f8e621fbe723fde725";

    private sealed class Rgba
    {
        public int W, H;
        public byte[] D = Array.Empty<byte>();
        public int R(int x, int y) => D[(y * W + x) * 4];
        public int G(int x, int y) => D[(y * W + x) * 4 + 1];
        public int B(int x, int y) => D[(y * W + x) * 4 + 2];
    }

    private sealed class Fig
    {
        public string Caption = "";
        public Page Page = null!;
        public List<IPdfImage> Row = new();
    }

    // ------------------------------------------------------------------ texto
    private static float Num(string s)
    {
        s = s.Trim();
        if (Regex.IsMatch(s, @"^-?\d{1,3}(\.\d{3})+(,\d+)?$")) s = s.Replace(".", "");
        return float.Parse(s.Replace(',', '.'), Ci);
    }

    private static float? Find(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? Num(m.Groups[1].Value) : null;
    }

    private static string FindS(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>Texto de una página reconstruido por líneas (palabras agrupadas por línea base, de arriba abajo).</summary>
    private static string PageText(Page p)
    {
        var words = p.GetWords().Where(w => w.TextOrientation == TextOrientation.Horizontal).ToList();
        var lines = new List<List<Word>>();
        foreach (var w in words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
        {
            var line = lines.FirstOrDefault(l => Math.Abs(l[0].BoundingBox.Bottom - w.BoundingBox.Bottom) < 2.2);
            if (line == null) lines.Add(line = new List<Word>());
            line.Add(w);
        }
        return string.Join(" ", lines.Select(l => string.Join(" ", l.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text))));
    }

    // ------------------------------------------------------------------ imágenes
    private static Rgba? Decode(IPdfImage im)
    {
        var img = new Image();
        Error err;
        if (im.TryGetPng(out var png)) err = img.LoadPngFromBuffer(png);
        else
        {
            var raw = im.RawBytes.ToArray();
            err = img.LoadJpgFromBuffer(raw);
            if (err != Error.Ok) err = img.LoadPngFromBuffer(raw);
        }
        if (err != Error.Ok || img.IsEmpty()) return null;
        img.Convert(Image.Format.Rgba8);
        return new Rgba { W = img.GetWidth(), H = img.GetHeight(), D = img.GetData() };
    }

    private static List<Fig> Figures(PdfDocument doc)
    {
        var figs = new List<Fig>();
        foreach (var page in doc.GetPages())
        {
            var images = page.GetImages().ToList();
            if (images.Count == 0) continue;
            var words = page.GetWords().Where(w => w.TextOrientation == TextOrientation.Horizontal).ToList();
            foreach (var fw in words.Where(w => w.Text == "Figura"))
            {
                var cap = string.Join(" ", words.Where(w => Math.Abs(w.BoundingBox.Bottom - fw.BoundingBox.Bottom) < 2 && w.BoundingBox.Left >= fw.BoundingBox.Left - 1)
                    .OrderBy(w => w.BoundingBox.Left).Select(w => w.Text));
                var above = images.Where(im => im.Bounds.Bottom >= fw.BoundingBox.Top - 2 && im.Bounds.Bottom - fw.BoundingBox.Top < 60).ToList();
                if (above.Count == 0) continue;
                double low = above.Min(im => im.Bounds.Bottom);
                figs.Add(new Fig { Caption = cap, Page = page, Row = above.Where(im => Math.Abs(im.Bounds.Bottom - low) < 3).OrderBy(im => im.Bounds.Left).ToList() });
            }
        }
        return figs;
    }

    // ------------------------------------------------------------------ escala
    private static (double k, double b) Fit(List<(double x, double y)> pts)
    {
        double mx = pts.Average(p => p.x), my = pts.Average(p => p.y);
        double sxx = pts.Sum(p => (p.x - mx) * (p.x - mx)), sxy = pts.Sum(p => (p.x - mx) * (p.y - my));
        double k = sxx < 1e-9 ? 0 : sxy / sxx;
        return (k, my - k * mx);
    }

    /// <summary>Ancho y alto (m) de la imagen y latitud/longitud del centro, desde las marcas de coordenadas.</summary>
    private static (double W, double H, double Lat, double Lon)? Georef(Page page, PdfRectangle r)
    {
        var words = page.GetWords().Where(w => w.TextOrientation == TextOrientation.Horizontal).ToList();
        var lons = new List<(double, double)>();
        foreach (var w in words)
        {
            var m = Regex.Match(w.Text, @"^(\d+,\d+)°$");
            if (!m.Success) continue;
            var hem = words.FirstOrDefault(o => Math.Abs(o.BoundingBox.Bottom - w.BoundingBox.Bottom) < 2 && o.BoundingBox.Left > w.BoundingBox.Right - 0.5 && o.BoundingBox.Left - w.BoundingBox.Right < 8);
            if (hem == null || hem.Text is not ("O" or "E")) continue;
            if (w.BoundingBox.Bottom > r.Bottom + 8 || w.BoundingBox.Top < r.Bottom - 18 || w.BoundingBox.Left < r.Left - 30 || w.BoundingBox.Left > r.Right + 10) continue;
            double v = Num(m.Groups[1].Value) * (hem.Text == "O" ? -1 : 1);
            lons.Add(((w.BoundingBox.Left + hem.BoundingBox.Right) / 2, v));
        }
        // latitudes: texto rotado, PdfPig entrega letras sueltas → se agrupan por columna y se leen de abajo hacia arriba
        var lats = new List<(double, double)>();
        var rot = page.Letters.Where(l => l.TextOrientation != TextOrientation.Horizontal && l.GlyphRectangle.Left >= r.Left - 26 && l.GlyphRectangle.Right <= r.Left + 1
                                         && l.GlyphRectangle.Bottom >= r.Bottom - 12 && l.GlyphRectangle.Top <= r.Top + 12).ToList();
        var cols = new List<List<Letter>>();
        foreach (var l in rot.OrderBy(l => (l.GlyphRectangle.Left + l.GlyphRectangle.Right) / 2))
        {
            double cx = (l.GlyphRectangle.Left + l.GlyphRectangle.Right) / 2;
            if (cols.Count > 0 && cx - cols[^1].Average(o => (o.GlyphRectangle.Left + o.GlyphRectangle.Right) / 2) < 3.5) cols[^1].Add(l);
            else cols.Add(new List<Letter> { l });
        }
        foreach (var col in cols)
        {
            static double Cy(Letter l) => (l.GlyphRectangle.Bottom + l.GlyphRectangle.Top) / 2;   // en texto rotado la caja puede venir invertida
            var sorted = col.OrderBy(Cy).ToList();
            var group = new List<Letter>();
            void Flush()
            {
                if (group.Count == 0) return;
                string t = string.Concat(group.Select(g => g.Value)).Replace(" ", "");
                var m = Regex.Match(t, @"(\d+,\d+)°([NS])");
                if (m.Success)
                    lats.Add(((Cy(group.First()) + Cy(group.Last())) / 2, Num(m.Groups[1].Value) * (m.Groups[2].Value == "S" ? -1 : 1)));
                group.Clear();
            }
            foreach (var l in sorted)
            {
                if (group.Count > 0 && Cy(l) - Cy(group[^1]) > 8) Flush();
                group.Add(l);
            }
            Flush();
        }
        if (lons.Count < 2 || lats.Count < 2) return null;
        var (kx, bx) = Fit(lons);
        var (ky, by) = Fit(lats);
        double lon0 = kx * r.Left + bx, lon1 = kx * r.Right + bx, latS = ky * r.Bottom + by, latN = ky * r.Top + by;
        double latc = (latS + latN) / 2;
        return (Math.Abs(lon1 - lon0) * 111320 * Math.Cos(latc * Math.PI / 180), Math.Abs(latN - latS) * 110574, latc, (lon0 + lon1) / 2);
    }

    /// <summary>Informes sin coordenadas: la barra de escala ("2 km") da metros por punto.</summary>
    private static (double W, double H)? ScaleBar(Page page, PdfRectangle r)
    {
        var words = page.GetWords().Where(w => w.TextOrientation == TextOrientation.Horizontal).ToList();
        (double d, double mpp)? best = null;
        foreach (var unit in words.Where(w => w.Text is "km" or "m"))
        {
            var numW = words.FirstOrDefault(o => Math.Abs(o.BoundingBox.Bottom - unit.BoundingBox.Bottom) < 2 && unit.BoundingBox.Left - o.BoundingBox.Right is > -0.5 and < 6 && Regex.IsMatch(o.Text, @"^[\d,]+$"));
            if (numW == null) continue;
            if (numW.BoundingBox.Left < r.Left - 5 || numW.BoundingBox.Left > r.Right || numW.BoundingBox.Top > r.Top + 5 || numW.BoundingBox.Bottom < r.Bottom - 20) continue;
            double meters = Num(numW.Text) * (unit.Text == "km" ? 1000 : 1);
            foreach (var path in page.ExperimentalAccess.Paths)
            {
                var bb = path.GetBoundingRectangle();
                if (bb == null) continue;
                var b = bb.Value;
                if (b.Height < 4 && b.Width > 8 && Math.Abs(b.Top - numW.BoundingBox.Bottom) < 16 && Math.Abs(b.Bottom - numW.BoundingBox.Top) < 16 && b.Left >= r.Left - 2 && b.Left <= r.Right)
                {
                    double d = Math.Abs(b.Left - numW.BoundingBox.Left) + Math.Abs(b.Top - numW.BoundingBox.Bottom);
                    if (best == null || d < best.Value.d) best = (d, meters / b.Width);
                }
            }
        }
        return best == null ? null : (r.Width * best.Value.mpp, r.Height * best.Value.mpp);
    }

    /// <summary>Temperatura media mensual desde la serie vectorial de la figura "Temperatura media mensual".</summary>
    private static float[]? MonthlyTemps(PdfDocument doc, string period)
    {
        foreach (var page in doc.GetPages())
        {
            var words = page.GetWords().Where(w => w.TextOrientation == TextOrientation.Horizontal).ToList();
            var fig = words.FirstOrDefault(w => w.Text == "Figura" && string.Join(" ", words.Where(o => Math.Abs(o.BoundingBox.Bottom - w.BoundingBox.Bottom) < 2).OrderBy(o => o.BoundingBox.Left).Select(o => o.Text)).Contains("Temperatura media mensual"));
            if (fig == null) continue;
            double capTop = fig.BoundingBox.Top;
            List<PdfPoint>? best = null;
            PdfRectangle bestBox = default;
            foreach (var path in page.ExperimentalAccess.Paths)
            {
                var bb = path.GetBoundingRectangle();
                if (bb == null || bb.Value.Bottom < capTop || bb.Value.Bottom - capTop > 140) continue;
                var pts = new List<PdfPoint>();
                foreach (var sp in path)
                    foreach (var c in sp.Commands)
                        if (c is PdfSubpath.Line ln) { pts.Add(ln.From); pts.Add(ln.To); }
                if (pts.Count > 40 && (best == null || pts.Count > best.Count)) { best = pts; bestBox = bb.Value; }
            }
            if (best == null) return null;
            var uniq = new List<PdfPoint>();
            foreach (var p in best)
                if (uniq.Count == 0 || Math.Abs(p.X - uniq[^1].X) > 0.3) uniq.Add(p);
            var ticks = words.Where(w => Regex.IsMatch(w.Text, @"^-?\d+$") && w.BoundingBox.Right < bestBox.Left + 3 && w.BoundingBox.Bottom > bestBox.Bottom - 10 && w.BoundingBox.Top < bestBox.Top + 10)
                .Select(w => ((w.BoundingBox.Bottom + w.BoundingBox.Top) / 2, (double)Num(w.Text))).ToList();
            if (ticks.Count < 2) return null;
            var (k, b) = Fit(ticks);
            int start = period.Length >= 5 && int.TryParse(period.Substring(3, 2), out var mm) ? mm : 1;
            var acc = Enumerable.Range(0, 12).Select(_ => new List<double>()).ToArray();
            for (int i = 0; i < uniq.Count; i++) acc[(start - 1 + i) % 12].Add(k * uniq[i].Y + b);
            if (acc.Any(a => a.Count == 0)) return null;
            return acc.Select(a => (float)Math.Round(a.Average(), 1)).ToArray();
        }
        return null;
    }

    // ------------------------------------------------------------------ conversión
    public static GeoImportResult Import(string pdfPath, float cell = 30f)
    {
        var res = new GeoImportResult();
        try { Convert(pdfPath, cell, res); }
        catch (Exception e)
        {
            res.Ok = false;
            res.Error = e.Message;
        }
        return res;
    }

    private static void Convert(string pdfPath, float cell, GeoImportResult res)
    {
        using var doc = PdfDocument.Open(pdfPath);
        string text = string.Join(" ", doc.GetPages().Select(PageText));
        text = Regex.Replace(text, @"\s+", " ");
        string id = FindS(text, @"Consulta (\w{6,})");
        if (id == "") throw new InvalidDataException("no parece un informe GeoClick (no se encontró \"Consulta …\")");
        res.Id = id;
        float elevMin = Find(text, @"elevación de ([\d.]+) a") ?? Find(text, @"Elevación ([\d.]+) a") ?? throw new InvalidDataException("sin rango de elevación");
        float elevMax = Find(text, @"elevación de [\d.]+ a ([\d.]+) m") ?? Find(text, @"Elevación [\d.]+ a ([\d.]+) m") ?? throw new InvalidDataException("sin rango de elevación");
        float area = Find(text, @"· ([\d,]+) km² ·") ?? 0;
        res.AreaReportKm2 = area;
        var meta = new Dictionary<string, object?>
        {
            ["consulta"] = id, ["id"] = id, ["area_km2"] = area,
            ["periodo"] = FindS(text, @"período (\d\d-\d\d-\d{4} a \d\d-\d\d-\d{4})"),
            ["elev_min"] = elevMin, ["elev_max"] = elevMax,
            ["elev_mediana"] = Find(text, @"mediana (\d[\d.]*) m"),
            ["pend_mediana"] = Find(text, @"Pendiente mediana(?: \S+){0,8}? (\d+,\d)°"),
            ["km2_sobre_25"] = Find(text, @"([\d,]+) km² sobre 25°") ?? 0,
            ["agua_km2"] = Find(text, @"([\d,]+) km² de agua") ?? 0,
            ["cicatrices_ha"] = Find(text, @"Cicatrices de aluvión o remoción, ([\d.,]+) ha") ?? 0,
            ["quemadas_ha"] = Find(text, @"áreas quemadas, ([\d.,]+) ha") ?? 0,
            ["inundaciones"] = Find(text, @"Inundaciones: (\d+) en") ?? 0,
            ["lluvia_anual_mm"] = Find(text, @"Lluvia: ([\d.,]+) mm al año"),
            ["max_diaria_mm"] = Find(text, @"máxima diaria ([\d.,]+) mm"),
            ["max_diaria_fecha"] = FindS(text, @"máxima diaria [\d.,]+ mm el (\d\d-\d\d-\d{4})"),
            ["tendencia_mm_decada"] = Find(text, @"tendencia (-?[\d.,]+) mm por década"),
            ["temp_media_c"] = Find(text, @"Temperatura media (-?[\d,]+) °C"),
            ["temp_tendencia_c"] = Find(text, @"°C; (-?[\d,]+) °C por década"),
            ["heladas_anio"] = Find(text, @"por década; ([\d,]+) heladas"),
            ["fecha_sentinel"] = FindS(text, @"Imagen Sentinel-2 más reciente sin nubes: (\d\d-\d\d-\d{4})"),
        };
        res.SlopeMedianReport = (float?)meta["pend_mediana"] ?? 0;
        // tablas: cada etiqueta va seguida de su km² y su % (las columnas pueden venir intercaladas)
        var slopeKm = new[] { "0–5°", "5–15°", "15–25°", "25–35°", "Más de 35°" }.Select(l => Find(text, Regex.Escape(l) + @" ([\d,]+) \d+ ") ?? -1).ToArray();
        if (slopeKm.All(v => v >= 0)) meta["pend_clases"] = slopeKm.Select(v => Math.Round(v / Math.Max(1e-6, slopeKm.Sum()), 4)).ToArray();
        int covIdx = text.IndexOf("Cobertura km² % del área", StringComparison.Ordinal);
        string covText = covIdx >= 0 ? text.Substring(covIdx, Math.Min(500, text.Length - covIdx)) : "";
        var cover = new Dictionary<string, float>();
        foreach (var e in Esa)
            if (Find(covText, @"(?<![A-Za-zá-ú])" + Regex.Escape(e.Name) + @" ([\d,]+) \d+") is float km) cover[e.Name] = km;
        meta["cobertura"] = cover;
        var months = new[] { "Ene", "Feb", "Mar", "Abr", "May", "Jun", "Jul", "Ago", "Sep", "Oct", "Nov", "Dic" };
        int climIdx = text.IndexOf("Mes Media Mediana P10 P90", StringComparison.Ordinal);
        if (climIdx >= 0)
        {
            string ct = text.Substring(climIdx, Math.Min(900, text.Length - climIdx));
            var rain = months.Select(mo => Find(ct, @"\b" + mo + @" ([\d.,]+) [\d.,]+ [\d.,]+ [\d.,]+")).ToArray();
            if (rain.All(r => r != null)) meta["lluvia_mensual_mm"] = rain.Select(r => r!.Value).ToArray();
        }
        int rpIdx = text.IndexOf("Períodos de retorno (Gumbel)", StringComparison.Ordinal);
        if (rpIdx >= 0)
            meta["retorno"] = Regex.Matches(text.Substring(rpIdx, Math.Min(500, text.Length - rpIdx)), @"(\d+) años ([\d.,]+) mm ([\d.,]+) mm")
                .Take(6).Select(m => new[] { int.Parse(m.Groups[1].Value), Num(m.Groups[2].Value), Num(m.Groups[3].Value) }).ToArray();

        // figuras ráster
        Rgba? elev = null, cov = null, slope = null, sat = null, scar = null, burn = null;
        (double W, double H)? size = null;
        double? lat = null, lon = null;
        foreach (var f in Figures(doc))
        {
            for (int k = 0; k < f.Row.Count; k++)
            {
                var im = f.Row[k];
                string cap = f.Caption;
                if (cap.Contains("Elevación") && k == 0 && elev == null)
                {
                    elev = Decode(im);
                    var g = Georef(f.Page, im.Bounds);
                    if (g != null) { size = (g.Value.W, g.Value.H); lat = g.Value.Lat; lon = g.Value.Lon; }
                    else size = ScaleBar(f.Page, im.Bounds);
                }
                else if ((cap.Contains("Cobertura de suelo") || (cap.Contains("cobertura") && k == 1)) && cov == null) cov = Decode(im);
                else if (cap.Contains("Pendiente del terreno") && slope == null) slope = Decode(im);
                else if (cap.Contains("Color real") && sat == null) sat = Decode(im);
                else if (cap.Contains("Cicatrices"))
                {
                    var d = Decode(im);
                    if (d == null) continue;
                    // capa de marcas: píxeles no negros; el color medio dice si es cicatriz (naranjo) o quema (granate)
                    double sr = 0, sg = 0, cntM = 0;
                    for (int i = 0; i < d.W * d.H; i++)
                        if (d.D[i * 4] + d.D[i * 4 + 1] + d.D[i * 4 + 2] > 40 && d.D[i * 4 + 3] > 128 && !(d.D[i * 4] > 200 && d.D[i * 4 + 1] > 200)) { sr += d.D[i * 4]; sg += d.D[i * 4 + 1]; cntM++; }
                    if (cntM < 3 || d.W > 700) continue;   // la imagen satelital de fondo no es una capa de marcas
                    if (sg / cntM > 60) scar ??= d; else burn ??= d;
                }
            }
        }
        if (sat == null)
            foreach (var f in Figures(doc).Where(f => f.Caption.Contains("Área consultada")))
                sat ??= Decode(f.Row[0]);
        if (elev == null) throw new InvalidDataException("no se encontró la figura de elevación");
        if (size == null) throw new InvalidDataException("no se pudo determinar la escala (ni coordenadas ni barra de escala)");
        res.HasCoords = lat != null;

        // caja de datos de la figura de elevación (sin el marco claro)
        int w0 = elev.W, h0 = elev.H;
        int r0 = h0, r1 = 0, c0 = w0, c1 = 0;
        for (int y = 0; y < h0; y++)
        for (int x = 0; x < w0; x++)
        {
            bool bg = elev.R(x, y) > 228 && elev.G(x, y) > 228 && elev.B(x, y) > 228;
            if (bg) continue;
            r0 = Math.Min(r0, y); r1 = Math.Max(r1, y + 1); c0 = Math.Min(c0, x); c1 = Math.Max(c1, x + 1);
        }
        if (r1 <= r0 || c1 <= c0) throw new InvalidDataException("figura de elevación vacía");
        double wm = size.Value.W, hm = size.Value.H;
        double dwm = wm * (c1 - c0) / w0, dhm = hm * (r1 - r0) / h0;
        // a escala real: celdas de 30 m (resolución del DEM); si el área es grande o alargada, la celda crece en pasos de
        // 10 m para no superar 360 celdas de lado (rendimiento), sin perder la escala
        if (Math.Max(dwm, dhm) / cell > MaxCells) cell = (float)(Math.Ceiling(Math.Max(dwm, dhm) / MaxCells / 10.0) * 10.0);
        int nGrid = (int)Math.Ceiling(Math.Max(dwm, dhm) / cell);
        if (cell > 120) throw new InvalidDataException($"área demasiado grande ({Math.Max(dwm, dhm) / 1000:0.0} km de lado): necesitaría celdas de {cell:0} m");
        res.CellM = cell;
        if (cell > 30) res.Notes.Add($"área de {Math.Max(dwm, dhm) / 1000:0.0} km de lado: celdas de {cell:0} m para mantener el rendimiento");
        double side = nGrid * cell;
        double cu = (c0 + c1) / 2.0 / w0, cv = (r0 + r1) / 2.0 / h0;
        var uu = new double[nGrid];
        var vv = new double[nGrid];
        for (int i = 0; i < nGrid; i++)
        {
            double xm = (i + 0.5) * cell - side / 2;
            uu[i] = cu + xm / wm;
            vv[i] = cv + xm / hm;
        }
        bool InBox(int gx, int gy) => uu[gx] >= (double)c0 / w0 && uu[gx] < (double)c1 / w0 && vv[gy] >= (double)r0 / h0 && vv[gy] < (double)r1 / h0;
        (int, int, int, int) Nn(Rgba im, int gx, int gy)
        {
            int px = Math.Clamp((int)(uu[gx] * im.W), 0, im.W - 1), py = Math.Clamp((int)(vv[gy] * im.H), 0, im.H - 1);
            int o = (py * im.W + px) * 4;
            return (im.D[o], im.D[o + 1], im.D[o + 2], im.D[o + 3]);
        }

        // elevación: color viridis → índice (0..255) → mediana 3×3 → bilineal → metros
        var lut = new int[256 * 3];
        for (int i = 0; i < 256 * 3; i++) lut[i] = System.Convert.ToInt32(ViridisHex.Substring(i * 2, 2), 16);
        var tIdx = new byte[w0 * h0];
        var valid = new bool[w0 * h0];
        var cache = new Dictionary<int, (byte, int)>();
        for (int i = 0; i < w0 * h0; i++)
        {
            int r = elev.D[i * 4], g = elev.D[i * 4 + 1], b = elev.D[i * 4 + 2];
            int key = (r << 16) | (g << 8) | b;
            if (!cache.TryGetValue(key, out var hit))
            {
                int best = 0, bd = int.MaxValue;
                for (int k = 0; k < 256; k++)
                {
                    int dr = r - lut[k * 3], dg = g - lut[k * 3 + 1], db = b - lut[k * 3 + 2];
                    int dd = dr * dr + dg * dg + db * db;
                    if (dd < bd) { bd = dd; best = k; }
                }
                hit = ((byte)best, bd);
                cache[key] = hit;
            }
            tIdx[i] = hit.Item1;
            valid[i] = hit.Item2 < 45 * 45;
        }
        var med = new byte[w0 * h0];
        var win = new byte[9];
        for (int y = 0; y < h0; y++)
        for (int x = 0; x < w0; x++)
        {
            int c = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                win[c++] = tIdx[Math.Clamp(y + dy, 0, h0 - 1) * w0 + Math.Clamp(x + dx, 0, w0 - 1)];
            Array.Sort(win);
            med[y * w0 + x] = win[4];
        }
        int n = nGrid;
        var egrid = new double[n * n];
        var ok = new bool[n * n];
        for (int gy = 0; gy < n; gy++)
        for (int gx = 0; gx < n; gx++)
        {
            double fx = uu[gx] * w0 - 0.5, fy = vv[gy] * h0 - 0.5;
            int x0 = Math.Clamp((int)Math.Floor(fx), 0, w0 - 2), y0 = Math.Clamp((int)Math.Floor(fy), 0, h0 - 2);
            double tx = Math.Clamp(fx - x0, 0, 1), ty = Math.Clamp(fy - y0, 0, 1);
            double T(int x, int y) => med[y * w0 + x] / 255.0;
            double t = T(x0, y0) * (1 - tx) * (1 - ty) + T(x0 + 1, y0) * tx * (1 - ty) + T(x0, y0 + 1) * (1 - tx) * ty + T(x0 + 1, y0 + 1) * tx * ty;
            egrid[gy * n + gx] = elevMin + t * (elevMax - elevMin);
            int px = Math.Clamp((int)Math.Round(fx), 0, w0 - 1), py = Math.Clamp((int)Math.Round(fy), 0, h0 - 1);
            ok[gy * n + gx] = valid[py * w0 + px] && InBox(gx, gy);
        }

        // cobertura (polígono), pendiente y marcas
        var coverGrid = Enumerable.Repeat((byte)255, n * n).ToArray();
        var names = cover.Keys.Where(k => Esa.Any(e => e.Name == k)).ToList();
        if (names.Count == 0) names = Esa.Select(e => e.Name).ToList();
        var pal = names.Select(nm => Esa.First(e => e.Name == nm)).ToArray();
        if (cov != null)
        {
            res.Layers.Add("cobertura");
            for (int gy = 0; gy < n; gy++)
            for (int gx = 0; gx < n; gx++)
            {
                if (!InBox(gx, gy)) continue;
                var (r, g, b, a) = Nn(cov, gx, gy);
                if (a < 128 || r + g + b < 30) continue;   // fuera del polígono (transparente / negro)
                int best = -1, bd = 70 * 70;
                for (int k = 0; k < pal.Length; k++)
                {
                    int dd = (r - pal[k].R) * (r - pal[k].R) + (g - pal[k].G) * (g - pal[k].G) + (b - pal[k].B) * (b - pal[k].B);
                    if (dd <= bd) { bd = dd; best = k; }
                }
                if (best >= 0) coverGrid[gy * n + gx] = (byte)Array.IndexOf(Soils, pal[best].Soil);
            }
        }
        var slopeGrid = Enumerable.Repeat((byte)255, n * n).ToArray();
        if (slope != null)
        {
            res.Layers.Add("pendiente");
            for (int gy = 0; gy < n; gy++)
            for (int gx = 0; gx < n; gx++)
            {
                if (!InBox(gx, gy)) continue;
                var (r, g, b, a) = Nn(slope, gx, gy);
                if (a < 128 || r + g + b < 30) continue;
                int best = -1, bd = 50 * 50;
                for (int k = 0; k < SlopeColors.Length; k++)
                {
                    int dd = (r - SlopeColors[k].R) * (r - SlopeColors[k].R) + (g - SlopeColors[k].G) * (g - SlopeColors[k].G) + (b - SlopeColors[k].B) * (b - SlopeColors[k].B);
                    if (dd <= bd) { bd = dd; best = k; }
                }
                if (best >= 0) slopeGrid[gy * n + gx] = (byte)best;
            }
        }
        var marks = new byte[n * n];
        foreach (var (layer, bit, name) in new[] { (scar, 1, "cicatrices"), (burn, 2, "quemas") })
        {
            if (layer == null) continue;
            res.Layers.Add(name);
            for (int gy = 0; gy < n; gy++)
            for (int gx = 0; gx < n; gx++)
            {
                if (!InBox(gx, gy)) continue;
                var (r, g, b, a) = Nn(layer, gx, gy);
                if (a > 128 && r + g + b > 40) marks[gy * n + gx] |= (byte)bit;
            }
        }
        res.Layers.Insert(0, "elevación");
        if (coverGrid.Any(c => c != 255))
            for (int i = 0; i < n * n; i++) ok[i] &= coverGrid[i] != 255;
        ok = Erode(ok, n);
        egrid = FillInvalid(egrid, ok, n);

        // imagen satelital del cuadrado del mapa (1024 px) o textura sintética si viene vacía
        byte[] satJpg;
        {
            const int res2 = 1024;
            var img = Image.CreateEmpty(res2, res2, false, Image.Format.Rgb8);
            var data = new byte[res2 * res2 * 3];
            double sum = 0;
            int cnt = 0;
            if (sat != null)
            {
                res.Layers.Add("Sentinel-2");
                for (int y = 0; y < res2; y++)
                for (int x = 0; x < res2; x++)
                {
                    double xm = (x + 0.5) / res2 * side - side / 2, ym = (y + 0.5) / res2 * side - side / 2;
                    double u = cu + xm / wm, v = cv + ym / hm;
                    bool inb = u >= (double)c0 / w0 && u < (double)c1 / w0 && v >= (double)r0 / h0 && v < (double)r1 / h0;
                    int o = (y * res2 + x) * 3;
                    if (!inb) { data[o] = 40; data[o + 1] = 44; data[o + 2] = 50; continue; }
                    int px = Math.Clamp((int)(u * sat.W), 0, sat.W - 1), py = Math.Clamp((int)(v * sat.H), 0, sat.H - 1);
                    int so = (py * sat.W + px) * 4;
                    data[o] = sat.D[so]; data[o + 1] = sat.D[so + 1]; data[o + 2] = sat.D[so + 2];
                    sum += sat.D[so] + sat.D[so + 1] + sat.D[so + 2];
                    cnt += 3;
                }
            }
            if (sat == null || cnt == 0 || sum / cnt < 35)
            {
                res.SyntheticSatellite = true;
                data = SyntheticTexture(egrid, coverGrid, n, cell, res2);
            }
            img.SetData(res2, res2, false, Image.Format.Rgb8, data);
            satJpg = img.SaveJpgToBuffer(0.86f);
        }

        // validación: área del polígono y pendiente mediana
        int inside = coverGrid.Count(c => c != 255);
        res.AreaRebuiltKm2 = (float)(inside * cell * cell / 1e6);
        var slopes = new List<double>();
        for (int y = 1; y < n - 1; y++)
        for (int x = 1; x < n - 1; x++)
        {
            if (coverGrid[y * n + x] == 255) continue;
            double gx = (egrid[y * n + x + 1] - egrid[y * n + x - 1]) / (2 * cell), gy = (egrid[(y + 1) * n + x] - egrid[(y - 1) * n + x]) / (2 * cell);
            slopes.Add(Math.Atan(Math.Sqrt(gx * gx + gy * gy)) * 180 / Math.PI);
        }
        slopes.Sort();
        res.SlopeMedianCalc = slopes.Count > 0 ? (float)slopes[slopes.Count / 2] : 0;
        res.N = n;
        res.WidthM = (float)dwm;
        res.HeightM = (float)dhm;
        if (inside == 0) throw new InvalidDataException("no se reconoció el polígono (cobertura vacía)");
        if (res.AreaReportKm2 > 0 && Math.Abs(res.AreaRebuiltKm2 / res.AreaReportKm2 - 1) > 0.15)
            res.Notes.Add("ATENCIÓN: el área reconstruida difiere más de 15 % del informe");

        var temps = MonthlyTemps(doc, (string)meta["periodo"]!);
        res.HasTemps = temps != null;
        meta["temp_mensual_c"] = temps;
        meta["celda_m"] = cell;
        meta["n"] = n;
        meta["lado_m"] = Math.Round(side);
        meta["ancho_datos_m"] = Math.Round(dwm);
        meta["alto_datos_m"] = Math.Round(dhm);
        meta["lat"] = lat.HasValue ? Math.Round(lat.Value, 4) : null;
        meta["lon"] = lon.HasValue ? Math.Round(lon.Value, 4) : null;
        meta["satelite"] = id + "_sat.jpg";
        meta["satelite_sintetico"] = res.SyntheticSatellite;
        meta["fuente"] = Path.GetFileName(pdfPath);
        meta["importado"] = true;
        meta["suelos"] = Soils;
        var elevDm = new byte[n * n * 2];
        for (int i = 0; i < n * n; i++)
        {
            ushort v = (ushort)Math.Clamp(Math.Round((egrid[i] - elevMin) * 10), 0, 65535);
            elevDm[i * 2] = (byte)(v & 0xff);
            elevDm[i * 2 + 1] = (byte)(v >> 8);
        }
        meta["elev_dm"] = B64(elevDm);
        meta["cobertura_grid"] = B64(coverGrid);
        meta["pendiente_grid"] = B64(slopeGrid);
        meta["marcas_grid"] = B64(marks);

        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(UserDir));
        string json = JsonSerializer.Serialize(meta);
        res.JsonPath = UserDir + id + ".json";
        using (var f = Godot.FileAccess.Open(res.JsonPath, Godot.FileAccess.ModeFlags.Write)) f.StoreString(json);
        using (var f = Godot.FileAccess.Open(UserDir + id + "_sat.jpg", Godot.FileAccess.ModeFlags.Write)) f.StoreBuffer(satJpg);
        res.Ok = true;
    }

    private static string B64(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(data, 0, data.Length);
        return System.Convert.ToBase64String(ms.ToArray());
    }

    private static bool[] Erode(bool[] ok, int n)
    {
        var o = new bool[n * n];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            bool all = true;
            for (int dy = -1; dy <= 1 && all; dy++)
            for (int dx = -1; dx <= 1 && all; dx++)
            {
                int xx = x + dx, yy = y + dy;
                all = xx >= 0 && yy >= 0 && xx < n && yy < n && ok[yy * n + xx];
            }
            o[y * n + x] = all;
        }
        return o;
    }

    private static double NeighborMean(double[] e, bool[]? known, int n, int x, int y, out int count)
    {
        double s = 0;
        count = 0;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int xx = Math.Clamp(x + dx, 0, n - 1), yy = Math.Clamp(y + dy, 0, n - 1);
            if (known != null && !known[yy * n + xx]) continue;
            s += e[yy * n + xx];
            count++;
        }
        return count > 0 ? s / count : 0;
    }

    /// <summary>Relleno fuera del polígono: crece desde el borde y tiende a la elevación media del borde (sin franjas).</summary>
    private static double[] FillInvalid(double[] e0, bool[] ok, int n)
    {
        if (!ok.Any(o => o)) return e0;
        var e = (double[])e0.Clone();
        var known = (bool[])ok.Clone();
        var dist = new int[n * n];
        int step = 0;
        while (known.Any(k => !k))
        {
            step++;
            var grow = new List<(int, double)>();
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = y * n + x;
                if (known[i]) continue;
                double m = NeighborMean(e, known, n, x, y, out int c);
                if (c > 0) grow.Add((i, m));
            }
            if (grow.Count == 0) break;
            foreach (var (i, m) in grow) { e[i] = m; known[i] = true; dist[i] = step; }
        }
        double baseE = 0;
        int bc = 0;
        var inner = Erode(ok, n);
        for (int i = 0; i < n * n; i++) if (ok[i] && !inner[i]) { baseE += e[i]; bc++; }
        baseE = bc > 0 ? baseE / bc : e.Where((_, i) => ok[i]).Average();
        for (int i = 0; i < n * n; i++)
            if (!ok[i]) { double w = Math.Clamp(dist[i] / 25.0, 0, 1); e[i] = e[i] * (1 - w) + baseE * w; }
        for (int it = 0; it < 40; it++)
        {
            var nx = (double[])e.Clone();
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if (!ok[y * n + x]) nx[y * n + x] = NeighborMean(e, null, n, x, y, out _);
            e = nx;
        }
        return e;
    }

    private static byte[] SyntheticTexture(double[] e, byte[] cover, int n, float cell, int res)
    {
        var data = new byte[res * res * 3];
        for (int y = 0; y < res; y++)
        for (int x = 0; x < res; x++)
        {
            int gx = Math.Clamp(x * n / res, 1, n - 2), gy = Math.Clamp(y * n / res, 1, n - 2);
            double dx = (e[gy * n + gx + 1] - e[gy * n + gx - 1]) / (2 * cell), dy = (e[(gy + 1) * n + gx] - e[(gy - 1) * n + gx]) / (2 * cell);
            double slope = Math.Atan(Math.Sqrt(dx * dx + dy * dy)), asp = Math.Atan2(-dx, dy);
            double hs = Math.Clamp(Math.Sin(0.698) * Math.Cos(slope) + Math.Cos(0.698) * Math.Sin(slope) * Math.Cos(5.498 - asp), 0, 1);
            byte c = cover[gy * n + gx];
            int[] col = c == 255 ? new[] { 40, 44, 50 } : Natural[Math.Min(c, Natural.Length - 1)];
            int o = (y * res + x) * 3;
            for (int k = 0; k < 3; k++) data[o + k] = (byte)Math.Clamp(col[k] * (0.55 + 0.6 * hs), 0, 255);
        }
        return data;
    }
}
