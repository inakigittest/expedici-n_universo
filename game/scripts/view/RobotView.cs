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
    public Vector3 Facing => _facing;
    /// <summary>Cámara de cabina: el sprite del rover se oculta (el helicóptero se ve desde dentro, con las caras interiores descartadas).</summary>
    public bool Cockpit
    {
        set
        {
            if (_heli == null && Rig != null) Rig.Visible = !value;
            // en cabina las palas cruzarían la vista: queda solo el disco translúcido del rotor
            if (_rotor != null) foreach (var c in _rotor.GetChildren()) if (c is MeshInstance3D { Mesh: BoxMesh } mi) mi.Visible = !value;
        }
    }
    /// <summary>Centro visual del cuerpo (el helicóptero incluye su altura de vuelo).</summary>
    public Vector3 BodyPosition => _heli != null ? _heli.GlobalPosition + Vector3.Up * 0.9f : GlobalPosition + Vector3.Up * 1.2f;
    /// <summary>Punto de vista de la cabina (−Z hacia adelante): tras el parabrisas del helicóptero o sobre el mástil del rover.</summary>
    public Transform3D EyeTransform
    {
        get
        {
            if (_heli != null)
            {
                var t = _heli.GlobalTransform;
                var fwd = (t.Basis * Vector3.Right).Normalized();
                return new Transform3D(Basis.LookingAt(fwd, Vector3.Up), t * new Vector3(0.62f, 1.12f, 0));
            }
            return new Transform3D(Basis.LookingAt(_facing, Vector3.Up), GlobalPosition + Vector3.Up * 2.1f + _facing * 0.5f);
        }
    }

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

public enum CamMode { Tactica, Sobre, Cabina, Libre }

/// <summary>
/// Cámara con cuatro modos (C cicla):
///   * TÁCTICA: ortográfica 3/4 (la del proyecto de mechas): Q/E o arrastre central giran con inercia, ⟲⟳ a 45°, la rueda
///     multiplica el zoom (×1,15 por paso); seguimiento con muelle críticamente amortiguado.
///   * SOBRE EL VEHÍCULO: perspectiva de persecución, detrás y por encima siguiendo el rumbo; arrastre central/Q/E orbitan,
///     la rueda acerca o aleja (arrastrar hacia abajo llega a la vista cenital).
///   * CABINA: primera persona tras el parabrisas (o sobre el mástil del rover); arrastre central/Q/E miran alrededor y la
///     rueda cambia el campo de visión. El HUD de cabina dibuja rumbo, altura, velocidad y la decisión de la IA.
///   * LIBRE: vuelo libre: W/A/S/D o flechas mueven, rueda o RePág/AvPág suben y bajan, Mayús acelera, arrastre central mira.
/// La sacudida (trauma 0..1) funciona en todos los modos. Usa el tiempo real.
/// </summary>
public partial class CameraRig : Node3D
{
    public Node3D? Target;
    public RobotView? Vehicle;
    public float Zoom = 2.5f;
    public Camera3D Camera { get; private set; } = null!;
    public float MaxZoom = 7f, MinZoom = 0.8f;
    public float FollowTime = 0.16f, LookHeight = 1.2f, YawSpeed = 110f;
    public CamMode Mode { get; private set; } = CamMode.Tactica;
    public static event Action<CamMode>? ModeChanged;
    private const float ZoomStep = 1.15f, DragDegPerPx = 0.35f;
    private float _yawDeg, _yawVel, _zoomTarget = 2.5f, _trauma, _shakeT, _baseSize, _distance = 45f, _pitchDeg = 30f;
    private float? _snapTo;
    private Vector3 _vel;
    private ulong _lastUsec;
    private readonly FastNoiseLite _noise = new() { NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex, Frequency = 1f };
    // modos en perspectiva
    private float _lookYaw, _lookPitch, _lookYawVel;   // desvío de la mirada (sobre / cabina) en grados
    private float _chaseDist = 16f, _fov = 70f;
    private Vector3 _camPos, _camVel, _freePos;
    private float _freeYaw, _freePitch = -18f;
    private Basis _eyeBasis = Basis.Identity;
    private bool _fresh = true;

    public static string ModeName(CamMode m) => m switch
    {
        CamMode.Sobre => "Sobre el vehículo", CamMode.Cabina => "Cabina", CamMode.Libre => "Libre", _ => "Táctica",
    };

    /// <summary>Mapas a escala real (km): cámara más lejos (el relieve puede superar los 100 m de altura de mundo) y más zoom.</summary>
    public void ConfigureForLarge(bool large)
    {
        _distance = large ? 420f : 45f;
        MaxZoom = large ? 48f : 7f;
        _chaseDist = large ? 9f : 8f;
        if (Mode == CamMode.Tactica)
        {
            Camera.Position = OrthoOffset;
            Camera.Far = large ? 2400f : 300f;
        }
        EnvironmentFx.FogScale = 45f / _distance;
        Zoom = _zoomTarget = Mathf.Min(Zoom, MaxZoom);
    }

    private Vector3 OrthoOffset => new Vector3(0, Mathf.Sin(Mathf.DegToRad(_pitchDeg)), Mathf.Cos(Mathf.DegToRad(_pitchDeg))) * _distance;

    public override void _Ready()
    {
        var skel = MechSkeletonData.Get();
        _pitchDeg = skel.Pitch;
        float p = Mathf.DegToRad(skel.Pitch);
        int vh = ProjectSettings.GetSetting("display/window/size/viewport_height").AsInt32();
        _baseSize = vh * skel.PixelSize;
        _zoomTarget = Zoom;
        Camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal, Size = _baseSize * Zoom, Near = 0.5f, Far = 300f,
            Position = OrthoOffset, Rotation = new Vector3(-p, 0, 0),
        };
        AddChild(Camera);
        Camera.MakeCurrent();
        _lastUsec = Time.GetTicksUsec();
    }

    // ------------------------------------------------------------------ modos
    public void SetMode(CamMode m)
    {
        if (Camera == null) return;
        var prev = Camera.GlobalTransform;
        Mode = m;
        _lookYaw = _lookPitch = _lookYawVel = 0;
        _fresh = true;
        bool large = Grid.Map != null && Grid.Large;
        if (m == CamMode.Tactica)
        {
            Camera.TopLevel = false;
            Camera.Projection = Camera3D.ProjectionType.Orthogonal;
            Camera.Position = OrthoOffset;
            Camera.Rotation = new Vector3(-Mathf.DegToRad(_pitchDeg), 0, 0);
            Camera.Near = 0.5f;
            Camera.Far = large ? 2400f : 300f;
        }
        else
        {
            Camera.TopLevel = true;
            Camera.GlobalTransform = prev;
            Camera.Projection = Camera3D.ProjectionType.Perspective;
            _fov = m == CamMode.Cabina ? 72f : 58f;
            Camera.Fov = _fov;
            Camera.Near = m == CamMode.Cabina ? 0.05f : 0.15f;
            Camera.Far = large ? 3200f : 700f;
            if (m == CamMode.Libre)
            {
                // arranca detrás y por encima del vehículo, mirándolo
                var focus = Vehicle?.BodyPosition ?? GlobalPosition;
                var back = new Vector3(Mathf.Sin(Mathf.DegToRad(_yawDeg)), 0, Mathf.Cos(Mathf.DegToRad(_yawDeg)));
                _freePos = focus + back * _chaseDist * 1.4f + Vector3.Up * _chaseDist * 0.8f;
                var dir = (focus - _freePos).Normalized();
                _freeYaw = Mathf.RadToDeg(Mathf.Atan2(-dir.X, -dir.Z));
                _freePitch = Mathf.RadToDeg(Mathf.Asin(dir.Y));
            }
        }
        if (Vehicle != null) Vehicle.Cockpit = m == CamMode.Cabina;
        ModeChanged?.Invoke(m);
    }

    public void CycleMode() => SetMode((CamMode)(((int)Mode + 1) % 4));

    public override void _Process(double delta)
    {
        ulong now = Time.GetTicksUsec();
        float dt = Mathf.Min((now - _lastUsec) / 1e6f, 0.1f);
        _lastUsec = now;
        if (dt <= 0) return;
        if (Mode == CamMode.Tactica)
        {
            UpdateYaw(dt);
            Zoom = Mathf.Lerp(Zoom, _zoomTarget, 1f - Mathf.Exp(-12f * dt));
            Camera.Size = _baseSize * Zoom;
        }
        if (Target != null)
            GlobalPosition = SmoothDamp(GlobalPosition, Target.GlobalPosition + Vector3.Up * LookHeight, ref _vel, FollowTime, dt);
        switch (Mode)
        {
            case CamMode.Sobre: UpdateChase(dt); break;
            case CamMode.Cabina: UpdateCockpit(dt); break;
            case CamMode.Libre: UpdateFree(dt); break;
        }
        UpdateShake(dt);
    }

    private float LookInput(float dt)
    {
        float input = (Input.IsActionPressed("cam_right") ? 1f : 0f) - (Input.IsActionPressed("cam_left") ? 1f : 0f);
        _lookYawVel = input != 0 ? Mathf.MoveToward(_lookYawVel, input * YawSpeed, YawSpeed * 5f * dt) : Mathf.MoveToward(_lookYawVel, 0, YawSpeed * 6f * dt);
        return _lookYawVel * dt;
    }

    private static float HeadingYaw(Vector3 f) => Mathf.Atan2(-f.X, -f.Z);   // yaw de Godot que mira hacia f

    private void UpdateChase(float dt)
    {
        if (Vehicle == null) return;
        _lookYaw -= LookInput(dt);
        var body = Vehicle.BodyPosition;
        float yaw = HeadingYaw(Vehicle.Facing) + Mathf.DegToRad(_lookYaw);
        float elev = Mathf.DegToRad(Mathf.Clamp(24f + _lookPitch, 4f, 88f));
        var back = new Vector3(Mathf.Sin(yaw), 0, Mathf.Cos(yaw));
        var goal = body + back * _chaseDist * Mathf.Cos(elev) + Vector3.Up * (_chaseDist * Mathf.Sin(elev) + 1.2f);
        if (Grid.Map != null) goal.Y = Mathf.Max(goal.Y, Grid.HeightAt(goal.X, goal.Z) + 1.0f);
        if (_fresh) { _camPos = goal; _camVel = Vector3.Zero; _fresh = false; }
        _camPos = SmoothDamp(_camPos, goal, ref _camVel, 0.45f, dt);
        var look = body + Vector3.Up * 0.6f - back * 2f;
        Camera.GlobalTransform = new Transform3D(Basis.Identity, _camPos).LookingAt(look, Vector3.Up);
        Camera.Fov = Mathf.Lerp(Camera.Fov, _fov, 1 - Mathf.Exp(-10f * dt));
    }

    private void UpdateCockpit(float dt)
    {
        if (Vehicle == null) return;
        _lookYaw -= LookInput(dt);
        var eye = Vehicle.EyeTransform;
        // el rumbo del vehículo cambia por celdas: la mirada lo sigue con suavidad
        _eyeBasis = _fresh ? eye.Basis : new Basis(_eyeBasis.GetRotationQuaternion().Slerp(eye.Basis.GetRotationQuaternion(), 1 - Mathf.Exp(-5f * dt)));
        _fresh = false;
        var look = new Basis(Vector3.Up, Mathf.DegToRad(_lookYaw)) * new Basis(Vector3.Right, Mathf.DegToRad(Mathf.Clamp(-8f + _lookPitch, -75f, 60f)));
        Camera.GlobalTransform = new Transform3D(_eyeBasis * look, eye.Origin);
        Camera.Fov = Mathf.Lerp(Camera.Fov, _fov, 1 - Mathf.Exp(-10f * dt));
    }

    private void UpdateFree(float dt)
    {
        _freeYaw -= LookInput(dt);
        float fwd = 0, side = 0, up = 0;
        if (Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up)) fwd += 1;
        if (Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down)) fwd -= 1;
        if (Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right)) side += 1;
        if (Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left)) side -= 1;
        if (Input.IsPhysicalKeyPressed(Key.Pageup)) up += 1;
        if (Input.IsPhysicalKeyPressed(Key.Pagedown)) up -= 1;
        float ground = Grid.Map != null ? Grid.HeightAt(_freePos.X, _freePos.Z) : 0;
        float agl = Mathf.Max(1, _freePos.Y - ground);
        float speed = Mathf.Clamp(agl * 1.2f, 6f, Grid.Map != null && Grid.Large ? 400f : 80f) * (Input.IsPhysicalKeyPressed(Key.Shift) ? 4f : 1f);
        float y = Mathf.DegToRad(_freeYaw);
        var f = new Vector3(-Mathf.Sin(y), 0, -Mathf.Cos(y));
        var r = new Vector3(Mathf.Cos(y), 0, -Mathf.Sin(y));
        _freePos += (f * fwd + r * side + Vector3.Up * up) * speed * dt;
        if (Grid.Map != null)
        {
            float lim = Grid.Half * 1.3f;
            _freePos.X = Mathf.Clamp(_freePos.X, -lim, lim);
            _freePos.Z = Mathf.Clamp(_freePos.Z, -lim, lim);
            _freePos.Y = Mathf.Max(_freePos.Y, Grid.HeightAt(_freePos.X, _freePos.Z) + 0.8f);
        }
        _freePitch = Mathf.Clamp(_freePitch, -89f, 80f);
        Camera.GlobalTransform = new Transform3D(new Basis(Vector3.Up, y) * new Basis(Vector3.Right, Mathf.DegToRad(_freePitch)), _freePos);
    }

    // ------------------------------------------------------------------ giro (táctica)
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
            switch (Mode)
            {
                case CamMode.Tactica:
                    _snapTo = null;
                    _yawVel = 0;
                    _yawDeg -= mm.Relative.X * DragDegPerPx;
                    break;
                case CamMode.Libre:
                    _freeYaw -= mm.Relative.X * 0.25f;
                    _freePitch -= mm.Relative.Y * 0.25f;
                    break;
                case CamMode.Sobre:
                    _lookYaw -= mm.Relative.X * 0.3f;
                    _lookPitch = Mathf.Clamp(_lookPitch + mm.Relative.Y * 0.25f, -20f, 64f);
                    break;
                default:
                    _lookYaw -= mm.Relative.X * 0.25f;
                    _lookPitch = Mathf.Clamp(_lookPitch - mm.Relative.Y * 0.25f, -67f, 68f);
                    break;
            }
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>Botones ⟲⟳: en táctica termina el giro en el siguiente múltiplo de 45° (con muelle); en los demás modos gira la mirada 45°.</summary>
    public void Rotate45(int sign)
    {
        if (Mode == CamMode.Tactica) _snapTo = (Mathf.Round(((_snapTo ?? _yawDeg)) / 45f) + sign) * 45f;
        else if (Mode == CamMode.Libre) _freeYaw -= 45f * sign;
        else _lookYaw -= 45f * sign;
    }

    // ------------------------------------------------------------------ zoom
    /// <summary>Rueda: táctica ×1,15 de zoom por paso; sobre el vehículo, distancia; cabina, campo de visión; libre, altura.</summary>
    public void Wheel(int step, float factor = 1f)
    {
        switch (Mode)
        {
            case CamMode.Sobre:
                _chaseDist = Mathf.Clamp(_chaseDist * Mathf.Pow(ZoomStep, step * factor), 5f, Grid.Map != null && Grid.Large ? 240f : 70f);
                break;
            case CamMode.Cabina:
                _fov = Mathf.Clamp(_fov + 5f * step * factor, 30f, 95f);
                break;
            case CamMode.Libre:
                float agl = Mathf.Max(2f, _freePos.Y - (Grid.Map != null ? Grid.HeightAt(_freePos.X, _freePos.Z) : 0));
                _freePos.Y += step * factor * agl * 0.15f;
                break;
            default:
                _zoomTarget = Mathf.Clamp(_zoomTarget * Mathf.Pow(ZoomStep, step * factor), MinZoom, MaxZoom);
                break;
        }
    }

    /// <summary>Zoom continuo (pellizco en pantallas táctiles).</summary>
    public void ZoomBy(float factor)
    {
        if (Mode == CamMode.Sobre) _chaseDist = Mathf.Clamp(_chaseDist * factor, 5f, Grid.Map != null && Grid.Large ? 240f : 70f);
        else if (Mode == CamMode.Cabina) _fov = Mathf.Clamp(_fov * factor, 30f, 95f);
        else _zoomTarget = Mathf.Clamp(_zoomTarget * factor, MinZoom, MaxZoom);
    }

    public void Snap()
    {
        _zoomTarget = Zoom;
        if (Target != null) GlobalPosition = Target.GlobalPosition + Vector3.Up * LookHeight;
        _vel = Vector3.Zero;
        _fresh = true;
        if (Camera != null && Mode == CamMode.Tactica) Camera.Size = _baseSize * Zoom;
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
        float s = _trauma * _trauma * (Mode == CamMode.Tactica ? Camera.Size * 0.025f : Mode == CamMode.Cabina ? 0.06f : 0.4f);
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
