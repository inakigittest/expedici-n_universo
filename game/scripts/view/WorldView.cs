using System;
using System.Collections.Generic;
using System.Linq;
using AresX.AI;
using AresX.Core;
using AresX.Sim;
using Godot;

namespace AresX.View;

/// <summary>Conversión entre la cuadrícula de la simulación y el mundo 3D (1 celda = 3 unidades; escala vertical real).</summary>
public static class Grid
{
    public const float U = 3f;                       // unidades de mundo por celda
    public static float V => U / Map.CellM;          // unidades de mundo por metro de altura (escala real, sin exageración)
    public static WorldMap Map = null!;
    /// <summary>Malla del terreno de un escenario a escala (la superposición de creencias la reutiliza).</summary>
    public static ArrayMesh? TerrainMesh;
    public static bool Large => Map != null && Map.N > 64;

    public static float Half => Map.N * U / 2f;

    public static Vector3 CellCenter(Cell c) => new(c.X * U + U / 2 - Half, Map.Height(c) * V, c.Y * U + U / 2 - Half);

    /// <summary>Altura del relieve fino interpolada en (x, z) del mundo.</summary>
    public static float HeightAt(float x, float z)
    {
        float fx = (x + Half) / U * WorldMap.Sub, fz = (z + Half) / U * WorldMap.Sub;
        int fn = Map.FN;
        fx = Mathf.Clamp(fx, 0, fn - 1.001f);
        fz = Mathf.Clamp(fz, 0, fn - 1.001f);
        int i = (int)fx, j = (int)fz;
        float tx = fx - i, tz = fz - j;
        float a = Mathf.Lerp(Map.FineAt(i, j), Map.FineAt(i + 1, j), tx);
        float b = Mathf.Lerp(Map.FineAt(i, j + 1), Map.FineAt(i + 1, j + 1), tx);
        return Mathf.Lerp(a, b, tz) * V;
    }

    public static Vector3 OnGround(float x, float z, float lift = 0) => new(x, HeightAt(x, z) + lift, z);

    public static Cell WorldToCell(Vector3 p) => new((int)MathF.Floor((p.X + Half) / U), (int)MathF.Floor((p.Z + Half) / U));
}

/// <summary>Paletas de color del terreno por suelo y planeta.</summary>
public static class Palette
{
    public static Color Soil(Soil s, TerrainStyle st) => s switch
    {
        Sim.Soil.Regolito => st == TerrainStyle.Mercurio ? new Color("7d7a76") : new Color("b0643c"),
        Sim.Soil.Roca => st == TerrainStyle.Mercurio ? new Color("5e5b58") : st == TerrainStyle.Venus ? new Color("6b4a2a") : new Color("8a4a2e"),
        Sim.Soil.Arena => new Color("cf8f5a"),
        Sim.Soil.Hielo => new Color("dfe9f0"),
        Sim.Soil.Basalto => new Color("8a5a2c"),
        Sim.Soil.Urbano => new Color("a8a39a"),
        Sim.Soil.Bosque => new Color("3f6a34"),
        Sim.Soil.Matorral => new Color("8a9a4a"),
        Sim.Soil.SueloDesnudo => new Color("b89a72"),
        Sim.Soil.Pastizal => new Color("a8b860"),
        Sim.Soil.Agua => new Color("3a78c8"),
        Sim.Soil.Barro => new Color("6a5034"),
        Sim.Soil.Nube => new Color("d8c39a"),
        Sim.Soil.Cultivo => new Color("b8a868"),
        Sim.Soil.Quemado => new Color("4a3a30"),
        _ => new Color("909090"),
    };

    public static Color Giant(string id, float t) => id switch
    {
        "jupiter" => new Color("d9b48a").Lerp(new Color("a8643c"), t),
        "saturno" => new Color("ead6a4").Lerp(new Color("c8a868"), t),
        "urano" => new Color("bfe8ec").Lerp(new Color("8cc8d0"), t),
        _ => new Color("5a82e0").Lerp(new Color("2a4aa8"), t),
    };
}

/// <summary>Malla del terreno: triángulos de caras planas coloreados por celda (como el terreno del proyecto de mechas).</summary>
public partial class TerrainView : Node3D
{
    private MeshInstance3D _mesh = null!;
    private int _builtVersion = -1;

    public override void _Ready()
    {
        _mesh = new MeshInstance3D
        {
            Name = "TerrainMesh",
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 1f },
        };
        AddChild(_mesh);
    }

    public override void _Process(double delta)
    {
        if (Grid.Map != null && Grid.Map.HeightVersion != _builtVersion)
            Rebuild();
    }

    public void Rebuild()
    {
        var map = Grid.Map;
        _builtVersion = map.HeightVersion;
        if (map.IsScenario)
        {
            RebuildScenario(map);
            return;
        }
        Grid.TerrainMesh = null;
        int fn = map.FN;
        float step = Grid.U / WorldMap.Sub;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var noise = new Noise2D(new Rng((ulong)map.Seed + 5));
        Vector3 P(int i, int j) => new(i * step - Grid.Half, map.FineAt(i, j) * Grid.V, j * step - Grid.Half);
        for (int j = 0; j < fn - 1; j++)
        for (int i = 0; i < fn - 1; i++)
        {
            var cell = new Cell(Math.Min(i / WorldMap.Sub, map.N - 1), Math.Min(j / WorldMap.Sub, map.N - 1));
            int ci = map.Idx(cell);
            Color col = map.Planet.Style == TerrainStyle.Gaseoso
                ? Palette.Giant(map.Planet.Id, 0.5f + 0.5f * MathF.Sin(j * 0.18f + noise.Fbm(i * 0.05f, j * 0.05f) * 4f))
                : Palette.Soil(map.Soils[ci], map.Planet.Style);
            float n = noise.Fbm(i * 0.21f, j * 0.21f, 2) * 0.07f;
            col = col.Lightened(n);
            var cls = WorldMap.ClassOf(map.Hazards[ci]);
            if (cls == TerrainClass.Barranco) col = col.Darkened(0.45f);
            if (map.Hazards[ci] == Hazard.Downdraft) col = new Color("2a2440");
            Vector3 a = P(i, j), b = P(i + 1, j), c = P(i, j + 1), d = P(i + 1, j + 1);
            st.SetColor(col);
            if (((i + j) & 1) == 0)
            {
                Tri(st, a, b, d);
                Tri(st, a, d, c);
            }
            else
            {
                Tri(st, a, b, c);
                Tri(st, b, d, c);
            }
        }
        st.GenerateNormals();
        _mesh.Mesh = st.Commit();
    }

    private static void Tri(SurfaceTool st, Vector3 p0, Vector3 p1, Vector3 p2)
    {
        st.AddVertex(p0);
        st.AddVertex(p1);
        st.AddVertex(p2);
    }

    private static NoiseTexture2D? _detail;

    /// <summary>Textura de grano (ruido fractal sin costuras) que modula la imagen satelital entre ×0,78 y ×1,1.</summary>
    private static Texture2D DetailTexture()
    {
        if (_detail != null) return _detail;
        var grad = new Gradient();
        grad.SetColor(0, new Color(0.78f, 0.78f, 0.78f));
        grad.SetColor(1, new Color(1.1f, 1.1f, 1.08f));
        _detail = new NoiseTexture2D
        {
            Width = 256, Height = 256, Seamless = true, ColorRamp = grad, GenerateMipmaps = true,
            Noise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular, Frequency = 0.035f, FractalType = FastNoiseLite.FractalTypeEnum.Fbm, FractalOctaves = 3 },
        };
        return _detail;
    }

    /// <summary>
    /// Escenario GeoClick: malla indexada con un vértice por esquina de celda (≈100 000 vértices para 9 km a 30 m) y
    /// la imagen Sentinel-2 del informe como textura (el detalle visual viene de la textura, no de más polígonos).
    /// </summary>
    private void RebuildScenario(WorldMap map)
    {
        int n = map.N, vn = n + 1;
        var tex = Core.Scenarios.Satellite(map.Geo);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int j = 0; j < vn; j++)
        for (int i = 0; i < vn; i++)
        {
            st.SetUV(new Vector2(i / (float)n, j / (float)n));
            st.SetUV2(new Vector2(i * 0.5f, j * 0.5f));
            if (tex == null)
            {
                var c = map.Clamp(new Cell(i, j));
                st.SetColor(Palette.Soil(map.Soils[map.Idx(c)], map.Planet.Style));
            }
            st.AddVertex(new Vector3(i * Grid.U - Grid.Half, map.FineAt(i * WorldMap.Sub, j * WorldMap.Sub) * Grid.V, j * Grid.U - Grid.Half));
        }
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            int a = j * vn + i, b = a + 1, c = a + vn, d = c + 1;
            st.AddIndex(a); st.AddIndex(b); st.AddIndex(d);
            st.AddIndex(a); st.AddIndex(d); st.AddIndex(c);
        }
        st.GenerateNormals();
        var mesh = st.Commit();
        _mesh.Mesh = mesh;
        Grid.TerrainMesh = mesh;
        var mat = tex != null
            ? new StandardMaterial3D { AlbedoTexture = tex, Roughness = 1f, TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic }
            : new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 1f };
        // detalle procedural (asset generado, no geometría): grano de suelo multiplicado sobre la imagen satelital,
        // para que de cerca no se vea solo el píxel de 8–10 m de Sentinel-2
        mat.DetailEnabled = true;
        mat.DetailBlendMode = BaseMaterial3D.BlendModeEnum.Mul;
        mat.DetailUVLayer = BaseMaterial3D.DetailUV.UV2;
        mat.DetailAlbedo = DetailTexture();
        _mesh.MaterialOverride = mat;
    }
}

/// <summary>Objetos del terreno según los peligros de verdad: rocas, detritos, edificios, árboles, tormentas.</summary>
public partial class PropsView : Node3D
{
    private int _builtVersion = -1, _builtHeights = -1;
    private readonly Dictionary<Hazard, MultiMeshInstance3D> _mm = new();

    public override void _Process(double delta)
    {
        if (Grid.Map != null && (Grid.Map.Version != _builtVersion || Grid.Map.HeightVersion != _builtHeights))
            Rebuild();
    }

    private MultiMeshInstance3D Get(Hazard h)
    {
        if (_mm.TryGetValue(h, out var m)) return m;
        Mesh mesh = h switch
        {
            Hazard.Building => new BoxMesh { Size = new Vector3(1, 1, 1) },
            Hazard.Tree => new CylinderMesh { TopRadius = 0f, BottomRadius = 0.5f, Height = 1f, RadialSegments = 6, Rings = 1 },
            Hazard.Storm => new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 8, Rings = 4 },
            _ => new SphereMesh { Radius = 0.5f, Height = 0.8f, RadialSegments = 6, Rings = 3 },
        };
        var mat = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 1f };
        if (h == Hazard.Storm)
        {
            mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            mat.AlbedoColor = new Color(1, 1, 1, 0.85f);
        }
        mesh.SurfaceSetMaterial(0, mat);
        m = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = mesh },
        };
        AddChild(m);
        _mm[h] = m;
        return m;
    }

    public void Rebuild()
    {
        var map = Grid.Map;
        _builtVersion = map.Version;
        _builtHeights = map.HeightVersion;
        var rng = new Rng((ulong)map.Seed + 77);
        var groups = new Dictionary<Hazard, List<(Transform3D T, Color C)>>();
        for (int y = 0; y < map.N; y++)
        for (int x = 0; x < map.N; x++)
        {
            var c = new Cell(x, y);
            var h = map.Hazards[map.Idx(c)];
            if (h is Hazard.None or Hazard.Cliff or Hazard.Crater or Hazard.Water or Hazard.Downdraft or Hazard.Alien) continue;
            var key = h == Hazard.Debris ? Hazard.Rock : h;
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
            var center = Grid.CellCenter(c);
            float oh = MathF.Max(0.6f, map.ObstacleH[map.Idx(c)]);
            Color baseCol = Palette.Soil(map.Planet.Style == TerrainStyle.Tierra ? Soil.Roca : map.Soils[map.Idx(c)], map.Planet.Style).Darkened(0.15f);
            switch (key)
            {
                case Hazard.Building:
                {
                    var s = new Vector3(2.2f, oh * Grid.V * 1.6f, 2.2f);
                    var p = Grid.OnGround(center.X, center.Z, s.Y / 2);
                    list.Add((new Transform3D(Basis.Identity.Scaled(s), p), new Color("c8c2b8").Darkened(rng.F() * 0.3f)));
                    break;
                }
                case Hazard.Tree:
                    for (int k = 0; k < (Grid.Large ? 2 : 3); k++)
                    {
                        float hh = oh * Grid.V * rng.Range(0.8f, 1.3f) + 1f;
                        var p = Grid.OnGround(center.X + rng.Range(-0.9f, 0.9f), center.Z + rng.Range(-0.9f, 0.9f), hh / 2);
                        list.Add((new Transform3D(Basis.Identity.Scaled(new Vector3(1.2f, hh, 1.2f)), p), new Color("2f5a2a").Lightened(rng.F() * 0.2f)));
                    }
                    break;
                case Hazard.Storm:
                    for (int k = 0; k < 3; k++)
                    {
                        float r = rng.Range(1.4f, 2.4f);
                        var p = Grid.OnGround(center.X + rng.Range(-0.7f, 0.7f), center.Z + rng.Range(-0.7f, 0.7f), 1.2f + k * 1.1f);
                        list.Add((new Transform3D(Basis.Identity.Scaled(new Vector3(r, r * 0.8f, r)), p), new Color("4a4058").Lightened(k * 0.12f)));
                    }
                    break;
                default:
                {
                    int count = h == Hazard.Debris ? 4 : 2;
                    for (int k = 0; k < count; k++)
                    {
                        float r = oh * Grid.V * rng.Range(0.9f, 1.6f) + 0.5f;
                        var basis = new Basis(Vector3.Up, rng.Range(0, 6.28f)).Scaled(new Vector3(r * rng.Range(0.9f, 1.5f), r, r * rng.Range(0.8f, 1.3f)));
                        var p = Grid.OnGround(center.X + rng.Range(-0.8f, 0.8f), center.Z + rng.Range(-0.8f, 0.8f), r * 0.25f);
                        list.Add((new Transform3D(basis, p), (h == Hazard.Debris ? new Color("6a5034") : baseCol).Lightened(rng.F() * 0.15f)));
                    }
                    break;
                }
            }
        }
        foreach (var (h, inst) in _mm) inst.Multimesh.InstanceCount = 0;
        foreach (var (h, list) in groups)
        {
            var mm = Get(h).Multimesh;
            mm.InstanceCount = list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                mm.SetInstanceTransform(i, list[i].T);
                mm.SetInstanceColor(i, list[i].C);
            }
        }
    }
}

/// <summary>
/// Capas informativas sobre el terreno: creencias del rover (prohibidas, vetadas, riesgo), rutas (planificada,
/// candidatas del AG, ejecutada), sitios, refugios, objetivo, celda evaluada y alcance de los sensores.
/// </summary>
public partial class OverlayView : Node3D
{
    public Mission? Mission;
    public bool ShowRisk = true, ShowCandidates = true, ShowTruth;

    private MeshInstance3D _cells = null!, _routes = null!, _front = null!, _range = null!, _target = null!;
    private Node3D _markers = null!;
    private int _beliefVersion = -1, _trailCount = -1, _mapVersion = -1;
    private object? _lastRoute, _lastGa;
    private bool _lastShowRisk, _lastShowCand, _lastTruth;
    private readonly Dictionary<int, (Node3D Node, Label3D Label, MeshInstance3D Pole)> _siteNodes = new();
    private float _t;

    public override void _Ready()
    {
        var alphaMat = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled, NoDepthTest = false,
        };
        _cells = new MeshInstance3D { Name = "BeliefCells", MaterialOverride = alphaMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _routes = new MeshInstance3D { Name = "Routes", MaterialOverride = alphaMat.Duplicate() as Material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _front = new MeshInstance3D { Name = "FrontCell", MaterialOverride = alphaMat.Duplicate() as Material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _range = new MeshInstance3D
        {
            Name = "SensorRange",
            Mesh = new TorusMesh { InnerRadius = 0.97f, OuterRadius = 1f, Rings = 48, RingSegments = 3 },
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.4f, 0.9f, 1f, 0.35f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        _target = new MeshInstance3D
        {
            Name = "Target",
            Mesh = new TorusMesh { InnerRadius = 1.1f, OuterRadius = 1.45f, Rings = 32, RingSegments = 6 },
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color("ffd860"), EmissionEnabled = true, Emission = new Color("ffd860") },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        _markers = new Node3D { Name = "Markers" };
        AddChild(_cells);
        AddChild(_routes);
        AddChild(_front);
        AddChild(_range);
        AddChild(_target);
        AddChild(_markers);
    }

    public void Reset()
    {
        foreach (var c in _markers.GetChildren()) c.QueueFree();
        _siteNodes.Clear();
        _beliefVersion = _trailCount = _mapVersion = -1;
        _lastRoute = _lastGa = null;
        BuildMarkers();
    }

    private void BuildMarkers()
    {
        var m = Mission!;
        foreach (var r in m.Map.Refuges)
        {
            var p = Grid.CellCenter(r);
            var col = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 1.2f, BottomRadius = 1.2f, Height = 9f },
                MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.3f, 1f, 0.5f, 0.22f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha },
                Position = p + Vector3.Up * 4.5f,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            _markers.AddChild(col);
            var pad = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 1.4f, BottomRadius = 1.5f, Height = 0.3f },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("5a6a7a"), Metallic = 0.3f },
                Position = p + Vector3.Up * 0.1f,
            };
            _markers.AddChild(pad);
            _markers.AddChild(MakeLabel(r == m.Map.Start ? "BASE · REFUGIO" : "ESTACIÓN · REFUGIO", p + Vector3.Up * 9.8f, new Color("8cff9a")));
        }
        foreach (var s in m.Map.Sites) AddSiteMarker(s);
    }

    private void AddSiteMarker(ScienceSite s)
    {
        {
            var p = Grid.CellCenter(s.Cell);
            var node = new Node3D { Position = p };
            var pole = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.12f, Height = 4.5f },
                MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color("ffb347") },
                Position = Vector3.Up * 2.25f,
            };
            var flag = new MeshInstance3D
            {
                Mesh = new PrismMesh { Size = new Vector3(1.2f, 0.8f, 0.1f) },
                MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color("ffb347") },
                Position = new Vector3(0.6f, 4.2f, 0),
                RotationDegrees = new Vector3(0, 0, -90),
            };
            node.AddChild(pole);
            node.AddChild(flag);
            var label = MakeLabel("", Vector3.Up * 5.4f, new Color("ffd8a0"));
            node.AddChild(label);
            _markers.AddChild(node);
            _siteNodes[s.Id] = (node, label, pole);
        }
    }

    private static Label3D MakeLabel(string text, Vector3 pos, Color c) => new()
    {
        Text = text, Position = pos, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 40, PixelSize = 0.012f,
        Modulate = c, OutlineSize = 10, OutlineModulate = new Color(0, 0, 0, 0.85f), NoDepthTest = true, RenderPriority = 10,
    };

    public override void _Process(double delta)
    {
        var m = Mission;
        if (m == null) return;
        _t += (float)delta;
        if (m.Belief.Version != _beliefVersion || ShowRisk != _lastShowRisk || ShowTruth != _lastTruth || m.Map.HeightVersion != _mapVersion)
            BuildCells(m);
        if (!ReferenceEquals(m.Route, _lastRoute) || !ReferenceEquals(m.LastGa, _lastGa) || m.Trail.Count != _trailCount || ShowCandidates != _lastShowCand)
            BuildRoutes(m);
        // sitios
        foreach (var s in m.Map.Sites)
        {
            if (!_siteNodes.ContainsKey(s.Id)) AddSiteMarker(s);   // objetivos nuevos (p. ej. propuestos por el dron)
            var (node, label, pole) = _siteNodes[s.Id];
            bool planned = m.Plan?.Order.Contains(s) == true;
            int order = m.Plan?.Order.IndexOf(s) ?? -1;
            label.Text = $"{s.Name} ★{s.Value:0.00}{(s.Anomaly ? " ◆" : "")}\n{s.Kind}" + (s.Visited ? "\n✓ muestreado" : planned ? $"\nplan #{order + 1}" : "\nfuera del plan");
            var col = s.Visited ? new Color("8a8f98") : s.Anomaly ? new Color("d08cff") : planned ? new Color("ffb347") : new Color("c8a070");
            label.Modulate = col.Lightened(0.3f);
            ((StandardMaterial3D)pole.MaterialOverride).AlbedoColor = col;
            node.Position = Grid.CellCenter(s.Cell);
        }
        // objetivo
        _target.Position = Grid.CellCenter(m.Target) + Vector3.Up * (0.4f + 0.15f * MathF.Sin(_t * 4));
        _target.RotationDegrees = new Vector3(0, _t * 40, 0);
        // celda evaluada en este paso y celda bajo el mouse
        BuildFront(m);
        UpdateBeacon(m);
        // alcance de sensores (limitado por la visibilidad)
        float rangeCells = Mathf.Clamp(m.Env.Visibility / m.Map.CellM, 1.5f, 4.2f);
        if (!m.Env.IsDay) rangeCells = Mathf.Min(rangeCells, 3.2f);
        _range.Scale = new Vector3(rangeCells * Grid.U, 1, rangeCells * Grid.U);
    }

    public Vector3 RobotPos { get; set; }
    public Cell? HoverCell { get; set; }
    private MeshInstance3D? _beacon;
    private Label3D? _destLabel;

    public override void _PhysicsProcess(double delta) => _range.Position = RobotPos + Vector3.Up * 0.25f;

    private void Quad(SurfaceTool st, Cell c, Color col, float lift, float inset = 0.06f)
    {
        float x0 = c.X * Grid.U - Grid.Half + inset * Grid.U, x1 = (c.X + 1) * Grid.U - Grid.Half - inset * Grid.U;
        float z0 = c.Y * Grid.U - Grid.Half + inset * Grid.U, z1 = (c.Y + 1) * Grid.U - Grid.Half - inset * Grid.U;
        int seg = 3;
        st.SetColor(col);
        for (int j = 0; j < seg; j++)
        for (int i = 0; i < seg; i++)
        {
            float ax = Mathf.Lerp(x0, x1, i / (float)seg), bx = Mathf.Lerp(x0, x1, (i + 1) / (float)seg);
            float az = Mathf.Lerp(z0, z1, j / (float)seg), bz = Mathf.Lerp(z0, z1, (j + 1) / (float)seg);
            Vector3 a = Grid.OnGround(ax, az, lift), b = Grid.OnGround(bx, az, lift), cc = Grid.OnGround(ax, bz, lift), d = Grid.OnGround(bx, bz, lift);
            st.AddVertex(a); st.AddVertex(b); st.AddVertex(d);
            st.AddVertex(a); st.AddVertex(d); st.AddVertex(cc);
        }
    }

    private void Cross(SurfaceTool st, Cell c, Color col, float lift)
    {
        var ctr = Grid.CellCenter(c);
        float h = Grid.U * 0.38f;
        Ribbon(st, Grid.OnGround(ctr.X - h, ctr.Z - h, lift), Grid.OnGround(ctr.X + h, ctr.Z + h, lift), 0.22f, col);
        Ribbon(st, Grid.OnGround(ctr.X - h, ctr.Z + h, lift), Grid.OnGround(ctr.X + h, ctr.Z - h, lift), 0.22f, col);
    }

    private static void Ribbon(SurfaceTool st, Vector3 a, Vector3 b, float w, Color col)
    {
        var dir = b - a;
        dir.Y = 0;
        if (dir.LengthSquared() < 1e-6f) return;
        var side = new Vector3(-dir.Z, 0, dir.X).Normalized() * w / 2;
        st.SetColor(col);
        st.AddVertex(a - side); st.AddVertex(a + side); st.AddVertex(b + side);
        st.AddVertex(a - side); st.AddVertex(b + side); st.AddVertex(b - side);
    }

    private MeshInstance3D? _cellTexMesh;
    private ImageTexture? _cellTex;
    private byte[]? _cellBytes;

    /// <summary>
    /// Mapas grandes: las creencias se pintan en una textura N×N (un píxel por celda) aplicada sobre la misma malla del
    /// terreno, en lugar de miles de quads por paso. Fuera del polígono analizado se oscurece.
    /// </summary>
    private void BuildCellsTexture(Mission m)
    {
        if (Grid.TerrainMesh == null) { _beliefVersion = -1; return; }
        _beliefVersion = m.Belief.Version;
        _lastShowRisk = ShowRisk;
        _lastTruth = ShowTruth;
        _mapVersion = m.Map.HeightVersion;
        var b = m.Belief;
        var map = m.Map;
        int n = map.N;
        _cellBytes ??= new byte[n * n * 4];
        var px = _cellBytes;
        for (int i = 0; i < n * n; i++)
        {
            var c = new Cell(i % n, i / n);
            byte r = 0, g = 0, bl = 0, a = 0;
            void Set(Color col) { r = (byte)(col.R * 255); g = (byte)(col.G * 255); bl = (byte)(col.B * 255); a = (byte)(col.A * 255); }
            if (!map.InBounds(c)) Set(new Color(0.03f, 0.04f, 0.06f, 0.62f));
            else if (b.ConfirmedCliff[i]) Set(new Color(1f, 0.15f, 0.1f, 0.6f));
            else if (b.ConfirmedObstacle[i]) Set(new Color(1f, 0.55f, 0.1f, 0.5f));
            else if (b.Vetoed[i]) Set(new Color(0.9f, 0.2f, 0.9f, 0.55f));
            else if (ShowRisk && b.Observed[i])
            {
                float rk = b.Risk(c);
                if (rk > 0.15f)
                {
                    var col = rk < 0.5f ? new Color(1f, 0.9f, 0.2f).Lerp(new Color(1f, 0.5f, 0.1f), (rk - 0.15f) / 0.35f)
                                        : new Color(1f, 0.5f, 0.1f).Lerp(new Color(1f, 0.1f, 0.1f), (rk - 0.5f) / 0.5f);
                    col.A = 0.14f + 0.26f * rk;
                    Set(col);
                }
            }
            if (ShowTruth && map.InBounds(c) && map.ClassAt(c) != TerrainClass.Libre && !b.Forbidden(c)) Set(new Color(0.3f, 0.6f, 1f, 0.5f));
            px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = bl; px[i * 4 + 3] = a;
        }
        var img = Image.CreateFromData(n, n, false, Image.Format.Rgba8, px);
        if (_cellTex == null || _cellTexMesh == null || _cellTexMesh.Mesh != Grid.TerrainMesh)
        {
            _cellTex = ImageTexture.CreateFromImage(img);
            _cellTexMesh?.QueueFree();
            _cellTexMesh = new MeshInstance3D
            {
                Name = "BeliefTexture", Mesh = Grid.TerrainMesh, Position = Vector3.Up * 0.06f, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoTexture = _cellTex, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, RenderPriority = 1,
                },
            };
            AddChild(_cellTexMesh);
        }
        else _cellTex.Update(img);
        _cells.Mesh = null;
    }

    private void BuildCells(Mission m)
    {
        if (m.Map.IsScenario)
        {
            BuildCellsTexture(m);
            return;
        }
        _beliefVersion = m.Belief.Version;
        _lastShowRisk = ShowRisk;
        _lastTruth = ShowTruth;
        _mapVersion = m.Map.HeightVersion;
        var b = m.Belief;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int n = m.Map.N;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            var c = new Cell(x, y);
            int i = b.Idx(c);
            if (b.ConfirmedCliff[i])
            {
                Quad(st, c, new Color(1f, 0.15f, 0.1f, 0.38f), 0.12f);
                Cross(st, c, new Color(1f, 0.85f, 0.8f, 0.9f), 0.2f);
            }
            else if (b.ConfirmedObstacle[i]) Quad(st, c, new Color(1f, 0.55f, 0.1f, 0.35f), 0.12f);
            else if (b.Vetoed[i])
            {
                Quad(st, c, new Color(0.9f, 0.2f, 0.9f, 0.32f), 0.12f);
                Cross(st, c, new Color(1f, 0.7f, 1f, 0.8f), 0.2f);
            }
            else if (ShowRisk && b.Observed[i])
            {
                float r = b.Risk(c);
                if (r > 0.15f)
                {
                    var col = r < 0.5f ? new Color(1f, 0.9f, 0.2f).Lerp(new Color(1f, 0.5f, 0.1f), (r - 0.15f) / 0.35f)
                                       : new Color(1f, 0.5f, 0.1f).Lerp(new Color(1f, 0.1f, 0.1f), (r - 0.5f) / 0.5f);
                    col.A = 0.18f + 0.3f * r;
                    Quad(st, c, col, 0.1f);
                }
            }
            if (ShowTruth && m.Map.InBounds(c) && m.Map.ClassAt(c) != TerrainClass.Libre && !b.Forbidden(c))
                Quad(st, c, new Color(0.3f, 0.6f, 1f, 0.3f), 0.14f, 0.3f);
        }
        _cells.Mesh = st.Commit();
    }

    private void Path(SurfaceTool st, IReadOnlyList<Cell> path, float w, Color col, float lift, bool dashed = false)
    {
        for (int k = 1; k < path.Count; k++)
        {
            var a = Grid.CellCenter(path[k - 1]);
            var b = Grid.CellCenter(path[k]);
            const int sub = 4;
            for (int s = 0; s < sub; s++)
            {
                if (dashed && (k * sub + s) % 2 == 1) continue;
                var p0 = a.Lerp(b, s / (float)sub);
                var p1 = a.Lerp(b, (s + 1) / (float)sub);
                Ribbon(st, Grid.OnGround(p0.X, p0.Z, lift), Grid.OnGround(p1.X, p1.Z, lift), w, col);
            }
        }
    }

    public static readonly Color[] CandidateColors = { new("e35d5d"), new("9a7ad8"), new("e0b040") };

    private void BuildRoutes(Mission m)
    {
        _lastRoute = m.Route;
        _lastGa = m.LastGa;
        _trailCount = m.Trail.Count;
        _lastShowCand = ShowCandidates;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        if (ShowCandidates && m.LastGa != null)
            for (int i = m.LastGa.Candidates.Count - 1; i >= 0; i--)
            {
                var c = m.LastGa.Candidates[i];
                if (ReferenceEquals(c.Path, m.Route) || c.Path.SequenceEqual(m.Route)) continue;
                var col = CandidateColors[i % 3];
                col.A = 0.75f;
                Path(st, c.Path, 0.22f, col, 0.32f + i * 0.02f);
            }
        Path(st, m.Trail, 0.42f, new Color(0.2f, 0.6f, 1f, 0.9f), 0.38f);
        if (m.Route.Count > 1)
        {
            var rest = m.Route.Skip(Math.Max(0, m.RouteIdx)).ToList();
            Path(st, rest, 0.5f, new Color(0.2f, 1f, 0.45f, 0.95f), 0.45f, dashed: true);
        }
        _routes.Mesh = st.Commit();
    }

    private void BuildFront(Mission m)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var d = m.LastDecision;
        if (m.FrontCell is Cell f && m.Map.InBounds(f) && d != null && !m.Finished)
        {
            var col = d.Veto ? new Color(1, 0.15f, 0.15f) : d.Alert || d.Action != RoverAction.Avanzar ? new Color(1, 0.8f, 0.1f) : new Color(0.3f, 1, 0.4f);
            float pulse = 0.55f + 0.35f * MathF.Sin(_t * 8);
            col.A = pulse;
            var c = Grid.CellCenter(f);
            float h = Grid.U * 0.48f;
            Vector3 p00 = Grid.OnGround(c.X - h, c.Z - h, 0.3f), p10 = Grid.OnGround(c.X + h, c.Z - h, 0.3f);
            Vector3 p11 = Grid.OnGround(c.X + h, c.Z + h, 0.3f), p01 = Grid.OnGround(c.X - h, c.Z + h, 0.3f);
            Ribbon(st, p00, p10, 0.25f, col);
            Ribbon(st, p10, p11, 0.25f, col);
            Ribbon(st, p11, p01, 0.25f, col);
            Ribbon(st, p01, p00, 0.25f, col);
        }
        if (HoverCell is Cell hc && m.Map.InBounds(hc))
        {
            var c = Grid.CellCenter(hc);
            float h = Grid.U * 0.5f;
            var col = m.Belief.Forbidden(hc) ? new Color(1, 0.3f, 0.3f, 0.9f) : new Color(1, 1, 1, 0.85f);
            Vector3 p00 = Grid.OnGround(c.X - h, c.Z - h, 0.35f), p10 = Grid.OnGround(c.X + h, c.Z - h, 0.35f);
            Vector3 p11 = Grid.OnGround(c.X + h, c.Z + h, 0.35f), p01 = Grid.OnGround(c.X - h, c.Z + h, 0.35f);
            Ribbon(st, p00, p10, 0.12f, col);
            Ribbon(st, p10, p11, 0.12f, col);
            Ribbon(st, p11, p01, 0.12f, col);
            Ribbon(st, p01, p00, 0.12f, col);
        }
        _front.Mesh = st.Commit();
    }

    /// <summary>Baliza vertical en el destino del operador con su evaluación resumida.</summary>
    private void UpdateBeacon(Mission m)
    {
        if (_beacon == null)
        {
            _beacon = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.25f, BottomRadius = 0.35f, Height = 14f },
                MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.3f, 0.9f, 1f, 0.45f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_beacon);
            _destLabel = MakeLabel("", Vector3.Zero, new Color("9fe8ff"));
            AddChild(_destLabel);
        }
        bool show = m.HasDestination || m.PendingDestination != null;
        _beacon.Visible = _destLabel!.Visible = show;
        if (!show) return;
        var cell = m.PendingDestination?.Cell ?? m.Target;
        var p = Grid.CellCenter(cell);
        _beacon.Position = p + Vector3.Up * 7f;
        var r = m.DestReport;
        var col = r == null ? new Color("9fe8ff") : r.Verdict switch
        {
            DestVerdict.Viable => new Color("7dffa0"), DestVerdict.Arriesgado or DestVerdict.SoloIda => new Color("ffd860"), _ => new Color("ff7060"),
        };
        ((StandardMaterial3D)_beacon.MaterialOverride).AlbedoColor = new Color(col, 0.35f + 0.15f * MathF.Sin(_t * 3));
        int left = Math.Max(0, m.Route.Count - 1 - m.RouteIdx);
        _destLabel.Position = p + Vector3.Up * 14.6f;
        _destLabel.Modulate = col;
        _destLabel.Text = m.PendingDestination != null ? "DESTINO EN TRÁNSITO (enlace)" :
            $"DESTINO · {m.TargetName}\n{r?.VerdictText ?? ""} · faltan {left} pasos · {left * m.Map.CellM:0} m";
    }
}
