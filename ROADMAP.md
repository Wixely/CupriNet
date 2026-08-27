# CupriNet roadmap

This is an honest snapshot of what's built, what's next, and what's deliberately out of scope for now. It's a
**map, not a promise** — order and detail will change. CupriNet is **pre-1.0** and **not yet security-audited**;
treat everything here as evolving until a 1.0 freeze (see the bottom).

Legend: ✅ done & tested · 🔨 in progress / near-term · 🧭 planned · 💤 deferred / accepted (not a bug).

## ✅ Done — the working core

The two-layer stack is real, wired end-to-end, and covered by the test suite (**434** unit tests green).

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
- **Ferryman relay.** Opt-in public relay that brokers a hole punch between two NAT'd peers (signaling only) and
  drops out — it **never carries L2 content** (punch succeeds → direct session; punch fails → Tor, never TURN). The
  requester reaches the relay with a throwaway identity (the relay never learns its Sigil), an anti-squat signed
  reservation binds a handle to its key, and the app gates the relay with **SSH-style TOFU** (name + bech32
  fingerprint, remembered in a `known_relays` file). A natural role for a public Lodestar (`EnableFerryman`, on by
  default). Full design: [design/ferryman.md](design/ferryman.md).
- **Browser clients (WebRTC).** A node accepts browser **DataChannel** peers with **no signalling server** — it
  publishes its static WebRTC params (ICE-lite ufrag/pwd, DTLS fingerprint, port) in the signed **Intonation**, and a
  browser dials them directly; Noise + Consecration then run over the DataChannel unchanged. The managed ICE/DTLS/SCTP
  stack is [CupriWebRTC](https://github.com/Wixely/CupriWebRTC) (multi-client on one UDP socket, NAT-rebinding
  migration, idle eviction), consumed through the `CupriNet.WebRtc` binding behind the `IWebRtcTransport` seam — any
  node can opt in via `CupriNodeOptions.WebRtcTransport`; **Lodestar turns it on by default**. See
  [design/webrtc-clients.md](design/webrtc-clients.md).
- **Shrines — hosting content over L2.** A node serves content at a **Signet**: a generated key whose bech32
  `cupri1…` rendering **is** the address, self-authenticating like a `.onion` and **separate from the node's overlay
  Sigil**, so hosting leaks no L1 identity. A **Pilgrim** (an ephemeral visitor, throwaway per-visit identity) dials
  it, **pins the Signet** through the existing Noise handshake — nothing identifying goes on the wire, and only the
  key-holder can answer — and fetches over the **Oracle** rite (HTTP-shaped `request → response` with static-file and
  app-callback handlers), or attends an **Auspice**: a named live feed pushed over the same connection (snapshot then
  updates, no polling and no WebSocket), size-quantised against traffic analysis. Bulk content past the message
  ceiling (an app blob) travels as a **Relic**: a chunked, hash-verified Reliquary transfer on its own stream, every
  chunk checked against its manifest — a client proves a blob's integrity **before** executing it. A protocol that is
  none of those three shapes gets a **Conduit**: a raw duplex session on the same visit, framed and sealed but
  otherwise uninterpreted — the seam an existing framed protocol lands on when it moves to L2, keyless and
  author-less so a Pilgrim stays unlinkable. No overlay join, direct-only, and browser DataChannels route straight
  into it. A node may **opt in** to advertising its Shrine in
  its signed Intonation. The Pilgrim half needs **no node at all** (`Pilgrimage.OverVesselAsync` in the socket-free
  `CupriNet.Shrine` package — the browser/WASM entry). See [design/shrines.md](design/shrines.md).
- **Monikers.** Optional self-asserted node label (a human-readable name a node claims for itself, carried
  **unverified** in its signed link + peer record with length/content Wards); trust always rests on the fingerprint,
  never the name — validating a Moniker is the consuming client's job (fingerprint pinning, allow-list, side channel).
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
  console / Windows Service / systemd / Docker, an optional HTTP status page (link + QR, auto-refresh, clearnet/Tor
  transport split), and a **browser WebRTC entry point on by default** (docker-configurable). See its
  [README](node/CupriNet.Lodestar/README.md).
- **CupriChatLite.** Barebones chat + file-transfer sample (Avalonia; clearnet + Tor, bootstrap UI) — a demo, not a product.

## 🔨 Near-term

- ~~**Finish CupriMark coverage** of the remaining low-value version points: `Toll`, `LanPresence`, `KnownPeer`.~~
  **Done** — all three now range-negotiate/accept through the catalogue (`toll` negotiates; `lan-presence` and
  `known-peer` accept), so every versioned wire component is CupriMark-covered. The site rites added since
  (`oracle`, `auspice`) carry their own components too — the first rites added *after* a release, so unlike
  Epistle/Conduit they can no longer inherit compatibility from the handshake.
- **Live-verify Tor on Lodestar** — confirm the overlay `.onion` and the status-page `.onion` both publish
  (two onion services on one Tor client) on a real network, end to end.
- **Browser example (hosted webpage) — the Nodestar product.** Both server halves are done (the WebRTC transport and
  the Shrine/Oracle content layer); what's missing is the **hosted web page** that dials a node's published WebRTC
  params (no signalling) and renders a Shrine — the "validate against a real Chromium" milestone. It ships as
  **Nodestar** (a separate repo): an SDK + reference host pairing a CupriNode with a clearnet HTTP front, serving a
  CupriFace WASM client. Must be a hosted page, not a local static file.
- **Decouple the core build from the private feed, or accept it explicitly.** CupriMark (and CupriWebRTC / CupriTor
  via the optional `CupriNet.WebRtc` / `CupriNet.Tor` bindings — both packed and pushed since #2) come from the
  Wixely GitHub Packages feed, so the build (and CI) needs `read:packages` (`PACKAGES_TOKEN`). Options: publish to nuget.org, or document the token as required. (Tracked;
  today CI authenticates the feed.)
- **UPnP / PCP** port mapping alongside the existing NAT-PMP.
- **Docs/API sweep** — keep READMEs and the public `CupriNode` surface in step as things move.

## 🧭 Planned — larger pieces

- **Simulation & fuzzing at scale** — a virtual-node harness (hundreds–thousands) for poisoning / partition
  / eclipse / Sybil scenarios, and decoder fuzzing across every frame and document parser.
- **IPv6 direct** paths and pluggable rendezvous.
- **A mobile sample** exercising suspend/reconnect against the retry layer.

### Explored ideas (shaping)

Ideas we've scoped but not started — captured here so the intent is on record:

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

*(Built since this list was first written and moved up to ✅ Done: **Monikers** — self-asserted node labels; the
**browser-clients-over-WebRTC** transport — realised as Intonation-as-signalling rather than the rendezvous-brokered
SDP exchange originally sketched here, so no signalling server is involved at all; and **websites over CupriNet**,
now built as **Shrines** — the site key, one-sided auth and bech32 self-auth address landed as sketched, but with **no
browser extension or local gateway**: the viewer is a served web page reaching the node over WebRTC. The remaining
browser piece is that page — the **Nodestar** product, a separate repo — tracked under 🔨 Near-term.)*

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
