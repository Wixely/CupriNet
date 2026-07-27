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
| `DataDirectory` | `CUPRINET_LODESTAR_DataDirectory` | per‑OS | Hot path: identity, master key, known peers. |
| `SeedLinks` | `CUPRINET_LODESTAR_SeedLinks__0`, … | `[]` | Seed links (array). |
| `SeedsFile` | `CUPRINET_LODESTAR_SeedsFile` | *(none)* | File of seed links, one per line (`#` comments ok). |
| `EnablePortMapping` | `CUPRINET_LODESTAR_EnablePortMapping` | `false` | Ask the gateway (NAT‑PMP) to forward the port. |
| `EnableLanDiscovery` | `CUPRINET_LODESTAR_EnableLanDiscovery` | `false` | Announce/discover peers on the LAN. |
| `EnableCoverTraffic` | `CUPRINET_LODESTAR_EnableCoverTraffic` | `false` | Run anonymity cover traffic (extra bandwidth). |

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

```powershell
# publish a self-contained exe first (see below), then:
sc.exe create "CupriNet Lodestar" binPath= "C:\cuprinet\cuprinet-lodestar.exe" start= auto
# configure via machine environment variables (CUPRINET_LODESTAR_*) or an appsettings.json beside the exe
sc.exe start  "CupriNet Lodestar"
```

The app auto‑detects the service host (`AddWindowsService`), so lifetime and logging integrate with Windows.

### systemd (Ubuntu)

See [`deploy/cuprinet-lodestar.service`](deploy/cuprinet-lodestar.service) for a ready unit (Type=notify,
`StateDirectory=` for the hot path, hardening). Then `systemctl enable --now cuprinet-lodestar` and follow with
`journalctl -u cuprinet-lodestar -f`.

### Docker

```bash
# build from the repo root (the Dockerfile needs src/ in context)
docker build -f node/CupriNet.Lodestar/Dockerfile -t cuprinet-lodestar .

docker run -d --name lodestar \
  -p 43820:43820 \
  -v lodestar-data:/data \
  -e CUPRINET_LODESTAR_Concordium=example.chat \
  -e CUPRINET_LODESTAR_PublicHost=lodestar.example.net \
  -e CUPRINET_LODESTAR_SEEDS="cuprinet://intone/AAA…;cuprinet://intone/BBB…" \
  cuprinet-lodestar

docker logs -f lodestar    # copy the node's own link from here
```

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
