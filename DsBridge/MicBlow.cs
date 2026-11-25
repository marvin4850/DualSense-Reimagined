using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DsBridge;

/// <summary>Blow detector (user 21.11.2026: "blowing into the pad microphone uses the scavenger whistle").
/// A blow into a microphone is loud broadband wind noise with no pitch; speech and singing are voiced (a strong
/// autocorrelation peak at 80-400 Hz) and a tap / bump is short. 20 ms frames: loud (&gt; LevelDb) and unvoiced
/// (normalised autocorrelation &lt; MaxVoicing) frames; a blow = at least 14 such frames in the last 18 (~280 of 360 ms).
/// Nothing is recorded or stored: frames are measured and dropped.</summary>
public sealed class BlowDetector
{
    public double LevelDb = -26, MaxVoicing = 0.45;
    public int Window = 18, Need = 14, CooldownMs = 3000;
    readonly Queue<bool> _hist = new();
    readonly List<float> _buf = new();
    long _lastFireMs = long.MinValue / 2;
    public double LastDb, LastVoicing;
    /// <summary>v49: a loud burst (>= 5 loud frames = 100 ms) that ended without a blow: "loud N frames, unvoiced M, max dB,
    /// mean voicing" - logged so the thresholds can be tuned from a game session. Null when there was none.</summary>
    public string? LastMiss;
    int _loudRun, _unvoicedRun; double _runMaxDb = -200, _runVoicing;

    /// <summary>Feeds mono samples (-1..1) at <paramref name="rate"/> Hz; true when a blow is recognised (once per cooldown).</summary>
    public bool Feed(ReadOnlySpan<float> mono, int rate, long nowMs)
    {
        bool fired = false;
        int frame = Math.Max(1, rate / 50);
        foreach (var x in mono)
        {
            _buf.Add(x);
            if (_buf.Count < frame) continue;
            var f = _buf.ToArray(); _buf.Clear();
            bool blow = IsBlowFrame(f, rate);
            if (LastDb > LevelDb) { _loudRun++; if (blow) _unvoicedRun++; _runMaxDb = Math.Max(_runMaxDb, LastDb); _runVoicing += LastVoicing; }
            else
            {
                if (_loudRun >= 5 && !fired && nowMs - _lastFireMs > 1500)
                    LastMiss = $"loud {_loudRun} frames, unvoiced {_unvoicedRun}, max {_runMaxDb:F0} dB, voicing {_runVoicing / _loudRun:F2}";
                _loudRun = 0; _unvoicedRun = 0; _runMaxDb = -200; _runVoicing = 0;
            }
            _hist.Enqueue(blow); while (_hist.Count > Window) _hist.Dequeue();
            if (_hist.Count == Window && _hist.Count(b => b) >= Need && nowMs - _lastFireMs >= CooldownMs)
            { _lastFireMs = nowMs; _hist.Clear(); fired = true; }
        }
        return fired;
    }

    bool IsBlowFrame(float[] f, int rate)
    {
        double e = 0; foreach (var x in f) e += x * x;
        double rms = Math.Sqrt(e / f.Length);
        LastDb = 20 * Math.Log10(rms + 1e-9);
        LastVoicing = Voicing(f, rate, e);
        return LastDb > LevelDb && LastVoicing < MaxVoicing;
    }

    /// <summary>Max normalised autocorrelation for lags of 80..400 Hz (1 = perfectly periodic, ~0 = noise).</summary>
    static double Voicing(float[] f, int rate, double e0)
    {
        if (e0 <= 1e-12) return 0;
        int lo = Math.Max(1, rate / 400), hi = Math.Min(f.Length - 1, rate / 80);
        double best = 0;
        for (int lag = lo; lag <= hi; lag++)
        {
            double c = 0, e1 = 0, e2 = 0;
            for (int i = 0; i + lag < f.Length; i++) { c += f[i] * f[i + lag]; e1 += f[i] * f[i]; e2 += f[i + lag] * f[i + lag]; }
            if (e1 <= 0 || e2 <= 0) continue;
            double r = c / Math.Sqrt(e1 * e2);
            if (r > best) best = r;
        }
        return best;
    }
}

/// <summary>Listens to the pad's own microphone (the capture endpoint with the pad's Windows Container ID, else the first
/// capture device named DualSense / Wireless Controller) and raises <see cref="Blown"/>. USB only: Bluetooth gives Windows no
/// pad audio device. Off with run --no-mic.</summary>
public sealed class MicBlow : IDisposable
{
    readonly BlowDetector _det = new();
    WasapiCapture? _cap;
    public event Action? Blown;
    public string Device = "";
    /// <summary>False once the capture stopped (device unplugged / endpoint lost): run reopens the microphone (v50).</summary>
    public bool Alive { get; private set; }

    public static MMDevice? FindMic(string? hidPath)
    {
        var en = new MMDeviceEnumerator();
        var all = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        if (hidPath != null && DeviceIds.ContainerOfHid(hidPath) is Guid cid && all.FirstOrDefault(d => DeviceIds.ContainerOf(d) == cid) is { } same)
            return same;
        return all.FirstOrDefault(d => d.FriendlyName.Contains("DualSense", StringComparison.OrdinalIgnoreCase)
                                    || d.FriendlyName.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase));
    }

    public bool Start(string? hidPath, Action<string> log)
    {
        try
        {
            var dev = FindMic(hidPath);
            if (dev == null) { log("mic: no pad microphone (Bluetooth, or Windows shows none) - whistle by blowing is off"); return false; }
            Device = dev.FriendlyName;
            _cap = new WasapiCapture(dev, true, 20);
            var fmt = _cap.WaveFormat;
            _cap.DataAvailable += (_, a) =>
            {
                try
                {
                    int ch = fmt.Channels, bytes = fmt.BitsPerSample / 8, n = a.BytesRecorded / (bytes * ch);
                    if (n <= 0) return;
                    var mono = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        int o = i * ch * bytes;   // first channel only
                        mono[i] = fmt.Encoding == WaveFormatEncoding.IeeeFloat || (fmt.Encoding == WaveFormatEncoding.Extensible && bytes == 4)
                            ? BitConverter.ToSingle(a.Buffer, o)
                            : bytes == 2 ? BitConverter.ToInt16(a.Buffer, o) / 32768f : 0f;
                    }
                    bool blown = _det.Feed(mono, fmt.SampleRate, Environment.TickCount64);
                    if (_det.LastMiss != null) { log("mic: loud burst, not a blow (" + _det.LastMiss + ")"); _det.LastMiss = null; }
                    if (blown)
                    {
                        log($"mic: blow recognised ({_det.LastDb:F0} dB, voicing {_det.LastVoicing:F2}) -> scavenger whistle");
                        Blown?.Invoke();
                    }
                }
                catch (Exception ex) { log("mic: " + ex.Message); }
            };
            _cap.RecordingStopped += (_, a) => { Alive = false; if (a.Exception != null) log("mic stopped: " + a.Exception.Message); };
            _cap.StartRecording();
            Alive = true;
            log($"mic: listening on '{Device}' ({fmt.SampleRate} Hz) for a blow = scavenger whistle (nothing is recorded; --no-mic turns it off)");
            return true;
        }
        catch (Exception ex) { log("mic: cannot open the pad microphone: " + ex.Message); return false; }
    }

    public void Dispose()
    {
        try { _cap?.StopRecording(); } catch { }
        _cap?.Dispose(); _cap = null;
    }
}
