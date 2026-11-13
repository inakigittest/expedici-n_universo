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
    public bool Heli;                 // helicóptero: modelo 3D de primitivas en vez del mech
    public float HeliAglM;            // altura sobre el terreno informada por la simulación
    private Node3D? _heli, _rotor, _tailRotor;
    private float _heliAlt;
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
        if (Heli) BuildHeli();
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
        if (Heli) UpdateHeli(dt);
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

    /// <summary>Helicóptero no tripulado de primitivas (escala exagerada ×4 para que se lea sobre celdas de 30 m).</summary>
    private void BuildHeli()
    {
        Rig.Visible = false;
        _heli = new Node3D { Name = "Heli" };
        AddChild(_heli);
        StandardMaterial3D M(string hex, float rough = 0.5f, float metal = 0.1f) => new() { AlbedoColor = new Color(hex), Roughness = rough, Metallic = metal };
        var body = M("e9edf0");
        var accent = M("d9622b");
        var dark = M("2a2e35", 0.4f, 0.3f);
        var glass = new StandardMaterial3D { AlbedoColor = new Color(0.15f, 0.3f, 0.42f, 0.8f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Roughness = 0.05f, Metallic = 0.6f };
        MeshInstance3D Add(Node3D parent, Mesh mesh, Material mat, Vector3 pos, Vector3? rotDeg = null)
        {
            var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = pos, RotationDegrees = rotDeg ?? Vector3.Zero };
            parent.AddChild(mi);
            return mi;
        }
        Add(_heli, new CapsuleMesh { Radius = 0.5f, Height = 2.1f }, body, new Vector3(0, 0.9f, 0), new Vector3(0, 0, 90));
        Add(_heli, new BoxMesh { Size = new Vector3(1.4f, 0.12f, 1.02f) }, accent, new Vector3(-0.1f, 0.95f, 0));
        Add(_heli, new SphereMesh { Radius = 0.46f, Height = 0.8f }, glass, new Vector3(0.75f, 1.0f, 0));
        Add(_heli, new CylinderMesh { TopRadius = 0.08f, BottomRadius = 0.16f, Height = 2.3f }, body, new Vector3(-1.9f, 1.05f, 0), new Vector3(0, 0, 90));
        Add(_heli, new BoxMesh { Size = new Vector3(0.45f, 0.75f, 0.06f) }, accent, new Vector3(-3.0f, 1.35f, 0), new Vector3(0, 0, -15));
        Add(_heli, new BoxMesh { Size = new Vector3(0.5f, 0.05f, 0.8f) }, body, new Vector3(-2.7f, 1.05f, 0));
        foreach (float z in new[] { -0.55f, 0.55f })
        {
            Add(_heli, new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.05f, Height = 2.2f }, dark, new Vector3(0.05f, 0.08f, z), new Vector3(0, 0, 90));
            Add(_heli, new CylinderMesh { TopRadius = 0.035f, BottomRadius = 0.035f, Height = 0.5f }, dark, new Vector3(0.45f, 0.32f, z * 0.85f), new Vector3(z * 20, 0, 0));
            Add(_heli, new CylinderMesh { TopRadius = 0.035f, BottomRadius = 0.035f, Height = 0.5f }, dark, new Vector3(-0.45f, 0.32f, z * 0.85f), new Vector3(z * 20, 0, 0));
        }
        Add(_heli, new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.09f, Height = 0.35f }, dark, new Vector3(0, 1.55f, 0));
        _rotor = new Node3D { Position = new Vector3(0, 1.75f, 0) };
        _heli.AddChild(_rotor);
        Add(_rotor, new BoxMesh { Size = new Vector3(6.2f, 0.04f, 0.22f) }, dark, Vector3.Zero);
        Add(_rotor, new BoxMesh { Size = new Vector3(0.22f, 0.04f, 6.2f) }, dark, Vector3.Zero);
        Add(_rotor, new CylinderMesh { TopRadius = 3.1f, BottomRadius = 3.1f, Height = 0.01f, RadialSegments = 32 },
            new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.9f, 0.95f, 0.16f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded }, Vector3.Zero);
        _tailRotor = new Node3D { Position = new Vector3(-3.05f, 1.3f, 0.1f) };
        _heli.AddChild(_tailRotor);
        Add(_tailRotor, new BoxMesh { Size = new Vector3(0.9f, 0.1f, 0.03f) }, dark, Vector3.Zero);
        Add(_tailRotor, new BoxMesh { Size = new Vector3(0.1f, 0.9f, 0.03f) }, dark, Vector3.Zero);
        _heli.AddChild(new OmniLight3D { LightColor = new Color("ff3b30"), LightEnergy = 0.8f, OmniRange = 2.5f, Position = new Vector3(-0.2f, 0.4f, 0) });
    }

    private void UpdateHeli(float dt)
    {
        if (_heli == null) return;
        bool flying = HeliAglM > 0.5f;
        // altura visual: proporcional a la real (90 m) pero acotada para que el helicóptero siga en cuadro
        float goal = flying ? Mathf.Clamp(HeliAglM * Grid.V, 3.5f, 9f) : 0f;
        _heliAlt = Mathf.Lerp(_heliAlt, goal, 1 - Mathf.Exp(-2.2f * dt));
        float yaw = Mathf.Atan2(-_facing.Z, _facing.X);
        float pitch = Busy && flying ? -10f : 0f;
        float cur = Mathf.RadToDeg(_heli.Rotation.Z);
        _heli.Rotation = new Vector3(0, yaw, Mathf.DegToRad(Mathf.Lerp(cur, pitch, 1 - Mathf.Exp(-4f * dt))));
        _heli.Position = new Vector3(0, _heliAlt + (flying ? 0.15f * MathF.Sin(_bob * 1.7f) : 0), 0);
        float spin = flying ? 32f : _heliAlt > 0.05f ? 14f : 3f;
        _rotor!.RotateY(spin * dt);
        _tailRotor!.RotateZ(spin * 1.6f * dt);
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
/// Cámara ortográfica 3/4 (la del proyecto de mechas, adaptada): el pitch y la escala salen del set de sprites. Todo es
/// continuo y suavizado:
///   * giro: Q/E mantenidos (acelera y frena con inercia), arrastrar con el botón central, o los botones ⟲⟳ (45°);
///     los sprites eligen su dirección respecto a la cámara en cada frame;
///   * zoom: la rueda multiplica el zoom objetivo (×1,15 por paso) y el encuadre se acerca a él con suavidad;
///   * seguimiento: muelle críticamente amortiguado (sin tirones);
///   * sacudida: trauma (0..1) que crece con el cuadrado y se apaga solo.
/// Usa el tiempo real. En mapas a escala real la cámara se aleja y el zoom llega mucho más lejos.
/// </summary>
public partial class CameraRig : Node3D
{
    public Node3D? Target;
    public float Zoom = 2.5f;
    public Camera3D Camera { get; private set; } = null!;
    public float MaxZoom = 7f, MinZoom = 0.8f;
    public float FollowTime = 0.16f, LookHeight = 1.2f, YawSpeed = 110f;
    private const float ZoomStep = 1.15f, DragDegPerPx = 0.35f;
    private float _yawDeg, _yawVel, _zoomTarget = 2.5f, _trauma, _shakeT, _baseSize, _distance = 45f;
    private float? _snapTo;
    private Vector3 _vel;
    private ulong _lastUsec;
    private readonly FastNoiseLite _noise = new() { NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex, Frequency = 1f };

    /// <summary>Mapas a escala real (km): cámara más lejos (el relieve puede superar los 100 m de altura de mundo) y más zoom.</summary>
    public void ConfigureForLarge(bool large)
    {
        _distance = large ? 420f : 45f;
        MaxZoom = large ? 48f : 7f;
        float p = Mathf.DegToRad(MechSkeletonData.Get().Pitch);
        Camera.Position = new Vector3(0, Mathf.Sin(p), Mathf.Cos(p)) * _distance;
        Camera.Far = large ? 2400f : 300f;
        EnvironmentFx.FogScale = 45f / _distance;
        Zoom = _zoomTarget = Mathf.Min(Zoom, MaxZoom);
    }

    public override void _Ready()
    {
        var skel = MechSkeletonData.Get();
        float p = Mathf.DegToRad(skel.Pitch);
        int vh = ProjectSettings.GetSetting("display/window/size/viewport_height").AsInt32();
        _baseSize = vh * skel.PixelSize;
        _zoomTarget = Zoom;
        Camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal, Size = _baseSize * Zoom, Near = 0.5f, Far = 300f,
            Position = new Vector3(0, Mathf.Sin(p), Mathf.Cos(p)) * _distance, Rotation = new Vector3(-p, 0, 0),
        };
        AddChild(Camera);
        Camera.MakeCurrent();
        _lastUsec = Time.GetTicksUsec();
    }

    public override void _Process(double delta)
    {
        ulong now = Time.GetTicksUsec();
        float dt = Mathf.Min((now - _lastUsec) / 1e6f, 0.1f);
        _lastUsec = now;
        if (dt <= 0) return;
        UpdateYaw(dt);
        Zoom = Mathf.Lerp(Zoom, _zoomTarget, 1f - Mathf.Exp(-12f * dt));
        Camera.Size = _baseSize * Zoom;
        if (Target != null)
            GlobalPosition = SmoothDamp(GlobalPosition, Target.GlobalPosition + Vector3.Up * LookHeight, ref _vel, FollowTime, dt);
        UpdateShake(dt);
    }

    // ------------------------------------------------------------------ giro
    private void UpdateYaw(float dt)
    {
        float input = (Input.IsActionPressed("cam_right") ? 1f : 0f) - (Input.IsActionPressed("cam_left") ? 1f : 0f);
        if (input != 0)
        {
            _snapTo = null;
            _yawVel = Mathf.MoveToward(_yawVel, input * YawSpeed, YawSpeed * 5f * dt);
        }
        else if (_snapTo is { } goal)
        {
            float diff = goal - _yawDeg;
            _yawVel = Mathf.Lerp(_yawVel, diff * 9f, 1f - Mathf.Exp(-14f * dt));
            if (Mathf.Abs(diff) < 0.05f && Mathf.Abs(_yawVel) < 1f)
            {
                _yawDeg = goal;
                _yawVel = 0;
                _snapTo = null;
            }
        }
        else _yawVel = Mathf.MoveToward(_yawVel, 0, YawSpeed * 6f * dt);
        _yawDeg += _yawVel * dt;
        RotationDegrees = new Vector3(0, _yawDeg, 0);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm && (mm.ButtonMask & MouseButtonMask.Middle) != 0)
        {
            _snapTo = null;
            _yawVel = 0;
            _yawDeg -= mm.Relative.X * DragDegPerPx;
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>Botones ⟲⟳: termina el giro en el siguiente múltiplo de 45° (con muelle).</summary>
    public void Rotate45(int sign) => _snapTo = (Mathf.Round(((_snapTo ?? _yawDeg)) / 45f) + sign) * 45f;

    // ------------------------------------------------------------------ zoom
    /// <summary>Rueda: ×1,15 por paso (se alcanza con suavidad).</summary>
    public void Wheel(int step, float factor = 1f) =>
        _zoomTarget = Mathf.Clamp(_zoomTarget * Mathf.Pow(ZoomStep, step * factor), MinZoom, MaxZoom);

    /// <summary>Zoom continuo (pellizco en pantallas táctiles).</summary>
    public void ZoomBy(float factor) => _zoomTarget = Mathf.Clamp(_zoomTarget * factor, MinZoom, MaxZoom);

    public void Snap()
    {
        _zoomTarget = Zoom;
        if (Target != null) GlobalPosition = Target.GlobalPosition + Vector3.Up * LookHeight;
        _vel = Vector3.Zero;
        if (Camera != null) Camera.Size = _baseSize * Zoom;
    }

    // ------------------------------------------------------------------ seguimiento
    /// <summary>Muelle críticamente amortiguado (como SmoothDamp): llega sin pasarse ni dar tirones.</summary>
    private static Vector3 SmoothDamp(Vector3 cur, Vector3 goal, ref Vector3 vel, float time, float dt)
    {
        float omega = 2f / Mathf.Max(time, 0.0001f);
        float x = omega * dt;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        Vector3 change = cur - goal;
        Vector3 temp = (vel + omega * change) * dt;
        vel = (vel - omega * temp) * exp;
        return goal + (change + temp) * exp;
    }

    // ------------------------------------------------------------------ sacudida
    /// <summary>Sacudida (sismo, impacto, colisión): amount ≈ 0,1–0,6; la duración sale del decaimiento del trauma.</summary>
    public void Shake(float amount, float seconds) => _trauma = Mathf.Min(1f, _trauma + Mathf.Clamp(amount * (0.6f + seconds * 0.4f), 0, 1));

    private void UpdateShake(float dt)
    {
        _trauma = Mathf.Max(0, _trauma - 1.4f * dt);
        _shakeT += dt * 22f;
        float s = _trauma * _trauma * Camera.Size * 0.025f;
        Camera.HOffset = s * _noise.GetNoise2D(_shakeT, 0.5f);
        Camera.VOffset = s * _noise.GetNoise2D(0.5f, _shakeT);
    }
}

/// <summary>Cielo, sol, niebla y partículas (lluvia/polvo) según el estado ambiental de la simulación.</summary>
public partial class EnvironmentFx : Node3D
{
    /// <summary>La niebla se escala con la distancia de la cámara (mapas a escala real la alejan).</summary>
    public static float FogScale = 1f;
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
        float fogD = FogScale * Mathf.Clamp(e.DustOpacity * 0.012f + (e.P.HasRain ? e.RainMmH * 0.00035f : 0) + (e.P.Style is TerrainStyle.Venus or TerrainStyle.Gaseoso ? 0.003f : 0), 0, 0.013f);
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
