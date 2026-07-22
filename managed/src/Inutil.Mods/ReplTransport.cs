// Inutil.Repl transport internals — the two pieces every REPL transport shares, factored to ONE home so the
// three faces (ReplServer telnet, ReplMcpServer SSE-MCP, ReplHttpServer plain-HTTP) cannot drift:
//
//   • ReplDispatch.EvalOnMain — the main-thread dispatch contract (docs/contribution/architecture/18-repl.md):
//     a submission is POSTED to the main (il2cpp-attached) thread via MainThread.Post, so proxy touches run
//     where il2cpp requires and the §7.12 deadlock guard applies; the BACKGROUND transport thread blocks on the
//     result, the main thread never blocks. Not-pumping-yet fails loud instead of queueing into a dead pump.
//
//   • MiniHttp — the minimal HTTP/1.1 request reader (header block to CRLFCRLF + Content-Length body) and the
//     bare status writer, hand-rolled over the raw NetworkStream because System.Net.HttpListener needs http.sys,
//     which is unreliable under Wine (where the game runs).
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace Inutil.Repl;

internal static class ReplDispatch
{
    // Run one submission ON THE MAIN THREAD, dispatched from a background transport thread.
    internal static ReplResult EvalOnMain(ReplSession session, string code)
    {
        if (!Inutil.Host.MainThread.IsPumping)
            return ReplResult.Bad("the game is not pumping yet (no frame has run) — retry once it is live.");
        try { return Inutil.Host.MainThread.Post(() => session.Eval(code)).GetAwaiter().GetResult(); }
        catch (Exception ex) { return ReplResult.Bad($"{ex.GetType().Name}: {ex.Message}"); }
    }
}

internal sealed class MiniHttpRequest
{
    public string Method = "", Path = "", Query = "", Body = "";

    public string? QueryValue(string key)
    {
        foreach (string part in Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part.Substring(0, eq) == key) return Uri.UnescapeDataString(part.Substring(eq + 1));
        }
        return null;
    }
}

internal static class MiniHttp
{
    internal static MiniHttpRequest? ReadRequest(NetworkStream stream)
    {
        var header = new List<byte>(1024);
        int b, matched = 0;                                   // read until CR LF CR LF
        while (matched < 4 && (b = stream.ReadByte()) != -1)
        {
            header.Add((byte)b);
            matched = (b == (matched % 2 == 0 ? '\r' : '\n')) ? matched + 1 : (b == '\r' ? 1 : 0);
        }
        if (header.Count == 0) return null;

        string text = Encoding.ASCII.GetString(header.ToArray());
        string[] lines = text.Split("\r\n");
        string[] start = lines[0].Split(' ');
        if (start.Length < 2) return null;

        var req = new MiniHttpRequest { Method = start[0] };
        string target = start[1];
        int q = target.IndexOf('?');
        req.Path = q < 0 ? target : target.Substring(0, q);
        req.Query = q < 0 ? "" : target.Substring(q + 1);

        int contentLength = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            int c = lines[i].IndexOf(':');
            if (c > 0 && lines[i].Substring(0, c).Trim().ToLowerInvariant() == "content-length")
                int.TryParse(lines[i].Substring(c + 1).Trim(), out contentLength);
        }
        if (contentLength > 0)
        {
            var buf = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = stream.Read(buf, read, contentLength - read);
                if (n <= 0) break;
                read += n;
            }
            req.Body = Encoding.UTF8.GetString(buf, 0, read);
        }
        return req;
    }

    internal static void WriteStatus(NetworkStream stream, int code, string reason)
    {
        byte[] resp = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {code} {reason}\r\nContent-Length: 0\r\nAccess-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\nAccess-Control-Allow-Headers: *\r\n\r\n");
        try { stream.Write(resp, 0, resp.Length); stream.Flush(); } catch { }
    }
}
