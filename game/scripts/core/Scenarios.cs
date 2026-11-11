using System.Collections.Generic;
using System.Linq;
using AresX.Sim;
using Godot;

namespace AresX.Core;

/// <summary>
/// Catálogo de escenarios GeoClick convertidos (res://data/escenarios): un JSON por informe con clima, tablas y
/// grillas a escala real, más la textura satelital del mapa. Los metadatos se leen una vez; las grillas al iniciar.
/// </summary>
public static class Scenarios
{
    public const string Dir = "res://data/escenarios/";
    private static List<GeoReport>? _list;

    public static IReadOnlyList<GeoReport> All
    {
        get
        {
            if (_list != null) return _list;
            _list = new List<GeoReport>();
            using var d = DirAccess.Open(Dir);
            if (d == null) return _list;
            foreach (var f in d.GetFiles().Where(f => f.EndsWith(".json") && f != "indice.json").OrderBy(f => f))
            {
                try { _list.Add(GeoReport.FromJson(FileAccess.GetFileAsString(Dir + f), withGrids: false)); }
                catch (System.Exception e) { GD.PushWarning($"Escenario {f} ilegible: {e.Message}"); }
            }
            _list = _list.OrderBy(g => g.AreaKm2).ToList();
            return _list;
        }
    }

    public static GeoReport? Get(string id) => All.FirstOrDefault(g => g.Id == id);

    /// <summary>Carga completa (con grillas) de un escenario.</summary>
    public static GeoReport? Load(string id)
    {
        string path = Dir + id + ".json";
        return FileAccess.FileExists(path) ? GeoReport.FromJson(FileAccess.GetFileAsString(path)) : null;
    }

    public static Texture2D? Satellite(GeoReport g) =>
        string.IsNullOrEmpty(g.SatelliteFile) || !ResourceLoader.Exists(Dir + g.SatelliteFile) ? null : GD.Load<Texture2D>(Dir + g.SatelliteFile);
}
