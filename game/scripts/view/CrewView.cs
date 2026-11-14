using System;
using System.Collections.Generic;
using AresX.Mechs;
using AresX.Sim;
using Godot;

namespace AresX.View;

/// <summary>Un piloto (familia de sprites "pilots" del proyecto de mechas) con su pintura y orientación respecto a la cámara.</summary>
public partial class PilotActor : Node3D
{
    public string Paint = "player";
    public float SizeScale = 1.9f;
    public MechRig Rig { get; private set; } = null!;
    public Vector3 Facing = new(0, 0, 1);

    public override void _Ready()
    {
        Rig = new MechRig { Name = "Rig", Family = MechSkeletonData.Pilots, DepthBias = 0.08f };
        AddChild(Rig);
        Rig.EquipFactory("pilot");
        Rig.SizeScale = SizeScale;   // a escala del vehículo (el piloto del proyecto de mechas mide 1/4 de mecha)
        Rig.SetPaint(Paint);
        Rig.Play("idle");
    }

    public void Face(Vector3 dir)
    {
        dir.Y = 0;
        if (dir.LengthSquared() > 1e-4f) Facing = dir.Normalized();
    }

    public override void _Process(double delta)
    {
        float camYaw = GetViewport().GetCamera3D()?.GlobalRotation.Y ?? 0f;
        float yaw = Mathf.Atan2(-Facing.Z, Facing.X) - camYaw;
        int dirs = Rig.Skeleton.Dirs;
        Rig.Direction = Mathf.PosMod(Mathf.RoundToInt(yaw / Mathf.Tau * dirs), dirs);
    }
}

/// <summary>
/// TRIPULACIÓN del vehículo. Cada salida es una secuencia de tramos y el vehículo NO avanza mientras dure (Main espera a
/// que el piloto vuelva a subir):
///   * muestra: el piloto baja, corre al punto, se agacha a analizar y vuelve;
///   * rescate: baja (o desciende por cable si el helicóptero está en vuelo estacionario), corre hasta la persona, la
///     ayuda a incorporarse y vuelven juntos; la persona sube a bordo;
///   * entrega: el piloto acompaña a las personas hasta el punto médico (quedan ahí) y vuelve.
/// Si el helicóptero va a aterrizar, la salida espera a que toque tierra. En la base y la estación hay pilotos
/// conversando junto al refugio.
/// </summary>
public partial class CrewView : Node3D
{
    public Mission? Mission;
    public RescueView? Rescue;
    public RobotView? Vehicle;
    /// <summary>Tripulación de un helicóptero de apoyo: su altura (la del H-1 sale de la misión) y sin pilotos en los refugios.</summary>
    public Func<float>? AglFn;
    public bool Primary = true;
    /// <summary>Lo que hace la tripulación ahora (vacío si está a bordo): lo muestran la ficha y el HUD de cabina.</summary>
    public static string Task = "";

    private enum LegKind { WaitLanding, Move, Rope, Stay }
    private readonly record struct Leg(LegKind Kind, Vector3 A, Vector3 B, float Dur, string Anim, bool Escort);

    private PilotActor? _pilot;
    private readonly List<Leg> _legs = new();
    private int _leg = -1;
    private float _legT;
    private readonly List<PilotActor> _escort = new();
    private bool _escortAboard;               // al terminar, las personas quedan a bordo (ocultas)
    private readonly List<PilotActor> _idle = new();
    private MeshInstance3D _rope = null!;
    private int _delivered;

    public bool Busy => _leg >= 0;

    public override void _Ready()
    {
        if (Primary) Task = "";
        var m = Mission;
        if (m == null) return;
        if (!Primary) goto actors;
        // dos pilotos conversando en cada refugio (base y estación)
        int k = 0;
        foreach (var r in m.Map.Refuges)
        {
            var c = Grid.CellCenter(r);
            for (int s = -1; s <= 1; s += 2)
            {
                var p = new PilotActor { Paint = k++ % 2 == 0 ? "player" : "orbital" };
                AddChild(p);
                var pos = new Vector3(c.X + s * 0.9f, 0, c.Z + 2.1f);
                p.GlobalPosition = new Vector3(pos.X, Grid.HeightAt(pos.X, pos.Z), pos.Z);
                p.Face(new Vector3(-s, 0, 0));
                p.Rig.Play("talk");
                _idle.Add(p);
            }
        }
        actors:
        _pilot = new PilotActor { Paint = "player", Visible = false };
        AddChild(_pilot);
        _rope = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.035f, BottomRadius = 0.035f, Height = 1f, RadialSegments = 6 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("e8e2c8"), Roughness = 0.8f },
            Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_rope);
    }

    // ------------------------------------------------------------------ geometría de la salida
    private bool IsHeli => AglFn != null || Mission?.Heli == true;
    private bool Hovering => AglFn != null ? AglFn() > 0.5f : Mission is { Heli: true } m && m.HeliAglM > 0.5f;
    private Vector3 Ground(Vector3 p) => new(p.X, Grid.HeightAt(p.X, p.Z), p.Z);
    private Vector3 Side => Vehicle == null ? Vector3.Right : new Vector3(-Vehicle.Facing.Z, 0, Vehicle.Facing.X);
    private Vector3 Door => Vehicle == null ? Vector3.Zero : Ground(Vehicle.GlobalPosition + Side * (IsHeli ? 0.9f : 0.7f));
    private Vector3 HoistTop => Vehicle == null ? Vector3.Zero : Vehicle.BodyPosition - Vector3.Up * 0.5f;
    private Vector3 HoistGround => Vehicle == null ? Vector3.Zero : Ground(Vehicle.GlobalPosition);

    /// <summary>Segundos reales por unidad de tramo: la salida dura ~5 s a 1× y ~2 s a 8×.</summary>
    private static float Pace(float simSpeed) => 1f / MathF.Sqrt(MathF.Max(1f, simSpeed));

    private void Begin(string task)
    {
        if (_pilot == null || Vehicle == null) return;
        if (Primary) Task = task;
        _leg = 0;
        _legT = 0;
        _pilot.Visible = false;
    }

    /// <summary>Bajada: espera el aterrizaje o desciende por cable. Devuelve el punto de partida en el suelo.</summary>
    private Vector3 AddDescent(float k, bool escortRides = false)
    {
        if (Hovering)
        {
            _legs.Add(new Leg(LegKind.Rope, HoistTop, HoistGround, 1.1f * k, "climb", escortRides));
            return HoistGround;
        }
        if (IsHeli) _legs.Add(new Leg(LegKind.WaitLanding, Door, Door, 3f, "idle", false));
        return Door;
    }

    private void AddAscent(Vector3 from, float k, bool escort)
    {
        if (Hovering)
        {
            if (from.DistanceTo(HoistGround) > 0.2f) _legs.Add(new Leg(LegKind.Move, from, HoistGround, 0.6f * k, "run", escort));
            _legs.Add(new Leg(LegKind.Rope, HoistGround, HoistTop, 1.2f * k, "climb", escort));
        }
        else _legs.Add(new Leg(LegKind.Move, from, Door, 0.9f * k, "run", escort));
    }

    /// <summary>Muestra: el piloto baja, analiza junto al vehículo y vuelve a subir.</summary>
    public void StartSample(float simSpeed)
    {
        if (_pilot == null || Vehicle == null) return;
        _legs.Clear();
        _escort.Clear();
        float k = Pace(simSpeed);
        var start = AddDescent(k);
        var site = Ground(start + Vehicle.Facing * 1.8f + Side * 0.6f);
        _legs.Add(new Leg(LegKind.Move, start, site, 0.8f * k, "run", false));
        _legs.Add(new Leg(LegKind.Stay, site, site, 1.2f * k, "crouch", false));
        AddAscent(site, k, false);
        Begin("PILOTO FUERA · muestra");
    }

    /// <summary>Rescate: el piloto va por la(s) persona(s), las ayuda y vuelven juntos; quedan a bordo.</summary>
    public void StartPickup(IReadOnlyList<Victim> vs, float simSpeed)
    {
        if (_pilot == null || Vehicle == null || Rescue == null) return;
        _legs.Clear();
        _escort.Clear();
        float k = Pace(simSpeed);
        var start = AddDescent(k);
        var at = start;
        foreach (var v in vs)
        {
            var actor = Rescue.Actor(v.Id);
            if (actor == null) continue;
            Rescue.Managed.Add(v.Id);
            var p = Ground(actor.GlobalPosition - (actor.GlobalPosition - at).Normalized() * 0.7f);
            _legs.Add(new Leg(LegKind.Move, at, p, MathF.Max(0.5f, at.DistanceTo(p) / 4f) * k, "run", _escort.Count > 0));
            _legs.Add(new Leg(LegKind.Stay, p, p, 1.0f * k, "crouch", false));
            _escort.Add(actor);
            at = p;
        }
        AddAscent(at, k, true);
        _escortAboard = true;
        Begin(vs.Count > 1 ? $"PILOTO FUERA · rescate de {vs.Count}" : $"PILOTO FUERA · rescate · {vs[0].Name}");
    }

    /// <summary>Entrega: las personas bajan con el piloto, caminan al punto médico y se quedan ahí; el piloto vuelve.</summary>
    public void StartDelivery(IReadOnlyList<Victim> vs, float simSpeed)
    {
        if (_pilot == null || Vehicle == null || Rescue == null) return;
        _legs.Clear();
        _escort.Clear();
        float k = Pace(simSpeed);
        foreach (var v in vs)
        {
            var actor = Rescue.Actor(v.Id);
            if (actor == null) continue;
            Rescue.Managed.Add(v.Id);
            _escort.Add(actor);
        }
        var start = AddDescent(k, true);
        var drop = Ground(start + Side * 3.2f + Vehicle.Facing * (0.8f * (_delivered % 4) - 1.2f));
        _legs.Add(new Leg(LegKind.Move, start, drop, 1.0f * k, "run", true));
        _legs.Add(new Leg(LegKind.Stay, drop, drop, 0.6f * k, "talk", true));
        AddAscent(drop, k, false);
        _escortAboard = false;
        _delivered++;
        Begin($"PILOTO FUERA · entrega de {vs.Count}");
    }

    // ------------------------------------------------------------------ animación
    public override void _Process(double delta)
    {
        if (_pilot == null || _leg < 0) return;
        if (_leg >= _legs.Count) { Finish(); return; }
        var leg = _legs[_leg];
        if (leg.Kind == LegKind.WaitLanding)
        {
            // el helicóptero baja a la vista: la puerta se abre al tocar tierra
            _legT += (float)delta;
            bool down = Vehicle == null || Vehicle.BodyPosition.Y - Ground(Vehicle.GlobalPosition).Y < 1.25f;
            if (down || _legT > leg.Dur) Next();
            _pilot.Visible = false;
            foreach (var e in _escort) if (!_escortAboard) e.Visible = false;
            return;
        }
        _legT += (float)delta / MathF.Max(0.05f, leg.Dur);
        float t = Mathf.Clamp(_legT, 0, 1);
        var pos = leg.A.Lerp(leg.B, leg.Kind == LegKind.Rope ? Smooth(t) : t);
        if (leg.Kind == LegKind.Move) pos = Ground(pos);
        _pilot.Visible = true;
        _pilot.GlobalPosition = pos;
        var dir = leg.B - leg.A;
        if (leg.Kind == LegKind.Move) _pilot.Face(dir);
        _pilot.Rig.Play(leg.Anim);
        // cable del cabrestante
        _rope.Visible = leg.Kind == LegKind.Rope;
        if (_rope.Visible)
        {
            var top = HoistTop;
            float len = MathF.Max(0.05f, top.Y - (pos.Y + 1.2f));
            _rope.GlobalPosition = new Vector3(top.X, top.Y - len / 2, top.Z);
            _rope.Scale = new Vector3(1, len, 1);
        }
        // acompañantes: detrás del piloto en tierra, colgados junto a él en el cable
        for (int i = 0; i < _escort.Count; i++)
        {
            var e = _escort[i];
            if (!leg.Escort)
            {
                if (!_escortAboard && _leg < FirstEscortLeg()) e.Visible = false;   // entrega: aún dentro
                continue;
            }
            e.Visible = true;
            Vector3 ep;
            if (leg.Kind == LegKind.Rope) ep = pos + Side * (0.45f * (i + 1));
            else
            {
                var back = dir.LengthSquared() > 1e-4f ? -new Vector3(dir.X, 0, dir.Z).Normalized() : -Side;
                ep = Ground(pos + back * (0.8f * (i + 1)) + Side * (i % 2 == 0 ? 0.25f : -0.25f));
            }
            e.GlobalPosition = ep;
            if (leg.Kind == LegKind.Move) e.Face(dir);
            e.Rig.Play(leg.Kind == LegKind.Rope ? "climb" : leg.Kind == LegKind.Stay ? "idle" : "run");
        }
        if (_legT >= 1) Next();
    }

    private int FirstEscortLeg()
    {
        for (int i = 0; i < _legs.Count; i++) if (_legs[i].Escort) return i;
        return int.MaxValue;
    }

    private static float Smooth(float t) => t * t * (3 - 2 * t);

    private void Next()
    {
        _leg++;
        _legT = 0;
        if (_leg >= _legs.Count) Finish();
    }

    private void Finish()
    {
        _leg = -1;
        if (Primary) Task = "";
        if (_pilot != null) _pilot.Visible = false;
        _rope.Visible = false;
        foreach (var e in _escort)
        {
            if (_escortAboard) e.Visible = false;   // a bordo
            else { e.Visible = true; e.Rig.Play("idle"); e.Face(-Side); }   // atendidas en el punto médico
        }
        _escort.Clear();
    }
}
