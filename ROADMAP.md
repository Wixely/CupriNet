# CupriNet roadmap

This is an honest snapshot of what's built, what's next, and what's deliberately out of scope for now. It's a
**map, not a promise** — order and detail will change. CupriNet is **pre-1.0** and **not yet security-audited**;
treat everything here as evolving until a 1.0 freeze (see the bottom).

Legend: ✅ done & tested · 🔨 in progress / near-term · 🧭 planned · 💤 deferred / accepted (not a bug).

## ✅ Done — the working core

The two-layer stack is real, wired end-to-end, and covered by the test suite (**326** unit tests green).

- **Transport (Vessel).** Managed TCP with length-prefixed framing and logical stream multiplexing; a
  reliable-UDP transport for NAT paths. No native deps, no OS TLS, no QUIC.
- **Handshake (Conjunction).** Noise_XX first contact + Ed25519 identity binding over the Noise handshake
  hash; a stateless pre-handshake cookie (Toll); bounded, off-loop handshakes (slow-loris resistant).
- **Overlay (L1, Concordance).** Bucketed peer table, sampled gossip, iterative discovery/referrals,
  PoW-priced channel advertisements (Tribute), local reputation, subnet allow/deny policy.
- **Channels (L2, Arcanum).** `Name#Salt` Watchword → Argon2id → HKDF subkeys; the Consecration key-
  confirmation handshake; ownership (descriptor / membership investiture / ascension chain / accepted
  schism). L2 content is direct-only — never relayed.
- **Rites.** Message (Epistle) + reliable retry (Vigil), generic data (Conduit), chunked/hash-verified
  file transfer (Reliquary).
- **Traversal.** LAN discovery, NAT-PMP auto port-mapping, mutual-link UDP hole punching.
- **Crypto (Alembic).** Swappable seam over BouncyCastle (Ed25519 / X25519 / ChaCha20-Poly1305 / Argon2id
  / HKDF / SHA-2); a gated insecure dev suite (Simulacrum) for parity testing.
- **Privacy / cover.** Private-address stripping from links & gossip, overlay gossip fuzzing, hot fuzz,
  decoy channel sessions (effigies), fake groups (pageants).
- **Tor.** Onion transport via [CupriTor](https://github.com/Wixely/CupriTor) (managed, no native daemon)
  behind the `IOnionTransport` seam — **dual-stack** (clearnet + onion) or strict **onion-only**.
- **Version negotiation ([CupriMark](https://github.com/Wixely/CupriMark)).** Handshakes range-negotiate a
  shared version (Conjunction, Consecration), bound into each transcript; signed documents accept by
  security floor / buried lifecycle (Intonation, Decree, ChannelDescriptor, Investiture). See
  [`src/CupriNet.Marks`](src/CupriNet.Marks/README.md).
- **Persistence.** Encrypted local store; warm-start peer cache; OS-appropriate file permissions.
- **Lodestar.** Headless keep-the-network-alive node: hot-path identity/peer cache, seed bootstrap, genesis,
  console / Windows Service / systemd / Docker, and an optional HTTP status page (link + QR, auto-refresh,
  clearnet/Tor transport split). See its [README](node/CupriNet.Lodestar/README.md).
- **CupriChat.** Avalonia sample chat app (clearnet + Tor, bootstrap UI).

## 🔨 Near-term

- **Finish CupriMark coverage** of the remaining low-value version points: `Toll`, `LanPresence`, `KnownPeer`.
- **Live-verify Tor on Lodestar** — confirm the overlay `.onion` and the status-page `.onion` both publish
  (two onion services on one Tor client) on a real network, end to end.
- **Decouple the core build from the private feed, or accept it explicitly.** CupriMark is now a *core*
  dependency from the Wixely GitHub Packages feed, so the whole build (and CI) needs `read:packages`
  (`PACKAGES_TOKEN`). Options: publish CupriMark to nuget.org, or document the token as required. (Tracked;
  today CI authenticates the feed.)
- **UPnP / PCP** port mapping alongside the existing NAT-PMP.
- **Docs/API sweep** — keep READMEs and the public `CupriNode` surface in step as things move.

## 🧭 Planned — larger pieces

- **Ferryman relay** — an optional, separate capability that relays **L1 data only** and coordinates **L2
  hole-punching** (signaling), never carrying L2 content. Lets a symmetric-NAT pair that can't be punched
  still discover/coordinate; keeps the server-independent core intact.
- **Simulation & fuzzing at scale** — a virtual-node harness (hundreds–thousands) for poisoning / partition
  / eclipse / Sybil scenarios, and decoder fuzzing across every frame and document parser.
- **IPv6 direct** paths and pluggable rendezvous.
- **A mobile sample** exercising suspend/reconnect against the retry layer.

## 💤 Deferred / accepted (not bugs)

- **First contact needs a seed.** Isolated partitions with no shared contact can't discover each other; links
  / known peers / (later) rendezvous bootstrap. Documented, not "solved".
- **Genesis is a manual step** — the first two nodes of a brand-new network exchange links out-of-band once.
- **No guaranteed symmetric-NAT traversal** — static links carry candidates and expire; guaranteed
  reachability across all NAT types needs the Ferryman.
- **Split-brain / ownership forks** are tolerated, not consensus-resolved; honest clients converge per side.
- **Mobile nodes are unreliable routers** — accepted; the retry layer + always-on nodes compensate.
- **Not marketed as Sybil-resistant** unless/until a scarce-resource friction (PoW / invite / anchor) is a
  formal part of the protocol.

## Before 1.0

- **External security audit** of the hand-rolled Noise usage and the crypto seam before any production use.
- **Freeze the wire format and public API** — lean on CupriMark's security-floor / buried lifecycle so old
  versions retire cleanly rather than partitioning.
- **Broaden CI** beyond Linux (Windows/macOS test runs) and grow the simulation/fuzz suites into the gate.

---

*Naming: this repo uses a ceremonial vocabulary (Sigil, Beacon, Intonation, Concordance, Arcanum, Vessel,
Rites, Alembic…). Each term maps 1:1 to an ordinary technical component — see the design notes for the full
lexicon.*
