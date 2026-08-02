using System.Net;
using System.Text;
using System.Text.Json;
using CupriNet.Hosting;
using Microsoft.Extensions.Logging;

namespace CupriNet.Lodestar;

/// <summary>
/// A tiny read-only status page for a Lodestar: it shows the node's current connection link and a QR code. The
/// page loads its values once from a small JSON endpoint (no auto-refresh, no client-side library) — reload the
/// browser to pick up a regenerated link. Built on the BCL <see cref="HttpListener"/> (no ASP.NET Core). HTTP only
/// by design; put a reverse proxy in front for TLS. The link is served from <see cref="LodestarLinkProvider"/>, so
/// it is cached, not minted per request.
///
/// When a Tor face is configured (<see cref="RunAsync"/> with a tor-face port that an onion forwards to), requests
/// arriving on that port are served an ONION-ONLY link, while the clearnet port is served a CLEARNET-ONLY link — so
/// a visitor reaching the page over Tor is never handed the node's clearnet IP.
/// </summary>
public sealed class LodestarWebServer
{
    private readonly LodestarLinkProvider _links;
    private readonly CupriNode _node;
    private readonly string _network;
    private readonly int _refreshSeconds;
    private readonly LinkTransports _clearnetFace;
    private readonly int? _torFacePort;
    private readonly ILogger _log;

    /// <summary>The status page's own <c>.onion</c> address, once published — shown on the clearnet page so operators can find it.</summary>
    public string? TorPageAddress { get; set; }

    public LodestarWebServer(
        LodestarLinkProvider links, CupriNode node, string network, int refreshSeconds,
        LinkTransports clearnetFace, int? torFacePort, ILogger log)
    {
        _links = links;
        _node = node;
        _network = network;
        _refreshSeconds = Math.Max(5, refreshSeconds);
        _clearnetFace = clearnetFace;
        _torFacePort = torFacePort;
        _log = log;
    }

    public async Task RunAsync(string listenAddress, int port, CancellationToken ct)
    {
        var host = listenAddress is "0.0.0.0" or "any" or "*" or "+" or "" ? "+" : listenAddress;
        var clearnetPrefix = $"http://{host}:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(clearnetPrefix);
        if (_torFacePort is int torPort)
            listener.Prefixes.Add($"http://127.0.0.1:{torPort}/"); // the web onion forwards here; localhost-only

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            _log.LogError(ex,
                "Could not start the status page on {Prefix}. On Windows a wildcard bind needs an elevated " +
                "process or a urlacl (netsh http add urlacl url={Prefix} user=…); in Docker/Linux this is not needed.",
                clearnetPrefix, clearnetPrefix);
            return;
        }

        _log.LogInformation("Status page serving on {Prefix} (HTTP only — use a reverse proxy for TLS).", clearnetPrefix);
        using var reg = ct.Register(() => { try { listener.Stop(); } catch { /* stopping */ } });

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Status page accept failed; continuing.");
                continue;
            }

            try { await HandleAsync(context).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "Status page request failed."); }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        // The face is decided by which local port the request arrived on: the tor-face port (fed by the web onion)
        // gets an onion-only link; the clearnet port gets the clearnet face.
        var overTor = _torFacePort is int tfp && context.Request.LocalEndPoint?.Port == tfp;
        var transports = overTor ? LinkTransports.OnionOnly : _clearnetFace;

        var path = context.Request.Url?.AbsolutePath ?? "/";
        switch (path)
        {
            case "/":
            case "/index.html":
                await WriteAsync(context, 200, "text/html; charset=utf-8", Page).ConfigureAwait(false);
                break;
            case "/state":
                await WriteAsync(context, 200, "application/json; charset=utf-8", StateJson(transports, overTor)).ConfigureAwait(false);
                break;
            default:
                await WriteAsync(context, 404, "text/plain; charset=utf-8", "not found").ConfigureAwait(false);
                break;
        }
    }

    private string StateJson(LinkTransports transports, bool overTor)
    {
        var snapshot = _links.Current(transports);
        return JsonSerializer.Serialize(new
        {
            network = _network,
            sigil = Convert.ToHexStringLower(_node.Identity.Sigil.Span),
            link = snapshot.Link,
            qr = snapshot.QrDataUri,
            face = overTor ? "tor" : "clearnet",
            // Only advertise the page's own .onion to the clearnet face — the Tor face is already on it.
            torAddress = overTor ? null : TorPageAddress,
            generatedAt = snapshot.GeneratedAt,
            refreshSeconds = _refreshSeconds,
        });
    }

    private static async Task WriteAsync(HttpListenerContext context, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    // A single self-contained page: inline CSS + a few lines of vanilla JS that poll /state and swap the link + QR
    // in place. No external requests, no libraries.
    private const string Page = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>CupriNet Lodestar</title>
          <style>
            :root { --bg:#0f1216; --card:#171b21; --edge:#262c34; --ink:#e6e9ee; --dim:#8a93a0; --copper:#c8813f; --purple:#8a6fd6; }
            * { box-sizing: border-box; }
            body { margin:0; min-height:100vh; display:grid; place-items:center; background:
              radial-gradient(1200px 600px at 50% -10%, #1b2230 0, transparent 60%), var(--bg);
              color:var(--ink); font:15px/1.5 system-ui,Segoe UI,Roboto,Helvetica,Arial,sans-serif; padding:24px; }
            .card { width:min(520px,100%); background:var(--card); border:1px solid var(--edge); border-radius:16px;
              padding:28px; box-shadow:0 10px 40px rgba(0,0,0,.35); }
            .top { display:flex; align-items:center; justify-content:space-between; gap:10px; }
            h1 { margin:0; font-size:20px; font-weight:650; letter-spacing:.2px; }
            h1 .a { color:var(--copper); }
            .badge { font-size:11px; font-weight:600; padding:3px 9px; border-radius:999px; border:1px solid var(--edge); color:var(--dim); white-space:nowrap; }
            .badge.tor { color:#cbb9ff; border-color:var(--purple); }
            .badge.clearnet { color:#f0c08a; border-color:var(--copper); }
            .net { margin:2px 0 20px; color:var(--dim); font-size:13px; }
            .net b { color:var(--ink); font-weight:600; }
            .qrwrap { display:grid; place-items:center; margin:6px 0 20px; }
            .qr { width:220px; height:220px; image-rendering:pixelated; background:#fff; border-radius:12px; padding:10px; }
            .linkrow { display:flex; gap:8px; align-items:stretch; }
            .link { flex:1; min-width:0; overflow-wrap:anywhere; background:#0e1116; border:1px solid var(--edge);
              border-radius:10px; padding:10px 12px; font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;
              font-size:12.5px; color:#cdd3db; user-select:all; min-height:44px; }
            .copy { flex:0 0 auto; border:1px solid var(--edge); background:#0e1116; color:var(--ink); border-radius:10px;
              padding:0 16px; cursor:pointer; font-size:13px; transition:background .15s,border-color .15s; }
            .copy:hover { background:#1d222a; border-color:var(--copper); }
            .foot { margin:18px 0 0; color:var(--dim); font-size:12px; }
            .foot code { color:#aeb6c1; }
            .tor { margin:14px 0 0; padding:10px 12px; border:1px dashed var(--purple); border-radius:10px; display:none; }
            .tor.show { display:block; }
            .tor .lbl { color:#cbb9ff; font-size:12px; }
            .tor code { display:block; overflow-wrap:anywhere; color:#dcd3f5; font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace; font-size:12px; user-select:all; margin-top:3px; }
          </style>
        </head>
        <body>
          <main class="card">
            <div class="top">
              <h1>CupriNet <span class="a">Lodestar</span></h1>
              <span id="face" class="badge">…</span>
            </div>
            <p class="net">network <b id="net">…</b></p>
            <div class="qrwrap"><img id="qr" class="qr" alt="connection QR code"></div>
            <div class="linkrow">
              <code id="link" class="link">generating…</code>
              <button id="copy" class="copy" type="button">Copy</button>
            </div>
            <div id="tor" class="tor">
              <span class="lbl">This page over Tor (hands out an onion-only link):</span>
              <code id="toraddr"></code>
            </div>
            <p class="foot">node <code id="sigil">…</code></p>
          </main>
          <script>
            const $ = id => document.getElementById(id);
            async function load(){
              try {
                const s = await (await fetch('state', { cache:'no-store' })).json();
                if (s.qr) $('qr').src = s.qr;
                $('link').textContent = s.link;
                $('net').textContent = s.network;
                $('sigil').textContent = (s.sigil || '').slice(0,16) + '…';
                const face = $('face'); face.textContent = s.face === 'tor' ? 'via Tor · onion-only' : 'clearnet';
                face.className = 'badge ' + (s.face === 'tor' ? 'tor' : 'clearnet');
                if (s.torAddress) { $('toraddr').textContent = 'http://' + s.torAddress + '/'; $('tor').classList.add('show'); }
                else $('tor').classList.remove('show');
              } catch (e) { /* keep the placeholder values */ }
            }
            $('copy').onclick = () => {
              const t = $('link').textContent, b = $('copy');
              const done = () => { b.textContent = 'Copied'; setTimeout(() => b.textContent = 'Copy', 1200); };
              if (navigator.clipboard) { navigator.clipboard.writeText(t).then(done, () => fallback(t, done)); }
              else fallback(t, done);
            };
            function fallback(t, done){ const a=document.createElement('textarea'); a.value=t; document.body.appendChild(a); a.select();
              try { document.execCommand('copy'); done(); } catch(e){} a.remove(); }
            load();
          </script>
        </body>
        </html>
        """;
}
