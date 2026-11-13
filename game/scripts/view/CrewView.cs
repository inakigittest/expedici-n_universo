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
/// TRIPULACIÓN del vehículo: al muestrear, un piloto baja, corre al punto de muestra, se agacha a analizar y vuelve a
/// subir; en la base y la estación, pilotos conversando junto al refugio. (Las víctimas y los rescatistas de la misión de
/// rescate también usan PilotActor.)
/// </summary>
public partial class CrewView : Node3D
{
    public Mission? Mission;
    private PilotActor? _sampler;
    private Vector3 _from, _to;
    private float _t = -1, _dur = 2.6f;
    private readonly List<PilotActor> _idle = new();

    public override void _Ready()
    {
        var m = Mission;
        if (m == null) return;
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
        _sampler = new PilotActor { Paint = "player", Visible = false };
        AddChild(_sampler);
    }

    /// <summary>Muestreo: el piloto baja en <paramref name="from"/>, analiza en <paramref name="site"/> y vuelve.</summary>
    public void StartSample(Vector3 from, Vector3 site, float realSeconds)
    {
        if (_sampler == null) return;
        _from = from;
        _to = site;
        _dur = MathF.Max(0.8f, realSeconds);
        _t = 0;
        _sampler.Visible = true;
        _sampler.GlobalPosition = from;
        _sampler.Rig.Play("run", true);
    }

    public override void _Process(double delta)
    {
        if (_sampler == null || _t < 0) return;
        _t += (float)delta / _dur;
        float t = _t;
        Vector3 pos;
        if (t < 0.3f)
        {
            pos = _from.Lerp(_to, t / 0.3f);
            _sampler.Face(_to - _from);
            _sampler.Rig.Play("run");
        }
        else if (t < 0.72f)
        {
            pos = _to;
            _sampler.Rig.Play("crouch");   // analizar la muestra
        }
        else if (t < 1f)
        {
            pos = _to.Lerp(_from, (t - 0.72f) / 0.28f);
            _sampler.Face(_from - _to);
            _sampler.Rig.Play("run");
        }
        else
        {
            _sampler.Visible = false;
            _t = -1;
            return;
        }
        _sampler.GlobalPosition = new Vector3(pos.X, Grid.HeightAt(pos.X, pos.Z), pos.Z);
    }
}
