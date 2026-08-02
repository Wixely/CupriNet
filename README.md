# CupriNet

**A 100% managed, decentralized, pseudonymous peer-to-peer networking library for .NET 10.**

CupriNet is a from-scratch overlay network + secure channel stack written in pure C# — no native
dependencies, no OS TLS, no QUIC. Nodes find each other and route metadata under pseudonymous identities,
then open **direct, authenticated, end-to-end encrypted** channels to actually communicate. Transport
security is [Noise](https://noiseprotocol.org/) over TCP; cryptography is managed
([BouncyCastle](https://www.bouncycastle.org/) primitives behind a swappable seam).

> Status: pre-1.0, evolving. The cryptographic core (Noise_XX identity binding, channel Consecration,
> signed documents) is real and reviewed; **326 unit tests** pass. Not yet audited for production use.
> See the [roadmap](ROADMAP.md) for what's done, next, and deliberately out of scope.

## The model — two layers

```
Application  Message (Epistle) · Data (Conduit) · File (Reliquary)  + reliable retry (Vigil)
──────────────────────────────────────────────────────────────────────────────────────────
Channel (L2, Arcanum)   authenticated · DIRECT-ONLY  — channel content is never relayed
  Watchword (Name#Salt) → Argon2id → HKDF subkeys · Noise channel handshake · ownership/membership
──────────────────────────────────────────────────────────────────────────────────────────
Overlay (L1, Concordance)   pseudonymous · ROUTABLE  — only L1 metadata is relayed
  bucketed peer table · sampled gossip · iterative discovery · channel advertisements (PoW-priced)
──────────────────────────────────────────────────────────────────────────────────────────
Transport (Vessel)   TCP + length-prefixed framing + stream multiplexing · Noise XX/IK · UDP for NAT
```

- **Layer 1 (Concordance)** — a shared overlay many unrelated groups use. Nodes participate under a
  pseudonymous identity (a *Sigil*), exchange minimal metadata, route discovery/referrals, and **never
  forward application traffic**.
- **Layer 2 (Arcanum)** — the channels where clients actually talk. Members authenticate via a shared
  channel code (`Name#Salt`) plus optional owner-issued credentials, and connect **directly** to each
  other (hole-punched if needed). L2 content is never relayed through a third node.

## What's in the box

- **Mesh-magnet links (Intonations)** — `cuprinet://intone/…`: a signed, expiring bootstrap document
  carrying a node's reachability candidates + a sample of seed peers. Show it as text or a QR code.
- **NAT traversal** — LAN discovery, NAT-PMP auto port-mapping, mutual-link UDP hole punching, and a
  pure-managed reliable-UDP transport, all behind the transport-agnostic pairing seam.
- **Tor** — optional onion transport ([CupriTor](https://github.com/Wixely/CupriTor), managed, no native
  daemon): **dual-stack** (clearnet + `.onion` at once) or a strict **onion-only** mode that hides the IP.
- **Traffic-analysis cover** — overlay gossip fuzzing, long-lived decoy connections (hot fuzz), decoy
  channel sessions (effigies), and fake groups (pageants).
- **Warm start** — an encrypted local cache of known peers so a node reconnects directly after restart.
- **Version negotiation ([CupriMark](https://github.com/Wixely/CupriMark))** — handshakes range-negotiate a
  shared protocol version (bound into the transcript against downgrades); signed documents accept by security
  floor / buried lifecycle. Newer and older nodes interoperate through a deliberate window instead of a flag day.

## Repository layout

| Path | What |
|---|---|
| `src/CupriNet.Abstractions` | IDs, options, records (Sigil, Seal, Beacon, Intonation…) |
| `src/CupriNet.Alembic*` | crypto seam + BouncyCastle suite (+ a gated insecure dev suite) |
| `src/CupriNet.Codex` | canonical serialization, framing, versioning |
| `src/CupriNet.Vessel` · `CupriNet.Noise` | TCP/UDP transport, mux; Noise XX/IK handshake |
| `src/CupriNet.Concordance` | peer table, gossip, discovery routing |
| `src/CupriNet.Arcanum` | channel codes, key derivation, adverts, ownership |
| `src/CupriNet.Rites` | message / data / file protocols + retry |
| `src/CupriNet.Traversal` | LAN discovery, NAT-PMP, hole punch, reliable-UDP |
| `src/CupriNet.Persistence` | `ISecretStore` + encrypted file store |
| `src/CupriNet.Hosting` | **`CupriNode`** — the public API |
| `src/CupriNet.Marks` | CupriMark version negotiation — the [catalogue](src/CupriNet.Marks/README.md) & floors |
| `src/CupriNet.Tor` | CupriTor onion transport binding (needs the CupriTor feed) |
| `node/CupriNet.Lodestar` | headless "keep the network alive" overlay node — see its [README](node/CupriNet.Lodestar/README.md) |
| `samples/CupriChatLite` | Avalonia sample chat app — see its [README](samples/CupriChatLite/README.md) |
| `tests/CupriNet.UnitTests` | the test suite |

## Quick start

```csharp
await using var node = await CupriNode.CreateAsync(new CupriNodeOptions
{
    Concordium = "example.chat",           // which network
    ListenAddress = IPAddress.Any,
});

// Mint a link to share (QR/paste), or connect to one you received.
string link = node.IntoneUri(TimeSpan.FromHours(2), DateTimeOffset.UtcNow);
var peer = await node.ConjoinAsync(intonation, DateTimeOffset.UtcNow);   // pair + Noise handshake

// Open an authenticated channel over the paired peer.
var watchword = Watchword.Parse("Dungeons&Dragons#<salt>");
var channel = await node.ConsecrateAsync(peer, watchword, DateTimeOffset.UtcNow);
```

## Building

Requires the **.NET 10 SDK** and access to the **[Wixely GitHub Packages](https://github.com/Wixely)** feed
(a GitHub token with `read:packages`; see [`nuget.config`](nuget.config)). The core depends on
[CupriMark](https://github.com/Wixely/CupriMark) (version negotiation), and the Tor bits on
[CupriTor](https://github.com/Wixely/CupriTor) — both published there.

```bash
# Full test suite
dotnet test tests/CupriNet.UnitTests/CupriNet.UnitTests.csproj -c Release

# The headless Lodestar node (self-contained single file)
dotnet publish node/CupriNet.Lodestar/CupriNet.Lodestar.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -o out/lodestar
```

Tor is exposed purely through the `IOnionTransport` seam, so the library's *design* keeps it optional even
though the package is pulled in for the samples and Lodestar.

## Continuous integration

`.github/workflows/build.yml` runs on every push. Because the core now depends on CupriMark (and the Tor
bits on CupriTor) from the Wixely feed, the build jobs authenticate it with a `PACKAGES_TOKEN` secret (an org
`read:packages` PAT) or the workflow token:

- **test** — builds and runs the full suite (the authoritative gate).
- **pack** — packs the core NuGet packages.
- **publish-lodestar / docker-lodestar** — `win-x64` + `linux-x64` Lodestar binaries and a container image
  (the Docker build takes the feed token as a BuildKit secret).
- **publish-app (CupriChatLite)** — the Tor-enabled sample (best-effort).

On a `v*` tag, artifacts are attached to a GitHub Release and the Lodestar image is pushed to GHCR.

## License

MIT. Copyright © Wixely. Cryptographic primitives via BouncyCastle (MIT-compatible).
