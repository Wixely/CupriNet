# CupriNet WebRTC clients — design

**How a browser (or any WebRTC-only client) connects to a CupriNet node without native TCP — by treating a WebRTC
DataChannel as just another `IVessel`, brokered by a signalling rendezvous, with Noise + Consecration running
end-to-end on top and no L2 content ever relayed.**

> Status: design / investigation. Elaborates the "browser clients over WebRTC (short-lived clients)" item in the
> [roadmap](../ROADMAP.md). Reuses the same signalling-not-content pattern as the [Ferryman](ferryman.md).

## Why this is needed

A browser can't open a raw TCP socket, so it can't speak CupriNet's Noise-over-TCP transport directly. Its only
peer-to-peer primitive is **WebRTC DataChannels** (ICE for connectivity, DTLS for the pipe, SCTP for framing). To let
a browser participate, we add a **WebRTC transport binding** — not a change to L1/L2. Browser clients are framed as
**short-lived clients**: an ephemeral tab is a transient participant, not infrastructure.

## The one idea that makes this cheap

CupriNet already abstracts the transport behind **`IVessel`** — a connected, multiplexed pipe of length-delimited
frames on numbered streams. Every reachability path (TCP, LAN, Tor onion, Ferryman-hole-punched UDP) does one thing:
**produce an `IVessel`, then call `ConjoinOverVesselAsync(vessel, expectedPeerSigil, …)`.** Everything above the
vessel — the Toll, the Noise handshake pinned to the peer's Sigil, the Consecration (L2 channel handshake), and the
Rites (messages/files) — is transport-agnostic and reused verbatim.

So the entire WebRTC feature reduces to two things:

1. **Establish a DataChannel** (signalling + ICE).
2. **Wrap the DataChannel as an `IVessel`** (map CupriNet's numbered streams onto SCTP stream IDs, or multiplex one
   DataChannel).

Then `ConjoinOverVesselAsync` runs unchanged, and the browser is a first-class peer at the protocol layer. **No new
handshake, identity, or channel code.**

## Roles

| Role | Who | Does |
|---|---|---|
| **Client** | The browser (WebRTC-only) | Generates an ephemeral identity, offers a DataChannel, runs Noise + Consecration over it |
| **Target** | A native CupriNet node that opted into serving browser clients (a Lodestar, or a channel member) | Answers the DataChannel, is the actual peer the client pairs/consecrates with |
| **Rendezvous** | A signalling broker — naturally a **Lodestar** | Relays SDP offer/answer + ICE candidates between client and target. **Signalling only** |
| **STUN** | The Lodestar or a public STUN server | Supplies reflexive candidates for ICE. **No content** |

## Connection sequence

```
browser (client)                 rendezvous (Lodestar)              target node
     |   wss: signalling channel        |                                |
     |--------------------------------->|                                |
     |  create RTCPeerConnection        |                                |
     |  + DataChannel, gather ICE       |                                |
     |  SDP offer  {toSigil, fromSigil} |                                |
     |--------------------------------->|  forward over L1 overlay       |
     |                                  |------------------------------->|
     |                                  |   create answer, gather ICE    |
     |                                  |<-------------------------------|
     |  SDP answer                      |                                |
     |<---------------------------------|                                |
     |   ICE candidates (trickle) <---------------> both ways ---------->|
     |                                  |                                |
     |======== direct DTLS DataChannel established (ICE succeeded) ======|
     |                                  X  rendezvous drops out          |
     |------ Noise handshake (XX, client pins target Sigil) ----------->|
     |------ Consecration (Watchword, epoch) → Arcanum session -------->|
     |------ Epistles / Reliquary (chat, files) ----------------------->|
```

1. The browser loads the web client and **generates an ephemeral Seal keypair** → a throwaway **Sigil** for this
   session.
2. It opens a **WebSocket** to a rendezvous Lodestar's signalling endpoint (`wss://…/signal`). WebSocket is the
   browser-friendly entry the Lodestar exposes alongside its existing HTTP status page (TLS via a reverse proxy).
3. The browser creates an `RTCPeerConnection` (STUN configured, **no TURN** — see below), opens a DataChannel,
   makes an **SDP offer**, and starts gathering ICE candidates.
4. It sends `{offer, fromSigil, toSigil}` to the rendezvous. `toSigil` is the target (from a link/QR, or a channel
   provider the rendezvous helped discover).
5. The rendezvous **forwards the offer to the target over the CupriNet overlay** (an L1 control message — the same
   kind of brokering the Ferryman does for hole punches). The target answers; ICE candidates **trickle** both ways
   through the rendezvous.
6. ICE completes → a **direct, DTLS-secured DataChannel** exists between browser and target. The **rendezvous drops
   out** — it never saw content.
7. Over the DataChannel (wrapped as an `IVessel`): the **Noise handshake** runs, pinned to the target's Sigil; then
   the **Consecration** with the channel Watchword; then **Epistles/Reliquary** flow — all unchanged.

## Where the cryptography lives (DTLS is *not* the trust layer)

WebRTC DataChannels are already DTLS-encrypted, with the peer's certificate **fingerprint carried in the SDP**. But
our SDP travels through an **untrusted rendezvous**, so we must **not** trust the DTLS fingerprint for identity — a
malicious rendezvous could swap it. Instead:

- **Noise over the DataChannel is the identity layer.** The client pins the **target Sigil** (from the link) and the
  Noise handshake proves the target holds the matching Seal key — exactly as on the TCP path. A rendezvous that
  substitutes a fake target **fails the Noise Sigil check**. DTLS is just the pipe.
- **Defence in depth:** bind the negotiated **DTLS fingerprints into the Noise prologue**, so the Noise session is
  cryptographically tied to *this* DTLS channel — a swapped DataChannel can't be spliced under a genuine Noise
  transcript.

Net: the rendezvous and STUN are **untrusted** and can only see **metadata** (which Sigils are trying to connect,
timing, and — for STUN — reflexive IPs), never content. This is the same trust posture as the Ferryman.

## Identity & trust for a short-lived client

- **Ephemeral identity.** The browser mints a fresh Seal key per session (or per tab); its Sigil is throwaway. It
  **persists no overlay state** and takes **no routing/relay duty** — the "short-lived client" posture.
- **Node → client trust** rests on **channel membership**, not on a long-lived key: an open (Aperture) channel needs
  only the Watchword; a gated (Sanction/Sealed) channel requires the client to present a persona key / **Investiture**
  the user imports. A disposable Sigil earns no standing on its own — by design.
- **Client → node trust** rests on the **target Sigil** (pinned in Noise), presented to the user as a **bech32
  fingerprint** to compare, same as every other CupriNet client.

## The TURN / no-relay tension (important)

WebRTC's usual fallback for two hard-NAT'd peers is a **TURN relay**, which **relays the media/data**. That directly
violates CupriNet's core invariant that **L2 content is never relayed** (even encrypted). So:

- **We use STUN, never TURN.** STUN only reveals reflexive candidates (discovery/signalling) — no content flows
  through it. A Lodestar may act as a STUN server.
- **Direct-or-fail.** If ICE can't form a direct DataChannel (e.g. symmetric NAT on both ends), the connection
  **fails** — the same accepted outcome as CupriNet's "no direct path → no L2 session." We do **not** fall back to a
  content relay, because that's exactly what the invariant forbids.
- For the common first case — **browser → an always-on node** (public or itself hole-punched) — ICE succeeds on the
  node's host/reflexive candidate, so TURN isn't needed anyway.

**Rejected alternative — a WebRTC↔TCP gateway.** A "gateway" that terminates the browser's DataChannel and bridges it
onto the TCP overlay would be **relaying L2 content** through the gateway (encrypted or not) — breaking the invariant
and the "no third party can confirm a conversation happened" property. Rejected. The DataChannel must be **direct to
the actual peer**, with Noise/Consecration end-to-end.

## Client implementation options

| Option | What | Trade |
|---|---|---|
| **A. WASM (Blazor)** | Compile `CupriNet.Core/Arcanum/Rites` to WebAssembly; a WebRTC transport calls `RTCPeerConnection` via JS interop; Noise/BouncyCastle run in WASM | **One codebase, zero protocol duplication.** Cost: bundle size + BouncyCastle-in-WASM performance — needs a prototype |
| **B. Thin JS client** | Hand-write just the Noise handshake + Consecration + Epistle framing in JS/TS over the DataChannel | Small, fast to load. Cost: **duplicates protocol + crypto** in JS (maintenance + audit burden) |
| **C. Hybrid** | WASM for the crypto/protocol core, JS for the WebRTC/DOM glue | Middle ground; most of A's reuse with tighter DOM integration |

**Leaning A** for the first cut — it reuses the exact C# protocol code, so the browser can't drift from native
behaviour. The open question is WASM crypto weight; prototype the Noise handshake + a Consecration in Blazor WASM and
measure before committing.

## Native node WebRTC stack

The target node needs an **ICE/DTLS/SCTP** implementation to answer a DataChannel. Candidate: **SIPSorcery** (a
managed-C# WebRTC library) — managed, which fits the ethos, though not zero-dependency. To keep this off ordinary
nodes, make WebRTC-serving **opt-in**: only Lodestars and channel members who enable "accept browser clients" run the
stack. Everyone else is unaffected.

## Signalling protocol (sketch)

A tiny message set over the WS (browser↔rendezvous) and relayed over L1 (rendezvous↔target), all **keyed by Sigil**,
all **content-free**:

- `OFFER   { fromSigil, toSigil, sdp }`
- `ANSWER  { fromSigil, toSigil, sdp }`
- `CANDIDATE { fromSigil, toSigil, cand }`  (trickle ICE)
- `ERROR   { toSigil, reason }`  (target offline / declined / rate-limited)

Rendezvous responsibilities: authenticate nothing about *content* (it can't), **rate-limit by source** (reuse the
Toll's cheap-cookie idea to resist signalling floods), and **not persist** offers beyond a short TTL. The metadata it
learns (who signals whom) is the documented privacy cost, identical to the Ferryman's.

## Open problems

- **WASM crypto weight** — is BouncyCastle-in-WASM's handshake latency acceptable on a phone browser? (Prototype.)
- **Keyless trust** — a disposable browser Sigil has nothing to pin against long-term; fingerprint pinning still
  works per-session, but "is this the same visitor as yesterday?" has no answer by design. Fine for open channels;
  gated channels lean on imported persona keys / Investitures.
- **Browser ↔ browser** (both ephemeral, both NAT'd) — works only where ICE forms a direct path; no TURN fallback, so
  some pairs simply can't connect (accepted).
- **Rendezvous discovery from a browser** — the browser needs a `wss://` URL to *some* Lodestar to start; that first
  contact is a link/QR/config, the same bootstrap cost as everywhere else in CupriNet.
- **Managed-purity** — SIPSorcery (or any WebRTC stack) is a dependency on native nodes; weigh against the
  100%-managed goal, or scope WebRTC-serving to Lodestars only.

## Phased first cut

1. **Signalling spec + Lodestar `wss://…/signal` endpoint**, relaying `OFFER/ANSWER/CANDIDATE` to a target over the
   overlay. STUN via the Lodestar. (No crypto changes.)
2. **Native WebRTC `IVessel`** (SIPSorcery) — a DataChannel wrapped as an `IVessel`, so `ConjoinOverVesselAsync`
   pairs a WebRTC peer with **zero changes** above the transport. Prove it against a native test client first.
3. **Browser client (Blazor WASM)** with a JS-interop WebRTC `IVessel`, running Noise + Consecration in WASM;
   join an open channel and exchange Epistles with a native peer.
4. **DTLS-fingerprint-in-prologue** binding + signalling rate-limiting + the "reached a non-member/relay → degrade
   gracefully" UX already used for the CupriChatLite bootstrap path.

The elegant part throughout: because a DataChannel is *just another `IVessel`*, WebRTC is a **transport project**, not
a protocol project — the identity, channel, and messaging layers never learn it exists.
