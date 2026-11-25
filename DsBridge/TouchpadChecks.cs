using System.Buffers.Binary;

namespace DsBridge;

public static class TouchpadChecks
{
    static readonly TouchPoint Off = new(false, 0, 0, 0);
    static TouchFrame Frame(int x, int y, int id = 3, bool click = false, bool second = false) =>
        new(new(true, id, x, y), second ? new(true, 4, 800, 600) : Off, click);
    static TouchFrame Lift => new(Off, Off, false);
    static TouchFrame Two(int x1, int y1, int x2, int y2, int id1 = 3) => new(new(true, id1, x1, y1), new(true, 9, x2, y2), false);

    public static IEnumerable<(string name, bool ok)> Run()
    {
        foreach (var (dx, dy, expected) in new[] { (600, 0, SwipeDirection.Right), (-600, 0, SwipeDirection.Left), (0, -400, SwipeDirection.Up), (0, 400, SwipeDirection.Down) })
        {
            var g = new TouchpadGestures();
            g.Feed(Lift, 1000, true); g.Feed(Frame(900, 500), 1010, true);
            var during = g.Feed(Frame(900 + dx, 500 + dy), 1110, true);
            var after = g.Feed(Lift, 1130, true);
            yield return ($"touch: {expected} once on release", during == null && after == expected && g.Feed(Lift, 1140, true) == null);
        }
        foreach (string kind in new[] { "tap", "diagonal", "click", "two fingers", "changed contact", "focus lost", "gap", "too fast", "too slow", "reversal", "attach mid touch", "reset mid touch" })
        {
            var g = new TouchpadGestures();
            if (kind != "attach mid touch") g.Feed(Lift, 1000, true);
            g.Feed(Frame(600, 500), 1010, true);
            if (kind == "reset mid touch") g.Reset();
            SwipeDirection? result;
            if (kind == "too slow")
            {
                for (int i = 1; i <= 8; i++) g.Feed(Frame(600 + i * 60, 500), 1010 + i * 100, true);
                result = g.Feed(Lift, 1830, true);
            }
            else
            {
                int x = kind == "tap" ? 620 : 1200;
                int y = kind == "diagonal" ? 850 : 500;
                long time = kind == "gap" ? 1300 : kind == "too fast" ? 1020 : 1110;
                g.Feed(Frame(x, y, kind == "changed contact" ? 5 : 3, kind == "click", kind == "two fingers"), time, kind != "focus lost");
                if (kind == "reversal") g.Feed(Frame(700, 500), 1130, true);
                result = g.Feed(Lift, time + 20, true);
            }
            yield return ($"touch: rejects {kind}", result == null);
            // Every rejection must recover only after all fingers lift.
            g.Feed(Lift, 3000, true); g.Feed(Frame(600, 500), 3010, true); g.Feed(Frame(1200, 500), 3110, true);
            yield return ($"touch: recovers after {kind}", g.Feed(Lift, 3130, true) == SwipeDirection.Right);
        }
        {
            var g = new TouchpadGestures(); g.Feed(Lift, 1000, true);
            g.Feed(Frame(600, 500), 1010, true); g.Feed(Frame(1200, 500), 1110, true); g.Feed(Lift, 1130, true);
            g.Feed(Frame(1200, 500), 1150, true); g.Feed(Frame(600, 500), 1250, true);
            yield return ("touch: cooldown blocks accidental second swipe", g.Feed(Lift, 1270, true) == null);
            g.Feed(Lift, 1510, true); g.Feed(Frame(1200, 500), 1520, true); g.Feed(Frame(600, 500), 1620, true);
            yield return ("touch: later repeated left/right swipe works", g.Feed(Lift, 1640, true) == SwipeDirection.Left);
        }
        foreach (bool bt in new[] { false, true })
        {
            var bytes = Report(bt);
            bool ok = TouchpadReport.TryRead(bytes, bytes.Length, bt, out var frame);
            yield return ($"touch HID: {(bt ? "BT" : "USB")} packed coordinates and click", ok && frame.First == new TouchPoint(true, 7, 1919, 1079) && !frame.Second.Active && frame.Click);
            yield return ($"touch HID: {(bt ? "BT" : "USB")} short report rejected", !TouchpadReport.TryRead(bytes, bt ? 77 : 63, bt, out _));
            if (bt)
            {
                bytes[40] ^= 1;
                yield return ("touch HID: bad Bluetooth CRC rejected", !TouchpadReport.TryRead(bytes, bytes.Length, true, out _));
            }
            else
            {
                bytes[34] = 0x80; bytes[35] = 0x07; // x=1920, out of range
                yield return ("touch HID: out of range coordinates rejected", !TouchpadReport.TryRead(bytes, bytes.Length, false, out _));
            }
        }
        yield return ("touch HID: empty report rejected", !TouchpadReport.TryRead(Array.Empty<byte>(), 0, false, out _));
        yield return ("touch HID: oversized length rejected", !TouchpadReport.TryRead(new byte[1], 64, false, out _));
        yield return ("touch HID: BT basic has no touch data", !TouchpadReport.TryRead(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 10, true, out _));
        yield return ("touch: menu swipes switch tabs (right=next, left=previous, up/down nothing)", TouchpadControl.MenuActionFor(SwipeDirection.Right) == "tab_next" && TouchpadControl.MenuActionFor(SwipeDirection.Left) == "tab_prev" && TouchpadControl.MenuActionFor(SwipeDirection.Up) == null && TouchpadControl.MenuActionFor(SwipeDirection.Down) == null);
        yield return ("touch map: file format is locale-free integers", TouchpadControl.FormatMap(3, 2.5, -0.25, 0.1234) == "3|2500|-250|123");
        {
            var m = new MapGestures();
            m.Feed(Frame(960, 540), 1000); m.Feed(Frame(1500, 540), 1010);
            yield return ("touch map: one finger cannot pan an unzoomed map", m.Scale == 1 && m.Tx == 0 && m.Ty == 0);
            m.Feed(Lift, 1020);
            m.Feed(Two(760, 540, 1160, 540), 1030);
            bool changed = m.Feed(Two(560, 540, 1360, 540), 1040); // fingers twice as far apart around the centre
            yield return ("touch map: pinch out around the centre doubles the scale", changed && Math.Abs(m.Scale - 2) < 1e-6 && Math.Abs(m.Tx) < 1e-6 && Math.Abs(m.Ty) < 1e-6);
            m.Feed(Lift, 1050);
            m.Feed(Frame(960, 540), 1060); m.Feed(Frame(1152, 540), 1070); // +0.1 of the pad width
            yield return ("touch map: one finger drags the zoomed map", Math.Abs(m.Tx - 0.1) < 1e-6 && Math.Abs(m.Ty) < 1e-6);
            m.Feed(Lift, 1075); m.Feed(Frame(10, 540), 1077); m.Feed(Frame(1900, 540), 1080);
            yield return ("touch map: drag is clamped so the map covers its frame", Math.Abs(m.Tx - 0.5) < 1e-6);
            m.Feed(Two(1900, 540, 1700, 540, 4), 1090); // second finger lands: re-anchor, no jump
            yield return ("touch map: a landing second finger does not jump", Math.Abs(m.Tx - 0.5) < 1e-6 && Math.Abs(m.Scale - 2) < 1e-6);
            m.Feed(Two(1900, 540, 1838, 540, 4), 1100); // pinch in hard
            yield return ("touch map: zoom never goes below 1 and recentres", m.Scale == 1 && m.Tx == 0 && m.Ty == 0);
            m.Feed(Lift, 1110); m.Feed(Two(900, 540, 1020, 540), 1120); m.Feed(Two(0, 540, 1919, 540), 1130);
            yield return ("touch map: zoom is capped", m.Scale == MapGestures.MaxScale);
            m.ResetView();
            yield return ("touch map: new map screen starts from identity", m.Scale == 1 && m.Tx == 0 && m.Ty == 0);
        }
        yield return ("touch: four requested action bindings", TouchpadControl.ActionFor(SwipeDirection.Left) == "names" && TouchpadControl.ActionFor(SwipeDirection.Right) == "map" && TouchpadControl.ActionFor(SwipeDirection.Up) == "journal" && TouchpadControl.ActionFor(SwipeDirection.Down) == "quicksave");
    }

    static byte[] Report(bool bt)
    {
        byte[] bytes = new byte[bt ? 78 : 64];
        bytes[0] = bt ? (byte)0x31 : (byte)1;
        int p = bt ? 34 : 33;
        bytes[p] = 7; bytes[p + 1] = 0x7f; bytes[p + 2] = 0x77; bytes[p + 3] = 0x43; // x=0x77f=1919, y=0x437=1079
        bytes[p + 4] = 0x80;
        bytes[bt ? 11 : 10] = 2;
        if (bt)
        {
            // Independent bitwise CRC over seed + bytes, as used by the Windows/Linux HID protocol.
            uint crc = 0xffffffff;
            foreach (byte b in new byte[] { 0xa1 }.Concat(bytes.Take(74)))
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(74), ~crc);
        }
        return bytes;
    }
}
