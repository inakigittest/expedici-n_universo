using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using NLayer;
using HttpClient = System.Net.Http.HttpClient;

namespace AresX.Core;

public sealed record RadioStation(string Name, string Url, int Bitrate, string Tags);

/// <summary>
/// Directorio de radios chilenas por internet: lo consulta en Radio Browser (directorio abierto y comunitario,
/// radio-browser.info; solo se piden radios de Chile en MP3, sin enviar datos del usuario) y lo guarda en caché en
/// user://radios_cl.json para arrancar sin conexión. Solo MP3: el decodificador (NLayer) es C# puro y funciona igual en
/// Windows y Android; las radios que transmiten en AAC no se listan.
/// </summary>
public static class RadioDirectory
{
    private const string Api = "https://all.api.radio-browser.info/json/stations/search?countrycode=CL&codec=MP3&hidebroken=true&order=clickcount&reverse=true&limit=120";
    public const string UserAgent = "ExpedicionUniverso/1.1";
    private static readonly string CachePath = ProjectSettings.GlobalizePath("user://radios_cl.json");
    public static List<RadioStation> Stations { get; private set; } = LoadCache();
    public static bool Loading { get; private set; }
    public static string LastError = "";
    private static volatile bool _updated;
    /// <summary>La lista cambió (se dispara en el hilo principal desde RadioPlayer).</summary>
    public static event Action? Updated;

    private static List<RadioStation> LoadCache()
    {
        try { return File.Exists(CachePath) ? Parse(File.ReadAllText(CachePath)) : new(); }
        catch { return new(); }
    }

    private static List<RadioStation> Parse(string json)
    {
        var list = new List<RadioStation>();
        var seen = new HashSet<string>();
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string name = (e.TryGetProperty("name", out var n) ? n.GetString() : "")?.Trim() ?? "";
            string url = (e.TryGetProperty("url_resolved", out var u) ? u.GetString() : "")?.Trim() ?? "";
            if (url == "" && e.TryGetProperty("url", out var u2)) url = u2.GetString()?.Trim() ?? "";
            if (name.Length < 2 || !url.StartsWith("http") || url.Contains(".m3u8") || !seen.Add(name.ToLowerInvariant())) continue;
            int br = e.TryGetProperty("bitrate", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 0;
            string tags = e.TryGetProperty("tags", out var t) ? t.GetString() ?? "" : "";
            list.Add(new RadioStation(name, url, br, tags));
        }
        return list;
    }

    /// <summary>Actualiza la lista en segundo plano (no bloquea el juego).</summary>
    public static void Refresh()
    {
        if (Loading) return;
        Loading = true;
        Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                string json = await http.GetStringAsync(Api);
                var list = Parse(json);
                if (list.Count > 0)
                {
                    Stations = list;
                    try { File.WriteAllText(CachePath, json); } catch { }
                }
                LastError = list.Count > 0 ? "" : "el directorio no devolvió radios";
            }
            catch (Exception ex) { LastError = "sin conexión al directorio (" + ex.GetType().Name + ")"; }
            finally { Loading = false; _updated = true; }
        });
    }

    internal static void Pump()
    {
        if (!_updated) return;
        _updated = false;
        Updated?.Invoke();
    }
}

/// <summary>
/// Reproductor de radio por streaming. Un hilo lee el stream HTTP (pidiendo los metadatos ICY para mostrar la canción),
/// separa los cuadros MP3 (Mp3FrameReader: un stream de radio no admite posicionarse), los decodifica con el
/// decodificador por cuadros de NLayer y deja muestras estéreo en un búfer circular; el hilo principal las entrega a un
/// AudioStreamGenerator de Godot. Antes de sonar acumula ~1 s (sin cortes por la red). Si se corta, reintenta. La
/// elección (radio, volumen, encendida) se guarda en user://ajustes.cfg. En Android se detiene al pasar a segundo plano.
/// </summary>
public partial class RadioPlayer : Node
{
    public static RadioPlayer? I { get; private set; }
    private const string SettingsPath = "user://ajustes.cfg";

    public RadioStation? Station { get; private set; }
    public string NowPlaying { get; private set; } = "";
    public string Status { get; private set; } = "apagada";
    public bool On { get; private set; }
    public float Volume { get; private set; } = 0.5f;
    public event Action? Changed;

    private AudioStreamPlayer _player = null!;
    private AudioStreamGeneratorPlayback? _pb;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();
    private float[] _ring = new float[48000 * 2 * 8];   // 8 s estéreo
    private int _head, _count;                           // en muestras (no cuadros)
    private volatile int _rate, _activeRate;
    private volatile string _status = "apagada", _title = "";
    private bool _primed, _pausedByOs;
    private int _gen;   // generación del stream (descarta datos de una radio anterior)

    public override void _Ready()
    {
        I = this;
        ProcessMode = ProcessModeEnum.Always;
        _player = new AudioStreamPlayer { Name = "Radio", Bus = "Master" };
        AddChild(_player);
        var cfg = new ConfigFile();
        string? test = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--radio-play="))?[13..].ToLowerInvariant();
        if (test != null)
        {
            // prueba: sintoniza la primera radio que coincida, sin guardar nada, y registra el búfer cada 2 s
            Persist = false;
            _logT = 0;
            void TryPlay()
            {
                var st = RadioDirectory.Stations.FirstOrDefault(x => x.Name.ToLowerInvariant().Contains(test));
                if (st != null && Station == null) Play(st);
            }
            RadioDirectory.Updated += TryPlay;
            TryPlay();
        }
        else if (cfg.Load(SettingsPath) == Error.Ok)
        {
            Volume = (float)cfg.GetValue("radio", "volumen", 0.5f);
            string url = (string)cfg.GetValue("radio", "url", ""), name = (string)cfg.GetValue("radio", "nombre", "");
            if (url != "") Station = new RadioStation(name, url, 0, "");
            if ((bool)cfg.GetValue("radio", "encendida", false) && Station != null && DisplayServer.GetName() != "headless") Play(Station);
        }
        ApplyVolume();
        if (DisplayServer.GetName() != "headless") RadioDirectory.Refresh();
    }

    /// <summary>false en pruebas: no toca user://ajustes.cfg.</summary>
    public bool Persist = true;
    private long _pushed, _underruns;
    private double _logT = -1;

    private void Save()
    {
        if (!Persist) return;
        var cfg = new ConfigFile();
        cfg.Load(SettingsPath);
        cfg.SetValue("radio", "url", Station?.Url ?? "");
        cfg.SetValue("radio", "nombre", Station?.Name ?? "");
        cfg.SetValue("radio", "volumen", Volume);
        cfg.SetValue("radio", "encendida", On);
        cfg.Save(SettingsPath);
    }

    // ------------------------------------------------------------------ control
    public void Play(RadioStation s)
    {
        StopStream();
        Station = s;
        On = true;
        NowPlaying = "";
        _title = "";
        _status = "conectando…";
        int gen = ++_gen;
        var cts = _cts = new CancellationTokenSource();
        Task.Run(() => StreamLoop(s.Url, gen, cts.Token));
        Save();
        Changed?.Invoke();
    }

    public void Stop()
    {
        StopStream();
        On = false;
        _status = "apagada";
        Save();
        Changed?.Invoke();
    }

    public void Toggle()
    {
        if (On) Stop();
        else if (Station != null) Play(Station);
        else if (RadioDirectory.Stations.Count > 0) Play(RadioDirectory.Stations[0]);
    }

    /// <summary>Radio anterior / siguiente de la lista.</summary>
    public void Step(int dir)
    {
        var list = RadioDirectory.Stations;
        if (list.Count == 0) return;
        int i = Station == null ? -1 : list.FindIndex(x => x.Url == Station.Url);
        Play(list[((i < 0 ? (dir > 0 ? -1 : 0) : i) + dir + list.Count) % list.Count]);
    }

    public void SetVolume(float v)
    {
        Volume = Mathf.Clamp(v, 0, 1);
        ApplyVolume();
        Save();
    }

    private void ApplyVolume() => _player.VolumeDb = Volume <= 0.001f ? -80f : Mathf.LinearToDb(Volume * Volume);

    private void StopStream()
    {
        _gen++;
        _cts?.Cancel();
        _cts = null;
        _player.Stop();
        _pb = null;
        _primed = false;
        _activeRate = 0;
        lock (_lock) { _head = 0; _count = 0; }
    }

    public override void _Notification(int what)
    {
        // Android: no gastar datos en segundo plano
        if (what == NotificationApplicationPaused && On) { _pausedByOs = true; StopStream(); _status = "en pausa"; }
        if (what == NotificationApplicationResumed && _pausedByOs && Station != null) { _pausedByOs = false; Play(Station); }
    }

    // ------------------------------------------------------------------ hilo de red y decodificación
    private async Task StreamLoop(string url, int gen, CancellationToken ct)
    {
        int attempts = 0;
        while (!ct.IsCancellationRequested && attempts < 4)
        {
            attempts++;
            try
            {
                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(RadioDirectory.UserAgent);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Icy-MetaData", "1");
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connect.CancelAfter(TimeSpan.FromSeconds(12));
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, connect.Token);
                resp.EnsureSuccessStatusCode();
                int metaInt = resp.Headers.TryGetValues("icy-metaint", out var mv) && int.TryParse(mv.FirstOrDefault(), out var mi) ? mi : 0;
                var raw = await resp.Content.ReadAsStreamAsync(ct);
                using var reg = ct.Register(() => { try { raw.Dispose(); } catch { } });
                Stream src = metaInt > 0 ? new IcyStream(raw, metaInt, t => _title = t) : raw;
                var reader = new Mp3FrameReader(src);
                var decoder = new MpegFrameDecoder();
                var buf = new float[1152 * 2];
                bool first = true;
                while (!ct.IsCancellationRequested)
                {
                    var frame = reader.Next();
                    if (frame == null) break;
                    int n;
                    try { n = decoder.DecodeFrame(frame, buf, 0); }
                    catch { decoder.Reset(); continue; }   // cuadro dañado: se salta
                    if (first)
                    {
                        _rate = frame.SampleRate;
                        _status = "en el aire";
                        attempts = 0;
                        first = false;
                    }
                    Write(buf, n, frame.Channels, gen);
                    // no adelantarse más de ~6 s
                    while (!ct.IsCancellationRequested && Buffered() > _ring.Length - buf.Length * 2) await Task.Delay(40, CancellationToken.None);
                }
                if (!ct.IsCancellationRequested) _status = "la radio cortó la transmisión · reconectando…";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _status = $"sin señal ({Short(ex)}) · reintento {attempts}/4";
            }
            catch { return; }
            if (!ct.IsCancellationRequested) await Task.Delay(2500, CancellationToken.None);
        }
        if (!ct.IsCancellationRequested && gen == _gen) _status = "no se pudo sintonizar esta radio (pruebe otra)";
    }

    // ------------------------------------------------------------------ pruebas sin ventana
    /// <summary>Descarga el directorio de forma síncrona (prueba).</summary>
    public static List<RadioStation> TestDirectory(out string error)
    {
        RadioDirectory.Refresh();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (RadioDirectory.Loading && sw.ElapsedMilliseconds < 20000) Thread.Sleep(50);
        error = RadioDirectory.LastError;
        return RadioDirectory.Stations;
    }

    /// <summary>Sintoniza una URL y decodifica unos segundos (prueba): formato, título ICY y tiempo hasta el primer audio.</summary>
    public static string TestTune(string url, float seconds)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(RadioDirectory.UserAgent);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Icy-MetaData", "1");
            using var resp = http.Send(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            int metaInt = resp.Headers.TryGetValues("icy-metaint", out var mv) && int.TryParse(mv.FirstOrDefault(), out var mi) ? mi : 0;
            string title = "";
            Stream src = resp.Content.ReadAsStream();
            if (metaInt > 0) src = new IcyStream(src, metaInt, t => title = t);
            var reader = new Mp3FrameReader(src);
            var decoder = new MpegFrameDecoder();
            long first = -1, frames = 0;
            var buf = new float[1152 * 2];
            float peak = 0;
            double sum2 = 0;
            long over = 0, cnt = 0;
            int rate = 0, ch = 0, kbps = 0, bad = 0;
            while (sw.ElapsedMilliseconds < 20000)
            {
                var f = reader.Next();
                if (f == null) break;
                int n;
                try { n = decoder.DecodeFrame(f, buf, 0); } catch { bad++; decoder.Reset(); continue; }
                if (first < 0) { first = sw.ElapsedMilliseconds; rate = f.SampleRate; ch = f.Channels; kbps = f.BitRate / 1000; }
                for (int i = 0; i < n; i++) { peak = MathF.Max(peak, MathF.Abs(buf[i])); sum2 += buf[i] * buf[i]; if (MathF.Abs(buf[i]) > 1) over++; cnt++; }
                frames += n / f.Channels;
                if (frames >= rate * seconds) break;
            }
            return rate > 0 && frames >= rate * seconds / 2
                ? $"OK {rate} Hz · {ch} canales · {kbps} kbps · primer audio {first} ms · pico {peak:0.00} · rms {Math.Sqrt(sum2 / Math.Max(1, cnt)):0.000} · >1: {over * 100.0 / Math.Max(1, cnt):0.00}%{(bad > 0 ? $" · {bad} cuadros dañados" : "")}{(title != "" ? " · ♪ " + title : "")}"
                : $"FALLO solo {frames} cuadros de audio";
        }
        catch (Exception ex) { return "FALLO " + Short(ex) + " · " + ex.Message.Split('\n')[0]; }
    }

    private static string Short(Exception ex) => ex switch
    {
        HttpRequestException h => h.StatusCode is { } c ? $"HTTP {(int)c}" : "red",
        TaskCanceledException => "tiempo agotado",
        _ => ex.GetType().Name.Replace("Exception", ""),
    };

    private int Buffered() { lock (_lock) return _count; }

    private void Write(float[] buf, int n, int ch, int gen)
    {
        lock (_lock)
        {
            if (gen != _gen) return;
            int frames = n / ch;
            for (int f = 0; f < frames; f++)
            {
                if (_count + 2 > _ring.Length) return;
                float l = Math.Clamp(buf[f * ch], -1f, 1f), r = ch > 1 ? Math.Clamp(buf[f * ch + 1], -1f, 1f) : l;
                int i = (_head + _count) % _ring.Length;
                _ring[i] = l;
                _ring[(i + 1) % _ring.Length] = r;
                _count += 2;
            }
        }
    }

    // ------------------------------------------------------------------ hilo principal: audio
    public override void _Process(double delta)
    {
        RadioDirectory.Pump();
        if (_title != NowPlaying || _status != Status)
        {
            NowPlaying = _title;
            Status = _status;
            Changed?.Invoke();
        }
        if (!On) return;
        int rate = _rate;
        if (rate > 0 && rate != _activeRate)
        {
            _activeRate = rate;
            _player.Stream = new AudioStreamGenerator { MixRate = rate, BufferLength = 0.5f };
            _pb = null;
            _primed = false;
        }
        if (_activeRate == 0) return;
        int buffered = Buffered() / 2;
        if (!_primed)
        {
            if (buffered < _activeRate) return;   // ~1 s antes de empezar
            _primed = true;
            _player.Play();
            _pb = (AudioStreamGeneratorPlayback)_player.GetStreamPlayback();
        }
        if (_pb == null) return;
        int want = Math.Min(_pb.GetFramesAvailable(), buffered);
        if (_logT >= 0 && (_logT += delta) > 2)
        {
            _logT = 0.001;
            GD.Print($"[Radio] {Status} · {Station?.Name} · {_activeRate} Hz · búfer {buffered / (float)_activeRate:0.0} s · entregados {_pushed / (float)_activeRate:0.0} s · vacíos {_underruns}{(NowPlaying != "" ? " · ♪ " + NowPlaying : "")}");
        }
        if (want <= 0)
        {
            if (buffered == 0) { _primed = false; _status = "cargando…"; _underruns++; }
            return;
        }
        _pushed += want;
        var frames = new Vector2[want];
        lock (_lock)
        {
            for (int f = 0; f < want; f++)
            {
                frames[f] = new Vector2(_ring[_head], _ring[(_head + 1) % _ring.Length]);
                _head = (_head + 2) % _ring.Length;
            }
            _count -= want * 2;
        }
        _pb.PushBuffer(frames);
    }
}

/// <summary>Quita del stream los bloques de metadatos ICY (cada metaInt bytes) y extrae StreamTitle.</summary>
internal sealed class IcyStream : Stream
{
    private readonly Stream _s;
    private readonly int _metaInt;
    private readonly Action<string> _onTitle;
    private int _left;

    public IcyStream(Stream s, int metaInt, Action<string> onTitle) { _s = s; _metaInt = metaInt; _left = metaInt; _onTitle = onTitle; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_left == 0) ReadMeta();
        int n = _s.Read(buffer, offset, Math.Min(count, _left));
        if (n > 0) _left -= n;
        return n;
    }

    private void ReadMeta()
    {
        int len = ReadByte(_s) * 16;
        if (len > 0)
        {
            var meta = new byte[len];
            int got = 0;
            while (got < len)
            {
                int r = _s.Read(meta, got, len - got);
                if (r <= 0) break;
                got += r;
            }
            string text = Encoding.UTF8.GetString(meta, 0, got).TrimEnd('\0');
            int i = text.IndexOf("StreamTitle='", StringComparison.Ordinal);
            if (i >= 0)
            {
                int j = text.IndexOf("';", i + 13, StringComparison.Ordinal);
                string t = (j > i ? text[(i + 13)..j] : text[(i + 13)..]).Trim();
                _onTitle(t);
            }
        }
        _left = _metaInt;
    }

    private static int ReadByte(Stream s)
    {
        var b = new byte[1];
        return s.Read(b, 0, 1) == 1 ? b[0] : 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _s.Dispose(); base.Dispose(disposing); }
}

/// <summary>Un cuadro MPEG de audio (cabecera + datos) para el decodificador por cuadros de NLayer.</summary>
internal sealed class Mp3Frame : IMpegFrame
{
    public byte[] Data = Array.Empty<byte>();
    public int SampleRate { get; init; }
    public int SampleRateIndex { get; init; }
    public int FrameLength { get; init; }
    public int BitRate { get; init; }
    public MpegVersion Version { get; init; }
    public MpegLayer Layer { get; init; }
    public MpegChannelMode ChannelMode { get; init; }
    public int ChannelModeExtension { get; init; }
    public int SampleCount { get; init; }
    public int BitRateIndex { get; init; }
    public bool IsCopyrighted { get; init; }
    public bool HasCrc { get; init; }
    public bool IsCorrupted => false;
    public int Channels => ChannelMode == MpegChannelMode.Mono ? 1 : 2;

    private int _offset, _bits;
    private ulong _bucket;

    public void Reset()
    {
        _offset = 4 + (HasCrc ? 2 : 0);
        _bucket = 0;
        _bits = 0;
    }

    public int ReadBits(int count)
    {
        while (_bits < count)
        {
            if (_offset >= FrameLength) throw new EndOfStreamException();
            _bucket = (_bucket << 8) | Data[_offset++];
            _bits += 8;
        }
        int v = (int)((_bucket >> (_bits - count)) & ((1UL << count) - 1));
        _bits -= count;
        return v;
    }

    private static readonly int[,] BrV1 =
    {
        { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 },
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 },
        { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 },
    };
    private static readonly int[,] BrV2 =
    {
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 },
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },
    };
    private static readonly int[] SrV1 = { 44100, 48000, 32000 };

    /// <summary>Interpreta una cabecera de 4 bytes; null si no es válida.</summary>
    public static Mp3Frame? Parse(byte b0, byte b1, byte b2, byte b3)
    {
        if (b0 != 0xFF || (b1 & 0xE0) != 0xE0) return null;
        int ver = (b1 >> 3) & 3, lay = (b1 >> 1) & 3, br = (b2 >> 4) & 15, sr = (b2 >> 2) & 3;
        if (ver == 1 || lay == 0 || br == 0 || br == 15 || sr == 3) return null;
        var version = ver == 3 ? MpegVersion.Version1 : ver == 2 ? MpegVersion.Version2 : MpegVersion.Version25;
        var layer = lay == 3 ? MpegLayer.LayerI : lay == 2 ? MpegLayer.LayerII : MpegLayer.LayerIII;
        int li = (int)layer - 1;
        int kbps = version == MpegVersion.Version1 ? BrV1[li, br] : BrV2[li, br];
        int rate = SrV1[sr] / (version == MpegVersion.Version1 ? 1 : version == MpegVersion.Version2 ? 2 : 4);
        int pad = (b2 >> 1) & 1;
        int bitrate = kbps * 1000;
        int len = layer == MpegLayer.LayerI ? (12 * bitrate / rate + pad) * 4
                : layer == MpegLayer.LayerIII && version != MpegVersion.Version1 ? 72 * bitrate / rate + pad
                : 144 * bitrate / rate + pad;
        if (len < 21) return null;
        int samples = layer == MpegLayer.LayerI ? 384 : layer == MpegLayer.LayerIII && version != MpegVersion.Version1 ? 576 : 1152;
        return new Mp3Frame
        {
            SampleRate = rate, SampleRateIndex = sr, FrameLength = len, BitRate = bitrate, Version = version, Layer = layer,
            ChannelMode = (MpegChannelMode)((b3 >> 6) & 3), ChannelModeExtension = (b3 >> 4) & 3, SampleCount = samples,
            BitRateIndex = br, IsCopyrighted = ((b3 >> 3) & 1) == 1, HasCrc = (b1 & 1) == 0,
        };
    }
}

/// <summary>
/// Lector de cuadros MP3 sobre un stream de red (sin posicionarse): busca la sincronía, valida que el formato sea
/// estable (versión, capa y frecuencia) y entrega cada cuadro completo. Salta etiquetas ID3 y basura entre cuadros.
/// </summary>
internal sealed class Mp3FrameReader
{
    private readonly Stream _s;
    private readonly byte[] _buf = new byte[16384];
    private int _start, _end;
    private int _lockRate, _lockVer = -1, _lockLayer = -1;

    public Mp3FrameReader(Stream s) => _s = s;

    private bool Fill(int need)
    {
        if (_end - _start >= need) return true;
        if (_start > 0)
        {
            Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        while (_end - _start < need)
        {
            int n = _s.Read(_buf, _end, _buf.Length - _end);
            if (n <= 0) return false;
            _end += n;
        }
        return true;
    }

    public Mp3Frame? Next()
    {
        while (true)
        {
            if (!Fill(4)) return null;
            var f = Mp3Frame.Parse(_buf[_start], _buf[_start + 1], _buf[_start + 2], _buf[_start + 3]);
            bool consistent = f != null && (_lockVer < 0 || ((int)f.Version == _lockVer && (int)f.Layer == _lockLayer && f.SampleRate == _lockRate));
            if (!consistent) { _start++; continue; }
            if (!Fill(f!.FrameLength + 4)) return null;
            // la cabecera siguiente también debe sincronizar (evita falsas sincronías dentro de los datos)
            if (_lockVer < 0)
            {
                var g = Mp3Frame.Parse(_buf[_start + f.FrameLength], _buf[_start + f.FrameLength + 1], _buf[_start + f.FrameLength + 2], _buf[_start + f.FrameLength + 3]);
                if (g == null || g.SampleRate != f.SampleRate || g.Layer != f.Layer) { _start++; continue; }
                _lockVer = (int)f.Version;
                _lockLayer = (int)f.Layer;
                _lockRate = f.SampleRate;
            }
            f.Data = new byte[f.FrameLength];
            Buffer.BlockCopy(_buf, _start, f.Data, 0, f.FrameLength);
            _start += f.FrameLength;
            f.Reset();
            return f;
        }
    }
}
