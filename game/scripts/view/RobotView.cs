using System;
using AresX.Core;
using AresX.Mechs;
using AresX.Sim;
using Godot;

namespace AresX.View;

/// <summary>
/// Robot visible: en superficie, el mech-rover EXPLORADOR de Expedición Universo (patas con bogies de 3 ruedas,
/// mástil de cámaras, brazo robótico, antena y paneles; tools/parts_expedicion.py); en modo sonda, el Solaris Ranger
/// con propulsores. Piezas pre-renderizadas en billboard (MechRig). Interpola entre celdas con la duración que dicta la velocidad difusa y elige animación:
/// caminar, reposo, flotar (sonda), impacto (veto/colisión) y destrucción (fracaso).
/// </summary>
public partial class RobotView : Node3D
{
    public static readonly (string Slot, string Part)[] RoverLoadout =
    {
        ("leg_l", "exp_bogie"), ("leg_r", "exp_bogie"), ("torso", "exp_chassis"),
        ("arm_l", "exp_antenna"), ("arm_r", "exp_robot_arm"), ("back", "exp_power"), ("head", "exp_mast"),
    };

    public static readonly (string Slot, string Part)[] ProbeLoadout =
    {
        ("leg_l", "solaris_trailblazer"), ("leg_r", "solaris_trailblazer"), ("torso", "solaris_frontier"),
        ("arm_l", "solaris_utility"), ("arm_r", "solaris_utility"), ("back", "solaris_survival"), ("head", "solaris_pathfinder"),
    };

    public MechRig Rig { get; private set; } = null!;
    public bool Probe;
    public bool Busy => _moveT < _moveDur;

    private Vector3 _from, _to, _facing = new(1, 0, 0);
    private float _moveT = 1, _moveDur = 0;
    private float _hitTimer, _bob;
    private bool _wrecked;
    private SpotLight3D _lamp = null!;
    private OmniLight3D _glow = null!;

    public override void _Ready()
    {
        Rig = new MechRig { Name = "Rig" };
        AddChild(Rig);
        foreach (var (slot, part) in Probe ? ProbeLoadout : RoverLoadout)
            Rig.Equip(slot, part);
        Rig.Play("idle");
        AddChild(MakeBlobShadow());
        _lamp = new SpotLight3D
        {
            LightColor = new Color("fff2d0"), LightEnergy = 0f, SpotRange = 16f, SpotAngle = 32f, Position = new Vector3(0, 1.6f, 0),
            ShadowEnabled = false,
        };
        AddChild(_lamp);
        _glow = new OmniLight3D { LightColor = new Color("80d0ff"), LightEnergy = 0f, OmniRange = 5f, Position = new Vector3(0, 1.2f, 0) };
        AddChild(_glow);
    }

    public void Place(Cell c)
    {
        _to = _from = Grid.CellCenter(c);
        GlobalPosition = _to;
        _moveT = _moveDur = 1;
        _wrecked = false;
        Rig.Play(Probe ? "hover" : "idle", true);
    }

    /// <summary>Anima el desplazamiento a una celda en "seconds" segundos reales.</summary>
    public void MoveTo(Cell c, float seconds)
    {
        _from = GlobalPosition;
        _to = Grid.CellCenter(c);
        var d = _to - _from;
        d.Y = 0;
        if (d.LengthSquared() > 0.01f) _facing = d.Normalized();
        _moveT = 0;
        _moveDur = Mathf.Max(0.05f, seconds);
    }

    public void Face(Cell dir)
    {
        var d = new Vector3(dir.X, 0, dir.Y);
        if (d.LengthSquared() > 0) _facing = d.Normalized();
    }

    public void Hit() => _hitTimer = Rig.Skeleton.Anims["hit"].Length;

    public void Wreck()
    {
        _wrecked = true;
        Rig.Play("wreck", true);
    }

    public void SetNight(bool night, float lampEnergy = 2.4f)
    {
        _lamp.LightEnergy = night ? lampEnergy : 0f;
        _glow.LightEnergy = night ? 0.6f : 0f;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _bob += dt;
        if (_moveT < _moveDur)
        {
            _moveT = Mathf.Min(_moveDur, _moveT + dt);
            float t = _moveT / _moveDur;
            var p = _from.Lerp(_to, t);
            p.Y = Grid.HeightAt(p.X, p.Z);
            GlobalPosition = p;
        }
        else if (!_wrecked)
        {
            var p = _to;
            p.Y = Grid.HeightAt(p.X, p.Z);
            GlobalPosition = p;
        }
        if (Probe)
            Rig.Position = new Vector3(0, 2.2f + 0.25f * MathF.Sin(_bob * 2.2f), 0);
        else
            Rig.Position = Vector3.Zero;

        // orientación de faros y dirección del sprite respecto a la cámara (8 direcciones)
        _lamp.LookAtFromPosition(GlobalPosition + new Vector3(0, 1.6f, 0), GlobalPosition + _facing * 6f + new Vector3(0, -0.5f, 0), Vector3.Up);
        float camYaw = GetViewport().GetCamera3D()?.GlobalRotation.Y ?? 0f;
        float yaw = Mathf.Atan2(-_facing.Z, _facing.X) - camYaw;
        int dirs = Rig.Skeleton.Dirs;
        Rig.Direction = Mathf.PosMod(Mathf.RoundToInt(yaw / Mathf.Tau * dirs), dirs);
        Rig.UpperDirection = null;

        if (_wrecked) return;
        if (_hitTimer > 0)
        {
            _hitTimer -= dt;
            Rig.PlayUpper("hit");
        }
        else Rig.ClearUpper();
        Rig.Play(Probe ? "hover" : Busy ? "walk" : "idle");
    }

    private static Decal MakeBlobShadow()
    {
        var grad = new Gradient();
        grad.SetColor(0, new Color(0, 0, 0, 0.55f));
        grad.SetColor(1, new Color(0, 0, 0, 0));
        var tex = new GradientTexture2D
        {
            Gradient = grad, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(0.5f, 0f),
            Width = 32, Height = 32,
        };
        return new Decal { TextureAlbedo = tex, Size = new Vector3(1.4f, 10f, 1.0f), Position = new Vector3(0, -4.6f, 0), CullMask = 1 };
    }
}

/// <summary>
/// Cámara ortográfica 3/4 que sigue al robot. El pitch y la escala salen del set de sprites (skeleton.json) para que
/// el mecha se vea nítido. Q/E giran 45°, la rueda cambia el zoom.
/// </summary>
public partial class CameraRig : Node3D
{
    public Node3D? Target;
    public float Zoom = 2.5f;
    public static readonly float[] ZoomLevels = { 1f, 1.5f, 2f, 2.5f, 3f, 4f, 5f, 6.5f };
    public Camera3D Camera { get; private set; } = null!;
    private float _yawDeg = 0, _baseSize;
    private const float Distance = 45f;

    public override void _Ready()
    {
        var skel = MechSkeletonData.Get();
        float p = Mathf.DegToRad(skel.Pitch);
        int vh = ProjectSettings.GetSetting("display/window/size/viewport_height").AsInt32();
        _baseSize = vh * skel.PixelSize;
        Camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal, Size = _baseSize * Zoom, Near = 0.5f, Far = 300f,
            Position = new Vector3(0, Mathf.Sin(p), Mathf.Cos(p)) * Distance, Rotation = new Vector3(-p, 0, 0),
        };
        AddChild(Camera);
        Camera.MakeCurrent();
    }

    public override void _Process(double delta)
    {
        if (Input.IsActionJustPressed("cam_left")) Rotate45(-1);
        if (Input.IsActionJustPressed("cam_right")) Rotate45(1);
        if (Target == null) return;
        Vector3 goal = Target.GlobalPosition + Vector3.Up * 1.2f;
        float t = 1f - Mathf.Exp(-6f * (float)delta);
        GlobalPosition = GlobalPosition.Lerp(goal, t);
    }

    public void Wheel(int step)
    {
        int i = Array.FindIndex(ZoomLevels, z => z >= Zoom - 0.01f);
        i = Mathf.Clamp((i < 0 ? ZoomLevels.Length - 1 : i) + step, 0, ZoomLevels.Length - 1);
        Zoom = ZoomLevels[i];
        CreateTween().TweenProperty(Camera, "size", _baseSize * Zoom, 0.18).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
    }

    /// <summary>Zoom continuo (pellizco en pantallas táctiles).</summary>
    public void ZoomBy(float factor)
    {
        Zoom = Mathf.Clamp(Zoom * factor, 0.8f, 7f);
        Camera.Size = _baseSize * Zoom;
    }

    public void Snap()
    {
        if (Target != null) GlobalPosition = Target.GlobalPosition + Vector3.Up * 1.2f;
        Camera.Size = _baseSize * Zoom;
    }

    public void Rotate45(int sign)
    {
        _yawDeg += 45f * sign;
        CreateTween().TweenProperty(this, "rotation_degrees:y", _yawDeg, 0.25).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
    }

    /// <summary>Sacudida breve (sismo, impacto, colisión).</summary>
    public void Shake(float amount, float seconds)
    {
        var tw = CreateTween();
        int n = Mathf.Max(2, (int)(seconds / 0.04f));
        for (int i = 0; i < n; i++)
        {
            float k = amount * (1f - i / (float)n);
            tw.TweenProperty(Camera, "h_offset", (float)GD.RandRange(-k, k), 0.04);
            tw.Parallel().TweenProperty(Camera, "v_offset", (float)GD.RandRange(-k, k), 0.04);
        }
        tw.TweenProperty(Camera, "h_offset", 0f, 0.05);
        tw.Parallel().TweenProperty(Camera, "v_offset", 0f, 0.05);
    }
}

/// <summary>Cielo, sol, niebla y partículas (lluvia/polvo) según el estado ambiental de la simulación.</summary>
public partial class EnvironmentFx : Node3D
{
    public SimEnvironment? Env;
    public Node3D? Follow;
    private WorldEnvironment _world = null!;
    private DirectionalLight3D _sun = null!;
    private GpuParticles3D _rain = null!, _dust = null!;
    private ParticleProcessMaterial _rainMat = null!, _dustMat = null!;
    private Color _skyDay, _skyNight, _fog, _sunColor;

    public override void _Ready()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color, AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightEnergy = 0.55f, TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        _world = new WorldEnvironment { Environment = env };
        AddChild(_world);
        _sun = new DirectionalLight3D { ShadowEnabled = !Core.Platform.IsMobile, LightEnergy = 1.1f };   // móvil: sin sombras (rendimiento)
        AddChild(_sun);
        (_rain, _rainMat) = MakeParticles(Core.Platform.IsMobile ? 600 : 1500, new Color(0.7f, 0.8f, 1f, 0.55f), new Vector2(0.04f, 0.7f));
        (_dust, _dustMat) = MakeParticles(Core.Platform.IsMobile ? 400 : 900, new Color(0.85f, 0.6f, 0.4f, 0.5f), new Vector2(0.18f, 0.18f));
        AddChild(_rain);
        AddChild(_dust);
    }

    private static (GpuParticles3D, ParticleProcessMaterial) MakeParticles(int amount, Color c, Vector2 size)
    {
        var mat = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(26, 1, 26),
            Direction = new Vector3(0, -1, 0), Spread = 5, InitialVelocityMin = 18, InitialVelocityMax = 24, Gravity = new Vector3(0, -9.8f, 0),
        };
        var quad = new QuadMesh { Size = size };
        quad.SurfaceSetMaterial(0, new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
        });
        var p = new GpuParticles3D
        {
            Amount = amount, Lifetime = 1.4f, ProcessMaterial = mat, DrawPass1 = quad, Emitting = false,
            VisibilityAabb = new Aabb(new Vector3(-40, -30, -40), new Vector3(80, 60, 80)), Position = new Vector3(0, 18, 0),
        };
        return (p, mat);
    }

    public void Setup(SimEnvironment env, PlanetProfile p)
    {
        Env = env;
        _skyDay = new Color(p.SkyDay);
        _skyNight = new Color(p.SkyNight);
        _fog = p.Fog == "" ? _skyDay : new Color(p.Fog);
        _sunColor = new Color(p.SunColor);
    }

    public override void _Process(double delta)
    {
        if (Env == null) return;
        var e = Env;
        var env = _world.Environment;
        float day = e.ForcedNight ? 0 : Mathf.Clamp(e.SunElev * 3f + 0.3f, 0, 1);
        var sky = _skyNight.Lerp(_skyDay, day);
        if (e.P.HasRain && e.CloudCover > 0) sky = sky.Lerp(new Color("6a7480"), e.CloudCover * 0.6f * day);
        env.BackgroundColor = sky.Lerp(_fog, Mathf.Clamp(e.DustOpacity * 0.8f, 0, 0.8f));
        env.AmbientLightColor = sky.Lerp(Colors.White, 0.35f);
        env.AmbientLightEnergy = 0.25f + 0.45f * day;
        float fogD = Mathf.Clamp(e.DustOpacity * 0.012f + (e.P.HasRain ? e.RainMmH * 0.00035f : 0) + (e.P.Style is TerrainStyle.Venus or TerrainStyle.Gaseoso ? 0.003f : 0), 0, 0.013f);
        env.FogEnabled = fogD > 0.001f;
        env.FogLightColor = _fog.Lerp(_skyNight, 1 - day);
        env.FogDensity = fogD;
        env.FogSkyAffect = 0;
        float elev = Mathf.Clamp(e.SunElev, -0.2f, 1f);
        _sun.RotationDegrees = new Vector3(-Mathf.Lerp(8f, 70f, Mathf.Clamp(elev, 0, 1)), e.DayPhase * 360f - 90f, 0);
        _sun.LightEnergy = e.IsDay ? 0.25f + 1.0f * Mathf.Clamp(e.SunElev * 2.5f, 0, 1) * (1f - 0.6f * e.DustOpacity) * (e.P.Style == TerrainStyle.Venus ? 0.45f : 1f) : 0.04f;
        _sun.LightColor = _sunColor.Lerp(new Color("ff9a60"), Mathf.Clamp(1 - e.SunElev * 3, 0, 1) * 0.6f);
        if (Follow != null)
        {
            var pos = Follow.GlobalPosition;
            _rain.GlobalPosition = pos + new Vector3(0, 18, 0);
            _dust.GlobalPosition = pos + new Vector3(0, 6, 0);
        }
        // viento: dirección de brújula (0 = N = −Z); las partículas van hacia donde sopla
        float r = Mathf.DegToRad(e.WindDirDeg);
        var windTo = new Vector3(-Mathf.Sin(r), 0, Mathf.Cos(r));
        float w = Mathf.Min(e.WindSpeed, 40f);
        _rain.Emitting = e.P.HasRain && e.RainMmH > 0.2f;
        _rain.AmountRatio = Mathf.Clamp(e.RainMmH / 20f, 0.1f, 1f);
        _rainMat.Gravity = new Vector3(windTo.X * w * 0.6f, -30f, windTo.Z * w * 0.6f);
        bool dusty = e.P.Style is TerrainStyle.Marte or TerrainStyle.Venus or TerrainStyle.Gaseoso && (e.DustOpacity > 0.15f || e.WindSpeed > e.P.WindMean * 1.4f);
        _dust.Emitting = dusty;
        _dust.AmountRatio = Mathf.Clamp(e.DustOpacity + 0.15f, 0.1f, 1f);
        _dustMat.Gravity = new Vector3(windTo.X * (4 + w), -0.5f, windTo.Z * (4 + w));
        _dustMat.Direction = windTo + Vector3.Up * 0.1f;
        _dustMat.InitialVelocityMin = 2 + w * 0.3f;
        _dustMat.InitialVelocityMax = 4 + w * 0.5f;
    }

    /// <summary>Destello y estela de un meteorito que cae en el centro de una celda.</summary>
    public void Meteor(Vector3 at)
    {
        var rock = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.6f, Height = 1.2f },
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color("ffd080"), EmissionEnabled = true, Emission = new Color("ff8030"), EmissionEnergyMultiplier = 4 },
        };
        AddChild(rock);
        rock.GlobalPosition = at + new Vector3(-30, 60, -20);
        var light = new OmniLight3D { LightColor = new Color("ffb060"), LightEnergy = 0, OmniRange = 30 };
        AddChild(light);
        light.GlobalPosition = at + Vector3.Up * 2;
        var tw = CreateTween();
        tw.TweenProperty(rock, "global_position", at, 0.7).SetEase(Tween.EaseType.In);
        tw.TweenCallback(Callable.From(() => rock.QueueFree()));
        tw.TweenProperty(light, "light_energy", 12f, 0.05);
        tw.TweenProperty(light, "light_energy", 0f, 0.9);
        tw.TweenCallback(Callable.From(() => light.QueueFree()));
    }
}
