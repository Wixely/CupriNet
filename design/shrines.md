# Shrines — hosting content over CupriNet L2 — design

**Serve content directly from a node over the Rites (L2) layer, addressed by a self-authenticating `cupri1…` *Signet*,
fetched by ephemeral *Pilgrims* over a request/response *Oracle* rite — direct member-to-member, never relayed.** This
is the CupriNet-core capability that the **Nodestar** product (separate repo, `CupriNodestar`) is built on.

> Status: **built** (the core capability — see *Phased plan* below for exactly what landed). This is the **Layer-A
> (protocol) half** of the Nodestar plan, split out to live where it belongs — in the core. It refines the
> core-protocol parts of [websites-l2.md](websites-l2.md) with Lexicon naming. The transport it rests on (a node
> accepting browser WebRTC DataChannels) is **built** — see [webrtc-clients.md](webrtc-clients.md). The product/SDK
> half (web front, served WASM client, author tooling, deploy) lives in the separate Nodestar repo.

## Using it

```csharp
// Host: the Signet's cupri1… address IS the site URL. Persist it so the address is stable across restarts.
var signet = await new SignetStore(secretStore).LoadOrCreateAsync(node.Suite);
node.HostShrine(signet, new StaticFileOracleHandler("wwwroot-l2"));   // or DelegateOracleHandler / your own
Console.WriteLine(node.ShrineAddress);                                 // cupri1…

// Pilgrim: dial the host, pin the Signet, fetch.
await using var shrine = await node.PilgrimageOverVesselAsync(vessel, signetSigil, ct);
var page = await shrine.ConsultAsync(OracleRequest.Get("/index.html"), ct);
```

A Pilgrim needs no node. Visiting is not being on the overlay — the visit mints a throwaway identity and skips the
join — so the same rite is reachable without one, from `CupriNet.Shrine` (a deliberately socket-free package):

```csharp
await using var shrine = await Pilgrimage.OverVesselAsync(vessel, signetSigil, network, suite, enableToll: true, ct);
```

That is the entry a browser uses: a WebAssembly build has no sockets, so it cannot construct a `CupriNode` (which
binds a listener), and `node.PilgrimageOverVesselAsync` is just this call with the node's own three values passed in.
It is equally the entry for a CLI that only wants to fetch a page, a test with no overlay, or an embedded host with no
listener. The point is that there is exactly **one** implementation of the Pilgrimage handshake — a second one
transcribed downstream would drift the first time the wire changes (0.3.2's Oracle version byte, for instance).

Live data rides the same connection. The host registers named feeds; the Pilgrim attends one and reads a snapshot
followed by updates, concurrently with any Oracle fetch:

```csharp
// Host: a named feed, emanated for as long as a Pilgrim attends it.
node.HostShrine(signet, handler, feeds: new Dictionary<string, IAuspiceSource>
{
    ["constellation"] = new DelegateAuspiceSource(async (publisher, token) =>
    {
        await publisher.SnapshotAsync(CurrentState(), token);
        await foreach (var change in Changes(token))
            await publisher.UpdateAsync(change, token);
    }),
});

// Pilgrim: attend it — the snapshot arrives first, then each update.
await foreach (var frame in shrine.AttendAsync("constellation", ct))
    Render(frame.Kind, frame.Payload);   // Snapshot, then Update…
```

Bulk content — anything past the message ceiling (an app blob, an asset pack) — travels as a named **relic**: a
Reliquary transfer carried over the same visit, chunk by chunk, each chunk verified against its manifest as it
arrives and the whole file verified before the bytes are handed back. So a client can prove a blob's integrity
*before* executing it:

```csharp
// Host: name the relic once, before hosting; the manifest (and its frame-ceiling fit) is proven at Add time.
var relics = new StaticRelicSource(node.Suite).Add("app/client.wasm", wasmBytes);
node.HostShrine(signet, handler, feeds, relics);

// Pilgrim: fetch it — verified end to end, concurrent with the Oracle and the Auspice on the same connection.
var wasm = await shrine.FetchRelicAsync("app/client.wasm", cancellationToken: ct);
```

A protocol that is neither request/response nor snapshot-then-updates gets the raw duplex rite: a **conduit**, on the
same visit, framed and sealed but otherwise uninterpreted. This is the seam an existing framed protocol lands on when
it moves to L2 — the transport below it changes, the protocol above it does not:

```csharp
// Host: one conduit per visit; the application's own ProtocolId discriminates protocols within it.
node.HostShrine(signet, handler, feeds, relics, conduit: new DelegateConduitHandler(async (session, token) =>
{
    while (await session.ReceiveAsync(token) is { } frame && !frame.IsSealed)
        await session.SendAsync(Handle(frame), token);
}));

// Pilgrim: send and receive, in whatever order the protocol above dictates.
await shrine.Conduits.SendAsync(new ConduitFrame { ProtocolId = 7, SchemaVersion = 1, Flags = 0, Payload = bytes }, ct);
var reply = await shrine.Conduits.ReceiveAsync(ct);   // a sealed frame carries the reason, then null: end of stream
```

A browser DataChannel arriving at a Shrine-hosting node is routed straight into the Pilgrimage, so browsers are
served by the same path with no extra wiring.

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

## The Oracle rite (request/response)

Today [`Conduit`](../src/CupriNet.Rites/Conduit.cs) carries raw `ProtocolId`/`Flags` frames with no request/response.
The **Oracle** rite adds an HTTP-shaped exchange: `request(method, path, headers) → response(status, headers, body)`.
A **content handler** maps a request to a directory of static files (reusing
[`FilePathGuard`](../src/CupriNet.Rites/FilePathGuard.cs) for path-traversal defence) or an app callback. For a
*non*-Oracle L2 protocol, a consumer can still drive the raw `Conduit` directly — over an `Arcanum` channel, or (since
0.3.5) over a Pilgrimage through `ShrineSession.Conduits`. The Oracle is the batteries-included path, not the only one.

**Bodies are whole messages, not streams.** A request and a response each carry one fully-materialised `byte[]`, sent
as a single message. There is no chunking, no backpressure and no ranges — see *The message ceiling* below for what
that costs and which rite to use instead.

## The Auspice rite (live feeds)

The Oracle answers once. The **Auspice** is its streaming sibling: a Pilgrim **attends** a named feed and the Shrine
emanates a **snapshot** followed by a stream of **updates** — subscribe-once, push-many, with no polling, no second
connection and no WebSocket/SSE machinery. It runs on its own logical stream, so a page fetch and its live data share
one Pilgrimage and are demultiplexed by a `VesselMux`.

The host seam mirrors the Oracle's: an `IAuspiceSource` is handed an `IAuspicePublisher` for the life of one
subscription (`SnapshotAsync` once, then `UpdateAsync`), and `AuspiceHost.ServeAsync` starts a source per attended
feed, stops it when the Pilgrim departs, and **seals only that feed** if the source throws — one bad feed never takes
the Shrine down. Attending a feed the Shrine does not host is answered with `Sealed("no such feed")` rather than
silence, so a client never hangs waiting for a stream that will never arrive.

**Why this lives in the core, not in a product.** The Pilgrim half of the rite runs inside the CupriNet client stack —
including where that stack is compiled to WebAssembly to run in a browser. Putting it downstream would mean either a
product injecting protocol code into a stack it does not own, or the WASM client taking a dependency on a product.
So it sits beside the Oracle, and a downstream host re-exposes it as its own sugar.

## The message ceiling (and what it rules out)

*(For the whole picture across both transport families — which ceiling applies where, every constant, and the three
ways past one — see [transports-and-limits.md](transports-and-limits.md). This section covers why the Shrine path has
the number it has.)*

Every rite here sends **whole messages**, and the browser sets a hard ceiling on how big one may be. A WebRTC
DataChannel message cannot exceed the `a=max-message-size` an endpoint advertises, and CupriWebRTC's SCTP association
fixes that at **256 KiB** (`SctpAssociation.MaxMessageBytes`), **rejecting** anything larger rather than truncating it,
in both directions. That is **64× tighter than the 16 MiB** a TCP vessel frame allows — the WebRTC path is the
constraint, not the relief.

- The **Auspice** enforces its own cap (`AuspiceCodec.MaxPayloadBytes`, 192 KiB) when encoding *and* decoding, so an
  oversized payload fails at the rite with a clear message instead of deep inside the transport. The remaining 64 KiB
  is headroom for rite framing, the vessel header and Noise/DTLS overhead.
- The **Conduit**, on a Pilgrimage, enforces the same 192 KiB (`ConduitCodec.MaxPayloadBytes`) both ways. On an
  `Arcanum` channel it keeps the 16 MiB a Vessel frame allows — no browser is on that path, and the cap exists to
  serve the transport, not to be a house style. A protocol needing more than one message's worth chunks it, or
  carries the bytes as a relic.
- The **Oracle** enforces its own body cap (`OracleCodec.MaxBodyBytes`, the same 192 KiB) when encoding *and*
  decoding. `OracleHost` turns an oversized handler response into a clear 500 (naming the Relic rite) rather than a
  torn-down visit, and `StaticFileOracleHandler` refuses an over-ceiling file *before reading it* — so a large body
  fails at the rite, with a reason, never at the transport.

**The consequence is structural, not cosmetic.** A hosted-app tier — serving a WASM application blob that the client
fetches and executes — is **blocked**, not merely inefficient: such a blob is multi-MB and the Oracle materialises a
whole body into one message. Serving one is therefore not a matter of tuning; it needs a chunked path.

**The route is the Reliquary, and it is now wired in as the Relic rite** (stream 8 on the Pilgrimage, beside the
Oracle and the Auspice): a named relic resolves to a `ReliquaryManifest`, and each chunk travels as its own
request/response under the same 192 KiB frame ceiling the Auspice enforces — so bulk content is carried by the one
rite built for it, and it brings a property a single opaque response could never have: **per-chunk hashes let the
client verify a blob's integrity before executing it.** A relic tops out around 1.1 GiB (its manifest must itself fit
one frame). The Oracle now guards its own bodies against the same ceiling, so the two rites meet cleanly: at or under
192 KiB a body may ride the Oracle; past it, the Oracle refuses with a reason and the Relic rite carries it.

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
- **What a feed may publish.** A Pilgrim is anonymous and unauthenticated, so an Auspice feed is world-readable. Peer
  data itself is fine to serve — `PeerRecord`s are signed by their owners precisely to be redistributed, and every
  Intonation's Litany already hands out sampled Sigils. The line is elsewhere: **never publish
  `ConstellationEntry.Bucket`, `Standing` or `Taint`.** Those are not facts about peers, they are *this node's private
  judgements* — the code is explicit that "local Standing/Taint never propagate as authoritative fact" — and streaming
  them tells a Sybil operator whether its identities have been noticed or quarantined, turning the feed into a
  detection-evasion aid. The trap is that `ConstellationEntry` bundles them in with the public record, so
  `Serialize(constellation.Entries)` leaks them by accident: project `entry.Record` explicitly instead. Rule of thumb —
  **if the control plane wouldn't serve it to a peer, don't push it to an anonymous visitor.**
- **Traffic analysis.** Encryption hides the bytes, not how many there are. Feed content and names are sealed, but
  message **size and timing** stay visible, and a live feed is a far richer signal than a request/response burst — update
  sizes track content, snapshot size tracks how much state exists. `AuspicePadding` quantises each message to a bucket
  (512-byte blocks by default; `PowersOfTwo` for ~10 buckets across the whole range) *before* it is sealed, so the
  padding is encrypted with everything else and a reader never sees it. It does **not** hide timing: an update per event
  still reveals the event rate, so a feed whose cadence is itself sensitive needs the source to smooth it.

## Scope & limits (v1)

- **Reachable hosts only** — a browser can dial only a node with a reachable WebRTC endpoint (public / WebRTC-enabled
  hosts). NAT'd hosts need brokered WebRTC later.
- **Live self-hosted** — reachable only while the host is online; nothing is stored in the overlay.

## Phased plan (the CupriNet-side work)

1. ✅ **Oracle rite** over `Conduit` — request/response with headers and bodies, plus the content-handler seam
   (`IOracleHandler`): a `FilePathGuard`-guarded static-file handler, a delegate handler, and `OracleHost.ServeAsync`
   (a keep-alive request loop that turns a handler throw into a 500 rather than dropping the Shrine).
2. ✅ **Signet** — the `cupri1…` keypair (`Signet`) + per-Shrine encrypted-store persistence (`SignetStore`).
3. ✅ **Pilgrimage** — `HostShrine` / `AcceptPilgrimageOverVesselAsync` / `PilgrimageOverVesselAsync`. The host
   presents *only* the Signet through the existing Noise XX + identity binding, so it is self-authenticating with
   **no new crypto** and nothing identifying on the wire; the Pilgrim pins it under a throwaway per-visit identity;
   no overlay join. A browser DataChannel routes into it automatically when a Shrine is hosted. The Pilgrim half is
   also reachable **without a node** — `Pilgrimage.OverVesselAsync` in `CupriNet.Shrine`, which references nothing
   that binds a socket, so it runs in a browser WASM build; `CupriNode.PilgrimageOverVesselAsync` delegates to it.
4. ✅ **Intonation Signet field** — optional signed `Shrine` field (`shrine-address` CupriMark component),
   **opt-in** via `HostShrine(…, advertiseInLink: true)`, with the L1-linkage trade documented at the call site.
5. ✅ **Publish `CupriNet.WebRtc`** to the Wixely feed, so downstream products can consume the WebRTC binding.
6. ✅ **Auspice rite** — named live feeds (attend → snapshot → updates) beside the Oracle, muxed onto one Pilgrimage,
   with the payload ceiling enforced both ways and a per-feed source seam (`IAuspiceSource` / `AuspiceHost`).
7. ✅ **Reliquary over the Shrine path — the Relic rite.** A named relic is fetched as its `ReliquaryManifest` then
   chunk-by-chunk request/responses on a stream of its own (no head-of-line blocking of the Oracle or the Auspice),
   every frame under the same 192 KiB ceiling the Auspice enforces, every chunk verified against the manifest as it
   arrives and the whole file before the bytes are returned — integrity provable **before** a blob is executed. The
   host names relics through `IRelicSource` (`StaticRelicSource` proves manifest fit at composition time); the
   Pilgrim end (`ShrineSession.FetchRelicAsync` / `RelicSession`) lives in the node-free client stack, so it runs in
   a browser WASM build. Frames carry the `relic` CupriMark component.
8. ✅ **Conduit over the Shrine path.** The raw duplex rite, reachable from a Pilgrimage at last: `IConduitHandler`
   / `ConduitHost.ServeAsync` on the host, `ShrineSession.Conduits` on the Pilgrim, on stream 4 beside the other
   three. The Pilgrimage constructor is **keyless** (the Noise vessel is already the boundary, as it is for the
   Oracle, Auspice and Relic — no session key is plumbed anywhere) and takes **no author identity**, so a Pilgrim
   stays unlinkable by construction. Padded by default (`ConduitPadding`) and capped at the browser ceiling; a
   Shrine hosting no conduit answers with a sealed frame rather than silence, and a handler that throws seals only
   the conduit. A sealed frame is yielded once, with its reason, and every read after it returns null — the host
   holds the visit open for the other rites, so a consumer waiting for a close that will never come would hang.
   The reserved bit is honoured on the Pilgrimage wire only: on an `Arcanum` channel the top flag predates it and
   stays the application's, so no pre-0.3.5 conduit is ended by a bit it was already using. Carries the `conduit` CupriMark component on the Pilgrimage wire only — the Arcanum conduit predates
   the catalogue and its bytes are unchanged.
9. **Next (product):** the Nodestar repo — the web front, the served client, and the author tooling.
10. **Later (protocol):** chunked/ranged Oracle
   bodies; Noise NK (a Pilgrim that sends no static key at all); **multi-Shrine** hosting on one endpoint (an encrypted
   target selector — deliberately *not* pre-Noise cleartext, so the network never learns which site); brokered WebRTC
   to NAT'd hosts; private (Watchword-gated) Shrines.

### A note on the session kind

The original sketch had a `Shrine` marker alongside `Channel` in the post-Noise session-kind byte. It turned out to be
unnecessary: because the host presents the Signet *as* its handshake identity, a Pilgrimage is already unambiguous to
both ends, and adding a marker would only have created a second way to say the same thing. It becomes relevant again
for multi-Shrine hosting, where one endpoint must route to one of several Signets.

## Consumers

The **Nodestar** product (separate repo, `CupriNodestar`) is the first consumer: it wraps a CupriNode + a clearnet HTTP
front + a served WASM client that runs the Pilgrimage + Oracle in the browser, plus author tooling. The Shrine protocol
here is generic, though — any CupriNet client can consult a Shrine.
