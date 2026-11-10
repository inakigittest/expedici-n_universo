using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace AresX.Mechs;

/// <summary>
/// Visual de un mecha modular: un quad billboard por ranura (shaders/mech_part.gdshader).
///
///   * Posición de cada capa: pos(hija) = pos(padre) + socket del padre − ancla de la hija, por frame. (x, y) en
///     píxeles y z en profundidad, así piezas con medidas distintas (piernas más cortas, hombros más anchos) encajan.
///   * Oclusión: cada píxel escribe la profundidad que tenía en el render 3D (no hay tabla de orden de capas).
///   * Dos canales: PIERNAS (dirección de la marcha y animación de movimiento) y PARTE SUPERIOR (torso, brazos,
///     cabeza, mochila y armas: dirección de apuntado y, si se dispara o golpea, su propia animación). Como la raíz
///     del mecha cae en el mismo punto en cualquier dirección y animación, las dos mitades encajan por los sockets:
///     se puede caminar hacia un lado y apuntar o disparar hacia otro.
/// </summary>
public partial class MechRig : Node3D
{
    private static Shader? _shader;

    /// <summary>Cuánto se acerca el mecha a la cámara para que los pies no se hundan en el suelo.</summary>
    [Export] public float DepthBias { get; set; } = 0.3f;

    public MechSkeletonData Skeleton { get; private set; } = null!;
    /// <summary>Animación de las piernas (y de todo el cuerpo si la parte superior no tiene una propia).</summary>
    public string Anim => _lower.Anim;
    public int Frame => _lower.Frame;
    public bool Finished => _lower.Finished;
    /// <summary>Dirección de las piernas (y de todo el cuerpo si no hay UpperDirection).</summary>
    public int Direction { get; set; } = 6;
    /// <summary>Dirección de la parte superior; null = la de las piernas.</summary>
    public int? UpperDirection { get; set; }
    /// <summary>Animación propia de la parte superior (disparo, golpe); null = la de las piernas.</summary>
    public string? UpperAnim => _upper?.Anim;
    public bool UpperFinished => _upper?.Finished ?? Finished;

    /// <summary>Piezas montadas por ranura. Las ranuras de arma vacías no aparecen.</summary>
    public IReadOnlyDictionary<string, MechPartData> Loadout => _parts;

    /// <summary>Hay pieza en todas las ranuras obligatorias (al montar un conjunto se pasa por estados a medias).</summary>
    public bool IsComplete => Skeleton.Slots.All(s => Skeleton.IsOptional(s) || _parts.ContainsKey(s));

    /// <summary>Se emite al cambiar cualquier pieza.</summary>
    public event Action? LoadoutChanged;

    private sealed class Channel
    {
        public string Anim = "idle";
        public double Time;
        public int Frame;
        public bool Finished;
    }

    private readonly Channel _lower = new();
    private Channel? _upper;
    private readonly Dictionary<string, MechPartData> _parts = new();
    private readonly Dictionary<string, ShaderMaterial> _materials = new();
    private readonly Dictionary<string, MeshInstance3D> _meshes = new();
    private readonly Dictionary<string, Vector3> _pos = new();
    private readonly Dictionary<string, (MechPartData Part, string Anim)> _bound = new();
    private float _glow;

    public override void _Ready()
    {
        Skeleton = MechSkeletonData.Get();
        _shader ??= GD.Load<Shader>("res://shaders/mech_part.gdshader");
        float span = Skeleton.Cell * Skeleton.PixelSize;
        foreach (string slot in Skeleton.Slots)
        {
            var mat = new ShaderMaterial { Shader = _shader };
            mat.SetShaderParameter("pixel_size", Skeleton.PixelSize);
            mat.SetShaderParameter("depth_range", Skeleton.DepthRange);
            _materials[slot] = mat;
            var mesh = new MeshInstance3D
            {
                Name = slot,
                Mesh = new QuadMesh { Size = Vector2.One },
                MaterialOverride = mat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                // el shader agranda el quad hasta la celda: caja de culling a mano
                CustomAabb = new Aabb(new Vector3(-span, -span, -span), new Vector3(2 * span, 2 * span, 2 * span)),
            };
            _meshes[slot] = mesh;
            AddChild(mesh);
        }
    }

    /// <summary>Monta un conjunto de fábrica entero ("vanguardia", "krell", "aurea"…).</summary>
    public void EquipFactory(string factory)
    {
        foreach (var (slot, part) in Skeleton.Factory[factory])
        {
            if (part == null)
                _parts.Remove(slot);
            else
                _parts[slot] = MechPartData.Get(part, slot);
        }
        Changed();
    }

    /// <summary>Monta una pieza; null vacía la ranura (solo en las de arma).</summary>
    public void Equip(string slot, string? partId)
    {
        if (partId == null)
        {
            if (!Skeleton.IsOptional(slot))
                throw new ArgumentException($"La ranura {slot} no puede ir vacía");
            _parts.Remove(slot);
        }
        else
        {
            if (!Skeleton.Fits(partId, slot))
                throw new ArgumentException($"{partId} no encaja en la ranura {slot}");
            _parts[slot] = MechPartData.Get(partId, slot);
        }
        Changed();
    }

    private void Changed()
    {
        _bound.Clear();   // hay que volver a enlazar texturas
        LoadoutChanged?.Invoke();
    }

    /// <summary>Animación de las piernas (y de todo el cuerpo si la parte superior no tiene una propia).</summary>
    public void Play(string anim, bool restart = false) => Start(_lower, anim, restart);

    /// <summary>Animación propia de la parte superior (disparo, golpe), independiente de las piernas.</summary>
    public void PlayUpper(string anim, bool restart = false)
    {
        _upper ??= new Channel { Anim = "" };
        Start(_upper, anim, restart);
    }

    /// <summary>La parte superior vuelve a seguir la animación de las piernas.</summary>
    public void ClearUpper() => _upper = null;

    private static void Start(Channel c, string anim, bool restart)
    {
        if (anim == c.Anim && !restart)
            return;
        c.Anim = anim;
        c.Time = 0;
        c.Frame = 0;
        c.Finished = false;
    }

    /// <summary>Mezcla del color de calor/impacto, 0..1.</summary>
    public void SetGlow(float amount)
    {
        if (Mathf.IsEqualApprox(amount, _glow))
            return;
        _glow = amount;
        foreach (var m in _materials.Values)
            m.SetShaderParameter("glow", amount);
    }

    public override void _Process(double delta)
    {
        Advance(_lower, delta);
        if (_upper != null)
            Advance(_upper, delta);
        UpdateLayers();
    }

    private void Advance(Channel c, double delta)
    {
        var anim = Skeleton.Anims[c.Anim];
        c.Time += delta;
        int f = (int)(c.Time * anim.Fps);
        if (anim.Loop)
            f %= anim.Frames;
        else if (f >= anim.Frames)
        {
            f = anim.Frames - 1;
            c.Finished = true;
        }
        c.Frame = f;
    }

    /// <summary>(animación, dirección, frame) de una ranura según su canal.</summary>
    private (string Anim, int Dir, int Frame) Pose(string slot)
    {
        if (Skeleton.RootSlots.Contains(slot))
            return (_lower.Anim, Direction, _lower.Frame);
        int dir = UpperDirection ?? Direction;
        return _upper == null ? (_lower.Anim, dir, _lower.Frame) : (_upper.Anim, dir, _upper.Frame);
    }

    private void UpdateLayers()
    {
        if (!IsComplete)
            return;
        int cell = Skeleton.Cell;
        Vector3 rootPx = PlaceLegs();
        foreach (string slot in Skeleton.Slots)
        {
            _meshes[slot].Visible = _parts.TryGetValue(slot, out var part);
            if (part == null)
                continue;   // ranura de arma vacía (no tiene hijas)
            var (anim, dir, frame) = Pose(slot);
            if (Skeleton.Attach.TryGetValue(slot, out var att))
            {
                Vector3 parentPos = att.Parent == "root" ? Vector3.Zero : _pos[att.Parent];
                Vector3 socket;
                if (att.Parent == "root")
                    socket = rootPx;
                else
                {
                    var (pa, pd, pf) = Pose(att.Parent);
                    socket = _parts[att.Parent].Point(att.Socket, pa, pd, pf);
                }
                _pos[slot] = parentPos + socket - part.Point(att.Socket, anim, dir, frame);
            }
            else if (!_pos.ContainsKey(slot))
                _pos[slot] = Vector3.Zero;

            var mat = _materials[slot];
            if (!_bound.TryGetValue(slot, out var b) || b.Part != part || b.Anim != anim)
            {
                var (color, depth) = part.Sheet(anim);
                mat.SetShaderParameter("albedo_tex", color);
                mat.SetShaderParameter("depth_tex", depth);
                mat.SetShaderParameter("sheet_size", color.GetSize());
                _bound[slot] = (part, anim);
            }
            Vector3 p = _pos[slot];
            // hoja recortada: la celda es el rectángulo crop; su centro se desplaza respecto al de la celda completa
            var c = part.Crop(anim, cell);
            mat.SetShaderParameter("region", new Vector4(frame * c.Size.X, dir * c.Size.Y, c.Size.X, c.Size.Y));
            var cropShift = new Vector2(c.Position.X + c.Size.X / 2 - cell / 2, -(c.Position.Y + c.Size.Y / 2 - cell / 2));
            mat.SetShaderParameter("offset_px", Skeleton.BaseOffset + cropShift + new Vector2(Mathf.Round(p.X), -Mathf.Round(p.Y)));
            mat.SetShaderParameter("layer_depth", p.Z);
            mat.SetShaderParameter("depth_bias", DepthBias);
        }
    }

    /// <summary>
    /// Coloca las piernas y devuelve el píxel de la raíz común. Cada pierna se renderizó con la raíz a la altura que
    /// ella sola le daría (su "support"); la raíz real queda a la altura mayor (la pierna que llega más abajo sostiene
    /// el mecha) y la otra pierna sube lo que falta. Subir Δ en el mundo es, en la proyección ortográfica, −Δ·cos(pitch)
    /// píxeles en vertical y −Δ·sin(pitch) en profundidad.
    /// </summary>
    private Vector3 PlaceLegs()
    {
        string anim = _lower.Anim;
        int dir = Direction, frame = _lower.Frame;
        float ry = float.NegativeInfinity;
        foreach (string slot in Skeleton.RootSlots)
            ry = Mathf.Max(ry, _parts[slot].Point("support", anim, dir, frame).X);
        float pitch = Mathf.DegToRad(Skeleton.Pitch);
        Vector3 root = Vector3.Zero;
        foreach (string slot in Skeleton.RootSlots)
        {
            float shift = ry - _parts[slot].Point("support", anim, dir, frame).X;
            _pos[slot] = new Vector3(0, -shift * Mathf.Cos(pitch) / Skeleton.PixelSize, -shift * Mathf.Sin(pitch));
            root = _pos[slot] + _parts[slot].Point("root", anim, dir, frame);
        }
        return root;
    }
}
