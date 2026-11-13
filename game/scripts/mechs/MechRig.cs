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

    /// <summary>Familia de sprites: "mechs" o "pilots" (assets/&lt;familia&gt;). Fijarla antes de añadir el nodo.</summary>
    [Export] public string Family { get; set; } = MechSkeletonData.Mechs;

    private MechSkeletonData? _skeleton;
    public MechSkeletonData Skeleton => _skeleton ??= MechSkeletonData.Get(Family);

    private Color[]? _paint;

    /// <summary>
    /// Pintura (familia de pilotos): 21 colores que sustituyen a los colores clave del sprite (traje, ribete y visor;
    /// ver MechSkeletonData.Paints). null = sin pintura.
    /// </summary>
    public void SetPaint(Color[]? colors)
    {
        _paint = colors;
        foreach (var m in _materials.Values)
            ApplyPaint(m);
    }

    /// <summary>Pinta con la paleta de un fabricante (claves de skeleton.json → paints; "player" = el jugador).</summary>
    public void SetPaint(string maker) =>
        SetPaint(Skeleton.Paints.TryGetValue(maker, out var c) ? c : Skeleton.Paints.GetValueOrDefault("player"));

    private void ApplyPaint(ShaderMaterial m)
    {
        m.SetShaderParameter("use_paint", _paint != null);
        if (_paint != null)
            m.SetShaderParameter("paint", _paint);
    }
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
    private float _glow, _flash, _outline, _scale = 1f;

    /// <summary>Armas guardadas a la vista como máximo (dos en la espalda y una en cada muslo).</summary>
    public const int MaxStowed = 4;
    private readonly List<(MechPartData Part, string Mount)> _stowed = new();

    /// <summary>
    /// Armas guardadas (de los juegos no activos) con su montaje: stow_back_r/l (espalda, del torso) o holster_r/l
    /// (muslos, de las piernas). Se dibujan con la hoja 'stow_back' / 'stow_leg' del arma; sin montaje no se ven.
    /// </summary>
    public void SetStowed(IEnumerable<(string Id, string? Mount)> items)
    {
        _stowed.Clear();
        foreach (var (id, mount) in items)
            if (mount != null && _stowed.Count < MaxStowed)
                _stowed.Add((MechPartData.Get(id, mount.EndsWith("_l") ? "weapon_l" : "weapon_r", Skeleton.RootPath), mount));
    }
    private readonly Dictionary<string, (Vector2 Center, Vector2 Size)> _rects = new();

    /// <summary>Tamaño del mecha entero (1 = normal). Los líderes enemigos son más grandes sin modelo nuevo.</summary>
    public float SizeScale
    {
        get => _scale;
        set
        {
            _scale = value;
            foreach (var m in _materials.Values)
                m.SetShaderParameter("scale", value);
        }
    }

    /// <summary>Contorno de la silueta (0 = sin contorno).</summary>
    public void SetOutline(Color color, float amount)
    {
        if (Mathf.IsEqualApprox(amount, _outline) && amount == 0)
            return;
        _outline = amount;
        foreach (var m in _materials.Values)
        {
            m.SetShaderParameter("outline", amount);
            m.SetShaderParameter("outline_color", color);
        }
    }

    /// <summary>Destello blanco de impacto, 0..1.</summary>
    public void SetFlash(float amount)
    {
        if (Mathf.IsEqualApprox(amount, _flash))
            return;
        _flash = amount;
        foreach (var m in _materials.Values)
            m.SetShaderParameter("flash", amount);
    }

    /// <summary>
    /// Rectángulo que ocupa ahora la pieza de una ranura, en píxeles de sprite respecto al origen del mecha (Y arriba,
    /// sin aplicar SizeScale): sirve para colocar efectos sobre una pieza (los ojos de un líder sobre la cabeza).
    /// </summary>
    public bool TryGetSlotRect(string slot, out Vector2 center, out Vector2 size)
    {
        bool ok = _rects.TryGetValue(slot, out var r) && _meshes[slot].Visible;
        center = r.Center;
        size = r.Size;
        return ok;
    }

    /// <summary>
    /// Desplazamiento (local, en el mundo) que en pantalla queda 'marginPx' píxeles de sprite por encima de la cabeza:
    /// para placas y barras sobre el mecha. Se mueve en el plano de la cámara, como los sprites.
    /// </summary>
    public Vector3 AboveHead(float marginPx)
    {
        float top = TryGetSlotRect("head", out var c, out var size) ? c.Y + size.Y / 2 : 110f;
        var cam = GetViewport()?.GetCamera3D();
        Vector3 up = cam?.GlobalBasis.Y ?? Vector3.Up;
        return up * (top + marginPx) * Skeleton.PixelSize * _scale;
    }

    /// <summary>Dirección (0-7) con la que se dibuja la pieza de una ranura.</summary>
    public int SlotDirection(string slot) => Pose(slot).Dir;

    public override void _Ready()
    {
        _shader ??= GD.Load<Shader>("res://shaders/mech_part.gdshader");
        float span = Skeleton.Cell * Skeleton.PixelSize;
        var layers = Skeleton.Slots.Concat(Enumerable.Range(0, MaxStowed).Select(i => $"stow{i}"));
        foreach (string slot in layers)
        {
            var mat = new ShaderMaterial { Shader = _shader };
            mat.SetShaderParameter("pixel_size", Skeleton.PixelSize);
            mat.SetShaderParameter("depth_range", Skeleton.DepthRange);
            mat.SetShaderParameter("scale", _scale);
            ApplyPaint(mat);
            _materials[slot] = mat;
            var mesh = new MeshInstance3D
            {
                Name = slot,
                Mesh = new QuadMesh { Size = Vector2.One },
                MaterialOverride = mat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                // el shader agranda el quad hasta la celda: caja de culling a mano
                CustomAabb = new Aabb(new Vector3(-2 * span, -2 * span, -2 * span), new Vector3(4 * span, 4 * span, 4 * span)),
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
                _parts[slot] = MechPartData.Get(part, slot, Skeleton.RootPath);
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
            _parts[slot] = MechPartData.Get(partId, slot, Skeleton.RootPath);
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

            DrawLayer(slot, part, anim, dir, frame, _pos[slot]);
        }
        PlaceStowed();
    }

    /// <summary>Coloca las armas guardadas en sus montajes del torso (espalda) o de las piernas (muslos).</summary>
    private void PlaceStowed()
    {
        bool wreck = _lower.Anim == "wreck";
        for (int i = 0; i < MaxStowed; i++)
        {
            string key = $"stow{i}";
            bool on = !wreck && i < _stowed.Count;
            if (on)
            {
                var (part, mount) = _stowed[i];
                bool back = mount.StartsWith("stow_back");
                string parent = back ? "torso" : "legs", anim = back ? "stow_back" : "stow_leg";
                var (pa, pd, pf) = Pose(parent);
                Vector3 socket = default, anchor = default;
                on = Skeleton.Anims.ContainsKey(anim) && _parts[parent].TryPoint(mount, pa, pd, pf, out socket) &&
                     part.TryPoint("stow", anim, pd, 0, out anchor);
                if (on)
                    DrawLayer(key, part, anim, pd, 0, _pos[parent] + socket - anchor);
            }
            _meshes[key].Visible = on;
        }
    }

    private void DrawLayer(string key, MechPartData part, string anim, int dir, int frame, Vector3 p)
    {
        int cell = Skeleton.Cell;
        var mat = _materials[key];
        if (!_bound.TryGetValue(key, out var b) || b.Part != part || b.Anim != anim)
        {
            var (color, depth) = part.Sheet(anim);
            mat.SetShaderParameter("albedo_tex", color);
            mat.SetShaderParameter("depth_tex", depth);
            mat.SetShaderParameter("sheet_size", color.GetSize());
            _bound[key] = (part, anim);
        }
        // hoja recortada: la celda es el rectángulo crop; su centro se desplaza respecto al de la celda completa
        var c = part.Crop(anim, cell);
        mat.SetShaderParameter("region", new Vector4(frame * c.Size.X, dir * c.Size.Y, c.Size.X, c.Size.Y));
        var cropShift = new Vector2(c.Position.X + c.Size.X / 2 - cell / 2, -(c.Position.Y + c.Size.Y / 2 - cell / 2));
        Vector2 offset = Skeleton.BaseOffset + cropShift + new Vector2(Mathf.Round(p.X), -Mathf.Round(p.Y));
        mat.SetShaderParameter("offset_px", offset);
        _rects[key] = (offset, c.Size);
        mat.SetShaderParameter("layer_depth", p.Z);
        mat.SetShaderParameter("depth_bias", DepthBias);
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
