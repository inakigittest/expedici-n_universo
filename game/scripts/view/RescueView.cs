using System;
using System.Collections.Generic;
using AresX.Sim;
using Godot;

namespace AresX.View;

/// <summary>
/// Misión de rescate: cada persona atrapada es un piloto tendido (animación "down") con una baliza pulsante y una
/// etiqueta con su supervivencia restante; los puntos médicos llevan una cruz roja. Al subir a bordo la persona
/// desaparece (después de que la tripulación llega a buscarla).
/// </summary>
public partial class RescueView : Node3D
{
    public Mission? Mission;
    private readonly Dictionary<int, (PilotActor Actor, Label3D Label, MeshInstance3D Beacon, float HideAt)> _victims = new();
    private float _t;

    public override void _Ready()
    {
        var m = Mission;
        if (m == null || !m.Rescue) return;
        var beaconMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 0.25f, 0.2f, 0.45f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, EmissionEnabled = true, Emission = new Color(1f, 0.2f, 0.15f),
        };
        foreach (var v in m.Victims)
        {
            var c = Grid.CellCenter(v.Cell);
            var actor = new PilotActor { Paint = v.Kind switch { VictimKind.Montana => "orbital", VictimKind.Inundacion => "nova", VictimKind.Aluvion => "vulkan", _ => "aegis" } };
            AddChild(actor);
            actor.GlobalPosition = new Vector3(c.X, Grid.HeightAt(c.X, c.Z), c.Z);
            actor.Rig.Play("down");
            var beacon = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.35f, Height = 9f, RadialSegments = 10 },
                MaterialOverride = beaconMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(beacon);
            beacon.GlobalPosition = actor.GlobalPosition + Vector3.Up * 4.5f;
            var label = new Label3D
            {
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 40, PixelSize = 0.012f, OutlineSize = 10,
                OutlineModulate = new Color(0, 0, 0, 0.85f), NoDepthTest = true, RenderPriority = 10, Modulate = new Color("ff8a7a"),
            };
            AddChild(label);
            label.GlobalPosition = actor.GlobalPosition + Vector3.Up * 3.2f;
            _victims[v.Id] = (actor, label, beacon, -1);
        }
        foreach (var mp in m.Medical)
        {
            var c = Grid.CellCenter(mp.Cell);
            var cross = new Node3D();
            AddChild(cross);
            cross.GlobalPosition = new Vector3(c.X, Grid.HeightAt(c.X, c.Z) + 0.15f, c.Z);
            var red = new StandardMaterial3D { AlbedoColor = new Color("e0302a"), EmissionEnabled = true, Emission = new Color("e0302a"), EmissionEnergyMultiplier = 0.6f };
            cross.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2.4f, 0.12f, 0.7f) }, MaterialOverride = red });
            cross.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.7f, 0.12f, 2.4f) }, MaterialOverride = red });
            AddChild(new Label3D
            {
                Text = "✚ " + mp.Name.ToUpperInvariant(), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 40, PixelSize = 0.012f,
                OutlineSize = 10, OutlineModulate = new Color(0, 0, 0, 0.85f), NoDepthTest = true, RenderPriority = 10, Modulate = new Color("ff6a60"),
                Position = cross.Position + Vector3.Up * 4f,
            });
        }
    }

    public override void _Process(double delta)
    {
        var m = Mission;
        if (m == null || !m.Rescue) return;
        _t += (float)delta;
        foreach (var v in m.Victims)
        {
            if (!_victims.TryGetValue(v.Id, out var e)) continue;
            bool gone = v.OnBoard || v.Delivered;
            if (gone && e.HideAt < 0) _victims[v.Id] = e = (e.Actor, e.Label, e.Beacon, _t + 1.2f);   // la tripulación llega primero
            bool hidden = gone && _t >= e.HideAt;
            e.Actor.Visible = !hidden;
            e.Beacon.Visible = !hidden && !v.Lost && !v.Unreachable;
            e.Label.Visible = !hidden;
            float pct = v.SurvivalMaxS > 0 ? v.SurvivalS / v.SurvivalMaxS : 0;
            e.Label.Text = v.Lost ? $"{v.Name} · sin vida" : v.Unreachable ? $"{v.Name} · inalcanzable" : $"{v.Name} · {v.SurvivalS / 60:0} min";
            e.Label.Modulate = v.Lost || v.Unreachable ? new Color(0.6f, 0.62f, 0.66f) : pct < 0.3f ? new Color("ff5a4a") : pct < 0.6f ? new Color("ffb347") : new Color("8cff9a");
            float pulse = 0.5f + 0.5f * MathF.Sin(_t * (pct < 0.3f ? 9f : 4f));
            e.Beacon.Scale = new Vector3(1 + 0.4f * pulse, 1, 1 + 0.4f * pulse);
        }
    }
}
