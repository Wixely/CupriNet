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
- **CupriChatLite.** Barebones chat + file-transfer sample (Avalonia; clearnet + Tor, bootstrap UI) — a demo, not a product.

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

- **Ferryman relay** — an **opt-in public relay** that brokers a direct connection between two NAT'd peers by
  **coordinating a hole punch** (signaling only) and then dropping out — it **never carries L2 content** (punch
  succeeds → direct session; punch fails → fall back to Tor, never TURN). The relay can't break end-to-end
  security (it only sees metadata + IPs), so the app gates it with **SSH-style TOFU trust** (show the relay's
  name + bech32 fingerprint, remember approved relays to a `known_relays` file, warn on a changed key). Same
  pattern as libp2p circuit-relay-v2 + DCUtR; a natural role for a public **Lodestar**. Full design:
  [design/ferryman.md](design/ferryman.md).
- **Simulation & fuzzing at scale** — a virtual-node harness (hundreds–thousands) for poisoning / partition
  / eclipse / Sybil scenarios, and decoder fuzzing across every frame and document parser.
- **IPv6 direct** paths and pluggable rendezvous.
- **A mobile sample** exercising suspend/reconnect against the retry layer.

### Explored ideas (shaping)

Three features we've scoped but not started — captured here so the intent is on record:

- **Websites over CupriNet + a browser bridge.** Serve HTTP-style request/response **directly from a node** over
  the Rites layer, viewed through a **browser extension / local gateway** that maps a `cuprinet://…` address to a
  fetch — the way you'd view a Tor onion site. It reuses the L2 transport but with **one-sided auth**: the *host*
  proves it owns the URL (a dedicated **site key**, self-authenticating like `.onion` — Noise NK-style, or XX with
  a throwaway per-visit visitor identity), while the *visitor* stays anonymous. A public site has **no access
  control by design**; add a Watchword and it becomes a private Arcanum channel that serves web content. The site
  key is **separate from the node's overlay Sigil**, so hosting reveals no L1 identity and keeps L2's
  unlinkability + no-relay guarantees. The **URL is the 32-byte (256-bit) self-auth key rendered as bech32** —
  branded human prefix + checksum (`cupri1…`, ~52 chars, v3-onion-grade) — unstealable, no registry, no CA.
  **Live self-hosted** for the first cut (reachable only while the host is online; nothing stored in the overlay).
  *Open problem:* resolving a **stable short URL to a moving host** without a global DHT — v1 embeds reachability
  in a signed link, or host the site as a **Tor onion** so the `.onion` is itself the stable self-auth URL.
  Addressable by link now, by a verifiable node **name** later (below); persistent/distributed hosting is *out of
  scope* for the first cut.
- **L3 — optional in-channel end-to-end encryption (recipient-anonymous sealed messages).** A *third* encryption
  layer **inside** an Arcanum channel: a member **seals** a payload to specific recipients' **portable public
  keys** and posts the **ciphertext** into the channel, so the channel — and every other member, even an untrusted
  or compromised one — sees only ciphertext and **only key-holders decrypt**. Built on a modern **age / HPKE-style**
  sealed format (X25519 + ChaCha20-Poly1305, already in our BouncyCastle stack) **rather than OpenPGP**, because it
  is **recipient-anonymous by construction** (no key-ids in the header → an observer can't prove *who* a message is
  for) and **deniable by default** (no mandatory signature) — plausible deniability for a PM sent through a group,
  without PGP's easy-to-misconfigure foot-guns. Recipients are **keys people already hold**, so it still works in a
  channel you no longer trust — lead with **SSH ed25519 keys**, with **OpenPGP keys** accepted as an option for the
  PGP-in-side-channels crowd. Sits on top of (and independent from) CupriNet's transport crypto. *Trade:*
  long-term-key sealing means **no forward secrecy** (a later key leak reveals past L3 messages); real FS would need
  a session/ratchet (MLS/Signal) that conflicts with the self-contained-blob-using-existing-keys model — an accepted
  trade for this use.
- **Monikers — self-asserted node labels (L1).** Let a node advertise a **Moniker**: a human-readable name it
  claims for itself (e.g. "Community Relay"), so popular/infrastructure nodes are recognisable. **The protocol carries
  the Moniker unverified** — it is a display hint, never a truth claim — and **validating it is the consuming
  client's job**, however that client chooses: match the node's fingerprint against one it already trusts (a Sigil
  pinned earlier, a side channel like the entity's own website, an allow-list, or a later domain/social proof).
  Trust always rests on the **fingerprint, not the name**: a node's **Sigil is the hash of its key**, the handshake
  already **pins an expected Sigil**, and fingerprints render as **bech32** (branded prefix + checksum). Monikers
  are **self-asserted labels, not a global namespace** — no registry, no verification baked into the core, no
  squatting-by-fiat. Scope: an optional signed `Moniker` field on the node's record/link + display helpers; the
  `KnownRelays` TOFU store already demonstrates the client-side name↔fingerprint pattern this generalises. (Pairs
  with the websites item: show a site's Moniker, validate by fingerprint.)

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
