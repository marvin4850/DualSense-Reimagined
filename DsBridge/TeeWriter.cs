using System.Text;

namespace DsBridge;

/// <summary>Console output to the window AND a file (AutoFlush, shared read): `run --record x.jsonl` -> x.log.</summary>
sealed class TeeWriter : TextWriter
{
    readonly TextWriter _a; readonly StreamWriter _b; readonly object _l = new();
    public TeeWriter(TextWriter a, string path)
    {
        _a = a;
        _b = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
    }
    public override Encoding Encoding => _a.Encoding;
    public override void Write(char value) { lock (_l) { _a.Write(value); try { _b.Write(value); } catch { } } }
    public override void Write(string? value) { lock (_l) { _a.Write(value); try { _b.Write(value); } catch { } } }
    public override void WriteLine(string? value) { lock (_l) { _a.WriteLine(value); try { _b.WriteLine(value); } catch { } } }
    public override void Flush() { lock (_l) { _a.Flush(); try { _b.Flush(); } catch { } } }
}
