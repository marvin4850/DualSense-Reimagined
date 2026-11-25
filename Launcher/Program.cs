// GothicDualSense.exe - player launcher (release package, user decision 24.11.2026):
// "запустил GothicDualSense.exe, включается бридж и играешь". Normal start = no windows at all.
// A window only when something does not work: game not found / not started, mod files missing, no DualSense,
// DSX open, DsBridge stopped with an error. Everything else goes to logs\launcher.log.
//
// Package layout (tools/release.ps1):
//   GothicDualSense\GothicDualSense.exe     this launcher
//   GothicDualSense\bin\DsBridge.exe        bridge (self-contained)
//   GothicDualSense\haptics\...             rules + HD bank
//   GothicDualSense\mod\G1R_DualSenseBridge mod copy, synced into <game>\G1R\Mods on every start
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GothicDualSense;

static class Program
{
    const string Caption = "Gothic DualSense";
    const string GameExe = "G1R-Win64-Shipping.exe";
    const string ModName = "G1R_DualSenseBridge";
    static string _logPath = "";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    const uint MB_OK = 0, MB_YESNO = 4, MB_RETRYCANCEL = 5, MB_ICONERROR = 0x10, MB_ICONWARNING = 0x30, MB_TOPMOST = 0x40000, MB_SETFOREGROUND = 0x10000;
    const int IDYES = 6, IDRETRY = 4;
    static int Box(string text, uint type) => MessageBoxW(IntPtr.Zero, text, Caption, type | MB_TOPMOST | MB_SETFOREGROUND);
    static int Fail(string text) { Log("FAIL: " + text); Box(text, MB_OK | MB_ICONERROR); return 1; }

    static void Log(string s)
    {
        try { File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {s}{Environment.NewLine}", Encoding.UTF8); } catch { }
    }

    static int Main(string[] args)
    {
        string root = AppContext.BaseDirectory.TrimEnd('\\');
        string logDir = WritableLogDir(root);
        _logPath = Path.Combine(logDir, "launcher.log");
        TrimLogs(logDir);
        Log("---- start " + string.Join(' ', args));
        try { return Run(root, logDir, args); }
        catch (Exception ex) { return Fail("Непредвиденная ошибка: " + ex.Message + "\n\nПодробности: " + _logPath); }
    }

    static int Run(string root, string logDir, string[] args)
    {
        // 1. game
        string? game = FindGame(root, args);
        if (game == null)
            return Fail("Не найдена Gothic 1 Remake.\n\nРаспакуйте архив мода в папку игры (туда, где лежит " + GameExe + ").");
        Log("game: " + game);

        // 2. package files
        string bridge = Path.Combine(root, "bin", "DsBridge.exe");
        string haptics = Path.Combine(root, "haptics", "haptics.json");
        string modSrc = Path.Combine(root, "mod", ModName);
        if (!File.Exists(bridge) || !File.Exists(haptics))
            return Fail("Файлы мода повреждены или неполные (нет bin\\DsBridge.exe или haptics).\n\nСкачайте и распакуйте архив заново.");

        // 3. mod in the game's official Mods folder (synced: an update of the package updates the mod)
        string modDst = Path.Combine(game, "G1R", "Mods", ModName);
        if (Directory.Exists(modSrc))
        {
            try { int n = SyncDir(modSrc, modDst); if (n > 0) Log($"mod synced: {n} file(s) -> {modDst}"); }
            catch (Exception ex) { return Fail("Не удалось скопировать мод в папку игры:\n" + modDst + "\n\n" + ex.Message); }
        }
        if (!File.Exists(Path.Combine(modDst, "GothicMod.json")))
            return Fail("Мод не установлен в папку игры:\n" + modDst + "\n\nСкачайте и распакуйте архив заново.");

        // 4. DSX would be a second writer to the pad
        while (Process.GetProcessesByName("DSX").Length > 0)
        {
            if (Box("Запущен DSX — он мешает эффектам DualSense.\n\nЗакрыть DSX?", MB_YESNO | MB_ICONWARNING) != IDYES) { Log("DSX kept open -> exit"); return 0; }
            foreach (var p in Process.GetProcessesByName("DSX")) try { p.Kill(); p.WaitForExit(3000); } catch { }
            Thread.Sleep(1000);
        }

        bool gameRunning = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameExe)).Length > 0;

        // 5. a bridge already serves the pad (one writer per pad) -> only the game
        if (Mutex.TryOpenExisting(@"Local\G1R_DsBridge_PadWriter", out var m))
        {
            m.Dispose(); Log("DsBridge already running");
            return gameRunning ? 0 : StartGame(game) ? 0 : 1;
        }

        // 6. bridge first: it must find the pad before the game starts
        string journal = Path.Combine(logDir, $"play_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
        Process? br;
        while (true)
        {
            br = StartBridge(bridge, haptics, modDst, journal, out bool padOk);
            if (br == null) return 1;
            if (padOk) break;
            if (br.HasExited && br.ExitCode == 3) { Log("another pad writer appeared"); br = null; break; }
            int code = br.HasExited ? br.ExitCode : -1;
            if (code == 2)
            {
                if (Box("DualSense не найден.\n\nПодключите геймпад кабелем USB или по Bluetooth и нажмите «Повтор».", MB_RETRYCANCEL | MB_ICONWARNING) == IDRETRY) continue;
                Log("no pad, cancelled"); return 0;
            }
            return Fail($"DsBridge не запустился (код {code}).\n\nПодробности: {Path.ChangeExtension(journal, ".log")}");
        }

        // 7. the game
        if (!gameRunning && !StartGame(game)) { try { br?.Kill(); } catch { } return 1; }
        if (br == null) return 0;

        // 8. stay (hidden) until the bridge leaves with the game; an error exit is the only window from here on
        br.WaitForExit();
        int exit = br.ExitCode;
        Log("DsBridge exit " + exit);
        if (exit == 3) return Fail("Игра не запустилась за 3 минуты — эффекты DualSense выключены.\n\nПодробности: " + _logPath);
        if (exit != 0) return Fail($"Эффекты DualSense остановились из-за ошибки (код {exit}).\n\nПодробности: {Path.ChangeExtension(journal, ".log")}");
        return 0;
    }

    static Process? StartBridge(string exe, string haptics, string modDir, string journal, out bool padOk)
    {
        padOk = false;
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "run", "--exit-with-game", "--haptics", haptics, "--mod-dir", modDir, "--record", journal }) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi)!; }
        catch (Exception ex) { Fail("Не удалось запустить DsBridge:\n" + ex.Message + "\n\nВозможно, его заблокировал антивирус."); return null; }
        Log("DsBridge pid " + p.Id);
        // the bridge logs "transport=..." right after it opened the pad (exit 2 = no pad). Output is drained to the end,
        // otherwise a full pipe would block the bridge.
        var ready = new ManualResetEventSlim();
        bool sawPad = false;
        p.OutputDataReceived += (_, e) => { if (e.Data != null && e.Data.Contains("transport=")) { sawPad = true; ready.Set(); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Log("bridge stderr: " + e.Data); };
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) => ready.Set();
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        ready.Wait(TimeSpan.FromSeconds(20));
        padOk = sawPad && !p.HasExited;
        if (!sawPad && !p.HasExited) { Log("no pad line within 20 s, continuing"); padOk = true; }
        if (p.HasExited) p.WaitForExit();
        return p;
    }

    static bool StartGame(string game)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(game, GameExe)) { WorkingDirectory = game, UseShellExecute = false };
            psi.ArgumentList.Add("-Mods=" + ModName);
            Process.Start(psi);
            Log("game started");
            return true;
        }
        catch (Exception ex) { Fail("Не удалось запустить игру:\n" + ex.Message); return false; }
    }

    // ---- game location: --game PATH, then folders above the launcher, then GOG / Steam registry ----
    static string? FindGame(string root, string[] args)
    {
        static bool Ok(string? d) => !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, GameExe));
        int gi = Array.IndexOf(args, "--game");
        if (gi >= 0 && gi + 1 < args.Length && Ok(args[gi + 1])) return Path.GetFullPath(args[gi + 1]);
        for (var d = new DirectoryInfo(root); d != null; d = d.Parent) if (Ok(d.FullName)) return d.FullName;
        foreach (var c in RegistryCandidates()) if (Ok(c)) return c;
        return null;
    }

    static IEnumerable<string> RegistryCandidates()
    {
        var list = new List<string>();
        try
        {
            using var gog = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
            if (gog != null)
                foreach (var id in gog.GetSubKeyNames())
                {
                    using var k = gog.OpenSubKey(id);
                    var name = k?.GetValue("gameName") as string ?? k?.GetValue("GAMENAME") as string ?? "";
                    if (name.Contains("Gothic", StringComparison.OrdinalIgnoreCase) && name.Contains("Remake", StringComparison.OrdinalIgnoreCase)
                        && (k?.GetValue("path") ?? k?.GetValue("PATH")) is string pth) list.Add(pth);
                }
        }
        catch { }
        try
        {
            if (Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") is string steam)
            {
                var libs = new List<string> { steam };
                var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match mm in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"")) libs.Add(mm.Groups[1].Value.Replace(@"\\", @"\"));
                foreach (var l in libs) list.Add(Path.Combine(l, "steamapps", "common", "Gothic 1 Remake"));
            }
        }
        catch { }
        return list;
    }

    // ---- helpers ----
    /// <summary>Copies files that are missing or differ (size / bytes); returns how many were copied.</summary>
    static int SyncDir(string src, string dst)
    {
        int n = 0;
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(dst, Path.GetRelativePath(src, f));
            var fi = new FileInfo(t);
            if (fi.Exists && fi.Length == new FileInfo(f).Length && File.ReadAllBytes(t).AsSpan().SequenceEqual(File.ReadAllBytes(f))) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t, true); n++;
        }
        return n;
    }

    static string WritableLogDir(string root)
    {
        foreach (var d in new[] { Path.Combine(root, "logs"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GothicDualSense", "logs") })
        {
            try { Directory.CreateDirectory(d); var t = Path.Combine(d, ".w"); File.WriteAllText(t, ""); File.Delete(t); return d; } catch { }
        }
        return Path.GetTempPath();
    }

    static void TrimLogs(string dir)
    {
        try
        {
            foreach (var f in new DirectoryInfo(dir).GetFiles("play_*").OrderByDescending(f => f.LastWriteTimeUtc).Skip(40)) f.Delete();
            var lf = new FileInfo(Path.Combine(dir, "launcher.log"));
            if (lf.Exists && lf.Length > 1_000_000) lf.Delete();
        }
        catch { }
    }
}
