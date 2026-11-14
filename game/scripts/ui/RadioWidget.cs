using System;
using System.Linq;
using System.Text.RegularExpressions;
using AresX.Core;
using Godot;

namespace AresX.UI;

/// <summary>
/// Control de la radio de fondo (configuración y panel CONTROL): encender/apagar, lista de radios chilenas, anterior /
/// siguiente, volumen, y lo que suena (título ICY) o el estado de la conexión. Se sincroniza con RadioPlayer y con el
/// directorio (que se actualiza en segundo plano).
/// </summary>
public partial class RadioWidget : VBoxContainer
{
    private OptionButton _list = null!;
    private Button _power = null!, _refresh = null!;
    private Label _info = null!;
    private HSlider _vol = null!;
    private bool _suppress;
    private readonly bool _compact;

    public RadioWidget(bool compact) => _compact = compact;
    public RadioWidget() : this(false) { }

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 3);
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 3);
        AddChild(top);
        _power = UiTheme.Button("▶", () => RadioPlayer.I?.Toggle(), "Encender / apagar la radio");
        _power.CustomMinimumSize = new Vector2(30, 0);
        top.AddChild(_power);
        _list = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, FitToLongestItem = false, TooltipText = "Radios de Chile por internet (directorio abierto Radio Browser, solo MP3)" };
        _list.CustomMinimumSize = new Vector2(_compact ? 120 : 260, 0);
        _list.ItemSelected += i =>
        {
            if (_suppress) return;
            var list = RadioDirectory.Stations;
            if (i >= 0 && i < list.Count) RadioPlayer.I?.Play(list[(int)i]);
        };
        top.AddChild(_list);
        _refresh = UiTheme.Button("↻", () => { RadioDirectory.Refresh(); Sync(); }, "Actualizar la lista de radios");
        _refresh.CustomMinimumSize = new Vector2(26, 0);
        top.AddChild(_refresh);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 3);
        AddChild(row);
        row.AddChild(Small("◀", () => RadioPlayer.I?.Step(-1), "Radio anterior"));
        row.AddChild(Small("▶", () => RadioPlayer.I?.Step(1), "Radio siguiente"));
        row.AddChild(UiTheme.Label("vol", 10, UiTheme.Dim));
        _vol = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.02, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(60, 16), TooltipText = "Volumen de la radio" };
        _vol.ValueChanged += v => { if (!_suppress) RadioPlayer.I?.SetVolume((float)v); };
        row.AddChild(_vol);

        _info = UiTheme.Label("", 10, UiTheme.Dim);
        _info.ClipText = true;
        _info.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _info.CustomMinimumSize = new Vector2(_compact ? 120 : 260, 0);
        AddChild(_info);
        if (!_compact)
            AddChild(UiTheme.Label("Radios de Chile por internet · ~1 MB por minuto a 128 kbps · solo radios en MP3", 9, UiTheme.Dim));

        Fill();
        Sync();
    }

    private static Button Small(string t, Action a, string tip)
    {
        var b = UiTheme.Button(t, a, tip);
        b.CustomMinimumSize = new Vector2(26, 0);
        return b;
    }

    public override void _EnterTree()
    {
        RadioDirectory.Updated += OnListUpdated;
        if (RadioPlayer.I != null) RadioPlayer.I.Changed += Sync;
    }

    public override void _ExitTree()
    {
        RadioDirectory.Updated -= OnListUpdated;
        if (RadioPlayer.I != null) RadioPlayer.I.Changed -= Sync;
    }

    private void OnListUpdated()
    {
        Fill();
        Sync();
    }

    private static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private void Fill()
    {
        _suppress = true;
        _list.Clear();
        var list = RadioDirectory.Stations;
        foreach (var st in list) _list.AddItem(Clean(st.Name) + (st.Bitrate > 0 ? $"  · {st.Bitrate} k" : ""));
        if (list.Count == 0) _list.AddItem(RadioDirectory.Loading ? "cargando radios…" : "sin lista (sin conexión)");
        _suppress = false;
    }

    private void Sync()
    {
        if (!IsInsideTree()) return;
        var r = RadioPlayer.I;
        _suppress = true;
        if (r != null)
        {
            int i = r.Station == null ? -1 : RadioDirectory.Stations.FindIndex(x => x.Url == r.Station.Url);
            if (i >= 0) _list.Selected = i;
            _vol.Value = r.Volume;
            _power.Text = r.On ? "■" : "▶";
            _power.TooltipText = r.On ? "Apagar la radio" : "Encender la radio";
            bool live = r.On && r.Status == "en el aire";
            string name = r.Station != null ? Clean(r.Station.Name) : "";
            _info.Text = !r.On ? (RadioDirectory.Loading ? "actualizando lista…" : RadioDirectory.LastError != "" && RadioDirectory.Stations.Count == 0 ? RadioDirectory.LastError : "radio apagada")
                       : live ? (r.NowPlaying != "" ? "♪ " + r.NowPlaying : "● en el aire · " + name)
                       : r.Status + (name != "" ? " · " + name : "");
            _info.AddThemeColorOverride("font_color", live ? UiTheme.Ok : r.On ? UiTheme.Warn : UiTheme.Dim);
            _info.TooltipText = _info.Text;
        }
        _suppress = false;
    }
}
