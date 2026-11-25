using System.Diagnostics;
﻿using System.ComponentModel;
using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace DsBridge;

/// <summary>
/// DualSense output over HID (USB report 0x02 / Bluetooth report 0x31).
///
/// Layout follows the Linux kernel driver hid-playstation.c
/// (struct dualsense_output_report_common / _usb / _bt):
///   common (47 bytes): [0] valid_flag0 [1] valid_flag1 [2] motor_right [3] motor_left
///     [4..7] audio [8] mute_button_led [9] power_save_control
///     [10..20] right trigger effect (11 bytes)  [21..31] left trigger effect (11 bytes)
///     [32..37] reserved  [38] valid_flag2  [39..40] reserved  [41] lightbar_setup
///     [42] led_brightness [43] player_leds [44] red [45] green [46] blue
///   USB: [0]=0x02, common at 1. Protocol length 48 (descriptor pads to 64).
///   BT : [0]=0x31, [1]=seq<<4, [2]=tag 0x10, common at 3, 24 reserved bytes,
///        CRC32 at [74..77] over seed 0xA2 + bytes [0..73]. Protocol length 78.
/// On Windows the HID write buffer must be OutputReportByteLength (547 for BT);
/// bytes after the 78-byte protocol report are zero padding, not part of the CRC.
///
/// Safety: only fields whose valid flags are set are applied by the controller,
/// so each command sets exactly the flags it needs and leaves other state untouched.
/// Trigger effect encodings: Nielk1 "Factories for all DualSense trigger effects" (MIT).
/// </summary>
public sealed class DualSense : IDisposable
{
    public const int SonyVid = 0x054C;
    public static readonly int[] DualSensePids = { 0x0CE6, 0x0DF2 };

    // valid_flag0
    public const byte F0_CompatVibration = 0x01, F0_HapticsSelect = 0x02, F0_RightTrigger = 0x04, F0_LeftTrigger = 0x08;
    // valid_flag1
    public const byte F1_MicLed = 0x01, F1_Lightbar = 0x04, F1_PlayerLeds = 0x10;
    // valid_flag2
    public const byte F2_LightbarSetup = 0x02, F2_CompatVibration2 = 0x04;
    public const byte LightbarSetupLightOut = 0x02;

    const int UsbProtocolLen = 48, BtProtocolLen = 78, BtCrcOffset = 74;

    readonly HidDevice _dev;
    readonly HidStream? _stream;
    readonly SafeFileHandle? _rawHandle;
    readonly object _lock = new();
    byte _btSeq;

    public bool IsBluetooth { get; }
    public int OutputReportLength { get; }
    public string Description { get; }
    public string Method { get; }
    public byte[]? LastReport { get; private set; }

    DualSense(HidDevice dev, bool bt, int outLen, string method, HidStream? s, SafeFileHandle? h)
    {
        _dev = dev; IsBluetooth = bt; OutputReportLength = outLen; Method = method; _stream = s; _rawHandle = h;
        Description = Describe(dev);
        if (_stream != null) _stream.WriteTimeout = 1000;
    }

    public static IEnumerable<HidDevice> Enumerate() =>
        DeviceList.Local.GetHidDevices(SonyVid).Where(d => DualSensePids.Contains(d.ProductID));

    static int SafeLen(Func<int> f) { try { return f(); } catch { return -1; } }

    /// <summary>HID interface path of this pad (its Windows Container ID finds the pad's own audio endpoint).</summary>
    public string DevicePath => _dev.DevicePath;

    /// <summary>True while this pad's HID interface is still present (false after unplug / BT drop / switch to cable).</summary>
    public bool StillPresent()
    {
        try { return Enumerate().Any(x => x.DevicePath == _dev.DevicePath); } catch { return true; }
    }

    public static string Describe(HidDevice d)
    {
        int inLen = SafeLen(d.GetMaxInputReportLength), outLen = SafeLen(d.GetMaxOutputReportLength);
        string transport = inLen == 64 ? "USB" : inLen > 64 ? "Bluetooth" : "unknown";
        string name = "?"; try { name = d.GetProductName(); } catch { }
        return $"{name} PID=0x{d.ProductID:X4} transport={transport} in={inLen} out={outLen}";
    }

    /// <param name="method">"write" = HidSharp WriteFile (default), "setreport" = HidD_SetOutputReport</param>
    /// <param name="target">"any" (default), "physical" (Bluetooth pad) or "virtual" (DSX virtual DualSense, USB-format)</param>
    public static DualSense? Open(bool allowBluetooth, string method, out string message, string target = "any")
    {
        message = "";
        // physical = the real pad over Bluetooth or USB cable (USB preferred: when the cable is in, the pad talks USB).
        // A USB-format device can also be DSX's virtual pad, so `run` refuses to start while DSX runs.
        // physical-bt = Bluetooth only (old behaviour, safe next to DSX).
        foreach (var d in Enumerate().OrderBy(x => SafeLen(x.GetMaxInputReportLength) > 64 ? 1 : 0))
        {
            int inLen = SafeLen(d.GetMaxInputReportLength), outLen = SafeLen(d.GetMaxOutputReportLength);
            bool bt = inLen > 64;
            if (target == "virtual" && bt) continue;
            if (target == "physical-bt" && !bt) continue;
            if (bt && !allowBluetooth)
            {
                message = $"Found {Describe(d)} over Bluetooth. Pass --allow-bt to write to it " +
                          "(the first BT output report switches the pad to extended report mode until it reconnects).";
                continue;
            }
            if (outLen <= 0) { message = $"Cannot read output report length of {Describe(d)}"; continue; }
            if (method == "setreport")
            {
                var h = CreateFile(d.DevicePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                   IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) { message = $"CreateFile failed ({Marshal.GetLastWin32Error()}) for {Describe(d)}"; continue; }
                message = $"Opened {Describe(d)} method=setreport";
                return new DualSense(d, bt, outLen, method, null, h);
            }
            if (!d.TryOpen(out HidStream s)) { message = $"Cannot open {Describe(d)} (in use or access denied)"; continue; }
            message = $"Opened {Describe(d)} method=write";
            return new DualSense(d, bt, outLen, "write", s, null);
        }
        if (string.IsNullOrEmpty(message)) message = "No DualSense found (VID 054C, PID 0CE6/0DF2).";
        return null;
    }

    // ---------- trigger effect factories (11 bytes) ----------

    public static byte[] EffectOff() { var e = new byte[11]; e[0] = 0x05; return e; }

    /// <summary>Resistance from zone position (0-9) to the end, strength 1-8.</summary>
    public static byte[] EffectFeedback(int position, int strength)
    {
        if (position is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(position));
        if (strength is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(strength));
        if (strength == 0) return EffectOff();
        byte force = (byte)((strength - 1) & 0x07);
        uint forceZones = 0; ushort active = 0;
        for (int i = position; i < 10; i++) { forceZones |= (uint)force << (3 * i); active |= (ushort)(1 << i); }
        var e = new byte[11];
        e[0] = 0x21; e[1] = (byte)active; e[2] = (byte)(active >> 8);
        e[3] = (byte)forceZones; e[4] = (byte)(forceZones >> 8); e[5] = (byte)(forceZones >> 16); e[6] = (byte)(forceZones >> 24);
        return e;
    }

    /// <summary>"Weapon" (Nielk1, mode 0x25): resistance between zone `start` (2-7) and `end` (start+1..8) that gives way
    /// past `end` - a short step / snap felt early in the pull, free afterwards. strength 1-8.</summary>
    public static byte[] EffectWeapon(int start, int end, int strength)
    {
        if (start is < 2 or > 7) throw new ArgumentOutOfRangeException(nameof(start));
        if (end <= start || end > 8) throw new ArgumentOutOfRangeException(nameof(end));
        if (strength is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(strength));
        if (strength == 0) return EffectOff();
        ushort zones = (ushort)((1 << start) | (1 << end));
        var e = new byte[11];
        e[0] = 0x25; e[1] = (byte)zones; e[2] = (byte)(zones >> 8); e[3] = (byte)(strength - 1);
        return e;
    }

    /// <summary>"Bow" (Nielk1, mode 0x22): resistance from zone `start` (0-7) to `end` (start+1..8), strength 1-8, then a
    /// snap back of `snapForce` 1-8 when the end is passed - a click with a little spring ("ring") after it.</summary>
    public static byte[] EffectBow(int start, int end, int strength, int snapForce)
    {
        if (start is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(start));
        if (end <= start || end > 8) throw new ArgumentOutOfRangeException(nameof(end));
        if (strength is < 0 or > 8 || snapForce is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(strength));
        if (strength == 0 || snapForce == 0) return EffectOff();
        ushort zones = (ushort)((1 << start) | (1 << end));
        uint forces = (uint)(((strength - 1) & 0x07) | (((snapForce - 1) & 0x07) << 3));
        var e = new byte[11];
        e[0] = 0x22; e[1] = (byte)zones; e[2] = (byte)(zones >> 8); e[3] = (byte)forces; e[4] = (byte)(forces >> 8);
        return e;
    }

    /// <summary>Feedback with a strength per zone (Nielk1 "multiple position feedback", mode 0x21): 10 values 0-8.</summary>
    public static byte[] EffectFeedbackZones(int[] strengths)
    {
        if (strengths.Length != 10) throw new ArgumentException("10 zone strengths expected");
        uint forceZones = 0; ushort active = 0;
        for (int i = 0; i < 10; i++)
        {
            int s = strengths[i];
            if (s is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(strengths));
            if (s == 0) continue;
            forceZones |= (uint)((s - 1) & 0x07) << (3 * i); active |= (ushort)(1 << i);
        }
        if (active == 0) return EffectOff();
        var e = new byte[11];
        e[0] = 0x21; e[1] = (byte)active; e[2] = (byte)(active >> 8);
        e[3] = (byte)forceZones; e[4] = (byte)(forceZones >> 8); e[5] = (byte)(forceZones >> 16); e[6] = (byte)(forceZones >> 24);
        return e;
    }

    /// <summary>Trigger vibration from zone position (0-9), amplitude 1-8, frequency 1-255 Hz.</summary>
    public static byte[] EffectVibration(int position, int amplitude, int frequency)
    {
        if (position is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(position));
        if (amplitude is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(amplitude));
        if (frequency is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(frequency));
        if (amplitude == 0 || frequency == 0) return EffectOff();
        byte amp = (byte)((amplitude - 1) & 0x07);
        uint zones = 0; ushort active = 0;
        for (int i = position; i < 10; i++) { zones |= (uint)amp << (3 * i); active |= (ushort)(1 << i); }
        var e = new byte[11];
        e[0] = 0x26; e[1] = (byte)active; e[2] = (byte)(active >> 8);
        e[3] = (byte)zones; e[4] = (byte)(zones >> 8); e[5] = (byte)(zones >> 16); e[6] = (byte)(zones >> 24);
        e[9] = (byte)frequency;
        return e;
    }

    // ---------- commands (each sets only its own valid flags) ----------

    public void SetRightTrigger(byte[] effect) => Send(c => { c[0] |= F0_RightTrigger; Buffer.BlockCopy(effect, 0, c, 10, 11); });

    public void SetLeftTrigger(byte[] effect) => Send(c => { c[0] |= F0_LeftTrigger; Buffer.BlockCopy(effect, 0, c, 21, 11); });

    /// <summary>Both triggers in one report (null = leave that trigger as it is).</summary>
    public void SetTriggers(byte[]? left, byte[]? right) => Send(c =>
    {
        if (right != null) { c[0] |= F0_RightTrigger; Buffer.BlockCopy(right, 0, c, 10, 11); }
        if (left != null) { c[0] |= F0_LeftTrigger; Buffer.BlockCopy(left, 0, c, 21, 11); }
    });

    public void ResetTriggers() => Send(c =>
    {
        c[0] |= (byte)(F0_RightTrigger | F0_LeftTrigger);
        Buffer.BlockCopy(EffectOff(), 0, c, 10, 11);
        Buffer.BlockCopy(EffectOff(), 0, c, 21, 11);
    });

    /// <summary>Classic rumble emulation. right = high-frequency (small) motor, left = low-frequency (large) motor.</summary>
    public void SetRumble(byte left, byte right) => Send(c =>
    {
        c[0] |= (byte)(F0_CompatVibration | F0_HapticsSelect);
        c[38] |= F2_CompatVibration2;
        c[2] = right; c[3] = left;
    });

    /// <summary>Prepares the pad for audio (HD) haptics: rumble emulation off (flag0 bits 0/1 = 0) and
    /// power_save_control (common[9]) = 0 -> haptics not muted, no haptic power save (valid flag1 bit1).</summary>
    public const byte F1_PowerSaveControl = 0x02;
    public void EnableAudioHaptics() => Send(c => { c[1] |= F1_PowerSaveControl; c[9] = 0x00; });

    /// <summary>BT HD haptics prime (AnyPS5 #2056: without it the actuators play ~12.5 %): flag1 power-save control (0x02) + haptic
    /// low-pass filter (0x20) + motor power level (0x40); power_save_control 0 = not muted, power reduction (common[36]) 0.</summary>
    public void PrimeBtHaptics() => Send(c => { c[1] |= (byte)(F1_PowerSaveControl | 0x20 | 0x40); c[9] = 0x00; c[36] = 0x00; });

    byte _btHapticsCounter;
    /// <summary>One block of HD haptics over Bluetooth: report 0x32 (BtHapticsReport), 32 frames of signed 8-bit stereo at 3 kHz.
    /// Shares the 4-bit sequence with the 0x31 reports (as DS5Dongle); same writer lock.</summary>
    public void SendBtHaptics(ReadOnlySpan<sbyte> block)
    {
        if (!IsBluetooth) throw new InvalidOperationException("report 0x32 is Bluetooth only");
        var copy = block.ToArray();
        lock (_lock)
        {
            var r = BtHapticsReport.Build(copy, _btSeq, _btHapticsCounter, OutputReportLength);
            _btSeq = (byte)((_btSeq + 1) & 0x0F); _btHapticsCounter++;
            Transmit(r);
        }
    }

    /// <summary>Audio stack init (community RE: lopya76/DualSense-Audio-Initialization, "0x0F->0xFC enables haptics,
    /// 0x55->0xD7 enables the rest of the audio stack"). Windows' driver never sends it.
    /// flag0 0xF0 = headphone/speaker/mic volume + audio control valid, rumble-emulation bits 0/1 = 0 (haptics armed),
    /// trigger bits untouched; flag1 0x82 = power-save control + audio control 2 valid.
    /// Values as in the "initialized" dump: headphone 0x9D, speaker 0x66, mic 0x40, routing 0x6C.
    /// Speaker channels 1/2 carry silence from DsBridge, so the speaker volume makes no sound.</summary>
    /// <summary>Raw common block (47 bytes after the report id) for experiments.</summary>
    public void SendRaw(byte[] common47) => Send(c => Buffer.BlockCopy(common47, 0, c, 0, Math.Min(47, common47.Length)));

    /// <summary>Variant D: everything that may keep the actuators away from audio, cleared in one report.
    /// Layout per nondebug/dualsense SetStateData: [0] flags0, [1] flags1, [4..7] volumes + audio control, [9] mute control,
    /// [36] motor power level (rumble/trigger reduction), [37] audio control 2, [38] flags2 (bit 2 = improved rumble emulation),
    /// [39] haptic low-pass filter. flags1: 0x02 audio mute, 0x20 haptic LPF, 0x40 motor power level, 0x80 audio control 2.</summary>
    public void ArmHapticsFull() => Send(c =>
    {
        c[0] = 0xF0;                         // volumes + audio control valid; rumble emulation / use-rumble = 0
        c[1] = 0x02 | 0x20 | 0x40 | 0x80;    // mute control, haptic LPF, motor power, audio control 2 valid
        c[4] = 0x9D; c[5] = 0x66; c[6] = 0x40; c[7] = 0x6C;
        c[9] = 0x00;                         // nothing muted, no power save
        c[36] = 0x00;                        // no motor power reduction
        c[37] = 0x00;
        c[38] = 0x00;                        // improved rumble emulation OFF
        c[39] = 0x00;                        // haptic low-pass filter off
    });

    /// <summary>Light re-arm: only what takes the actuators back from rumble — rumble-emulation bits 0 (flags0 = 0,
    /// nothing else marked valid), motor power level [36] = 0 and haptic low-pass [39] = 0 (flags1 0x60),
    /// improved rumble emulation off ([38] = 0). Volumes / audio routing are not re-sent.</summary>
    public void ArmHapticsLight() => Send(c => { c[0] = 0x00; c[1] = 0x20 | 0x40; c[36] = 0; c[38] = 0; c[39] = 0; });

    public void InitAudioStack(byte headphone = 0x9D, byte speaker = 0x66, byte mic = 0x40, byte routing = 0x6C) => Send(c =>
    {
        c[0] = 0xF0; c[1] = (byte)(F1_PowerSaveControl | 0x80);
        c[4] = headphone; c[5] = speaker; c[6] = mic; c[7] = routing; c[9] = 0x00;
    });

    public void SetLightbar(byte r, byte g, byte b, bool releaseStartupLight = false) => Send(c =>
    {
        c[1] |= F1_Lightbar;
        if (releaseStartupLight) { c[38] |= F2_LightbarSetup; c[41] = LightbarSetupLightOut; }
        c[44] = r; c[45] = g; c[46] = b;
    });

    // ---------- input (read-only, separate handle) ----------

    public const byte BtnSquare = 0x10, BtnCross = 0x20, BtnCircle = 0x40, BtnTriangle = 0x80;

    /// <summary>Face-button byte of an input report: BT extended 0x31 -> [9], BT basic 0x01 -> [5], USB 0x01 -> [8].
    /// Low nibble = D-pad, 0x10 square, 0x20 cross, 0x40 circle, 0x80 triangle.</summary>
    /// <summary>Analog L2 / R2 (0-255) from an input report. USB 0x01: [5] L2, [6] R2; BT extended 0x31: [6] L2, [7] R2;
    /// BT basic 0x01 (len &lt;= 10): [8] L2, [9] R2 (report id at [0]).</summary>
    public static bool TryGetTriggers(byte[] r, int len, bool bluetooth, out byte l2, out byte r2)
    {
        l2 = r2 = 0;
        if (r[0] == 0x31 && bluetooth && len >= 8) { l2 = r[6]; r2 = r[7]; return true; }
        if (r[0] == 0x01 && !bluetooth && len >= 7) { l2 = r[5]; r2 = r[6]; return true; }
        if (r[0] == 0x01 && bluetooth && len >= 10) { l2 = r[8]; r2 = r[9]; return true; }
        return false;
    }

    public static bool TryGetFaceButtons(byte[] r, int len, bool bluetooth, out byte buttons)
    {
        buttons = 0;
        if (len < 10) return false;
        int idx = r[0] switch
        {
            0x31 when bluetooth && len >= 11 => 9,
            0x01 when bluetooth => 5,
            0x01 when !bluetooth && len >= 9 => 8,
            _ => -1
        };
        if (idx < 0) return false;
        buttons = r[idx];
        return true;
    }

    /// <summary>The Create (SHARE) button: the byte after the face buttons, bit 0x10 (0x20 = Options). Test tools only (rule 6).</summary>
    public static bool TryGetCreate(byte[] r, int len, bool bluetooth, out bool pressed)
    {
        pressed = false;
        if (!TryGetFaceButtons(r, len, bluetooth, out _)) return false;
        int idx = r[0] == 0x31 && bluetooth ? 10 : r[0] == 0x01 && bluetooth ? 6 : 9;
        if (idx >= len) return false;
        pressed = (r[idx] & 0x10) != 0;
        return true;
    }

    /// <summary>Starts a background thread reading input reports through a second HID handle.</summary>
    public Thread StartInputReader(Action<byte[], int> onReport, CancellationToken ct)
    {
        if (!_dev.TryOpen(out HidStream rs)) throw new IOException("Cannot open DualSense for reading");
        rs.ReadTimeout = 500;
        var t = new Thread(() =>
        {
            var buf = new byte[Math.Max(SafeLen(_dev.GetMaxInputReportLength), 64)];
            using (rs)
            {
                while (!ct.IsCancellationRequested)
                {
                    try { int n = rs.Read(buf, 0, buf.Length); if (n > 0) onReport(buf, n); }
                    catch (TimeoutException) { }
                    catch (Exception) when (!ct.IsCancellationRequested) { Thread.Sleep(200); }
                }
            }
        }) { IsBackground = true, Name = "DualSense input" };
        t.Start();
        return t;
    }

    /// <summary>The five player LEDs under the touchpad: common[43] (bits 0-4), valid_flag1 bit 4.</summary>
    public void SetPlayerLeds(byte mask) => Send(c => { c[1] |= F1_PlayerLeds; c[43] = (byte)(mask & 0x1F); });

    /// <summary>How many of the five player LEDs are lit, as a symmetric pattern (Sony's own player-number layout):
    /// 1 = centre, 2 = 2nd+4th, 3 = 1st+3rd+5th, 4 = all but the centre, 5 = all.</summary>
    public static byte PlayerLedPattern(int count) => count switch { <= 0 => 0x00, 1 => 0x04, 2 => 0x0A, 3 => 0x15, 4 => 0x1B, _ => 0x1F };

    /// <summary>lightbar_setup = LIGHT_OUT only (no color): fades out the firmware's default light so the host owns the lightbar.</summary>
    public void ReleaseStartupLight() => Send(c => { c[38] |= F2_LightbarSetup; c[41] = LightbarSetupLightOut; });

    void Send(Action<byte[]> fillCommon)
    {
        lock (_lock)
        {
            var common = new byte[47];
            fillCommon(common);
            var report = IsBluetooth ? BuildBluetooth(common) : BuildUsb(common);
            LastReport = report;
            Transmit(report);
        }
    }

    byte[] BuildUsb(byte[] common)
    {
        var r = new byte[Math.Max(OutputReportLength, UsbProtocolLen)];
        r[0] = 0x02;
        Buffer.BlockCopy(common, 0, r, 1, common.Length);
        return r;
    }

    byte[] BuildBluetooth(byte[] common)
    {
        var r = BuildBluetoothStatic(common, _btSeq, OutputReportLength);
        _btSeq = (byte)((_btSeq + 1) & 0x0F);
        return r;
    }

    public static byte[] BuildBluetoothStatic(byte[] common, byte seq, int bufferLen)
    {
        var r = new byte[Math.Max(bufferLen, BtProtocolLen)];   // buffer = 547 on Windows, protocol = 78
        r[0] = 0x31;
        r[1] = (byte)(seq << 4);
        r[2] = 0x10;
        Buffer.BlockCopy(common, 0, r, 3, common.Length);
        uint crc = Crc32(r, BtCrcOffset);
        r[74] = (byte)crc; r[75] = (byte)(crc >> 8); r[76] = (byte)(crc >> 16); r[77] = (byte)(crc >> 24);
        return r;
    }

    /// <summary>CRC32 (IEEE, reflected) over seed byte 0xA2 followed by data[0..count).</summary>
    public static uint Crc32(byte[] data, int count)
    {
        uint c = 0xFFFFFFFF;
        void Feed(byte b) { c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1; }
        Feed(0xA2);
        for (int i = 0; i < count; i++) Feed(data[i]);
        return ~c;
    }

    /// <summary>Diagnostics (BT HD in the game, 15.11.2026): the report id and start time of a write in progress (0 = none),
    /// count / slowest write. A watchdog in `run` reports a write that hangs.</summary>
    public volatile int WriteInProgressId; public long WriteStartTicks, Writes; public double SlowestWriteMs;
    void Transmit(byte[] report)
    {
        WriteInProgressId = report[0]; WriteStartTicks = Stopwatch.GetTimestamp();
        try { TransmitCore(report); }
        finally
        {
            double ms = (Stopwatch.GetTimestamp() - WriteStartTicks) * 1000.0 / Stopwatch.Frequency;
            if (ms > SlowestWriteMs) SlowestWriteMs = ms;
            Writes++; WriteInProgressId = 0;
        }
    }
    void TransmitCore(byte[] report)
    {
        if (_stream != null) { _stream.Write(report, 0, report.Length); return; }
        if (!HidD_SetOutputReport(_rawHandle!, report, report.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_SetOutputReport failed");
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        try { _rawHandle?.Dispose(); } catch { }
    }

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, int len);
}
