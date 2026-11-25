namespace DsBridge;

public enum SwipeDirection { Left, Right, Up, Down }
public readonly record struct TouchPoint(bool Active, int Id, int X, int Y);
public readonly record struct TouchFrame(TouchPoint First, TouchPoint Second, bool Click);

/// <summary>DualSense native HID input; no writes, virtual controller or button remapping.</summary>
public static class TouchpadReport
{
    // Layout verified against Sony's Linux hid-playstation driver (see TOUCHPAD.md).
    public static bool TryRead(byte[] report, int length, bool bluetooth, out TouchFrame frame)
    {
        frame = default;
        if (length < 0 || length > report.Length) return false;
        int start;
        if (!bluetooth && length >= 64 && report[0] == 0x01) start = 33;
        else if (bluetooth && length >= 78 && report[0] == 0x31)
        {
            uint crc = Update(0xffffffff, 0xa1);
            for (int i = 0; i < 74; i++) crc = Update(crc, report[i]);
            if (~crc != System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(74, 4))) return false;
            start = 34;
        }
        else return false; // BT basic 0x01 has no touch coordinates.
        TouchPoint Read(int p) => new((report[p] & 0x80) == 0, report[p] & 0x7f,
            report[p + 1] | ((report[p + 2] & 0x0f) << 8), (report[p + 2] >> 4) | (report[p + 3] << 4));
        var a = Read(start); var b = Read(start + 4);
        if ((a.Active && (a.X >= 1920 || a.Y >= 1080)) || (b.Active && (b.X >= 1920 || b.Y >= 1080))) return false;
        frame = new(a, b, (report[bluetooth ? 11 : 10] & 2) != 0);
        return true;
    }

    static uint Update(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        return crc;
    }
}

/// <summary>One single-finger swipe on release; a click, second finger, lost focus or gap cancels until all fingers lift.</summary>
public sealed class TouchpadGestures
{
    bool armed, tracking;
    int contact;
    double startX, startY, lastX, lastY, path;
    long started, lastReport, lastSwipe = long.MinValue / 2;

    public void Reset() { armed = tracking = false; lastReport = 0; }

    public SwipeDirection? Feed(TouchFrame frame, long now, bool enabled)
    {
        if (!enabled) { Reset(); return null; }
        if (lastReport != 0 && (now < lastReport || now - lastReport > 150)) Reset();
        lastReport = now;
        int count = (frame.First.Active ? 1 : 0) + (frame.Second.Active ? 1 : 0);
        if (frame.Click || count > 1) { armed = tracking = false; return null; }
        if (count == 0)
        {
            SwipeDirection? result = null;
            if (tracking)
            {
                double dx = lastX - startX, dy = lastY - startY;
                double ax = Math.Abs(dx), ay = Math.Abs(dy), distance = Math.Sqrt(dx * dx + dy * dy);
                long duration = now - started;
                if (duration is >= 60 and <= 700 && now - lastSwipe >= 350 && path > 0 && distance / path >= 0.75)
                {
                    if (ax >= 0.18 && ax >= ay * 1.5) result = dx > 0 ? SwipeDirection.Right : SwipeDirection.Left;
                    else if (ay >= 0.18 && ay >= ax * 1.5) result = dy > 0 ? SwipeDirection.Down : SwipeDirection.Up;
                }
            }
            armed = true; tracking = false;
            if (result != null) lastSwipe = now;
            return result;
        }
        if (!armed) return null;
        var point = frame.First.Active ? frame.First : frame.Second;
        double x = point.X / 1920.0, y = point.Y / 1080.0;
        if (!tracking)
        {
            tracking = true; contact = point.Id; started = now;
            startX = lastX = x; startY = lastY = y; path = 0;
        }
        else if (point.Id != contact) { armed = tracking = false; }
        else
        {
            path += Math.Sqrt(Math.Pow(x - lastX, 2) + Math.Pow(y - lastY, 2));
            lastX = x; lastY = y;
        }
        return null;
    }
}

/// <summary>Map view (user 17.11.2026, like Google Maps on a phone): one finger drags the map, two fingers pinch-zoom around
/// their midpoint (and drag with it). State = render transform of the game's map widget in widget units, pivot at the centre:
/// screen = 0.5 + (p - 0.5) * Scale + (Tx, Ty). The map always covers its frame: Scale 1..MaxScale, |T| &lt;= (Scale - 1) / 2.
/// A pressed touchpad (click) or a gap/reset re-anchors without a jump; nothing is sent to the game from here.</summary>
public sealed class MapGestures
{
    public const double MaxScale = 4.0;
    public double Scale { get; private set; } = 1.0;
    public double Tx { get; private set; }
    public double Ty { get; private set; }
    int idA = -1, idB = -1, count;
    double ax, ay, bx, by;
    long lastReport;

    public void ResetView() { Scale = 1.0; Tx = Ty = 0; Release(); }
    public void Release() { count = 0; idA = idB = -1; lastReport = 0; }

    /// <summary>Returns true when the view changed.</summary>
    public bool Feed(TouchFrame frame, long now)
    {
        if (lastReport != 0 && (now < lastReport || now - lastReport > 150)) Release();
        lastReport = now;
        var pts = new List<TouchPoint>(2);
        if (frame.First.Active) pts.Add(frame.First);
        if (frame.Second.Active) pts.Add(frame.Second);
        if (frame.Click || pts.Count == 0) { count = 0; idA = idB = -1; return false; }
        double X(TouchPoint p) => p.X / 1920.0; double Y(TouchPoint p) => p.Y / 1080.0;
        // Any change of the finger set re-anchors: no jump when a second finger lands or lifts.
        bool same = pts.Count == count && pts[0].Id == idA && (count == 1 || pts[1].Id == idB);
        if (!same)
        {
            count = pts.Count; idA = pts[0].Id; ax = X(pts[0]); ay = Y(pts[0]);
            if (count == 2) { idB = pts[1].Id; bx = X(pts[1]); by = Y(pts[1]); } else idB = -1;
            return false;
        }
        double s0 = Scale, tx0 = Tx, ty0 = Ty;
        if (count == 1)
        {
            double nx = X(pts[0]), ny = Y(pts[0]);
            Tx += nx - ax; Ty += ny - ay; ax = nx; ay = ny;
        }
        else
        {
            double nax = X(pts[0]), nay = Y(pts[0]), nbx = X(pts[1]), nby = Y(pts[1]);
            double d0 = Math.Sqrt(Math.Pow(bx - ax, 2) + Math.Pow(by - ay, 2));
            double d1 = Math.Sqrt(Math.Pow(nbx - nax, 2) + Math.Pow(nby - nay, 2));
            double fx0 = (ax + bx) / 2, fy0 = (ay + by) / 2, fx1 = (nax + nbx) / 2, fy1 = (nay + nby) / 2;
            if (d0 > 0.03 && d1 > 0.03)
            {
                double s1 = Math.Clamp(Scale * d1 / d0, 1.0, MaxScale);
                // Keep the map point under the old midpoint under the new midpoint.
                Tx = fx1 - 0.5 - (fx0 - 0.5 - Tx) * s1 / Scale;
                Ty = fy1 - 0.5 - (fy0 - 0.5 - Ty) * s1 / Scale;
                Scale = s1;
            }
            ax = nax; ay = nay; bx = nbx; by = nby;
        }
        double lim = (Scale - 1) / 2;
        Tx = Math.Clamp(Tx, -lim, lim); Ty = Math.Clamp(Ty, -lim, lim);
        return Math.Abs(Scale - s0) > 1e-4 || Math.Abs(Tx - tx0) > 1e-4 || Math.Abs(Ty - ty0) > 1e-4;
    }
}
