using System;
using AresX.Core;
using AresX.Sim;
using Godot;

namespace AresX.View;

/// <summary>
/// Incendio en 3D: llamas (MultiMesh con shader procedural, una o dos por celda en llamas, tamaño según intensidad),
/// columnas de humo que derivan con el viento, y sobre el terreno una textura N×N con lo quemado (negro), lo mojado
/// por las descargas (cortafuego azul) y el resplandor de las celdas en llamas. Se reconstruye cuando cambia el fuego.
/// </summary>
public partial class FireView : Node3D
{
    public Mission? Mission;
    private MultiMeshInstance3D _flames = null!, _smoke = null!;
    private ShaderMaterial _smokeMat = null!;
    private MeshInstance3D? _overlay;
    private ImageTexture? _tex;
    private byte[]? _px;
    private int _version = -1;
    private float _texAge;
    private const int MaxFlames = 6000, MaxSmoke = 900;

    public override void _Ready()
    {
        var m = Mission;
        if (m == null || !m.FireMode) return;
        _flames = new MultiMeshInstance3D
        {
            Name = "Flames", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = new QuadMesh { Size = new Vector2(1, 1) } },
            MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/fire_flame.gdshader") },
        };
        AddChild(_flames);
        _smokeMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/fire_smoke.gdshader") };
        _smoke = new MultiMeshInstance3D
        {
            Name = "Smoke", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = new QuadMesh { Size = new Vector2(1, 1) } },
            MaterialOverride = _smokeMat,
        };
        AddChild(_smoke);
        // luz cálida del incendio (sigue al foco principal)
        AddChild(new OmniLight3D { Name = "Glow", LightColor = new Color("ff7a2a"), LightEnergy = 0f, OmniRange = Grid.U * 14, ShadowEnabled = false });
    }

    public override void _Process(double delta)
    {
        var m = Mission;
        if (m == null || !m.FireMode || _flames == null) return;
        _texAge += (float)delta;
        // viento para el humo (hacia donde sopla)
        float wr = m.Env.WindDirDeg * MathF.PI / 180f;
        _smokeMat.SetShaderParameter("wind", new Vector2(-MathF.Sin(wr), MathF.Cos(wr)) * MathF.Min(1.5f, m.Env.WindSpeed / 6f));
        if (m.FireVersion == _version && _texAge < 1.5f) return;
        _version = m.FireVersion;
        _texAge = 0;
        Rebuild(m);
    }

    private void Rebuild(Mission m)
    {
        int n = m.Map.N;
        float cellSize = Grid.U;
        // llamas
        int count = Math.Min(MaxFlames, m.Burning.Count * 2);
        var mm = _flames.Multimesh;
        mm.InstanceCount = count;
        int k = 0;
        Vector3 sum = Vector3.Zero;
        foreach (int i in m.Burning)
        {
            if (k >= count) break;
            var c = new Cell(i % n, i / n);
            var ctr = Grid.CellCenter(c);
            sum += ctr;
            float heat = m.FireHeat[i];
            float fuel = m.Fuel[i];
            for (int s = 0; s < 2 && k < count; s++)
            {
                float h = (i * 0.618f + s * 0.37f) % 1f;
                var off = new Vector3((h - 0.5f) * cellSize * 0.6f, 0, ((h * 7.3f) % 1f - 0.5f) * cellSize * 0.6f);
                var p = ctr + off;
                p.Y = Grid.HeightAt(p.X, p.Z);
                mm.SetInstanceTransform(k, new Transform3D(Basis.Identity, p));
                float size = cellSize * (0.45f + 0.55f * fuel) * (0.5f + 0.7f * heat) * (s == 0 ? 1f : 0.7f);
                mm.SetInstanceCustomData(k, new Color(size, h, heat, 0));
                k++;
            }
        }
        // humo: una columna cada pocas celdas en llamas
        var sm = _smoke.Multimesh;
        int sc = Math.Min(MaxSmoke, m.Burning.Count / 3 + (m.Burning.Count > 0 ? 1 : 0));
        sm.InstanceCount = sc;
        for (int j = 0; j < sc; j++)
        {
            int i = m.Burning[(j * 3) % m.Burning.Count];
            var ctr = Grid.CellCenter(new Cell(i % n, i / n));
            ctr.Y = Grid.HeightAt(ctr.X, ctr.Z) + cellSize * 0.8f;
            sm.SetInstanceTransform(j, new Transform3D(Basis.Identity, ctr));
            sm.SetInstanceCustomData(j, new Color(cellSize * 2.2f, (j * 0.137f) % 1f, cellSize * 9f, 0));
        }
        var glow = GetNode<OmniLight3D>("Glow");
        if (m.Burning.Count > 0)
        {
            var center = sum / Math.Max(1, Math.Min(m.Burning.Count, MaxFlames / 2));
            glow.Position = center + Vector3.Up * cellSize * 3;
            glow.LightEnergy = Math.Min(3f, 0.6f + m.Burning.Count / 80f);
            glow.OmniRange = cellSize * (10 + MathF.Sqrt(m.Burning.Count) * 2);
        }
        else glow.LightEnergy = 0;
        BuildOverlay(m);
    }

    /// <summary>Textura sobre el terreno: quemado, mojado (cortafuego) y resplandor de lo que arde.</summary>
    private void BuildOverlay(Mission m)
    {
        int n = m.Map.N;
        _px ??= new byte[n * n * 4];
        for (int i = 0; i < n * n; i++)
        {
            byte r = 0, g = 0, b = 0, a = 0;
            switch (m.FireState[i])
            {
                case 1: r = 255; g = 90; b = 20; a = 150; break;
                case 2: r = 18; g = 14; b = 12; a = 215; break;
                case 3: r = 60; g = 150; b = 255; a = 70; break;
            }
            _px[i * 4] = r; _px[i * 4 + 1] = g; _px[i * 4 + 2] = b; _px[i * 4 + 3] = a;
        }
        var img = Image.CreateFromData(n, n, false, Image.Format.Rgba8, _px);
        if (_tex == null) _tex = ImageTexture.CreateFromImage(img);
        else _tex.Update(img);
        if (_overlay != null) return;
        Mesh mesh;
        if (Grid.TerrainMesh != null) mesh = Grid.TerrainMesh;
        else
        {
            // terreno procedural: plano con la forma del relieve (rejilla de la celda)
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                Vector3 P(int cx, int cy) { float wx = cx * Grid.U - Grid.Half, wz = cy * Grid.U - Grid.Half; return new Vector3(wx, Grid.HeightAt(wx, wz), wz); }
                Vector2 T(int cx, int cy) => new((float)cx / n, (float)cy / n);
                var a = P(x, y); var b = P(x + 1, y); var c = P(x + 1, y + 1); var d = P(x, y + 1);
                st.SetUV(T(x, y)); st.AddVertex(a); st.SetUV(T(x + 1, y)); st.AddVertex(b); st.SetUV(T(x + 1, y + 1)); st.AddVertex(c);
                st.SetUV(T(x, y)); st.AddVertex(a); st.SetUV(T(x + 1, y + 1)); st.AddVertex(c); st.SetUV(T(x, y + 1)); st.AddVertex(d);
            }
            mesh = st.Commit();
        }
        _overlay = new MeshInstance3D
        {
            Name = "FireOverlay", Mesh = mesh, Position = Vector3.Up * 0.09f, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoTexture = _tex, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear, RenderPriority = 2,
            },
        };
        AddChild(_overlay);
    }
}
