# CupriNet WebRTC clients — design

**How a browser connects to a reachable CupriNet node over WebRTC with *no* signalling server — no WebSocket, no
HTTP — by publishing the node's static WebRTC parameters inside the signed Intonation and letting the browser dial
them directly, with Noise + Consecration running end-to-end on top and no L2 content ever relayed.**

> Status: design / investigation. Elaborates the "browser clients over WebRTC (short-lived clients)" item in the
> [roadmap](../ROADMAP.md).

## Why this is needed

A browser can't open a raw TCP/UDP socket, so it can't speak CupriNet's Noise-over-TCP transport. Its only
peer-to-peer primitive is **WebRTC** (`RTCPeerConnection` + DataChannel). We add a **WebRTC transport binding** — not
a change to L1/L2. Browser clients are framed as **short-lived clients**: an ephemeral tab is a transient
participant, not infrastructure.

## The problem WebRTC always has — and how the Intonation dissolves it

WebRTC won't send a byte until it has the remote peer's **session description** — the remote's ICE credentials
(ufrag/password), DTLS certificate fingerprint, and candidate address(es). Normally that's exchanged live over some
"signalling channel" (a WebSocket, an HTTP POST, …). But that description is just **static data**, and a **Lodestar
is a reachable server with a stable address** — so it can **publish its half once, inside the signed Intonation**.
The browser then already *has* the remote description and needs no live signalling at all.

Put a **WebRTC endpoint block** in the Intonation (a new optional, signed field):

- `ice-lite` + a **stable ICE ufrag/password**,
- one or more **host candidates** = the node's known public `IP:port` (its existing beacons),
- the node's **DTLS certificate fingerprint**,
- `setup:passive` (the node is the DTLS server; the browser is the DTLS client).

Everything the browser needs to open a DataChannel to the node is now in the link it already scanned.

## Connection sequence (no signalling server)

```
browser (client)                                            target node (Lodestar)
   reads the Intonation (already has the node's WebRTC params)
   RTCPeerConnection: createOffer → setLocalDescription
   setRemoteDescription( node's params from the Intonation )   ← no round-trip
   ICE fires UDP (STUN checks) straight at the node's host candidate ------------->
                                                    (ICE-lite) verify with own pwd,
                                                    learn browser addr peer-reflexively
   <--------------------------------- STUN binding responses ----------------------
   DTLS handshake (browser=client)  --------------------------------------------->
     browser verifies node cert == fingerprint from the SIGNED Intonation
                                                    node accepts any client cert
   ======================= direct DTLS DataChannel established ====================
   ---- Toll (anti-DoS cookie) over the DataChannel ----------------------------->
   ---- Noise handshake (XX, browser pins the node's Sigil) --------------------->
   ---- Consecration (Watchword, epoch) → Arcanum session ----------------------->
   ---- Epistles / Reliquary (chat, files) -------------------------------------->
```

Nothing is sent to the node before the browser's first ICE packet, and that packet goes to the address the **signed
link** told it — so the whole "signalling" step is replaced by *reading the Intonation*.

## Why the node can accept a "blind" incoming connection

The usual objection is "the node never saw the browser's offer, so it can't validate the incoming packets." Two
properties make that a non-issue:

1. **ICE-lite.** The node never initiates its own connectivity checks — it only *responds* to the browser's, and
   verifies each with **its own** published password. It learns the browser's address as a **peer-reflexive**
   candidate from the incoming packet. So it needs none of the browser's ICE credentials, and no STUN server is
   involved.
2. **Accept any client DTLS cert.** Normally the answerer pins the offerer's fingerprint from the offer it received;
   here the node never got one, so it accepts whatever client cert shows up. **This is the one non-standard bit, and
   it is safe in CupriNet because DTLS is not our trust layer** — the instant the DataChannel is up we run the
   **Noise handshake pinned to the node's Sigil**, which is the real authentication. A blind DTLS pipe that then
   fails Noise is dropped.

And a bonus in the other direction: because the node's DTLS fingerprint rides in the **signed** Intonation, the
*browser* verifies the node's cert against it — so even the DTLS layer is **MITM-resistant against a fake node**,
before Noise even runs.

## The one idea that makes the rest cheap

Above the DataChannel, nothing is new. CupriNet already abstracts transport behind **`IVessel`** — a connected,
multiplexed pipe of length-delimited frames on numbered streams — and every reachability path (TCP, LAN, Tor,
Ferryman-hole-punched UDP) does one thing: **produce an `IVessel`, then call `ConjoinOverVesselAsync(vessel,
expectedPeerSigil, …)`.** Wrap the WebRTC DataChannel as an `IVessel` and the **Toll, Noise handshake, Consecration,
and Rites run unchanged.** So this is a **transport project, not a protocol one** — the identity, channel, and
messaging layers never learn WebRTC exists.

## Security analysis

- **Authenticity of the endpoint block.** The Intonation is signed by the node's Seal key, so its ICE creds + DTLS
  fingerprint can't be forged or swapped by a network attacker. (An attacker can mint their *own* signed link to
  their *own* node — same as today — but the browser pins the **Sigil** it expected, and Noise enforces it.)
- **Identity.** Trust rests where it always does in CupriNet: the browser pins the node's **Sigil** (a bech32
  fingerprint the user can compare), and the Noise handshake proves the node holds the matching Seal key. DTLS and
  ICE are just the pipe.
- **Stable/long-lived ICE creds & fingerprint in the link** are fine: they authenticate nothing on their own (Noise
  does), so publishing or reusing them leaks no secret and grants no access. A replayed link just gets an attacker a
  DTLS pipe that must still pass Noise pinned to the node's Sigil.
- **DoS surface.** An ICE-lite endpoint that accepts blind DTLS from anyone is reachable by any host, so it must not
  do expensive work cheaply. Defences, in order: **DTLS's built-in HelloVerifyRequest cookie** (stateless
  round-trip, blocks blind-spoofed floods), then the existing **Toll** (a stateless HMAC cookie — now CupriMark-
  versioned) **run over the DataChannel** before any Noise state is allocated, then per-source **rate-limiting**.
  This is the same defence-in-depth the TCP path already uses, moved onto the DataChannel.
- **Metadata.** Because there's no rendezvous in this path, no third party even learns the browser is connecting —
  strictly better than the signalling-server design.

## Identity & trust for a short-lived client

- **Ephemeral identity.** The browser mints a fresh Seal key per session/tab; its Sigil is throwaway. It persists no
  overlay state and takes no routing/relay duty — the "short-lived client" posture.
- **Node → client trust** rests on **channel membership**, not a long-lived key: an open (Aperture) channel needs
  only the Watchword; a gated (Sanction/Sealed) channel requires an imported persona key / **Investiture**.
- **Client → node trust** rests on the pinned **Sigil**, shown to the user as a bech32 fingerprint to compare.

## What this does *not* solve (the honest boundary)

Intonation-as-signalling works when the target is a **publicly reachable node with a stable address** — which is
exactly a **Lodestar** (and any node with a public IP / port-forward). It does **not** cover:

- **Reaching a peer *behind* the Lodestar** (NAT'd, no fixed address). That peer can't publish a dial-able candidate,
  so the browser needs its *live* SDP — which requires the Lodestar to pass messages back and forth (a real
  signalling relay), or the Ferryman's existing hole-punch brokering adapted to carry SDP. This is the *only* place a
  live channel re-enters, and it's a later phase.
- **Browser ↔ browser** (both ephemeral, both NAT'd) — same reason; needs relayed signalling and a direct ICE path.

For the primary browser use — **"open the app, connect to an always-on node, join a channel"** — the Intonation is
enough, with zero signalling infrastructure.

### Falling back to a Lodestar for signalling is a consented, explained step

When the direct path fails and we do fall back to using a Lodestar to **relay signalling** to a NAT'd peer, the app
**must confirm with the user and explain why it matters — before using the relay** — exactly as CupriChatLite already
does for the Ferryman. This is the **same trust-and-metadata decision**:

- **Why it matters (what the user is consenting to):** the relay is a **third party** you're now leaning on. It never
  sees channel content (signalling only — SDP/ICE, then it drops out), but it **does learn metadata**: that you are
  trying to reach that peer, the timing, and both parties' candidate addresses. It's also a **trust choice** about
  which relay to use.
- **How to gate it (reuse what exists):** surface the relay's **bech32 fingerprint** and apply the **`KnownRelays`
  TOFU** flow (New / Known / changed-key) via the `RelayApprovalRequested` confirm-and-explain dialog CupriChatLite
  already implements for the Ferryman; remember approved relays, warn on a changed key. Do **not** silently route a
  browser client through a relay it hasn't approved.
- **Direct-or-ask, never silent:** the reachable-node path above needs no relay and no prompt. Only the NAT'd-peer
  fallback introduces a relay, and that is precisely where the app pauses to ask. (See [ferryman.md](ferryman.md).)

## Client implementation options

| Option | What | Trade |
|---|---|---|
| **A. WASM (Blazor)** | Compile `CupriNet.Core/Arcanum/Rites` to WebAssembly; a WebRTC transport drives `RTCPeerConnection` via a thin JS-interop shim; Noise/BouncyCastle run in WASM | **One codebase, zero protocol duplication.** Cost: bundle size + BouncyCastle-in-WASM performance — prototype it |
| **B. Thin JS client** | Hand-write the Noise handshake + Consecration + Epistle framing in JS/TS over the DataChannel | Small, fast to load. Cost: **duplicates protocol + crypto** in JS |
| **C. Hybrid** | WASM for crypto/protocol, JS for the WebRTC/DOM glue | Middle ground |

**Leaning A.** Note the browser-side JS needed to *drive* the connection is tiny here: parse the Intonation's WebRTC
block, hand it to `setRemoteDescription`, `createOffer`/`setLocalDescription`, done — because there's no signalling
loop to implement.

## Native node WebRTC stack

The node needs an **ICE/DTLS/SCTP** implementation configured for this static-endpoint mode: **ICE-lite**, **fixed
ufrag/pwd**, a **host candidate on its public address**, `setup:passive`, and **accept-any client cert**. Candidate:
**SIPSorcery** (managed C#). Keep it **opt-in** — only Lodestars (and nodes that choose to serve browsers) run it;
everyone else is unaffected.

## Open problems

- **Static-answer feasibility.** The browser generates a real offer; the node answers "blind." In this model the
  node's parameters are pre-published and the browser treats them as the answer — the node's WebRTC stack must accept
  and complete a session it never explicitly answered. Needs prototype validation with a real browser + SIPSorcery
  (single DataChannel, `mid=0`, canonical SCTP params — highly standardised, but verify).
- **Accept-any-cert in SIPSorcery.** Confirm the stack can be configured to skip client-fingerprint verification (or
  patch it). Safe by design here; may need a code change in the WebRTC lib.
- **WASM crypto weight** — is BouncyCastle-in-WASM's Noise handshake latency acceptable on a phone browser?
- **DoS hardening** on the blind endpoint (DTLS cookie + Toll-over-DataChannel + rate-limit) — implement and measure.
- **Candidate/port stability** — the published host candidate must stay valid; a moving port breaks the link (same
  freshness/expiry story as any Intonation beacon).
- **Managed purity** — SIPSorcery is a native-node dependency; weigh against the 100%-managed goal, or scope
  WebRTC-serving to Lodestars only.

## Phased first cut

1. **Intonation WebRTC-endpoint block** ✅ *(done)* — an optional, signed `WebRtcEndpoint` field on the Intonation
   (ICE ufrag/pwd, DTLS fingerprint + algorithm, host UDP port), carried with the same backward-compatible
   trailing-field pattern as `Moniker`, versioned via the CupriMark `webrtc-endpoint` component. Host address(es)
   reuse the link's existing beacons. Codec + options + mint wired; unit-tested. *(Still to spec: the exact
   static-SDP mapping the browser/node build from these fields — lands with phase 2/3.)*
2. **Node ICE-lite WebRTC listener** (SIPSorcery) — accept a blind DataChannel and hand it to
   `ConjoinOverVesselAsync` as an `IVessel`, so pairing works with **zero changes** above the transport. Prove it
   against a native test client first, then a real browser.
3. **Browser client (Blazor WASM)** — read the Intonation, preload the remote description, open the DataChannel, run
   Noise + Consecration, join an open channel and exchange Epistles with the node.
4. **Toll-over-DataChannel + DTLS-cookie DoS hardening**, then the **relayed-signalling extension** for reaching
   peers *behind* the node (the one case that still needs a live channel) — **consent-gated**: reuse the Ferryman's
   `RelayApprovalRequested` confirm-and-explain + `KnownRelays` TOFU so the app asks before routing a client through a
   Lodestar relay and explains the metadata trade.

The elegant part: for the reachable-node case there is **no signalling server at all** — the signed Intonation *is*
the signalling, and above the DataChannel it's just another `IVessel`.
