# Websites over CupriNet L2 — design

**Serve HTTP-style content directly from a node over the Rites (L2) layer, addressed by a self-authenticating
`cupri1…` site key, and browsed through a *clearnet-hosted web client* that reaches the network over WebRTC — no
browser extension, no signalling server, no Tor, no short-URL registry.**

> Status: design. Supersedes the "browser bridge / extension" sketch in the roadmap's *Websites over CupriNet* idea.
> The transport this rests on (a node accepting browser WebRTC DataChannels) is **built** — see
> [webrtc-clients.md](webrtc-clients.md). This doc is about what runs *over* that channel.

## The shape

The old plan was a Chrome extension that transparently proxied `cuprinet://…`. This replaces it with something with
**zero install**: a **web client** — an ordinary web page — that is itself the on-ramp.

```
   ┌─ clearnet ─────────────┐        ┌─ CupriNet ────────────────────────────────┐
   │  browser               │        │                                            │
   │  ┌──────────────────┐  │  HTTPS │  entry node (public, WebRTC-enabled)       │
   │  │ web client (SPA) │◄─┼────────┼─ serves the web client over clearnet       │
   │  │  address bar     │  │        │                                            │
   │  │  site viewport   │  │ WebRTC │  L1 attach: discovery / roaming            │
   │  └───────┬──────────┘◄─┼────────┼─►(DataChannel; Noise over it)              │
   │          │             │        │                                            │
   │          │             │ WebRTC │  site host node (may be the same node)     │
   │          └─────────────┼────────┼─►L2 site: connect-to-site + request/reply  │
   │                        │ direct │   (direct member-to-member; never relayed) │
   └────────────────────────┘        └────────────────────────────────────────────┘
```

1. A user opens the **web client** — a normal page, served over clearnet by *some* node (or any static host).
2. The client uses the browser's **native WebRTC** to open a DataChannel to a node → it is now a **short-lived L1
   participant** (a transient client, not infrastructure — throwaway identity, no persisted overlay state).
3. The client shows an **embedded address bar**. You enter/click an **L2 site address**; the client opens a
   DataChannel **directly to that site's host**, runs the site handshake, fetches over a request/response Rite, and
   renders it in a sandboxed viewport.
4. **One public node is a complete on-ramp**: it can serve the web client, be the WebRTC entry point, *and* host a
   site on itself — so "connect to my node" and "view my site" can be the same hop.

## Why this resolves the two hard problems

The earlier design had two open uncertainties. This model dissolves both:

- **Client-side viewing.** No extension and no bespoke gateway daemon — the viewer is a **web page** anyone can open.
- **Stable-URL → moving-host resolution.** **We do not need short URLs.** The site address travels *with its
  reachability* — carried in the link/Intonation that delivered it — so the client connects directly. The
  "resolve a bare 32-byte key to a moving host without a global DHT" problem simply doesn't arise for v1.

## The reuse that makes it cheap

The browser side is **not** a reimplementation of CupriNet. The browser's native `RTCDataChannel`, wrapped as the
existing [`IDataChannel`](../src/CupriNet.Vessel/DataChannelVessel.cs) → `DataChannelVessel`, feeds the **same**
handshake + Rites code — compiled to **WebAssembly (Blazor)**:

```
browser RTCDataChannel ──(JS interop)──► IDataChannel ──► DataChannelVessel ──► IVessel
                                                                                   │
                             the *same* Noise handshake + Rites, running in WASM ◄─┘
```

- The browser runs the **real client stack** (Noise, framing, the request/response Rite) — no protocol drift, no JS
  Noise reimplementation.
- The only net-new browser plumbing is a Blazor `IDataChannel` over `RTCPeerConnection`/`RTCDataChannel` via JS
  interop. It plugs into a seam that already exists.
- BouncyCastle is managed C# and runs under Blazor WASM. Cost: a multi-MB WASM bundle (acceptable for an app), and
  driving `RTCPeerConnection` through JS interop.

The node side is already done (CupriWebRTC accepts the DataChannel); the browser side meets it at the same
`ConjoinOverVessel`/Rites seam from the other end.

## Site identity — the address *is* the key

- A **site key**: a keypair **separate from the node's overlay Seal/Sigil**, persisted in the encrypted store,
  rendered as **bech32 `cupri1…`** (the [`Bech32`](../src/CupriNet.Core/Bech32.cs) primitive already exists). The key
  is the address — self-authenticating, no registry, no CA (v3-onion-grade, but native).
- **Identity separation.** Serving a site presents *only* the site key, never the node's overlay Sigil, so hosting
  leaks no L1 identity. The accept path is parameterised with the serving identity rather than always using the node
  identity.

### The Intonation-linkage trade-off (opt-in)

An Intonation **may** carry the L2 site address (a signed, optional field) — so discovering a node discovers what it
hosts, and "here's my node link" doubles as "here's my site." **But** advertising the site *in the node's Intonation*
**links the site to that node's L1 Sigil**, defeating identity separation. So:

- **Opt-in only.** Good for a **public / branded** node ("this Lodestar hosts this landing page").
- For **anonymous** hosting, do **not** put it in the Intonation — share the site link out-of-band; the site key
  stays unlinkable from the node's overlay identity.

## Connecting to a site (a lighter path than a channel)

A site visit reuses the handshake and framing primitives but is **not** the full `ConjoinOverVesselAsync` — a browser
visitor is a short-lived client, so it **skips the overlay join / anchoring**:

1. **Handshake — host-authenticated, visitor-anonymous.**
   - **v1:** reuse **Noise XX** with a **throwaway per-visit visitor identity**, pinning the **site key** as the
     expected peer. This reuses the existing handshake + key-pinning almost as-is.
   - **v2:** **Noise NK** (visitor sends *no* static key at all) — cleaner, but not yet implemented (only XX/IK
     exist). Deferred.
2. **Session kind `Site`** declared over the vessel (a new kind alongside `Channel`), so the accept dispatcher routes
   it to the site handler instead of the overlay/channel path.
3. **Request/response Rite** — a new HTTP-shaped Rite over [`Conduit`](../src/CupriNet.Rites/Conduit.cs):
   `request(method, path, headers) → response(status, headers, body)`; bodies streamed, content-types, ranges later.
4. **Content handler** — maps a request to a directory of static files (reusing
   [`FilePathGuard`](../src/CupriNet.Rites/FilePathGuard.cs) for path-traversal defence) or an app callback.

**Public vs private.** A public site has no access control by design — the site-key handshake *is* the security. Wrap
it in an **Arcanum channel** (Watchword-gated) and it becomes a *private* site, reusing existing L2 access control.

## The direct-only invariant is preserved

The browser → site-host DataChannel is a **direct** member-to-member vessel — no relay — so L2's no-relay rule
(**Sequestration**) is intact. The L1 entry node is used only for **discovery / roaming** (L1 control, which *may* be
relayed/queried). With reachability riding in the link, even L1 is optional for v1: *follow a link → WebRTC straight
to the host → view*. L1 attach is what turns "view one site" into "move around the network."

## Scope & limits (v1)

- **Reachable hosts only.** A browser can only dial a node with a reachable WebRTC endpoint, so v1 = **public,
  WebRTC-enabled hosts** (servers, Lodestars). NAT'd hosts need **brokered WebRTC** later (the Ferryman idea extended
  from TCP hole-punching to WebRTC offer/answer brokering).
- **Live self-hosted.** Reachable only while the host is online; nothing stored in the overlay.
- **Latency/throughput** are the DataChannel's; fine for documents/apps, not media.

## Security notes

- **Sandbox rendered content.** Fetched site HTML runs in a sandboxed `iframe` (`sandbox` / `srcdoc`, no same-origin)
  so a hostile site can't attack the web client or read its L1 session.
- **Visitor anonymity.** The throwaway per-visit identity (v1) / NK (v2) keeps the visitor unlinkable; the site learns
  nothing durable about who visited.
- **Host authenticity.** The site key is pinned during the handshake — the `cupri1…` address a user typed is exactly
  who answered (MITM-resistant without any CA), the same guarantee `.onion` gives, but native.

## Foundation to de-risk first

Everything above assumes the load-bearing fact that a **real browser can open a DataChannel to our endpoint from
synthesised SDP** (Intonation-as-signalling: build the answer from the published ufrag/pwd/fingerprint/host-candidate,
ICE-lite, accept-any-cert). The **riskiest interop layer is SCTP** (our minimal association vs. the browser's
usrsctp). So the first artefact is a throwaway **browser-interop probe** — a static page that dials a running
CupriWebRTC endpoint and opens a DataChannel — validating ICE → DTLS → SCTP → DataChannel against real Chromium before
any Blazor client is written. (See `probe/` in the CupriWebRTC repo.)

## Phased plan

1. **Probe (de-risk).** Static HTML+JS dials a CupriWebRTC endpoint from synthesised SDP; confirm a DataChannel opens
   and bytes round-trip in real Chromium. *If SCTP doesn't interop, that is the first thing to fix — everything rests
   on it.*
2. **Site core (native).** Site identity + `cupri1…` address; the `Site` session kind + connect-to-site path (XX +
   throwaway visitor, pinning the site key, no overlay join); the request/response Rite + static-file handler.
3. **Intonation field.** Optional signed site-address field; opt-in advertising with the linkage warning surfaced.
4. **Blazor WASM web client.** `IDataChannel` over native WebRTC; address bar + sandboxed viewport; served by a node
   over clearnet. This is the "hosted webpage" deliverable.
5. **Roaming.** L1 attach for discovery of sites/hosts you don't already hold a link for.
6. **Later.** Noise NK; brokered WebRTC to NAT'd hosts; private (Watchword) sites.

## Open questions

- **SCTP interop** with usrsctp (settled by the probe).
- **WASM footprint** — is the Blazor + BouncyCastle bundle acceptable, or is a trimmed/JS-shim edge needed?
- **How a short-lived browser identity earns trust** — fingerprint pinning of the *site* still applies, but the
  visitor has no long-lived key to be trusted *as*; fine for one-sided-auth sites, revisited for anything that needs
  to trust the visitor.
