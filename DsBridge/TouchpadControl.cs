using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DsBridge;

/// <summary>Shares run's existing HID reader. Only the foreground game can receive commands.</summary>
public sealed class TouchpadControl
{
    readonly TouchpadGestures gestures = new();
    readonly MapGestures map = new();
    readonly ConcurrentQueue<(SwipeDirection Direction, long Time)> pending = new();
    readonly string commandPath, statusPath, mapPath, token = Guid.NewGuid().ToString("N");
    readonly string processName;
    readonly Action<string> log;
    int sequence;
    long sentAt;
    string outstanding = "";
    long nextStatusRead, nextForegroundRead;
    bool foregroundCached;
    int mode; // 0 = no fresh status, 1 = world, 2 = menu, 3 = map (written by the game's touchpad extension)
    int mapEpoch;
    bool mapDirty;
    long nextMapWrite;
    string[] status = Array.Empty<string>();
    long lastGoodAt;

    public TouchpadControl(string modDir, string processName, Action<string> log)
    {
        commandPath = Path.Combine(modDir, "DsB_TouchCommand.txt");
        statusPath = Path.Combine(modDir, "DsB_TouchStatus.txt");
        mapPath = Path.Combine(modDir, "DsB_TouchMap.txt");
        this.processName = processName; this.log = log;
        log("touchpad: world: right=map, left=toggle nearby names, up=journal, down=quicksave; menu: right=next tab, left=previous tab; map: drag + pinch zoom (foreground game only)");
    }

    public void Reset()
    {
        // Called before a replacement HID reader starts; the old reader has been cancelled.
        lock (gestures) { gestures.Reset(); map.Release(); }
        while (pending.TryDequeue(out _)) { }
    }

    public void OnReport(byte[] report, int length, bool bluetooth)
    {
        int m = Volatile.Read(ref mode);
        bool foreground = GameIsForeground() && m != 0;
        lock (gestures)
        {
            if (!TouchpadReport.TryRead(report, length, bluetooth, out var frame)) { gestures.Reset(); map.Release(); return; }
            if (m == 3)
            {
                // Map open: the pad drives the map view; single-finger swipes are not commands here.
                gestures.Reset();
                if (foreground && map.Feed(frame, Environment.TickCount64)) mapDirty = true;
                else if (!foreground) map.Release();
                return;
            }
            map.Release();
            var direction = gestures.Feed(frame, Environment.TickCount64, foreground);
            if (direction is { } d && pending.Count < 8) pending.Enqueue((d, Environment.TickCount64));
        }
    }

    public void Tick()
    {
        long now = Environment.TickCount64;
        if (now >= nextStatusRead)
        {
            nextStatusRead = now + 75;
            try
            {
                status = File.Exists(statusPath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(statusPath)).TotalMilliseconds < 600
                    ? File.ReadAllText(statusPath).Trim().Split('|') : Array.Empty<string>();
            }
            catch (IOException) { status = Array.Empty<string>(); }
            catch (UnauthorizedAccessException) { status = Array.Empty<string>(); }
            int newMode = status.Length == 4 && int.TryParse(status[0], out int stamp) && stamp >= 0
                ? status[1] switch { "world" => 1, "menu" => 2, "map" => 3, _ => 0 } : 0;
            // v50 (user 21.11.2026: "after a pinch zoom, dragging sometimes snaps the map back to the normal size"; log: a
            // second "map open" with no command and no open=0 from the game): the status file was read while the game was
            // rewriting it (empty / partial) or a frame hitch made it briefly stale -> mode 0 for one read -> back to map ->
            // ResetView. A blip of "no status" now keeps the last known mode (not the stale status: commands wait) for up to 1.5 s; only a real
            // "world" / "menu" from the game, or a long silence, ends the map.
            if (newMode != 0) lastGoodAt = now;
            else if (Volatile.Read(ref mode) != 0 && now - lastGoodAt < 1500) newMode = Volatile.Read(ref mode);
            if (newMode == 3 && Volatile.Read(ref mode) != 3)
            {
                // A new map screen starts from the game's own view; the epoch lets the game ignore older files.
                lock (gestures) { map.ResetView(); mapDirty = true; }
                mapEpoch++;
                log("touchpad: map open - drag to move, two fingers to zoom");
            }
            Volatile.Write(ref mode, newMode);
            if (outstanding.Length > 0 && status.Length == 4 && status[3] == outstanding)
            { log("touchpad: game consumed command " + outstanding); outstanding = ""; }
        }
        if (outstanding.Length > 0 && Environment.TickCount64 - sentAt > 1500)
        { log("touchpad: game acknowledgement timed out; command is not retried"); outstanding = ""; }
        if (Volatile.Read(ref mode) == 3 && now >= nextMapWrite)
        {
            string? line = null;
            lock (gestures)
            {
                if (mapDirty)
                {
                    mapDirty = false;
                    line = FormatMap(mapEpoch, map.Scale, map.Tx, map.Ty);
                }
            }
            if (line != null)
            {
                nextMapWrite = now + 33;
                try
                {
                    var tmp = mapPath + ".tmp";
                    File.WriteAllText(tmp, line, new UTF8Encoding(false));
                    File.Move(tmp, mapPath, true);
                }
                catch (IOException) { lock (gestures) mapDirty = true; }
                catch (UnauthorizedAccessException) { lock (gestures) mapDirty = true; }
            }
        }
        // v48/v49: non-touch requests (mic blow -> whistle): world mode only, game in front, one command in flight.
        // v49 (game 21.11: 5 blows recognised, none reached the game, no reason logged): a request waits up to 1.5 s for
        // the conditions and every drop is logged with its reason.
        int nreq = requests.Count;
        for (int qi = 0; qi < nreq && requests.TryDequeue(out var req); qi++)
        {
            int m = Volatile.Read(ref mode);
            string? wait = !GameIsForeground() ? "game not in front"
                : m != 1 ? (m == 0 ? "no fresh status from the game's touchpad extension" : m == 2 ? "a menu is open" : "the map is open")
                : status.Length != 4 || !int.TryParse(status[0], out int sgms) || sgms < 0 ? "status unreadable"
                : outstanding.Length > 0 && status[3] != outstanding ? "previous command still pending" : null;
            if (wait == null) { Send(req.Action, int.Parse(status[0]), "request"); continue; }
            if (Environment.TickCount64 - req.Time > 1500) { log($"touchpad: {req.Action} dropped - {wait}"); continue; }
            requests.Enqueue(req);
        }
        while (pending.TryDequeue(out var swipe))
        {
            if (Environment.TickCount64 - swipe.Time > 350 || !GameIsForeground()) continue;
            try
            {
                int m = Volatile.Read(ref mode);
                if (m == 0)
                { log("touchpad: waiting for the game's touchpad extension"); continue; }
                if (status.Length != 4 || !int.TryParse(status[0], out int gameMs) || gameMs < 0) continue;
                var action = m == 1 ? ActionFor(swipe.Direction) : m == 2 ? MenuActionFor(swipe.Direction) : null;
                if (action == null) continue;
                if (outstanding.Length > 0 && status[3] != outstanding) { log("touchpad: previous command still pending; swipe skipped"); continue; }
                Send(action, gameMs, swipe.Direction.ToString());
            }
            catch (IOException ex) { log("touchpad command: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { log("touchpad command: " + ex.Message); }
        }
    }

    readonly ConcurrentQueue<(string Action, long Time)> requests = new();

    /// <summary>Queue a world-mode command from another input (v48: the pad microphone's blow -> "whistle").</summary>
    public void Request(string action) { if (requests.Count < 4) requests.Enqueue((action, Environment.TickCount64)); }

    void Send(string action, int gameMs, string from)
    {
        try
        {
            string key = token + ":" + (++sequence);
            var tmp = commandPath + ".tmp";
            File.WriteAllText(tmp, $"{key}|{gameMs}|{action}", new UTF8Encoding(false));
            File.Move(tmp, commandPath, true);
            outstanding = key; sentAt = Environment.TickCount64;
            log($"touchpad: {from} -> {action} (requested)");
        }
        catch (IOException ex) { log("touchpad command: " + ex.Message); }
        catch (UnauthorizedAccessException ex) { log("touchpad command: " + ex.Message); }
    }

    public static string ActionFor(SwipeDirection direction) => direction switch
    {
        SwipeDirection.Right => "map", SwipeDirection.Left => "names",
        SwipeDirection.Up => "journal", SwipeDirection.Down => "quicksave",
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    /// <summary>Menus (user 17.11.2026, inverted after the game test): the swipe direction is the direction the tab
    /// selection moves - right = next tab (to the right), left = previous tab (to the left).</summary>
    public static string? MenuActionFor(SwipeDirection direction) => direction switch
    {
        SwipeDirection.Right => "tab_next", SwipeDirection.Left => "tab_prev", _ => null
    };

    /// <summary>Map file for the game: epoch|scale*1000|tx*1000|ty*1000 (integers: the game parses them without locale).</summary>
    public static string FormatMap(int epoch, double scale, double tx, double ty) =>
        $"{epoch}|{(int)Math.Round(scale * 1000)}|{(int)Math.Round(tx * 1000)}|{(int)Math.Round(ty * 1000)}";

    bool GameIsForeground()
    {
        lock (this)
        {
            long now = Environment.TickCount64;
            if (now < nextForegroundRead) return foregroundCached;
            nextForegroundRead = now + 40;
            return foregroundCached = ReadForeground();
        }
    }

    bool ReadForeground()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out uint id);
        try { using var process = Process.GetProcessById((int)id); return string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);
}
