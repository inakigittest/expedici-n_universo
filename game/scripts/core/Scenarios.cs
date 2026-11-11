using System.Collections.Generic;
using System.Linq;
using AresX.Sim;
using Godot;

namespace AresX.Core;

/// <summary>
/// Catálogo de escenarios GeoClick: los incluidos en el juego (res://data/escenarios) y los importados por el usuario
/// desde un PDF (user://escenarios, ver <see cref="GeoClickPdf"/>). Un importado con el mismo id reemplaza al incluido.
/// Los metadatos se leen una vez; las grillas al iniciar la misión.
/// </summary>
public static class Scenarios
{
    public const string Dir = "res://data/escenarios/";
    public const string UserDir = GeoClickPdf.UserDir;
    private static List<GeoReport>? _list;
    private static readonly HashSet<string> _imported = new();

    public static bool IsImported(string id) => _imported.Contains(id);

    public static IReadOnlyList<GeoReport> All
    {
        get
        {
            if (_list != null) return _list;
            var byId = new Dictionary<string, GeoReport>();
            _imported.Clear();
            foreach (var (dir, imported) in new[] { (Dir, false), (UserDir, true) })
            {
                using var d = DirAccess.Open(dir);
                if (d == null) continue;
                foreach (var f in d.GetFiles().Where(f => f.EndsWith(".json") && f != "indice.json").OrderBy(f => f))
                {
                    try
                    {
                        var g = GeoReport.FromJson(FileAccess.GetFileAsString(dir + f), withGrids: false);
                        byId[g.Id] = g;
                        if (imported) _imported.Add(g.Id);
                    }
                    catch (System.Exception e) { GD.PushWarning($"Escenario {f} ilegible: {e.Message}"); }
                }
            }
            _list = byId.Values.OrderBy(g => g.AreaKm2).ToList();
            return _list;
        }
    }

    /// <summary>Vuelve a leer el catálogo (tras importar un informe).</summary>
    public static void Refresh() => _list = null;

    public static GeoReport? Get(string id) => All.FirstOrDefault(g => g.Id == id);

    private static string? PathOf(string id)
    {
        if (FileAccess.FileExists(UserDir + id + ".json")) return UserDir + id + ".json";
        return FileAccess.FileExists(Dir + id + ".json") ? Dir + id + ".json" : null;
    }

    /// <summary>Carga completa (con grillas) de un escenario.</summary>
    public static GeoReport? Load(string id) => PathOf(id) is string p ? GeoReport.FromJson(FileAccess.GetFileAsString(p)) : null;

    public static Texture2D? Satellite(GeoReport g)
    {
        if (string.IsNullOrEmpty(g.SatelliteFile)) return null;
        if (FileAccess.FileExists(UserDir + g.SatelliteFile) && FileAccess.FileExists(UserDir + g.Id + ".json"))
        {
            var img = Image.LoadFromFile(ProjectSettings.GlobalizePath(UserDir + g.SatelliteFile));
            if (img == null || img.IsEmpty()) return null;
            img.GenerateMipmaps();
            return ImageTexture.CreateFromImage(img);
        }
        return ResourceLoader.Exists(Dir + g.SatelliteFile) ? GD.Load<Texture2D>(Dir + g.SatelliteFile) : null;
    }
}
