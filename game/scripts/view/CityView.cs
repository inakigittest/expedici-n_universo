using System;
using System.Collections.Generic;
using AresX.Sim;
using Godot;

namespace AresX.View;

/// <summary>
/// Ciudad simulada de un escenario (WorldMap.City): los edificios del kit (BuildingKit) unidos en una malla por trozo de
/// Chunk×Chunk celdas con shaders/city_building.gdshader. Dos niveles de detalle: el completo se arma solo para los
/// trozos cercanos a la cámara (y se conserva); lejos, un bloque por edificio con su huella y altura. Con la cámara
/// muy alejada todo pasa al bloque. Ventanas encendidas de noche y recorte transparente alrededor del vehículo.
/// </summary>
public partial class CityView : Node3D
{
    public Mission? Mission;
    public Func<Vector3>? Focus;          // posición del vehículo (recorte transparente)
    private const int Chunk = 32;
    private readonly Dictionary<int, List<CityBuilding>> _chunks = new();
    private readonly Dictionary<int, MeshInstance3D> _far = new(), _near = new();
    private ShaderMaterial _mat = null!;
    private float _timer;
    private int _cn;

    public override void _Ready()
    {
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/city_building.gdshader") };
        var m = Mission;
        if (m?.Map.City == null || m.Map.City.Buildings.Count == 0) return;
        var map = m.Map;
        _mat.SetShaderParameter("units_per_meter", Grid.U / map.CellM);
        _cn = (map.N + Chunk - 1) / Chunk;
        foreach (var b in map.City.Buildings)
        {
            int cx = Math.Clamp((int)(b.X / map.CellM) / Chunk, 0, _cn - 1), cz = Math.Clamp((int)(b.Z / map.CellM) / Chunk, 0, _cn - 1);
            int key = cz * _cn + cx;
            if (!_chunks.TryGetValue(key, out var list)) _chunks[key] = list = new List<CityBuilding>();
            list.Add(b);
        }
        foreach (var (key, list) in _chunks)
        {
            var mi = new MeshInstance3D { Mesh = BuildMesh(list, map, false), MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(mi);
            _far[key] = mi;
        }
    }

    // ------------------------------------------------------------------ mallas
    private static readonly Vector3[] BoxV = BuildBox();
    private static readonly Vector3[] CylV = BuildCyl(10, false);
    private static readonly Vector3[] ConeV = BuildCyl(10, true);
    private static readonly Vector3[] EllV = BuildEll(8, 5);

    private static Vector3[] BuildBox()
    {
        var c = new Vector3[8];
        for (int i = 0; i < 8; i++) c[i] = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f);
        int[] f = { 0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5 };
        var v = new Vector3[f.Length];
        for (int i = 0; i < f.Length; i++) v[i] = c[f[i]];
        return v;
    }

    /// <summary>Cilindro (radio 1, alto 1, centrado) o cono (base abajo, punta arriba).</summary>
    private static Vector3[] BuildCyl(int seg, bool cone)
    {
        var v = new List<Vector3>();
        for (int i = 0; i < seg; i++)
        {
            float a0 = i * MathF.Tau / seg, a1 = (i + 1) * MathF.Tau / seg;
            var b0 = new Vector3(MathF.Cos(a0), -0.5f, MathF.Sin(a0)); var b1 = new Vector3(MathF.Cos(a1), -0.5f, MathF.Sin(a1));
            var t0 = cone ? new Vector3(0, 0.5f, 0) : new Vector3(b0.X, 0.5f, b0.Z); var t1 = cone ? new Vector3(0, 0.5f, 0) : new Vector3(b1.X, 0.5f, b1.Z);
            v.AddRange(new[] { b0, t0, b1 });
            if (!cone) v.AddRange(new[] { b1, t0, t1 });
            v.AddRange(new[] { new Vector3(0, -0.5f, 0), b0, b1 });
            if (!cone) v.AddRange(new[] { new Vector3(0, 0.5f, 0), t1, t0 });
        }
        return v.ToArray();
    }

    private static Vector3[] BuildEll(int seg, int rings)
    {
        var v = new List<Vector3>();
        Vector3 P(int i, int j)
        {
            float th = j * MathF.PI / rings, ph = i * MathF.Tau / seg;
            return new Vector3(MathF.Sin(th) * MathF.Cos(ph), MathF.Cos(th), MathF.Sin(th) * MathF.Sin(ph));
        }
        for (int j = 0; j < rings; j++)
        for (int i = 0; i < seg; i++)
        {
            v.AddRange(new[] { P(i, j), P(i + 1, j), P(i + 1, j + 1) });
            v.AddRange(new[] { P(i, j), P(i + 1, j + 1), P(i, j + 1) });
        }
        return v.ToArray();
    }

    /// <summary>Transformación metros locales del edificio → mundo (asentado en el punto más bajo de su huella).</summary>
    private static Transform3D Place(CityBuilding b, WorldMap map)
    {
        float k = Grid.U / map.CellM;
        float wx = b.X * k - Grid.Half, wz = b.Z * k - Grid.Half;
        var (w, d) = CityPlan.Footprint(b.Type);
        float a = b.YawDeg * MathF.PI / 180f, c = MathF.Cos(a), s = MathF.Sin(a);
        float ground = float.MaxValue;
        foreach (var (ox, oz) in new[] { (0f, 0f), (-w / 2, -d / 2), (w / 2, -d / 2), (-w / 2, d / 2), (w / 2, d / 2) })
            ground = MathF.Min(ground, Grid.HeightAt(wx + (ox * c - oz * s) * k, wz + (ox * s + oz * c) * k));
        // el plan gira (x, z) con +a; en Godot eso es una rotación de −a alrededor de Y
        return new Transform3D(new Basis(Vector3.Up, -a).Scaled(new Vector3(k, k, k)), new Vector3(wx, ground - 0.8f * k, wz));
    }

    private static Mesh BuildMesh(List<CityBuilding> list, WorldMap map, bool detail)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var b in list)
        {
            var tb = Place(b, map);
            if (!detail)
            {
                // bloque lejano: huella × altura con el color de la primera pieza (más una franja de ventanas)
                var (w, d) = CityPlan.Footprint(b.Type);
                var prims = BuildingKit.Get(b.Type);
                var col = prims.Length > 0 ? prims[0].Color : new Color("b8b4ac");
                float h = b.HeightM + 0.8f;
                Emit(st, BoxV, tb * new Transform3D(Basis.FromScale(new Vector3(w * 0.85f, h, d * 0.85f)), new Vector3(0, h / 2, 0)), col, (byte)(h > 9 ? 1 : 0), b.Seed);
                continue;
            }
            foreach (var p in BuildingKit.Get(b.Type))
            {
                var verts = p.Shape switch { BuildingKit.Shape.Box => BoxV, BuildingKit.Shape.Cyl => CylV, BuildingKit.Shape.Cone => ConeV, _ => EllV };
                var lift = new Transform3D(Basis.Identity, new Vector3(0, 0.8f, 0));   // el edificio sobre su base hundida
                Emit(st, verts, tb * lift * p.T, p.Color, p.Mat, b.Seed);
            }
        }
        st.GenerateNormals();
        return st.Commit();
    }

    private static void Emit(SurfaceTool st, Vector3[] verts, Transform3D t, Color c, byte mat, int seed)
    {
        st.SetColor(c);
        st.SetUV2(new Vector2(mat, seed % 997));
        for (int i = 0; i < verts.Length; i += 3)
        {
            // las mallas unitarias tienen el giro antihorario visto desde fuera: Godot usa horario como cara frontal
            st.AddVertex(t * verts[i]);
            st.AddVertex(t * verts[i + 2]);
            st.AddVertex(t * verts[i + 1]);
        }
    }

    // ------------------------------------------------------------------ nivel de detalle, noche y recorte
    public override void _Process(double delta)
    {
        var m = Mission;
        if (m?.Map.City == null || _chunks.Count == 0) return;
        if (Focus != null)
        {
            _mat.SetShaderParameter("player_pos", Focus());
            _mat.SetShaderParameter("cutaway_radius", 7.5f);
        }
        float night = Mathf.Clamp(0.35f - m.Env.SunElev * 3f, 0, 1);
        if (!m.Env.IsDay || m.Env.ForcedNight) night = 1;
        _mat.SetShaderParameter("night", night);
        _timer -= (float)delta;
        if (_timer > 0) return;
        _timer = 0.35f;
        var cam = GetViewport()?.GetCamera3D();
        if (cam?.GetParent() is not Node3D rig) return;
        float camSize = cam.Size;
        var map = m.Map;
        float chunkW = Chunk * Grid.U;
        bool closeView = camSize < 260f;                     // con la cámara muy alejada, todo en bloques
        float radius = MathF.Max(chunkW * 1.2f, camSize * 0.9f);
        int built = 0;
        foreach (var (key, list) in _chunks)
        {
            int cx = key % _cn, cz = key / _cn;
            var center = new Vector3((cx + 0.5f) * chunkW - Grid.Half, 0, (cz + 0.5f) * chunkW - Grid.Half);
            bool near = closeView && new Vector2(center.X - rig.GlobalPosition.X, center.Z - rig.GlobalPosition.Z).Length() < radius;
            if (near && !_near.ContainsKey(key) && built < 2)
            {
                // detalle bajo demanda (máx. 2 trozos por pasada para no trabar un frame)
                var mi = new MeshInstance3D { Mesh = BuildMesh(list, map, true), MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                AddChild(mi);
                _near[key] = mi;
                built++;
            }
            bool showNear = near && _near.ContainsKey(key);
            if (_near.TryGetValue(key, out var nm)) nm.Visible = showNear;
            _far[key].Visible = !showNear;
        }
    }
}
