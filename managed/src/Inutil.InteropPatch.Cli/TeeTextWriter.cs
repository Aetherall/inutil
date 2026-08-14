using System.Text;

namespace Inutil.InteropPatch.Cli;

// Write once, land twice. Used for --log: the pre-launch caller hosts this assembly inside its own process, so
// "stdout" is whatever that process's stdout is — under a wine-allocated console, nobody. The file copy is what
// makes the run readable afterwards; stdout still receives every line, so a caller that DOES have a real pipe
// loses nothing.
//
// Only the two Write primitives are overridden: every other TextWriter.Write/WriteLine overload funnels through
// them, so there is no per-overload list to keep in step with the base class.
internal sealed class TeeTextWriter : TextWriter
{
    readonly TextWriter _a, _b;

    public TeeTextWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }

    public override Encoding Encoding => _a.Encoding;

    public override void Write(char value) { _a.Write(value); _b.Write(value); }

    public override void Write(string? value) { _a.Write(value); _b.Write(value); }

    public override void Flush() { _a.Flush(); _b.Flush(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _b.Flush(); _b.Dispose(); } catch { } }   // never dispose _a: it is Console.Out
        base.Dispose(disposing);
    }
}
