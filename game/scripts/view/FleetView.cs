using System;
using System.Collections.Generic;
using System.Linq;
using AresX.Core;
using AresX.Sim;
using Godot;

namespace AresX.View;

/// <summary>Secuencia de pasos para un actor (caminar, agacharse, apuntar la manguera…): cada paso va de donde quedó a un punto.</summary>
internal sealed class ActorScript
{
    public readonly PilotActor A;
    private readonly List<(Vector3 To, string Anim, float Dur, Vector3? Face)> _steps = new();
    private int _k = -1;
    private float _t;
    private Vector3 _from;
    public bool HideAtEnd;
    public bool Done => _k < 0 || _k >= _steps.Count;

    public ActorScript(PilotActor a) => A = a;

    public ActorScript Go(Vector3 to, string anim, float dur, Vector3? face = null) { _steps.Add((to, anim, dur, face)); return this; }

    public void Start(Vector3 from)
    {
        _from = from;
        _k = 0;
        _t = 0;
        A.Visible = true;
        A.GlobalPosition = from;
    }

    public void Update(float dt)
    {
        if (Done) return;
        var s = _steps[_k];
        _t += dt / MathF.Max(0.05f, s.Dur);
        float t = Mathf.Clamp(_t, 0, 1);
        var p = _from.Lerp(s.To, t);
        p.Y = Grid.HeightAt(p.X, p.Z);
        A.GlobalPosition = p;
        if (s.Face is Vector3 f) A.Face(f - p);
        else if ((s.To - _from).LengthSquared() > 1e-4f) A.Face(s.To - _from);
        A.Rig.Play(s.Anim);
        if (_t >= 1)
        {
            _from = s.To;
            _k++;
            _t = 0;
            if (Done && HideAtEnd) A.Visible = false;
        }
    }
}

/// <summary>
/// FLOTA en 3D: helicópteros de apoyo (mismo modelo que el H-1, con su tripulación), carros de bomberos de primitivas
/// (cabina, estanque, escalera y balizas), los 6 bomberos (rescatan personas o despliegan mangueras con chorro de agua),
/// descargas del balde, etiquetas con el estado de cada vehículo y líneas de radio entre vehículos al comunicarse.
/// </summary>
public partial class FleetView : Node3D
{
    public Mission? Mission;
    public RescueView? Rescue;
    public RobotView? Main;
    public Func<float> SimSpeed = () => 1f;

    private sealed class Vis
    {
        public FleetUnit U = null!;
        public Node3D Body = null!;
        public RobotView? Heli;
        public CrewView? Crew;
        public Label3D Label = null!;
        public Cell LastPos;
        public int LastSerial;
        public Vector3 Pos, Facing = Vector3.Right;
        public readonly List<PilotActor> Firefighters = new();
        public readonly List<ActorScript> Scripts = new();
        public readonly List<(MeshInstance3D Hose, GpuParticles3D Spray, PilotActor Who)> Hoses = new();
        public Node3D? Beacons;
    }

    private readonly List<Vis> _vis = new();
    private MeshInstance3D _radio = null!;
    private ImmediateMesh _radioMesh = null!;
    private float _t;

    public override void _Ready()
    {
        var m = Mission;
        if (m == null) return;
        foreach (var u in m.Fleet)
        {
            var v = new Vis { U = u, LastPos = u.Pos, LastSerial = u.ActionSerial };
            if (u.Heli)
            {
                var rv = new RobotView { Name = u.Name, Heli = true };
                AddChild(rv);
                rv.Place(u.Pos);
                v.Heli = rv;
                v.Body = rv;
                var crew = new CrewView { Name = u.Name + "Crew", Mission = m, Vehicle = rv, Rescue = Rescue, Primary = false, AglFn = () => u.AglM };
                AddChild(crew);
                v.Crew = crew;
            }
            else
            {
                v.Body = BuildTruck(out var beacons);
                v.Beacons = beacons;
                AddChild(v.Body);
                v.Pos = Grid.CellCenter(u.Pos);
                v.Body.GlobalPosition = v.Pos;
                for (int i = 0; i < u.Crew; i++)
                {
                    var ff = new PilotActor { Paint = "krell", Visible = false };
                    AddChild(ff);
                    v.Firefighters.Add(ff);
                }
            }
            v.Label = new Label3D
            {
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 34, PixelSize = 0.012f, OutlineSize = 9, NoDepthTest = true, RenderPriority = 9,
                OutlineModulate = new Color(0, 0, 0, 0.85f), Modulate = u.Heli ? new Color("8fd8ff") : new Color("ff9a5a"),
            };
            AddChild(v.Label);
            _vis.Add(v);
        }
        _radioMesh = new ImmediateMesh();
        _radio = new MeshInstance3D
        {
            Mesh = _radioMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                NoDepthTest = true, RenderPriority = 8,
            },
        };
        AddChild(_radio);
    }

    // ------------------------------------------------------------------ carro de bomberos (primitivas, escala exagerada como el helicóptero)
    private static Node3D BuildTruck(out Node3D beacons)
    {
        var root = new Node3D { Name = "Truck" };
        StandardMaterial3D M(string hex, float rough = 0.5f, float metal = 0.1f) => new() { AlbedoColor = new Color(hex), Roughness = rough, Metallic = metal };
        var red = M("c8201c", 0.4f, 0.2f);
        var white = M("e8e8e8");
        var dark = M("23262b", 0.6f, 0.3f);
        var chrome = M("b9bec4", 0.2f, 0.8f);
        var glass = M("1b3a52", 0.05f, 0.6f);
        void Add(Mesh mesh, Material mat, Vector3 pos, Vector3? rot = null) =>
            root.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = pos, RotationDegrees = rot ?? Vector3.Zero });
        // eje +X = adelante
        Add(new BoxMesh { Size = new Vector3(4.6f, 0.35f, 1.7f) }, dark, new Vector3(0, 0.45f, 0));
        Add(new BoxMesh { Size = new Vector3(1.3f, 1.25f, 1.7f) }, red, new Vector3(1.7f, 1.2f, 0));          // cabina
        Add(new BoxMesh { Size = new Vector3(0.08f, 0.55f, 1.5f) }, glass, new Vector3(2.36f, 1.45f, 0));     // parabrisas
        Add(new BoxMesh { Size = new Vector3(3.1f, 1.45f, 1.75f) }, red, new Vector3(-0.55f, 1.32f, 0));     // carrocería / estanque
        Add(new BoxMesh { Size = new Vector3(3.12f, 0.12f, 1.77f) }, white, new Vector3(-0.55f, 1.0f, 0));   // franja
        Add(new BoxMesh { Size = new Vector3(3.3f, 0.1f, 0.32f) }, chrome, new Vector3(-0.45f, 2.15f, 0.42f)); // escalera
        Add(new BoxMesh { Size = new Vector3(3.3f, 0.1f, 0.32f) }, chrome, new Vector3(-0.45f, 2.15f, -0.42f));
        for (int i = 0; i < 7; i++) Add(new BoxMesh { Size = new Vector3(0.06f, 0.08f, 0.84f) }, chrome, new Vector3(-2.0f + i * 0.5f, 2.18f, 0));
        Add(new CylinderMesh { TopRadius = 0.25f, BottomRadius = 0.25f, Height = 1.6f }, chrome, new Vector3(-2.15f, 1.0f, 0), new Vector3(90, 0, 0)); // carrete de manguera
        foreach (float x in new[] { 1.5f, -0.6f, -1.6f })
        foreach (float z in new[] { 0.86f, -0.86f })
            Add(new CylinderMesh { TopRadius = 0.36f, BottomRadius = 0.36f, Height = 0.25f }, dark, new Vector3(x, 0.36f, z), new Vector3(90, 0, 0));
        beacons = new Node3D { Name = "Beacons" };
        root.AddChild(beacons);
        foreach (var (z, hex) in new[] { (0.45f, "3a7bff"), (-0.45f, "ff2a2a") })
        {
            var mat = new StandardMaterial3D { AlbedoColor = new Color(hex), EmissionEnabled = true, Emission = new Color(hex), EmissionEnergyMultiplier = 3f };
            beacons.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.22f, 0.16f, 0.4f) }, MaterialOverride = mat, Position = new Vector3(1.7f, 1.93f, z) });
            beacons.AddChild(new OmniLight3D { LightColor = new Color(hex), LightEnergy = 1.2f, OmniRange = 3.5f, Position = new Vector3(1.7f, 2.1f, z) });
        }
        root.Scale = Vector3.One * 0.9f;
        return root;
    }

    // ------------------------------------------------------------------ cuadro a cuadro
    public override void _Process(double delta)
    {
        var m = Mission;
        if (m == null) return;
        float dt = (float)delta;
        _t += dt;
        float speed = MathF.Max(1, SimSpeed());
        foreach (var v in _vis)
        {
            var u = v.U;
            // movimiento
            if (v.Heli != null)
            {
                if (u.Pos != v.LastPos) v.Heli.MoveTo(u.Pos, 0.45f / MathF.Sqrt(speed));
                v.Heli.HeliAglM = u.AglM;
                v.Pos = v.Heli.BodyPosition;
            }
            else
            {
                var goal = Grid.CellCenter(u.Pos);
                var d = goal - v.Pos;
                d.Y = 0;
                if (d.LengthSquared() > 0.01f) v.Facing = v.Facing.Lerp(d.Normalized(), 1 - MathF.Exp(-6 * dt)).Normalized();
                v.Pos = v.Pos.Lerp(goal, 1 - MathF.Exp(-4.5f * dt * MathF.Sqrt(speed)));
                v.Pos.Y = Grid.HeightAt(v.Pos.X, v.Pos.Z);
                v.Body.GlobalPosition = v.Pos;
                v.Body.Rotation = new Vector3(0, MathF.Atan2(-v.Facing.Z, v.Facing.X), 0);
                bool lights = u.Status.StartsWith("rumbo") || u.Busy != "";
                if (v.Beacons != null)
                {
                    v.Beacons.Visible = lights;
                    int ph = (int)(_t * 6) % 2;
                    for (int i = 0; i < v.Beacons.GetChildCount(); i++)
                        if (v.Beacons.GetChild(i) is OmniLight3D ol) ol.LightEnergy = (i < 2) == (ph == 0) ? 1.6f : 0.1f;
                }
            }
            v.LastPos = u.Pos;
            // etiqueta
            string extra = u.Aboard.Count > 0 ? $" · {u.Aboard.Count}/{u.Capacity} a bordo" : m.FireMode ? $" · {u.WaterL:0} L" : "";
            v.Label.Text = $"{u.Name} · {u.Status}{extra}";
            v.Label.GlobalPosition = (v.Heli != null ? v.Heli.BodyPosition : v.Pos) + Vector3.Up * (v.Heli != null ? 3.2f : 3.0f);
            // acciones con tripulación
            if (u.ActionSerial != v.LastSerial)
            {
                v.LastSerial = u.ActionSerial;
                StartAction(m, v, speed);
            }
            foreach (var s in v.Scripts) s.Update(dt);
            v.Scripts.RemoveAll(s => s.Done);
            UpdateHoses(m, v);
        }
        DrawRadio(m);
    }

    private void StartAction(Mission m, Vis v, float speed)
    {
        var u = v.U;
        float k = 1f / MathF.Sqrt(speed);
        switch (u.Busy)
        {
            case "rescate" when u.Goal is Victim victim:
                if (v.Crew != null) v.Crew.StartPickup(new[] { victim }, speed);
                else TruckRescue(v, victim, k);
                break;
            case "entrega":
                var list = u.Aboard.Where(x => x.Active || x.Delivered).ToList();
                if (list.Count == 0) break;
                if (v.Crew != null) v.Crew.StartDelivery(list, speed);
                else TruckDeliver(v, list, k);
                break;
            case "agua":
                if (v.Heli != null) WaterDrop(v.Heli.BodyPosition);
                else if (u.Goal is FireTask ft) DeployHoses(m, v, ft, k);
                break;
        }
    }

    // ------------------------------------------------------------------ bomberos
    private Vector3 Rear(Vis v) => v.Pos - v.Facing * 2.2f + new Vector3(-v.Facing.Z, 0, v.Facing.X) * 1.1f;

    private void TruckRescue(Vis v, Victim victim, float k)
    {
        var actor = Rescue?.Actor(victim.Id);
        if (actor == null) return;
        Rescue!.Managed.Add(victim.Id);
        var door = Rear(v);
        var at = actor.GlobalPosition;
        for (int i = 0; i < 2 && i < v.Firefighters.Count; i++)
        {
            var side = new Vector3(-v.Facing.Z, 0, v.Facing.X) * (i == 0 ? 0.6f : -0.6f);
            var s = new ActorScript(v.Firefighters[i]) { HideAtEnd = true }
                .Go(at + side, "run", 1.2f * k).Go(at + side, "crouch", 1.4f * k).Go(door + side * 0.5f, "run", 1.2f * k);
            s.Start(door);
            v.Scripts.Add(s);
        }
        var vs = new ActorScript(actor) { HideAtEnd = true }.Go(at, "down", 2.6f * k).Go(door, "run", 1.2f * k);
        vs.Start(at);
        v.Scripts.Add(vs);
    }

    private void TruckDeliver(Vis v, List<Victim> list, float k)
    {
        var door = Rear(v);
        var side = new Vector3(-v.Facing.Z, 0, v.Facing.X);
        for (int i = 0; i < list.Count; i++)
        {
            var actor = Rescue?.Actor(list[i].Id);
            if (actor == null) continue;
            Rescue!.Managed.Add(list[i].Id);
            var drop = door + side * (2.8f + 0.7f * i);
            var s = new ActorScript(actor).Go(drop, "run", 1.3f * k).Go(drop, "idle", 0.2f, drop + side);
            s.Start(door);
            v.Scripts.Add(s);
        }
        if (v.Firefighters.Count > 0)
        {
            var s = new ActorScript(v.Firefighters[0]) { HideAtEnd = true }.Go(door + side * 2.4f, "run", 1.3f * k).Go(door + side * 2.4f, "talk", 1f * k).Go(door, "run", 1.2f * k);
            s.Start(door);
            v.Scripts.Add(s);
        }
    }

    /// <summary>Los 6 bomberos salen en abanico hacia el fuego y apuntan las mangueras (chorro de agua) mientras dure la acción.</summary>
    private void DeployHoses(Mission m, Vis v, FireTask ft, float k)
    {
        ClearHoses(v);
        var door = Rear(v);
        var target = Grid.CellCenter(ft.Target);
        var dir = target - door;
        dir.Y = 0;
        float dist = dir.Length();
        dir = dist > 0.01f ? dir / dist : v.Facing;
        var side = new Vector3(-dir.Z, 0, dir.X);
        int n = v.Firefighters.Count;
        for (int i = 0; i < n; i++)
        {
            var ff = v.Firefighters[i];
            float spread = (i - (n - 1) / 2f) * 0.9f;
            var stand = door + dir * MathF.Min(dist * 0.55f, Grid.U * 1.4f) + side * spread;
            var s = new ActorScript(ff).Go(stand, "run", 1.1f * k).Go(stand, "aim", 60f, target + side * spread * 0.5f);
            s.Start(door);
            v.Scripts.Add(s);
            var hose = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.05f, Height = 1f, RadialSegments = 5 },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("d8c27a"), Roughness = 0.8f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(hose);
            var spray = WaterParticles(false);
            AddChild(spray);
            v.Hoses.Add((hose, spray, ff));
        }
    }

    private void UpdateHoses(Mission m, Vis v)
    {
        if (v.Hoses.Count == 0) return;
        var u = v.U;
        if (u.Busy != "agua")
        {
            // terminó: guardar mangueras y volver al carro
            var door = Rear(v);
            foreach (var ff in v.Firefighters.Where(f => f.Visible))
            {
                var s = new ActorScript(ff) { HideAtEnd = true }.Go(door, "run", 1.1f);
                s.Start(ff.GlobalPosition);
                v.Scripts.RemoveAll(x => x.A == ff);
                v.Scripts.Add(s);
            }
            ClearHoses(v);
            return;
        }
        var reel = v.Pos - v.Facing * 2.0f + Vector3.Up * 1.0f;
        var target = u.Goal is FireTask ft ? Grid.CellCenter(ft.Target) : v.Pos + v.Facing * 4;
        foreach (var (hose, spray, who) in v.Hoses)
        {
            var hand = who.GlobalPosition + Vector3.Up * 0.9f;
            var mid = (reel + hand) / 2;
            float len = reel.DistanceTo(hand);
            if (len < 0.05f) continue;
            var up = (hand - reel) / len;
            var basis = Basis.LookingAt(up.Cross(Vector3.Right).LengthSquared() > 1e-4f ? up.Cross(Vector3.Right) : Vector3.Forward, up);
            hose.GlobalTransform = new Transform3D(new Basis(basis.X, up, basis.Z).Orthonormalized().Scaled(new Vector3(1, len, 1)), mid);
            // chorro hacia el fuego
            var aim = target - hand;
            spray.GlobalPosition = hand;
            if (aim.LengthSquared() > 0.01f) spray.LookAt(hand + aim.Normalized() + Vector3.Up * 0.35f, Vector3.Up);
            spray.Emitting = who.Rig.Anim == "aim";
        }
    }

    private void ClearHoses(Vis v)
    {
        foreach (var (hose, spray, _) in v.Hoses) { hose.QueueFree(); spray.QueueFree(); }
        v.Hoses.Clear();
    }

    private static GpuParticles3D WaterParticles(bool drop)
    {
        var pm = new ParticleProcessMaterial
        {
            Direction = drop ? new Vector3(0, -1, 0) : new Vector3(0, 0, -1), Spread = drop ? 28f : 7f,
            InitialVelocityMin = drop ? 1.5f : 7f, InitialVelocityMax = drop ? 4f : 10f, Gravity = new Vector3(0, -9.8f, 0),
            ScaleMin = drop ? 0.6f : 0.35f, ScaleMax = drop ? 1.4f : 0.7f,
            Color = new Color(0.75f, 0.88f, 1f, 0.75f),
        };
        if (drop) { pm.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere; pm.EmissionSphereRadius = 0.8f; }
        return new GpuParticles3D
        {
            Amount = drop ? 260 : 40, Lifetime = drop ? 1.8f : 0.9f, OneShot = drop, Emitting = !drop, Explosiveness = drop ? 0.55f : 0f,
            ProcessMaterial = pm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            DrawPass1 = new QuadMesh
            {
                Size = new Vector2(0.35f, 0.35f),
                Material = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, VertexColorUseAsAlbedo = true,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, AlbedoColor = new Color(0.8f, 0.9f, 1f, 0.8f),
                },
            },
        };
    }

    /// <summary>Descarga del balde (cualquier helicóptero): cortina de agua hacia el suelo.</summary>
    public void WaterDrop(Vector3 at)
    {
        var p = WaterParticles(true);
        AddChild(p);
        p.GlobalPosition = at - Vector3.Up * 0.6f;
        p.Emitting = true;
        GetTree().CreateTimer(3.0).Timeout += () => { if (IsInstanceValid(p)) p.QueueFree(); };
    }

    /// <summary>Nodo 3D de un vehículo de la flota por nombre (H-2, B-1…).</summary>
    public Node3D? BodyOf(string name) => _vis.FirstOrDefault(v => v.U.Name == name)?.Body;

    // ------------------------------------------------------------------ radio
    private Vector3 PosOf(int id) => id == 0 ? Main?.BodyPosition ?? Vector3.Zero : _vis.FirstOrDefault(x => x.U.Id == id) is { } v ? (v.Heli?.BodyPosition ?? v.Pos + Vector3.Up * 2) : Vector3.Zero;

    private void DrawRadio(Mission m)
    {
        _radioMesh.ClearSurfaces();
        var recent = m.Comms.Where(c => m.StepCount - c.Step <= 3).ToList();
        if (recent.Count == 0) return;
        _radioMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        foreach (var (from, to, step) in recent)
        {
            var targets = to < 0 ? _vis.Select(x => x.U.Id).ToList() : new List<int> { to };
            float fade = 1f - (m.StepCount - step) / 4f;
            foreach (int t in targets)
            {
                var a = PosOf(from) + Vector3.Up * 0.8f;
                var b = PosOf(t) + Vector3.Up * 0.8f;
                // trazos que avanzan (paquetes de radio) en una curva sobre los vehículos
                int segs = 24;
                var lift = Vector3.Up * MathF.Min(8f, a.DistanceTo(b) * 0.15f);
                for (int s = 0; s < segs; s++)
                {
                    float t0 = s / (float)segs, t1 = (s + 0.6f) / segs;
                    if (((s + (int)(_t * 12)) % 3) != 0) continue;
                    Vector3 P(float x) => a.Lerp(b, x) + lift * 4 * x * (1 - x);
                    var col = new Color(0.4f, 1f, 0.6f, 0.85f * fade);
                    _radioMesh.SurfaceSetColor(col);
                    _radioMesh.SurfaceAddVertex(P(t0));
                    _radioMesh.SurfaceSetColor(col);
                    _radioMesh.SurfaceAddVertex(P(t1));
                }
            }
        }
        _radioMesh.SurfaceEnd();
    }
}
