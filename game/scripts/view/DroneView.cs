using System;
using AresX.Core;
using AresX.Sim;
using Godot;

namespace AresX.View;

/// <summary>
/// Dron explorador en 3D: cuadricóptero de primitivas (cuerpo, brazos, rotores que giran, luz de navegación), que
/// sigue suavemente la posición simulada a altura de vuelo; al escanear muestra un cono de barrido sobre la zona.
/// Acoplado, descansa sobre la cubierta del rover.
/// </summary>
public partial class DroneView : Node3D
{
    public Mission? Mission;
    public Node3D? Rover;
    private Node3D _body = null!;
    private readonly MeshInstance3D[] _rotors = new MeshInstance3D[4];
    private MeshInstance3D _scan = null!, _beam = null!;
    private OmniLight3D _light = null!;
    private float _t, _height = 0.6f;
    private Vector3 _pos;
    private bool _init;

    public override void _Ready()
    {
        _body = new Node3D();
        AddChild(_body);
        var dark = new StandardMaterial3D { AlbedoColor = new Color("2a2e35"), Roughness = 0.6f };
        var white = new StandardMaterial3D { AlbedoColor = new Color("e8ecef"), Roughness = 0.5f };
        var orange = new StandardMaterial3D { AlbedoColor = new Color("d9622b"), EmissionEnabled = true, Emission = new Color("d9622b"), EmissionEnergyMultiplier = 0.4f };
        _body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.55f, 0.18f, 0.55f) }, MaterialOverride = white });
        _body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.2f, 0.06f, 0.56f) }, MaterialOverride = orange, Position = new Vector3(0, 0.1f, 0) });
        _body.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.11f, Height = 0.22f }, MaterialOverride = dark, Position = new Vector3(0.22f, -0.1f, 0) });
        var rotorMat = new StandardMaterial3D { AlbedoColor = new Color(0.8f, 0.85f, 0.9f, 0.45f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        for (int i = 0; i < 4; i++)
        {
            float a = Mathf.Pi / 4 + i * Mathf.Pi / 2;
            var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            var arm = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.62f, 0.05f, 0.07f) }, MaterialOverride = dark, Position = dir * 0.3f };
            arm.RotationDegrees = new Vector3(0, -Mathf.RadToDeg(a), 0);
            _body.AddChild(arm);
            _body.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.06f, Height = 0.1f }, MaterialOverride = dark, Position = dir * 0.6f + Vector3.Up * 0.05f });
            _rotors[i] = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.3f, Height = 0.015f, RadialSegments = 12 }, MaterialOverride = rotorMat, Position = dir * 0.6f + Vector3.Up * 0.12f };
            _body.AddChild(_rotors[i]);
            _body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.55f, 0.02f, 0.04f) }, MaterialOverride = white, Position = dir * 0.6f + Vector3.Up * 0.13f });
        }
        _light = new OmniLight3D { LightColor = new Color("3ec8ff"), LightEnergy = 1.2f, OmniRange = 3f, Position = Vector3.Down * 0.2f };
        _body.AddChild(_light);
        _scan = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.15f, BottomRadius = 1f, Height = 1f, RadialSegments = 32, CapTop = false, CapBottom = false },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.85f, 1f, 0.16f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false,
        };
        AddChild(_scan);
        _beam = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = 0.96f, OuterRadius = 1f, Rings = 48, RingSegments = 3 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.95f, 1f, 0.8f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false,
        };
        AddChild(_beam);
    }

    public override void _Process(double delta)
    {
        var m = Mission;
        float dt = (float)delta;
        _t += dt;
        if (m == null || !m.Drone.Available && !m.Drone.Flying)
        {
            Visible = false;
            return;
        }
        Visible = true;
        var d = m.Drone;
        float wx = (d.X + 0.5f) * Grid.U - Grid.Half, wz = (d.Y + 0.5f) * Grid.U - Grid.Half;
        float targetH = d.Flying ? 7.5f : 2.25f;
        _height = Mathf.Lerp(_height, targetH, 1 - Mathf.Exp(-2.5f * dt));
        Vector3 goal = d.Flying || Rover == null
            ? new Vector3(wx, Grid.HeightAt(wx, wz) + _height, wz)
            : Rover.GlobalPosition + new Vector3(-0.35f, _height, 0);
        if (!_init) { _pos = goal; _init = true; }
        _pos = _pos.Lerp(goal, 1 - Mathf.Exp(-4f * dt));
        GlobalPosition = _pos;
        float spin = d.Flying ? 40f : 0f;
        foreach (var r in _rotors) r.RotateY(spin * dt);
        _body.Rotation = new Vector3(0, _t * (d.Flying ? 0.3f : 0f), d.Flying ? 0.06f * Mathf.Sin(_t * 3f) : 0);
        _body.Position = new Vector3(0, d.Flying ? 0.12f * Mathf.Sin(_t * 2.2f) : 0, 0);
        _light.LightEnergy = d.Flying ? 1.2f + 0.6f * Mathf.Sin(_t * 9f) : 0.2f;
        bool scanning = d.Mode == DroneMode.Escaneando;
        _scan.Visible = _beam.Visible = scanning;
        if (scanning)
        {
            float r = d.ScanRadius * Grid.U;
            var tc = Grid.CellCenter(d.Target);
            float ground = Grid.HeightAt(tc.X, tc.Z);
            float hh = MathF.Max(1, _pos.Y - ground);
            _scan.GlobalPosition = new Vector3(_pos.X, ground + hh / 2, _pos.Z);
            _scan.Scale = new Vector3(r, hh, r);
            float pulse = (_t * 0.6f) % 1f;
            _beam.GlobalPosition = new Vector3(tc.X, ground + 0.4f, tc.Z);
            _beam.Scale = new Vector3(r * pulse + 0.5f, 1, r * pulse + 0.5f);
        }
    }
}
