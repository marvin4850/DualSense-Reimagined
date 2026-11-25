using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DsBridge;

/// <summary>
/// Client for the DSX (Paliverse) Mod System, legacy UDP transport (DSX v3.1+).
/// Protocol per github.com/Paliverse/DSX "Mod System (DSX v3)" README and DSX_UDP_Example (Apache-2.0).
/// One JSON datagram per request: {"instructions":[{"type":N,"parameters":[...]}],"Diagnostics":true,"ModId":"..."}
/// parameters[0] = controller index, -1 = controller selected in DSX.
/// DSX drops mod changes ~60 s after the last valid packet (built-in failsafe).
/// </summary>
public sealed class DsxClient : IDisposable
{
    public enum Instr { GetDSXStatus = 0, TriggerUpdate = 1, RGBUpdate = 2, PlayerLED = 3, TriggerThreshold = 4, MicLED = 5,
                        PlayerLEDNewRevision = 6, ResetToUserSettings = 7, ToMode = 8, Rumble = 9, Haptics = 10, Audio = 11 }
    public enum TriggerMode { Normal = 0, GameCube = 1, VerySoft = 2, Soft = 3, Hard = 4, VeryHard = 5, Hardest = 6, Rigid = 7,
                              VibrateTrigger = 8, Choppy = 9, Medium = 10, VibrateTriggerPulse = 11, CustomTriggerValue = 12,
                              Resistance = 13, Bow = 14, Galloping = 15, SemiAutomaticGun = 16, AutomaticGun = 17, Machine = 18 }
    public enum Waveform { Sine = 0, Square = 1, Triangle = 2, Sawtooth = 3, WhiteNoise = 4, PinkNoise = 5 }
    public const int Left = 1, Right = 2;
    public const string ModId = "G1R.DsBridge";

    readonly UdpClient _udp;
    readonly IPEndPoint _ep;
    public int Port { get; }
    public int Controller { get; set; } = -1;

    public DsxClient(int? port = null)
    {
        Port = port ?? ReadPortFile() ?? 6969;
        _ep = new IPEndPoint(IPAddress.Loopback, Port);
        _udp = new UdpClient();
        _udp.Client.ReceiveTimeout = 700;
    }

    static int? ReadPortFile()
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSX", "DSX_UDP_PortNumber.txt");
            return File.Exists(p) && int.TryParse(File.ReadAllText(p).Trim(), out int v) && v is > 0 and < 65536 ? v : null;
        }
        catch { return null; }
    }

    public static string BuildPacket(params (Instr type, object[] p)[] instructions)
    {
        var arr = new JsonArray();
        foreach (var (type, p) in instructions)
        {
            var pars = new JsonArray();
            foreach (var x in p) pars.Add(JsonValue.Create(Convert.ToInt32(x)));
            arr.Add(new JsonObject { ["type"] = (int)type, ["parameters"] = pars });
        }
        return new JsonObject { ["instructions"] = arr, ["Diagnostics"] = true, ["ModId"] = ModId }.ToJsonString();
    }

    /// <summary>Sends a packet; returns DSX's JSON reply or null on timeout.</summary>
    public JsonNode? Send(params (Instr type, object[] p)[] instructions)
    {
        var bytes = Encoding.ASCII.GetBytes(BuildPacket(instructions));
        _udp.Send(bytes, bytes.Length, _ep);
        try
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            var reply = _udp.Receive(ref remote);
            return JsonNode.Parse(Encoding.UTF8.GetString(reply));
        }
        catch (SocketException) { return null; }
        catch (JsonException) { return null; }
    }

    public JsonNode? Status() => Send((Instr.GetDSXStatus, Array.Empty<object>()));

    public JsonNode? Trigger(int trigger, TriggerMode mode, params int[] values) =>
        Send((Instr.TriggerUpdate, new object[] { Controller, trigger, (int)mode }.Concat(values.Cast<object>()).ToArray()));

    /// <summary>large = left (strong) motor, small = right (weak) motor, 0..255; durationMs 1..5000.</summary>
    public JsonNode? Rumble(int large, int small, int durationMs) =>
        Send((Instr.Rumble, new object[] { Controller, large, small, durationMs }));

    public JsonNode? RumbleStop() => Send((Instr.Rumble, new object[] { Controller, 0, 0 }));

    /// <summary>Haptics preset: waveform, frequency 20..500 Hz, intensity 0..255, duration 1..5000 ms.</summary>
    public JsonNode? Haptics(Waveform w, int freqHz, int intensity, int durationMs) =>
        Send((Instr.Haptics, new object[] { Controller, 1, (int)w, freqHz, intensity, durationMs }));

    public JsonNode? HapticsStop() => Send((Instr.Haptics, new object[] { Controller, 0 }));

    public JsonNode? Rgb(int r, int g, int b, int brightness = 255) =>
        Send((Instr.RGBUpdate, new object[] { Controller, r, g, b, brightness }));

    public JsonNode? Reset() => Send((Instr.ResetToUserSettings, new object[] { Controller }));

    public static string Summarize(JsonNode? reply)
    {
        if (reply == null) return "no reply (DSX not running, Incoming UDP off, or wrong port)";
        var sb = new StringBuilder();
        sb.Append($"status={reply["Status"]} connected={reply["isControllerConnected"]} battery={reply["BatteryLevel"]}");
        if (reply["Devices"] is JsonArray devs)
            foreach (var d in devs)
                sb.Append($"\n    device #{d?["Index"]}: type={d?["DeviceType"]} conn={d?["ConnectionType"]} AT={d?["IsSupportAT"]} rumble={d?["IsSupportRumble"]} haptics={d?["IsSupportHaptics"]} lightbar={d?["IsSupportLightBar"]} speaker={d?["IsSupportSpeaker"]}");
        if (reply["Diagnostics"] is JsonArray diags && diags.Count > 0)
            foreach (var g in diags) sb.Append($"\n    diag: instr={g?["Instruction"]} code={g?["Code"]} msg={g?["Message"]}");
        return sb.ToString();
    }

    public void Dispose() => _udp.Dispose();
}
