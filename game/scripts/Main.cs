using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AresX.AI;
using AresX.Core;
using AresX.Sim;
using AresX.UI;
using AresX.View;
using Godot;

namespace AresX;

/// <summary>
/// Escena principal de Expedición Universo. Configuración → misión. Dinámica principal: el operador hace clic en el
/// terreno y el rover evalúa el destino y navega hasta él (cada paso: sensores → RNA → AG → difuso → supervisor).
/// También hay misión autónoma (plan del AG). Argumentos sin ventana: --selftest, --benchmark[=planeta], --diag=planeta.
/// Captura: --autostart --planet=marte [--dest=col,fila] [--mode=auto] --steps=N --screenshot=ruta.png [--lab=0..4].
/// </summary>
public partial class Main : Node3D, IGameHost
{
    private ConfigScreen _config = null!;
    private Hud _hud = null!;
    private Labs _labs = null!;
    private CameraRig _cam = null!;
    private EnvironmentFx _fx = null!;
    private Node3D _worldRoot = null!;
    private TerrainView? _terrain;
    private PropsView? _props;
    private OverlayView? _overlay;
    private RobotView? _robot;
    private Mission? _mission;
    private NeuralPerception? _rna;
    private readonly ConcurrentQueue<string> _trainMsgs = new();
    private volatile bool _trainDone;

    public bool Running { get; private set; }
    public float SimSpeed { get; private set; } = 1f;
    private float _idleTimer;
    private bool _showRisk = true, _showTruth, _showCand = true;
    private int _lastEventCount;

    // captura automática
    private string? _shotPath;
    private int _autoSteps, _shotWait = -1, _labTab = -1;
    private string[] _args = Array.Empty<string>();

    public override void _Ready()
    {
        _args = OS.GetCmdlineUserArgs();
        if (HeadlessRunner.TryRun(_args, out int code))
        {
            GetTree().Quit(code);
            return;
        }
        SetupInput();
        _fx = new EnvironmentFx { Name = "EnvironmentFx" };
        AddChild(_fx);
        _worldRoot = new Node3D { Name = "World" };
        AddChild(_worldRoot);
        _cam = new CameraRig { Name = "CameraRig" };
        AddChild(_cam);
        _hud = new Hud { Host = this };
        AddChild(_hud);
        _hud.ShowUi(false);
        _labs = new Labs();
        AddChild(_labs);
        _config = new ConfigScreen();
        AddChild(_config);
        _config.Start = StartMission;

        // fondo de la pantalla de configuración: un Marte de muestra
        PreviewWorld();

        // RNA: el modelo pre-entrenado viene dentro del juego (mismos pesos que produce la semilla 2026); si falta, se entrena
        string? modelJson = Godot.FileAccess.FileExists(Platform.ModelPath) ? Godot.FileAccess.GetFileAsString(Platform.ModelPath) : null;
        Task.Run(() =>
        {
            NeuralPerception rna;
            if (modelJson != null)
            {
                _trainMsgs.Enqueue("Cargando el modelo RNA pre-entrenado incluido en el juego (semilla 2026, 11→32→16→3)…");
                rna = NeuralPerception.FromJson(modelJson);
                NeuralPerception.SetShared(rna);
            }
            else rna = NeuralPerception.TrainShared(s => _trainMsgs.Enqueue(s));
            _rna = rna;
            _trainDone = true;
        });
        _labs.OpenedOnMobile = () => _hud.CloseDrawersForLab();
        _labs.Closed = () => _hud.SetMobileLabOpen(false);
        _hud.LabWindowsClosing += () => _labs.Close();
        if (Platform.IsMobile) DisplayServer.ScreenSetKeepOn(true);

        foreach (var a in _args)
        {
            if (a.StartsWith("--screenshot=")) _shotPath = a["--screenshot=".Length..];
            if (a.StartsWith("--steps=")) _autoSteps = a[8..].ToInt();
            if (a.StartsWith("--lab=")) _labTab = a[6..].ToInt();
        }
    }

    private static void SetupInput()
    {
        void Act(string name, params Key[] keys)
        {
            if (!InputMap.HasAction(name)) InputMap.AddAction(name);
            foreach (var k in keys) InputMap.ActionAddEvent(name, new InputEventKey { PhysicalKeycode = k });
        }
        Act("cam_left", Key.Q);
        Act("cam_right", Key.E);
    }

    private void PreviewWorld()
    {
        var map = new WorldMap(PlanetProfile.Get("marte"), false, 42, 1);
        Grid.Map = map;
        _terrain = new TerrainView { Name = "Terrain" };
        _worldRoot.AddChild(_terrain);
        _props = new PropsView { Name = "Props" };
        _worldRoot.AddChild(_props);
        var dummy = new Node3D { Position = Grid.CellCenter(map.Start) };
        _worldRoot.AddChild(dummy);
        _cam.Target = dummy;
        if (_cam.Camera != null) _cam.ConfigureForLarge(false);
        _cam.Zoom = 4f;
        var env = new SimEnvironment(map.Planet, map, new Rng(1), 11);
        _fx.Setup(env, map.Planet);
        _cam.CallDeferred(CameraRig.MethodName.Snap);
    }

    // ------------------------------------------------------------------ misión
    private void StartMission(MissionConfig cfg)
    {
        if (_rna == null) return;
        if (cfg.Scenario is { IsScenario: false } meta) cfg.Scenario = Scenarios.Load(meta.Id);   // grillas a escala real
        foreach (var c in _worldRoot.GetChildren()) c.QueueFree();
        _mission = new Mission(cfg, _rna);
        Grid.Map = _mission.Map;
        _terrain = new TerrainView { Name = "Terrain" };
        _props = new PropsView { Name = "Props" };
        _overlay = new OverlayView { Name = "Overlay", Mission = _mission, ShowRisk = _showRisk, ShowTruth = _showTruth, ShowCandidates = _showCand };
        _robot = new RobotView { Name = "Robot", Probe = cfg.Probe, Heli = cfg.Heli };
        _worldRoot.AddChild(_terrain);
        _worldRoot.AddChild(_props);
        _worldRoot.AddChild(_overlay);
        _worldRoot.AddChild(_robot);
        _worldRoot.AddChild(new DroneView { Name = "Drone", Mission = _mission, Rover = _robot });
        _overlay.Reset();
        _robot.Place(_mission.Rover.Pos);
        _cam.Target = _robot;
        _cam.ConfigureForLarge(Grid.Large);
        _cam.Zoom = Grid.Large ? 3f : 1.5f;
        _cam.Snap();
        _fx.Setup(_mission.Env, _mission.Planet);
        _fx.Follow = _robot;
        _hud.Bind(_mission);
        _hud.CallDeferred(Hud.MethodName.InitLayout);
        _hud.ShowUi(true);
        _hud.UpdateToggles(_showRisk, _showTruth);
        _labs.Mission = _mission;
        _labs.Rna = _rna;
        _config.ShowUi(false);
        Platform.SetUiScale(GetTree(), true);
        Running = false;
        _lastEventCount = 0;
        _hud.Flash(cfg.ClickMode
            ? "Haga CLIC en el terreno para asignar un destino: el rover evaluará terreno, energía, clima y riesgo y navegará hasta allí. Clic derecho = cancelar."
            : "Misión AUTÓNOMA: pulse Espacio (o ▶ Ejecutar) para iniciar, N para un solo paso.", UiTheme.Accent, 8);
        Running = cfg.ClickMode;
        // primer ciclo de observación para llenar el HUD
        DoStep();
    }

    private void DoStep()
    {
        if (_mission == null || _robot == null || _mission.Finished) return;
        var before = _mission.Rover.Pos;
        int logBefore = _mission.Log.Count;
        _mission.Step();
        var d = _mission.LastDecision;
        if (_mission.Rover.Pos != before)
        {
            float speed = d?.Action == RoverAction.Avanzar ? MathF.Max(0.1f, d.Speed) : 0.5f;
            float secs = (0.28f + 0.55f * (1f - speed)) / SimSpeed;
            if (_mission.Trail.Count >= 2 && _mission.Trail[^2] != before) secs *= 1.4f;   // deriva: dos tramos
            _robot.MoveTo(_mission.Rover.Pos, secs);
        }
        else if (_mission.FrontCell is Cell f)
        {
            _robot.Face(f - _mission.Rover.Pos);
        }
        if (d != null && (d.Veto || _mission.LastCollision)) _robot.Hit();
        if (_mission.LastCollision) _cam.Shake(0.3f, 0.3f);
        if (_mission.State == MissionState.Fracaso) _robot.Wreck();
        _robot.SetNight(!_mission.Env.IsDay || _mission.Env.DustOpacity > 0.5f);
        // efectos de eventos nuevos
        foreach (var l in _mission.Log.Skip(logBefore))
        {
            if (l.Text.StartsWith("IMPACTO") && _mission.Env.Get(EventKind.Meteorito)?.Where is Cell w) _fx.Meteor(Grid.CellCenter(w));
            if (l.Text.Contains("SISMO") || l.Text.StartsWith("Sismo espontáneo")) _cam.Shake(0.45f, 1.2f);
        }
        _hud.OnStep();
        _labs.NotifyStep();
        if (_mission.Finished) Running = false;
    }

    public override void _Process(double delta)
    {
        while (_trainMsgs.TryDequeue(out var msg)) _config.TrainProgress(msg);
        if (_trainDone && !_config.RnaReady && _rna != null)
        {
            _config.TrainDone(_rna);
            if (_args.Contains("--autostart")) AutoStart();
            else if (_shotPath != null) _shotWait = 30;
            foreach (var a in _args)
                if (a.StartsWith("--config-scenario=")) _config.SelectScenario(a[18..]);
        }
        if (_mission == null || _robot == null) { HandleShot(); return; }
        if (_overlay != null) _overlay.RobotPos = _robot.GlobalPosition;
        _robot.HeliAglM = _mission.HeliAglM;
        if (_config.IsShown) { HandleShot(); return; }

        if (!OS.HasFeature("mobile")) UpdateHover();
        if (Platform.IsMobile) UpdateTouch();
        if (!_robot.Busy && !_mission.Finished)
        {
            if (Running)
            {
                _idleTimer += (float)delta;
                bool waiting = _mission.Config.ClickMode && !_mission.HasDestination && !_mission.PendingSample && _mission.PendingDestination == null;
                float interval = (waiting ? 1.0f : 0.12f) / SimSpeed;
                if (_mission.LastDecision?.Action is not RoverAction.Avanzar) interval = MathF.Max(interval, 0.35f / SimSpeed);
                if (_idleTimer >= interval)
                {
                    _idleTimer = 0;
                    DoStep();
                }
            }
        }
        if (_autoSteps > 0 && !_robot.Busy && !_mission.Finished)
        {
            _autoSteps--;
            DoStep();
            if (_autoSteps == 0) _shotWait = 30;
        }
        HandleShot();
    }

    /// <summary>Celda del terreno bajo un punto de la pantalla: rayo de la cámara marchando sobre el relieve fino.</summary>
    private Cell? PickCell(Vector2 screen)
    {
        if (_mission == null) return null;
        var o = _cam.Camera.ProjectRayOrigin(screen);
        var d = _cam.Camera.ProjectRayNormal(screen);
        Vector3 prev = o;
        for (float t = 0; t < 400f; t += 0.4f)
        {
            var p = o + d * t;
            if (p.Y <= Grid.HeightAt(p.X, p.Z))
            {
                var a = prev;
                var b = p;
                for (int k = 0; k < 12; k++)
                {
                    var m = (a + b) / 2;
                    if (m.Y <= Grid.HeightAt(m.X, m.Z)) b = m; else a = m;
                }
                var c = Grid.WorldToCell(b);
                return _mission.Map.InBounds(c) ? c : null;
            }
            prev = p;
        }
        return null;
    }

    private Cell? _hoverCell;

    private void UpdateHover()
    {
        if (_mission == null || _overlay == null) return;
        var mouse = GetViewport().GetMousePosition();
        bool overUi = GetViewport().GuiGetHoveredControl() != null;
        Cell? c = overUi ? null : PickCell(mouse);
        _overlay.HoverCell = c;
        if (c != _hoverCell)
        {
            _hoverCell = c;
            _hud.SetHover(c is Cell cc ? HoverText(cc) : null);
        }
        _hud.MoveHover(mouse);
    }

    private string HoverText(Cell c)
    {
        var m = _mission!;
        var b = m.Belief;
        int i = b.Idx(c);
        string known = b.ConfirmedCliff[i] ? "BARRANCO CONFIRMADO" : b.ConfirmedObstacle[i] ? "OBSTÁCULO CONFIRMADO" : b.Vetoed[i] ? "VETADA" : b.Observed[i] ? $"riesgo RNA {b.Risk(c):0.00}" : "no observada (estimación orbital)";
        var site = m.Map.Sites.FirstOrDefault(s => s.Cell == c);
        string extra = site != null ? $"\n★ {site.Name} · {site.Kind} · valor {site.Value:0.00}{(site.Visited ? " · ✓" : "")}" : m.Map.Refuges.Contains(c) ? "\n⌂ refugio (recarga)" : "";
        return $"({c.Y},{c.X}) · {c.Dist(m.Rover.Pos) * m.Map.CellM:0} m en línea recta\n" +
               $"elev. est. {m.Map.BaseElevation + b.HeightEst[i]:0} m · pend. est. {b.SlopeEst[i]:0}° · {known}\n" +
               $"T {m.Env.TempAt(c):0} °C · {(b.Observed[i] ? WorldMap.SoilName(m.Map.SoilAt(c)) : "suelo desconocido")}{extra}\n" +
               (OS.HasFeature("mobile") ? $"[toque] {ToolName} · mantener = ver celda" : "[clic] ir aquí · [clic der.] cancelar · Mayús/Ctrl/Alt+clic: barranco/roca/despejar");
    }

    // ------------------------------------------------------------------ IGameHost
    public void TogglePlay()
    {
        if (_mission == null) return;
        Running = !Running;
        _idleTimer = 999;
    }

    public void StepOnce()
    {
        if (_mission == null || _robot == null || _robot.Busy) return;
        Running = false;
        DoStep();
    }

    public void Restart()
    {
        Running = false;
        _labs.Close();
        _hud.ShowUi(false);
        _config.ShowUi(true);
        Platform.SetUiScale(GetTree(), false);
    }

    public void Export()
    {
        if (_mission == null) return;
        string dir = HeadlessRunner.ExportDir();
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string baseName = $"mision_{_mission.Planet.Id}_s{_mission.Config.Seed}_{stamp}";
        File.WriteAllText(Path.Combine(dir, baseName + "_traza.csv"), _mission.TraceCsv());
        File.WriteAllText(Path.Combine(dir, baseName + "_resultado.json"), _mission.ResultJson());
        _hud.Flash($"Exportado: {Path.Combine(dir, baseName)}_traza.csv / _resultado.json", UiTheme.Ok, 6);
        GD.Print($"[Expedición] Exportado en {dir}/{baseName}_*");
    }

    public void ToggleMode()
    {
        if (_mission == null) return;
        _mission.Config.Mode = _mission.Config.ClickMode ? MissionMode.Autonomo : MissionMode.Destino;
        if (_mission.Config.ClickMode) _mission.CancelDestination();
        else
        {
            _mission.HasDestination = false;
            _mission.RequestMissionPlan();
        }
        _mission.AddLog("info", _mission.Config.ClickMode ? "Modo DESTINO POR CLIC: el operador elige el destino; el rover evalúa y navega" : "Modo AUTÓNOMO: el AG planifica la misión científica y el rover la ejecuta");
        _hud.Flash(_mission.Config.ClickMode ? "DESTINO POR CLIC · haga clic en el terreno" : "AUTÓNOMO · Espacio para ejecutar", UiTheme.Accent);
    }

    public void SetSpeed(float s) => SimSpeed = s;

    public void Inject(EventKind k)
    {
        if (_mission == null || _mission.Finished) return;
        _mission.Inject(k);
        if (k == EventKind.Meteorito && _mission.Env.Get(EventKind.Meteorito)?.Where is Cell w) _fx.Meteor(Grid.CellCenter(w));
        if (k == EventKind.Sismo) _cam.Shake(0.5f, 1.4f);
        _robot?.SetNight(!_mission.Env.IsDay);
    }

    public void RepairSensors() => _mission?.RepairSensors();

    public void Replan() => _mission?.RequestReplan("solicitud del operador");

    public void ToggleRisk()
    {
        _showRisk = !_showRisk;
        if (_overlay != null) _overlay.ShowRisk = _showRisk;
        _hud.UpdateToggles(_showRisk, _showTruth);
    }

    public void ToggleTruth()
    {
        _showTruth = !_showTruth;
        if (_overlay != null) _overlay.ShowTruth = _showTruth;
        _hud.Map.Truth = _showTruth;
        _hud.UpdateToggles(_showRisk, _showTruth);
    }

    public void OpenLab(int tab)
    {
        if (_mission == null) return;
        _labs.Open(tab);   // ventana flotante en vivo: la simulación sigue corriendo
    }

    public void MapEdit(Cell c, string what)
    {
        if (_mission == null) return;
        if (what == "dest") { AssignDestination(c); return; }
        _mission.EditMap(c, what);
    }

    private void AssignDestination(Cell c)
    {
        if (_mission == null || _mission.Finished) return;
        if (!_mission.Config.ClickMode)
        {
            _mission.Config.Mode = MissionMode.Destino;
            _mission.AddLog("info", "Clic en el terreno: se pasa a modo DESTINO POR CLIC");
        }
        _mission.SetDestination(c);
        Running = true;
        _idleTimer = 999;
        if (_mission.DestReport is { } r && r.Cell == _mission.Target)
            _hud.Flash($"DESTINO {r.Cell}: {r.VerdictText} · {r.PathM:0} m · {r.EnergyWh:0} Wh (+{(float.IsFinite(r.ReturnWh) ? r.ReturnWh.ToString("0") : "∞")} regreso) · ETA {r.EtaS / 60:0.0} min",
                r.Verdict == DestVerdict.Viable ? UiTheme.Ok : r.Verdict is DestVerdict.Arriesgado or DestVerdict.SoloIda ? UiTheme.Warn : UiTheme.Crit, 6);
    }

    // ------------------------------------------------------------------ entrada
    // ------------------------------------------------------------------ táctil (Android)
    private enum Tool { Destino, Barranco, Roca, Despejar, Dron }
    private Tool _tool = Tool.Destino;
    private string ToolName => _tool switch { Tool.Barranco => "barranco", Tool.Roca => "roca", Tool.Despejar => "despejar", Tool.Dron => "dron", _ => "destino" };
    private readonly System.Collections.Generic.Dictionary<int, Vector2> _touches = new();
    private Vector2 _tapPos;
    private double _tapTime, _hoverHideAt;
    private bool _tapMoved, _longShown;
    private float _pinchDist;

    public string CycleTool()
    {
        _tool = (Tool)(((int)_tool + 1) % 5);
        return ToolName;
    }

    public void CancelDestination() => _mission?.CancelDestination();

    /// <summary>Muestra/oculta una ventana (HUD o laboratorio). En móvil las del HUD son cajones.</summary>
    public void ToggleWindow(string id)
    {
        if (id.StartsWith("lab_")) _labs.Toggle(id);
        else if (Platform.IsMobile) _hud.ToggleDrawerFor(id);
        else WindowManager.Toggle(id);
    }
    /// <summary>D / botón: despliega el dron (a la celda bajo el cursor o a la zona más incierta) o, en vuelo, lo hace volver.</summary>
    public void DroneAction()
    {
        if (_mission == null) return;
        if (_mission.Drone.Flying) { _mission.RecallDrone(); return; }
        DeployDroneTo(_hoverCell);
    }

    private void DeployDroneTo(Cell? c)
    {
        if (_mission == null) return;
        if (_mission.Drone.Flying) { _hud.Flash("El dron ya está en vuelo: " + DroneState.ModeName(_mission.Drone.Mode), UiTheme.Warn, 3); return; }
        if (!_mission.DeployDrone(c, "operador")) _hud.Flash(_mission.Drone.LastDecision, UiTheme.Warn, 4);
    }

    public void ToggleDroneAuto()
    {
        if (_mission == null) return;
        _mission.Drone.Auto = !_mission.Drone.Auto;
        _hud.Flash(_mission.Drone.Auto ? "Dron: despliegue automático por lógica difusa ACTIVADO" : "Dron: despliegue automático desactivado (manual con D)", UiTheme.Accent, 3);
    }

    public void RotateCam(int dir) => _cam.Rotate45(dir);
    public void ZoomCam(int step) => _cam.Wheel(step);

    private static double Now => Time.GetTicksMsec() / 1000.0;

    private void ApplyTool(Cell c)
    {
        if (_mission == null) return;
        switch (_tool)
        {
            case Tool.Barranco: _mission.EditMap(c, "cliff"); break;
            case Tool.Roca: _mission.EditMap(c, "rock"); break;
            case Tool.Despejar: _mission.EditMap(c, "clear"); break;
            case Tool.Dron: DeployDroneTo(c); break;
            default: AssignDestination(c); break;
        }
    }

    private void HandleTouch(InputEventScreenTouch t)
    {
        if (t.Pressed)
        {
            if (_touches.Count == 0 && _hud.IsOverUi(t.Position)) return;
            _touches[t.Index] = t.Position;
            if (_touches.Count == 1)
            {
                _tapPos = t.Position;
                _tapTime = Now;
                _tapMoved = _longShown = false;
            }
            else if (_touches.Count == 2)
            {
                _tapMoved = true;
                var p = _touches.Values.ToArray();
                _pinchDist = p[0].DistanceTo(p[1]);
            }
            return;
        }
        if (!_touches.Remove(t.Index)) return;
        if (_touches.Count == 0 && !_tapMoved && !_longShown && Now - _tapTime < 0.5 && PickCell(t.Position) is Cell c) ApplyTool(c);
        if (_longShown) _hoverHideAt = Now + 3;
    }

    private void HandleDrag(InputEventScreenDrag d)
    {
        if (!_touches.ContainsKey(d.Index)) return;
        _touches[d.Index] = d.Position;
        if (_touches.Count >= 2)
        {
            var p = _touches.Values.ToArray();
            float dist = p[0].DistanceTo(p[1]);
            if (_pinchDist > 1 && dist > 1) _cam.ZoomBy(_pinchDist / dist);
            _pinchDist = dist;
        }
        else if (d.Position.DistanceTo(_tapPos) > 22) _tapMoved = true;
    }

    /// <summary>Mantener pulsado = previsualizar la celda (el equivalente táctil de pasar el mouse).</summary>
    private void UpdateTouch()
    {
        if (_touches.Count == 1 && !_tapMoved && !_longShown && Now - _tapTime > 0.45 && PickCell(_tapPos) is Cell c)
        {
            _longShown = true;
            if (_overlay != null) _overlay.HoverCell = c;
            _hud.SetHover(HoverText(c));
            _hud.MoveHover(_tapPos);
        }
        if (_hoverHideAt > 0 && Now > _hoverHideAt)
        {
            _hoverHideAt = 0;
            _hud.SetHover(null);
            if (_overlay != null) _overlay.HoverCell = null;
        }
    }

    /// <summary>Botón Atrás de Android: cierra labs o cajones; desde la misión vuelve a la configuración; desde ahí sale.</summary>
    public override void _Notification(int what)
    {
        if (what != NotificationWMGoBackRequest) return;
        if (_labs.IsOpen) _labs.Close();
        else if (_hud.CloseDrawer()) { }
        else if (_config.IsShown) GetTree().Quit();
        else Restart();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (OS.HasFeature("mobile") && _mission != null && !_config.IsShown)
        {
            if (e is InputEventScreenTouch st) { HandleTouch(st); return; }
            if (e is InputEventScreenDrag sd) { HandleDrag(sd); return; }
            if (e is InputEventMouseButton) return;   // ratón emulado desde el toque: ya lo manejamos arriba
        }
        if (e is InputEventMouseButton { Pressed: true } mb && _mission != null && !_config.IsShown)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp) _cam.Wheel(-1);
            if (mb.ButtonIndex == MouseButton.WheelDown) _cam.Wheel(1);
            if (mb.ButtonIndex == MouseButton.Right) { _mission.CancelDestination(); GetViewport().SetInputAsHandled(); }
            if (mb.ButtonIndex == MouseButton.Left && PickCell(mb.Position) is Cell c)
            {
                if (mb.ShiftPressed) _mission.EditMap(c, "cliff");
                else if (mb.CtrlPressed) _mission.EditMap(c, "rock");
                else if (mb.AltPressed) _mission.EditMap(c, "clear");
                else AssignDestination(c);
                GetViewport().SetInputAsHandled();
            }
        }
        if (e is not InputEventKey { Pressed: true, Echo: false } k || _mission == null || _config.IsShown) return;
        bool handled = true;
        if (k.PhysicalKeycode == Key.Enter && k.AltPressed)
        {
            bool full = DisplayServer.WindowGetMode() is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen;
            DisplayServer.WindowSetMode(full ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);
            GetViewport().SetInputAsHandled();
            return;
        }
        switch (k.PhysicalKeycode)
        {
            case Key.Space: TogglePlay(); break;
            case Key.N: StepOnce(); break;
            case Key.D: DroneAction(); break;
            case Key.R: Restart(); break;
            case Key.M: ToggleMode(); break;
            case Key.P: Replan(); break;
            case Key.V: ToggleRisk(); break;
            case Key.T: ToggleTruth(); break;
            case Key.C:
                _showCand = !_showCand;
                if (_overlay != null) _overlay.ShowCandidates = _showCand;
                break;
            case Key.Tab: ToggleWindow("lab_rna"); break;
            case Key.H: OpenLab(4); break;
            case Key.Key1: SetSpeed(1); break;
            case Key.Key2: SetSpeed(2); break;
            case Key.Key3: SetSpeed(4); break;
            case Key.Key4: SetSpeed(8); break;
            case Key.F1: Inject(EventKind.Sismo); break;
            case Key.F2: Inject(EventKind.Tormenta); break;
            case Key.F3: Inject(EventKind.Meteorito); break;
            case Key.F4:
                if (k.ShiftPressed) RepairSensors(); else Inject(EventKind.FalloSensor);
                break;
            case Key.F5: Inject(EventKind.Noche); break;
            case Key.F6: Inject(EventKind.BajaConectividad); break;
            case Key.F7: Inject(EventKind.Rafaga); break;
            case Key.F8: Inject(EventKind.Biofirma); break;
            case Key.F9: Inject(EventKind.Alien); break;
            case Key.F10: Inject(EventKind.BateriaBaja); break;
            case Key.F11: Inject(EventKind.Remocion); break;
            case Key.F12: Export(); break;
            default: handled = false; break;
        }
        if (handled) GetViewport().SetInputAsHandled();
    }

    // ------------------------------------------------------------------ captura automática (verificación visual)
    private void AutoStart()
    {
        var cfg = new MissionConfig();
        foreach (var a in _args)
        {
            if (a.StartsWith("--planet=")) cfg.PlanetId = a[9..];
            if (a.StartsWith("--seed=")) cfg.Seed = a[7..].ToInt();
            if (a == "--mode=auto") cfg.Mode = MissionMode.Autonomo;
            if (a == "--heli") cfg.Heli = true;
            if (a.StartsWith("--battery=")) cfg.BatteryPct = a[10..].ToFloat();
            if (a.StartsWith("--scenario=")) { cfg.PlanetId = "tierra"; cfg.Scenario = Scenarios.Load(a[11..]); }
        }
        cfg.Probe = !PlanetProfile.Get(cfg.PlanetId).SolidSurface && !cfg.Heli;
        StartMission(cfg);
        foreach (var a in _args)
        {
            if (a.StartsWith("--event=") && Enum.TryParse<EventKind>(a[8..], out var ev)) Inject(ev);
            if (a.StartsWith("--dest=")) { var xy = a[7..].Split(','); AssignDestination(new Cell(xy[0].ToInt(), xy[1].ToInt())); }
            if (a.StartsWith("--drone=")) { var xy = a[8..].Split(','); _mission?.DeployDrone(xy.Length == 2 ? new Cell(xy[0].ToInt(), xy[1].ToInt()) : null, "operador"); }
        }
        if (_autoSteps > 0) Running = false;   // captura: exactamente N pasos
        foreach (var a in _args)
            if (a.StartsWith("--drawer=")) _hud.OpenDrawer(a[9..].ToInt());
        foreach (var a in _args)
            if (a.StartsWith("--preset="))
            {
                string preset = a[9..];
                Callable.From(() => { _hud.InitLayout(); WindowManager.ApplyPreset(preset, _hud.UiSize); _hud.RefreshWindowButtons(); }).CallDeferred();
            }
        foreach (var a in _args)
            if (a.StartsWith("--zoom=")) { _cam.Zoom = a[7..].ToFloat(); _cam.Snap(); }
        SimSpeed = 8;
        if (_autoSteps == 0) _shotWait = 40;
    }

    private void HandleShot()
    {
        if (_shotPath == null || _shotWait < 0) return;
        if (_shotWait == 20 && _labTab >= 0) _labs.Open(_labTab);
        if (--_shotWait > 0) return;
        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(_shotPath);
        GD.Print($"[Expedición] Captura guardada en {_shotPath}");
        _shotPath = null;
        GetTree().Quit();
    }
}
