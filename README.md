# CupriNet

**A 100% managed, decentralized, pseudonymous peer-to-peer networking library for .NET 10.**

CupriNet is a from-scratch overlay network + secure channel stack written in pure C# — no native
dependencies, no OS TLS, no QUIC. Nodes find each other and route metadata under pseudonymous identities,
then open **direct, authenticated, end-to-end encrypted** channels to actually communicate. Transport
security is [Noise](https://noiseprotocol.org/) over TCP; cryptography is managed
([BouncyCastle](https://www.bouncycastle.org/) primitives behind a swappable seam).

> Status: pre-1.0, evolving. The cryptographic core (Noise_XX identity binding, channel Consecration,
> signed documents) is real and reviewed; **285+ unit tests** pass. Not yet audited for production use.

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
- **Tor** — optional onion transport ([CupriTor](https://github.com/Wixely/CupriTor)) with an enforced
  **Tor-only** mode: loopback bind, onion-only advertising/dialing, separate identity from clearnet.
- **Traffic-analysis cover** — overlay gossip fuzzing, long-lived decoy connections (hot fuzz), decoy
  channel sessions (effigies), and fake groups (pageants).
- **Warm start** — an encrypted local cache of known peers so a node reconnects directly after restart.

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
| `src/CupriNet.Tor` | CupriTor onion transport binding (needs the CupriTor feed) |
| `node/CupriNet.Lodestar` | headless "keep the network alive" overlay node — see its [README](node/CupriNet.Lodestar/README.md) |
| `samples/CupriChat` | Avalonia sample chat app — see its [README](samples/CupriChat/README.md) |
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

Requires the **.NET 10 SDK**.

```bash
# Core library + full test suite (no external feeds needed)
dotnet test tests/CupriNet.UnitTests/CupriNet.UnitTests.csproj -c Release

# The headless Lodestar node (self-contained single file)
dotnet publish node/CupriNet.Lodestar/CupriNet.Lodestar.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -o out/lodestar
```

**Tor is optional and gated behind a private feed.** `CupriNet.Tor` and the `CupriChat` sample depend on
the `CupriTor` package from the [Wixely GitHub Packages](https://github.com/Wixely) feed (see
[`nuget.config`](nuget.config)). Building them needs a GitHub token with the `read:packages` scope; the
rest of the solution builds with only nuget.org. The library exposes Tor purely through the
`IOnionTransport` seam, so the core has no Tor dependency.

## Continuous integration

`.github/workflows/build.yml` runs on every push:

- **test / pack / lodestar / docker** — token-free: builds and tests the core, packs the core NuGet
  packages, and produces `win-x64` + `linux-x64` Lodestar binaries and a container image.
- **publish-app (CupriChat)** — best-effort: builds the Tor-enabled sample; it needs the CupriTor feed,
  so it authenticates with the workflow token (or a `PACKAGES_TOKEN` secret) and won't fail the pipeline
  if that access isn't configured.

On a `v*` tag, artifacts are attached to a GitHub Release and the Lodestar image is pushed to GHCR.

## License

MIT. Copyright © Wixely. Cryptographic primitives via BouncyCastle (MIT-compatible).
