# CupriNet Lodestar

A **Lodestar** is a headless CupriNet node whose only job is to *keep the network alive*. It runs the
Layer‑1 **Concordance** overlay — gossiping, answering discovery/referrals, and carrying channel
**advertisements** (metadata *about* channels) — but it **never holds a channel session itself**. No Layer‑2
content ever flows through a Lodestar; the project doesn't even reference the channel (`Arcanum`) assembly, so
that guarantee is structural, not just conventional.

> *A Lodestar is the fixed point Wayfarers route by — always lit, always reachable.*

## What it does

- **Keeps the overlay healthy** — stays online, gossips, and relays L1 metadata so nodes can find each other.
- **Boots from seed links** — give it as many `cuprinet://intone/…` links as you like; it dials them all to
  join an existing network.
- **Remembers who it meets** — the keys (Sigils) of nodes it pairs with are written to a durable **hot path**
  and reloaded on every startup, so restarts are warm and it reconnects without needing the seed links again.
- **Can start a brand‑new network (genesis)** — with no seeds and no known peers it stands up its own network
  and prints a connection link (to console/logs and `lodestar.link`) for others to seed from.
- **Runs anywhere** — plain console (Windows/Ubuntu), a **Windows Service**, a **systemd** daemon, or **Docker**.

## Configuration

Settings bind from `appsettings.json` (the `Lodestar` section), then environment variables
(prefix `CUPRINET_LODESTAR_`), then command line — later wins.

| Setting | Env var | Default | Meaning |
|---|---|---|---|
| `Concordium` | `CUPRINET_LODESTAR_Concordium` | *(required)* | Which network this node serves. |
| `ListenAddress` | `CUPRINET_LODESTAR_ListenAddress` | `0.0.0.0` | Interface to bind. |
| `ListenPort` | `CUPRINET_LODESTAR_ListenPort` | `43820` | TCP port to listen on. |
| `PublicHost` | `CUPRINET_LODESTAR_PublicHost` | *(none)* | Reachable DNS/IP to advertise in this node's link. |
| `PublicPort` | `CUPRINET_LODESTAR_PublicPort` | = `ListenPort` | Port advertised with `PublicHost`. |
| `Moniker` | `CUPRINET_LODESTAR_Moniker` | *(none)* | Self-asserted display name advertised in this node's link + peer record (e.g. `Community Relay`). Carried **unverified** — a hint only; peers trust it solely by matching this node's fingerprint. |
| `AdvertisedAddresses` | `CUPRINET_LODESTAR_AdvertisedAddresses__0`, … | `[]` | Extra reachable `host` / `host:port` addresses to put in the link — for a bootstrap where the service has public IPs it can't discover itself (cloud NAT/LB, second NIC, DNS name). Added alongside `PublicHost`. |
| `EnableWeb` | `CUPRINET_LODESTAR_EnableWeb` | `false` | Serve a small read-only HTTP status page (link + QR, auto-refreshing). HTTP only — front with a reverse proxy for TLS. |
| `WebListenAddress` | `CUPRINET_LODESTAR_WebListenAddress` | `0.0.0.0` | Interface the status page binds to. |
| `WebPort` | `CUPRINET_LODESTAR_WebPort` | `8080` | Status-page TCP port. |
| `WebRefreshSeconds` | `CUPRINET_LODESTAR_WebRefreshSeconds` | `30` | How often the link is regenerated and the browser re-polls (cached between regenerations). |
| `WebSplit` | `CUPRINET_LODESTAR_WebSplit` | `true` | Dual‑stack only: serve a **clearnet‑only** link on the clearnet page and publish the page as its own `.onion` that serves a **Tor‑only** link, so a Tor visitor is never shown the clearnet IP. On by default (anti‑deanonymisation). Set `false` for a single all‑transports page. |
| `DataDirectory` | `CUPRINET_LODESTAR_DataDirectory` | per‑OS | Hot path: identity, master key, known peers. |
| `SeedLinks` | `CUPRINET_LODESTAR_SeedLinks__0`, … | `[]` | Seed links (array). |
| `SeedsFile` | `CUPRINET_LODESTAR_SeedsFile` | *(none)* | File of seed links, one per line (`#` comments ok). |
| `AllowedSubnets` | `CUPRINET_LODESTAR_AllowedSubnets__0`, … | `[]` | CIDR/netmask/IP ranges this node may connect to and accept from. An allow match beats a deny. |
| `DeniedSubnets` | `CUPRINET_LODESTAR_DeniedSubnets__0`, … | `[]` | Ranges to refuse (unless also allowed). |
| `EnableTor` | `CUPRINET_LODESTAR_EnableTor` | `false` | **Dual‑stack**: also publish a v3 `.onion` and accept/dial Tor peers *alongside* clearnet. The link carries both, so the node is reachable by clearnet **and** Tor peers. The `.onion` appears once Tor bootstraps. |
| `TorOnly` | `CUPRINET_LODESTAR_TorOnly` | `false` | Onion‑only (implies `EnableTor`): reach peers solely through Tor, hiding the node's IP. Clearnet settings don't apply. Restricts reachability to Tor peers — prefer dual‑stack unless anonymity is the goal. |
| `EnableFerryman` | `CUPRINET_LODESTAR_EnableFerryman` | `true` | Act as a **Ferryman** relay: broker hole punches between NAT'd peers (signaling only, never channel content). On by default — a reachable node is the ideal relay. No effect in onion‑only mode. |
| `EnablePortMapping` | `CUPRINET_LODESTAR_EnablePortMapping` | `false` | Ask the gateway (NAT‑PMP) to forward the port. |
| `EnableLanDiscovery` | `CUPRINET_LODESTAR_EnableLanDiscovery` | `false` | Announce/discover peers on the LAN. |
| `EnableCoverTraffic` | `CUPRINET_LODESTAR_EnableCoverTraffic` | `false` | Run anonymity cover traffic (extra bandwidth). |

**Private / LAN-only network.** Deny everything, then allow only your subnets (an allow-list match always wins).
For example, a LAN-only node:

```bash
-e CUPRINET_LODESTAR_DeniedSubnets__0="0.0.0.0/0" \
-e CUPRINET_LODESTAR_DeniedSubnets__1="::/0" \
-e CUPRINET_LODESTAR_AllowedSubnets__0="192.168.0.0/16" \
-e CUPRINET_LODESTAR_AllowedSubnets__1="10.0.0.0/8"
```

The node then only connects to and accepts from those ranges (the fence does not apply over Tor).

**Seed links** are additionally accepted from the `CUPRINET_LODESTAR_SEEDS` env var (`;`/`,`/newline separated)
and from repeated `--seed <link>` / `--seed=<link>` command‑line arguments.

## Running it

### Console (Windows or Ubuntu)

```bash
# from the repo root
dotnet run --project node/CupriNet.Lodestar -- \
  --Concordium example.chat --PublicHost lodestar.example.net \
  --seed "cuprinet://intone/AAA…" --seed "cuprinet://intone/BBB…"
```

Or run a published binary directly (`cuprinet-lodestar` / `cuprinet-lodestar.exe`). The node prints its own
connection link on startup — copy it to hand out to other nodes.

### Genesis (start your own network)

Just run it with a network name and no seeds:

```bash
CUPRINET_LODESTAR_Concordium=my.network dotnet run --project node/CupriNet.Lodestar
```

It logs `standing up a NEW network (genesis)` and prints the link others should seed from (also written to
`<DataDirectory>/lodestar.link`). Set `PublicHost` so that link is reachable off‑box.

### Windows Service

The Windows build (`lodestar-win-x64`) ships `install-service.ps1` / `uninstall-service.ps1`. From the extracted
folder, in an **elevated** PowerShell:

```powershell
.\install-service.ps1 -Concordium example.chat     # registers + starts the "CupriNetLodestar" service
# ...set any other CUPRINET_LODESTAR_* machine env vars, e.g. PublicHost, then restart the service
.\uninstall-service.ps1                            # stop + remove (keeps the data directory)
```

The app auto‑detects the service host (`AddWindowsService`), so lifetime and logging integrate with Windows
(logs go to the Application event log). Configure via machine `CUPRINET_LODESTAR_*` env vars or the
`appsettings.json` beside the exe; the node's link is written to `C:\ProgramData\CupriNet.Lodestar\lodestar.link`.

### systemd (Ubuntu)

See [`deploy/cuprinet-lodestar.service`](deploy/cuprinet-lodestar.service) for a ready unit (Type=notify,
`StateDirectory=` for the hot path, hardening). Then `systemctl enable --now cuprinet-lodestar` and follow with
`journalctl -u cuprinet-lodestar -f`.

### Docker

```bash
# build from the repo root (the Dockerfile needs src/ in context). The core pulls CupriMark from the
# Wixely GitHub Packages feed, so pass a read:packages token as a BuildKit secret:
PACKAGES_TOKEN=<your read:packages PAT> \
docker build --secret id=packages_token,env=PACKAGES_TOKEN \
  -f node/CupriNet.Lodestar/Dockerfile -t cuprinet-lodestar .

docker run -d --name lodestar \
  -p 43820:43820 \
  -p 8080:8080 \
  -v lodestar-data:/data \
  -e CUPRINET_LODESTAR_Concordium=example.chat \
  -e CUPRINET_LODESTAR_PublicHost=lodestar.example.net \
  -e CUPRINET_LODESTAR_AdvertisedAddresses__0=203.0.113.9 \
  -e CUPRINET_LODESTAR_AdvertisedAddresses__1=198.51.100.7:43820 \
  -e CUPRINET_LODESTAR_EnableWeb=true \
  -e CUPRINET_LODESTAR_SEEDS="cuprinet://intone/AAA…;cuprinet://intone/BBB…" \
  cuprinet-lodestar

docker logs -f lodestar    # copy the node's own link from here
```

### Status page

With `EnableWeb=true` (and the web port published, e.g. `-p 8080:8080`) the node serves a small read-only page at
`http://<host>:8080/` showing its current connection link and a QR code. The browser auto-refreshes them by polling
a tiny JSON endpoint — no manual reload, no client-side libraries — and the link is **cached** (regenerated at most
once per `WebRefreshSeconds`, not on every request). It is **HTTP only** by design; terminate TLS with a reverse
proxy (nginx/Caddy/Traefik) if you want HTTPS. The QR is rendered server-side (pure-managed, no native deps).

**Split faces over Tor (dual-stack).** When Tor is enabled, `WebSplit` (on by default) turns the page into two
faces so a single Lodestar effectively hands out two links, one per transport — and, importantly, a Tor visitor is
never shown the node's clearnet IP:

- the **clearnet** page (`http://<host>:8080/`) shows a **clearnet-only** link;
- the page is *also* published as its **own `.onion`** that shows a **Tor-only** link.

The status page's `.onion` address is surfaced three ways: it's **logged** (`Status page also reachable over Tor at:
http://<addr>.onion/`) once Tor finishes bootstrapping, written to **`<DataDirectory>/web.onion`**, and shown on the
clearnet page itself (a "This page over Tor" box). Open that `.onion` in Tor Browser to get the Tor-only link. Set
`WebSplit=false` to instead serve one page with an all-transports link.

### Ready-made compose files

Two sample stacks live in [`deploy/`](deploy/) — both use the **same image**, differing only by config:

```bash
docker compose -f deploy/docker-compose.clearnet.yml up -d   # clearnet only; set PublicHost
docker compose -f deploy/docker-compose.tor.yml      up -d   # clearnet + Tor (dual-stack)
```

- **Clearnet** publishes the overlay port (`43820`) and the status page (`8080`); set `PublicHost`/`AdvertisedAddresses` to a reachable address.
- **Tor** (`EnableTor=true`) is **dual-stack** — it keeps the clearnet address *and* publishes a `.onion` (minted from the persistent onion key in the data volume), so the node is reachable by clearnet **and** Tor peers; the `.onion` is added to the link once Tor bootstraps. Tor is a managed client baked into the binary — no extra packages or native Tor daemon. To run **onion-only** (hide the IP, Tor peers only) set `TorOnly=true` and drop the published overlay port; the sample has this commented inline.

**Which Tor mode?** For a public keep-alive node, prefer **dual-stack** (`EnableTor`): maximum reachability, with Tor as an extra lane. Use **onion-only** (`TorOnly`) only when you deliberately want to hide the node's IP — it costs reachability (Tor-capable peers only) and adds bootstrap latency and circuit overhead.

### Run in the debugger (VS Code)

The workspace [`.vscode/launch.json`](../../.vscode/launch.json) has **Lodestar (clearnet + web)** and **Lodestar (Tor + web)** configurations. Pick one from the Run and Debug panel and press F5: it builds the project, starts a genesis node on network `debug.local`, and serves the status page on `http://127.0.0.1:8080/` (clearnet) or `:8081` (Tor). Each writes its hot path to a gitignored `.debug-data*` folder beside the project.

The `/data` volume is the hot path — keep it to preserve the node's identity and known peers across restarts.

## Building published binaries

```bash
# self-contained single file (no .NET install needed on the target)
dotnet publish node/CupriNet.Lodestar/CupriNet.Lodestar.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -o out/linux
dotnet publish node/CupriNet.Lodestar/CupriNet.Lodestar.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -o out/win
```

CI (GitHub Actions, `.github/workflows/build.yml`) builds `win-x64` and `linux-x64` binaries and a Docker image
on every push, and attaches the binaries to GitHub Releases on a `v*` tag.
