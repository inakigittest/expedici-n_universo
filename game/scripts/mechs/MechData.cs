using System.Collections.Generic;
using System.Linq;
using Godot;

namespace AresX.Mechs;

/// <summary>Una animación del esqueleto común.</summary>
public sealed record MechAnim(string Name, int Frames, float Fps, bool Loop)
{
    public float Length => Frames / Fps;
}

/// <summary>
/// assets/&lt;familia&gt;/skeleton.json (lo escribe tools/export_mech25d.py --family): escala y cámara del pre-render,
/// ranuras y de qué socket del padre cuelga cada una, animaciones, rango de profundidad y conjuntos de fábrica.
/// Familias: "mechs" (por defecto) y "pilots" (el piloto: una sola ranura "body" y pintura por fabricante).
/// </summary>
public sealed class MechSkeletonData
{
    public const string Root = "res://assets/mechs";
    public const string Mechs = "mechs", Pilots = "pilots", Props = "props";
    private static readonly Dictionary<string, MechSkeletonData> Instances = new();

    /// <summary>Carpeta de la familia (res://assets/&lt;familia&gt;).</summary>
    public string RootPath { get; private init; } = Root;
    /// <summary>Pintura por fabricante (pilotos): 21 colores que sustituyen a los colores clave del sprite
    /// (7 traje, 7 ribete, 5 visor; ver tools/pilot25d.py y use_paint en mech_part.gdshader).</summary>
    public Dictionary<string, Color[]> Paints { get; } = new();

    public int Cell { get; private init; }
    public int Dirs { get; private init; }
    public float PixelSize { get; private init; }
    public float Pitch { get; private init; }
    public Vector2 Origin { get; private init; }
    /// <summary>Rango de la profundidad relativa codificada en las hojas *_depth.png (unidades de mundo).</summary>
    public float DepthRange { get; private init; }
    /// <summary>Ranuras en orden padre → hija (las piernas primero).</summary>
    public string[] Slots { get; private init; } = System.Array.Empty<string>();
    /// <summary>Ranuras que sostienen la raíz del mecha (las piernas); el torso cuelga de la raíz común "root".</summary>
    public string[] RootSlots { get; private init; } = System.Array.Empty<string>();
    /// <summary>ranura → (ranura padre, socket). Las piernas no cuelgan de nada.</summary>
    public Dictionary<string, (string Parent, string Socket)> Attach { get; } = new();
    public Dictionary<string, MechAnim> Anims { get; } = new();
    /// <summary>conjunto → ranura → pieza (null = ranura vacía, solo en las de arma).</summary>
    public Dictionary<string, Dictionary<string, string?>> Factory { get; } = new();
    /// <summary>conjunto → juegos de armas de fábrica (derecha, izquierda), hasta 3 (ver WeaponSets).</summary>
    public Dictionary<string, List<(string? R, string? L)>> FactorySets { get; } = new();
    /// <summary>pieza → (tipo de ranura, fabricante). Un arma es de tipo "weapon" y vale para las dos manos.</summary>
    public Dictionary<string, (string Type, string Maker)> Parts { get; } = new();
    /// <summary>ranura → tipo de pieza que admite (weapon_r y weapon_l → "weapon").</summary>
    public Dictionary<string, string> SlotTypes { get; } = new();

    /// <summary>Offset de Sprite3D centrado (píxeles, Y arriba) que deja el origen del mundo en los pies.</summary>
    public Vector2 BaseOffset => new(Cell / 2f - Origin.X, Origin.Y - Cell / 2f);

    public string SlotType(string slot) => SlotTypes.GetValueOrDefault(slot, slot);

    /// <summary>Las ranuras de arma pueden ir vacías; el resto son obligatorias.</summary>
    public bool IsOptional(string slot) => SlotType(slot) == "weapon";

    public bool Fits(string partId, string slot) => Parts[partId].Type == SlotType(slot);

    public IEnumerable<string> PartsForSlot(string slot) =>
        Parts.Where(p => p.Value.Type == SlotType(slot)).Select(p => p.Key).OrderBy(p => p);

    public static MechSkeletonData Get(string family = Mechs)
    {
        if (Instances.TryGetValue(family, out var cached))
            return cached;
        string root = $"res://assets/{family}";
        var j = Json.ParseString(FileAccess.GetFileAsString($"{root}/skeleton.json")).AsGodotDictionary();
        var origin = j["origin"].AsGodotArray();
        var s = new MechSkeletonData
        {
            RootPath = root,
            Cell = j["cell"].AsInt32(),
            Dirs = j["dirs"].AsInt32(),
            PixelSize = j["pixel_size"].AsSingle(),
            Pitch = j["pitch"].AsSingle(),
            Origin = new Vector2(origin[0].AsSingle(), origin[1].AsSingle()),
            DepthRange = j["depth_range"].AsSingle(),
            Slots = j["slots"].AsGodotArray().Select(v => v.AsString()).ToArray(),
            RootSlots = j["root_slots"].AsGodotArray().Select(v => v.AsString()).ToArray(),
        };
        foreach (var (slot, att) in j["attach"].AsGodotDictionary())
            if (att.VariantType != Variant.Type.Nil)
                s.Attach[slot.AsString()] = (att.AsGodotArray()[0].AsString(), att.AsGodotArray()[1].AsString());
        foreach (var (name, v) in j["anims"].AsGodotDictionary())
        {
            var a = v.AsGodotDictionary();
            s.Anims[name.AsString()] = new MechAnim(name.AsString(), a["frames"].AsInt32(), a["fps"].AsSingle(), a["loop"].AsBool());
        }
        foreach (var (slot, type) in j["slot_types"].AsGodotDictionary())
            s.SlotTypes[slot.AsString()] = type.AsString();
        foreach (var (name, v) in j["factory"].AsGodotDictionary())
            s.Factory[name.AsString()] = v.AsGodotDictionary().ToDictionary(kv => kv.Key.AsString(),
                kv => kv.Value.VariantType == Variant.Type.Nil ? null : kv.Value.AsString());
        if (j.TryGetValue("factory_sets", out var fsets))
            foreach (var (name, v) in fsets.AsGodotDictionary())
                s.FactorySets[name.AsString()] = v.AsGodotArray().Select(set =>
                {
                    var a = set.AsGodotArray();
                    string? Id(int i) => a[i].VariantType == Variant.Type.Nil ? null : a[i].AsString();
                    return (Id(0), Id(1));
                }).ToList();
        foreach (var (name, v) in j["parts"].AsGodotDictionary())
            s.Parts[name.AsString()] = (v.AsGodotDictionary()["slot"].AsString(), v.AsGodotDictionary()["maker"].AsString());
        if (j.TryGetValue("paints", out var paints))
            foreach (var (maker, cols) in paints.AsGodotDictionary())
                s.Paints[maker.AsString()] = cols.AsGodotArray().Select(c => new Color(c.AsString())).ToArray();
        return Instances[family] = s;
    }
}

/// <summary>
/// Una pieza: assets/mechs/parts/&lt;id&gt;/part.json + por animación una hoja PNG de color y otra de profundidad
/// (filas = direcciones, columnas = frames). Points[nombre][anim][dir][frame] = (x, y) píxel de su ancla o de un
/// socket dentro de la celda y z su profundidad relativa (unidades de mundo, + = más lejos de la cámara).
/// Un arma tiene una carpeta por mano: &lt;id&gt; (derecha) y &lt;id&gt;_l (izquierda), cada una renderizada en su brazo.
/// </summary>
public sealed class MechPartData
{
    private static readonly Dictionary<string, MechPartData> Cache = new();

    /// <summary>Id lógico de la pieza (el mismo en las dos manos; es la clave de data/parts.json).</summary>
    public string Id { get; private init; } = "";
    /// <summary>Carpeta de sprites (con sufijo _l para la mano izquierda).</summary>
    public string Folder { get; private init; } = "";
    /// <summary>Carpeta de la familia (res://assets/mechs o res://assets/pilots).</summary>
    public string FamilyRoot { get; private init; } = MechSkeletonData.Root;
    /// <summary>Ranura concreta para la que están renderizados estos sprites.</summary>
    public string Slot { get; private init; } = "";
    public string Maker { get; private init; } = "";
    public Dictionary<string, Dictionary<string, Vector3[][]>> Points { get; } = new();
    private readonly Dictionary<string, (Texture2D Color, Texture2D Depth)> _sheets = new();

    public (Texture2D Color, Texture2D Depth) Sheet(string anim)
    {
        if (!_sheets.TryGetValue(anim, out var s))
        {
            string dir = $"{FamilyRoot}/parts/{Folder}";
            _sheets[anim] = s = (GD.Load<Texture2D>($"{dir}/{anim}.png"), GD.Load<Texture2D>($"{dir}/{anim}_depth.png"));
        }
        return s;
    }

    public Vector3 Point(string name, string anim, int dir, int frame) => Points[name][anim][dir][frame];

    /// <summary>Como Point, pero false si la pieza no exporta ese punto en esa animación (export antiguo).</summary>
    public bool TryPoint(string name, string anim, int dir, int frame, out Vector3 p)
    {
        p = default;
        if (!Points.TryGetValue(name, out var byAnim) || !byAnim.TryGetValue(anim, out var dirs))
            return false;
        p = dirs[dir][Mathf.Min(frame, dirs[dir].Length - 1)];
        return true;
    }

    /// <summary>anim → rectángulo (x, y, w, h) dentro de la celda completa que ocupan los frames recortados.</summary>
    private readonly Dictionary<string, Rect2I> _crops = new();

    /// <summary>Recorte de la animación; sin recorte (export antiguo), la celda completa.</summary>
    public Rect2I Crop(string anim, int cell) =>
        _crops.TryGetValue(anim, out var r) ? r : new Rect2I(0, 0, cell, cell);

    /// <summary>Sprites de la pieza <paramref name="id"/> para la ranura <paramref name="slot"/> (familia: carpeta
    /// res://assets/&lt;familia&gt;, por defecto la de los mechas).</summary>
    public static MechPartData Get(string id, string slot, string familyRoot = MechSkeletonData.Root)
    {
        // piezas con lado (piernas, brazos, armas) montadas a la izquierda: carpeta con sufijo _l
        string folder = slot.EndsWith("_l") ? id + "_l" : id;
        string key = $"{familyRoot}/{folder}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;
        var j = Json.ParseString(FileAccess.GetFileAsString($"{familyRoot}/parts/{folder}/part.json")).AsGodotDictionary();
        var p = new MechPartData
        {
            Id = id, Folder = folder, FamilyRoot = familyRoot, Slot = j["slot"].AsString(), Maker = j["maker"].AsString(),
        };
        foreach (var (name, anims) in j["points"].AsGodotDictionary())
        {
            var byAnim = new Dictionary<string, Vector3[][]>();
            foreach (var (anim, dirs) in anims.AsGodotDictionary())
                byAnim[anim.AsString()] = dirs.AsGodotArray().Select(d => d.AsGodotArray().Select(f =>
                {
                    var a = f.AsGodotArray();
                    return new Vector3(a[0].AsSingle(), a[1].AsSingle(), a[2].AsSingle());
                }).ToArray()).ToArray();
            p.Points[name.AsString()] = byAnim;
        }
        if (j.TryGetValue("crop", out var crops))
            foreach (var (anim, r) in crops.AsGodotDictionary())
            {
                var a = r.AsGodotArray();
                p._crops[anim.AsString()] = new Rect2I(a[0].AsInt32(), a[1].AsInt32(), a[2].AsInt32(), a[3].AsInt32());
            }
        return Cache[key] = p;
    }
}
