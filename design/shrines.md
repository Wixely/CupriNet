# Shrines — hosting content over CupriNet L2 — design

**Serve content directly from a node over the Rites (L2) layer, addressed by a self-authenticating `cupri1…` *Signet*,
fetched by ephemeral *Pilgrims* over a request/response *Oracle* rite — direct member-to-member, never relayed.** This
is the CupriNet-core capability that the **Nodestar** product (separate repo, `CupriNodestar`) is built on.

> Status: design. This is the **Layer-A (protocol) half** of the Nodestar plan, split out to live where it belongs — in
> the core. It refines the core-protocol parts of [websites-l2.md](websites-l2.md) with Lexicon naming. The transport
> it rests on (a node accepting browser WebRTC DataChannels) is **built** — see [webrtc-clients.md](webrtc-clients.md).
> The product/SDK half (web front, served WASM client, author tooling, deploy) lives in the Nodestar repo.

## Naming (Lexicon)

Core CupriNet constructs take Lexicon names. (Nodestar re-exposes them under plain "site" words for authors, who never
need the vocabulary.) Mapping — proposed, easy to swap:

| Concept | Lexicon | Notes |
|---|---|---|
| the L2 hosted place | **Shrine** | a session kind beside `Arcanum` (Channel); a host serves it, Pilgrims fetch |
| the ephemeral visitor | **Pilgrim** | throwaway per-visit identity, no overlay state |
| the request/response rite | **Oracle** | HTTP-shaped fetch over `Conduit` — *consult* (request) → *answer* (response) |
| the self-authenticating address | **Signet** | `cupri1…`, the Shrine's key/address, distinct from the node's `Sigil` |
| the connect handshake | **Pilgrimage** | Noise handshake pinning the Signet; host-authenticated, visitor-anonymous |

The cluster is deliberate — *a **Pilgrim** makes a **Pilgrimage** to a **Shrine** to consult its **Oracle*** (cf.
Delphi), and **Arcanum / Shrine** pair as *secret circle* vs. *served place*. Alternatives on the table:
Shrine→Sanctum, Oracle→Catechism, Signet→Rune/Cachet.

## What a Shrine is

A **Shrine** is a one-sided L2 endpoint: a host publishes content at a **Signet**, and any **Pilgrim** can fetch it. It
reuses the handshake + framing primitives but is lighter than an `Arcanum` channel — a Pilgrim is a short-lived client,
so it **skips the overlay join / anchoring**.

- **Signet — the address *is* the key.** A keypair **separate from the node's overlay Seal/Sigil**, persisted in the
  encrypted store, rendered bech32 `cupri1…` (the [`Bech32`](../src/CupriNet.Core/Bech32.cs) primitive already exists).
  Self-authenticating — no registry, no CA — v3-onion-grade, but native. Serving a Shrine presents *only* the Signet,
  so hosting leaks no L1 identity: the accept path is parameterised with the **serving identity**, not always the node
  Sigil.
- **Shrine session kind.** A new kind declared over the vessel beside `Channel`, so the accept dispatcher routes it to
  the Shrine handler instead of the overlay/channel path.

## The Oracle rite (request/response over Conduit)

Today [`Conduit`](../src/CupriNet.Rites/Conduit.cs) carries raw `ProtocolId`/`Flags` frames with no request/response.
The **Oracle** rite adds an HTTP-shaped exchange over it: `request(method, path, headers) → response(status, headers,
body)`, bodies streamed; content-types and ranges later. A **content handler** maps a request to a directory of static
files (reusing [`FilePathGuard`](../src/CupriNet.Rites/FilePathGuard.cs) for path-traversal defence) or an app
callback. For a *non*-Oracle L2 protocol, a consumer can still drive raw `Arcanum`/`Conduit` directly — the Oracle is
the batteries-included path, not the only one.

## The Pilgrimage (connect path)

1. **Handshake — host-authenticated, Pilgrim-anonymous.**
   - **v1:** reuse **Noise XX** with a **throwaway per-visit Pilgrim identity**, pinning the **Signet** as the expected
     peer. Reuses the existing handshake + key-pinning almost as-is.
   - **v2:** **Noise NK** (the Pilgrim sends *no* static key) — cleaner, deferred (only XX/IK exist today).
2. **Session kind `Shrine`** declared over the vessel, routed to the Shrine handler.
3. **Oracle** exchange → content.

**Public vs private.** A public Shrine has no access control by design — the Signet handshake *is* the security. Wrap it
in an `Arcanum` (Watchword-gated) and it becomes a **private Shrine**, reusing existing L2 access control.

## Intonation Signet field (opt-in)

An Intonation **may** carry a Shrine's Signet (a signed, optional field, same backward-compatible trailing-field pattern
as `Moniker` / `WebRtcEndpoint`) — so discovering a node discovers what it hosts, and "here's my node link" doubles as
"here's my Shrine." **But** advertising the Signet in the node's Intonation **links the Shrine to that node's L1
Sigil**, defeating identity separation. So: **opt-in only** (good for a public / branded node). For anonymous hosting,
share the Signet out-of-band and keep it unlinkable from the node's overlay identity.

## The direct-only invariant holds

The Pilgrim → Shrine-host DataChannel is a **direct** member-to-member vessel — no relay — so L2's no-relay rule
(**Sequestration**) is intact. The L1 entry node is used only for **discovery / roaming** (L1 control, which *may* be
relayed). With reachability riding in the link, even L1 is optional for v1: *follow a link → WebRTC straight to the host
→ fetch*.

## Security

- **Host authenticity** — the **Signet** is pinned during the **Pilgrimage** (MITM-resistant with no CA — the guarantee
  `.onion` gives, native). In the WebRTC path the browser also verifies the node's DTLS fingerprint from the *signed*
  Intonation before Noise.
- **Pilgrim anonymity** — the throwaway per-visit identity (v1) / NK (v2) keeps the Pilgrim unlinkable; the Shrine learns
  nothing durable about who visited.
- **DoS on a blind endpoint** — DTLS HelloVerify cookie → **Toll**-over-DataChannel → per-source rate-limit.

## Scope & limits (v1)

- **Reachable hosts only** — a browser can dial only a node with a reachable WebRTC endpoint (public / WebRTC-enabled
  hosts). NAT'd hosts need brokered WebRTC later.
- **Live self-hosted** — reachable only while the host is online; nothing is stored in the overlay.

## Phased plan (the CupriNet-side work)

1. **Shrine session kind** + accept-dispatch routing, parameterised with a **serving identity**.
2. **Signet** — the `cupri1…` keypair + encrypted-store persistence + identity separation on the accept path.
3. **Oracle rite** over `Conduit` — request/response, streamed bodies, a `FilePathGuard` static handler + an
   app-callback seam.
4. **Pilgrimage** — Noise XX + throwaway Pilgrim pinning the Signet, no overlay join; `Shrine` session dispatch.
5. **Intonation Signet field** — optional signed field; opt-in advertising with the linkage warning surfaced.
6. **Publish `CupriNet.WebRtc`** to the Wixely feed (today `pack` skips it) so downstream products can consume the
   WebRTC binding — then ship all of the above in a **CupriNet minor release**.
7. **Later** — Noise NK; brokered WebRTC to NAT'd hosts; ranges / conditional Oracle requests.

## Consumers

The **Nodestar** product (separate repo, `CupriNodestar`) is the first consumer: it wraps a CupriNode + a clearnet HTTP
front + a served WASM client that runs the Pilgrimage + Oracle in the browser, plus author tooling. The Shrine protocol
here is generic, though — any CupriNet client can consult a Shrine.
