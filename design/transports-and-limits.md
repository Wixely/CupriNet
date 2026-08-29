# Transports and limits — what each part is, what it is called, what it will carry, and why

**Every size limit in CupriNet comes from one of two places: the frame ceiling of the transport underneath you, or a
rite's own cap.** They are different numbers for different reasons, and the most common mistake is to read a rite's
192 KiB as a CupriNet-wide rule. It is not. It is the browser's number, and it applies where a browser can reach.

This is the reference for which parts are which, what they are called, and what happens when you exceed them. Every
section also says **why** it is that way, because most of these numbers and shapes look arbitrary until you know what
they are defending against — and a limit whose reason is unknown is one someone eventually "fixes".

## The two vessel families

A **Vessel** is one transport connection. Everything above it — Noise, the mux, the rites — is written against
`IVessel` and does not know or care which family it is on. There are exactly two, and the difference between them is
the source of every ceiling in this document.

**Why two and not one.** The split is not an accident of growth; it is the one thing that genuinely differs between
these transports. A byte stream has no message boundaries, so a framing layer must invent them. A DataChannel already
has them, so inventing a second set would mean a length prefix inside a message whose length is already known — pure
overhead, and a second place for the two ends to disagree. Normalising both onto one representation would therefore
have meant either paying for framing nobody needs, or dropping down to a lowest common denominator that a byte stream
cannot provide. Instead the seam sits at `IVessel`, one level above the difference, and each family satisfies it in
the way its transport actually works. The cost of that choice is the whole of this document: two families means two
ceilings, and code that is portable between them has to respect the smaller.

### 1. The byte-stream family — TCP, reliable UDP, Tor

A duplex byte stream with **CupriNet's own length-prefixed framing** on top, because a byte stream has no message
boundaries of its own. One class, [`Vessel`](../src/CupriNet.Vessel/Vessel.cs), reached through four factories:

| You want | Use | Lives in |
|---|---|---|
| TCP (the ordinary path) | `TcpVessel.ConnectAsync` / `VesselListener` | [TcpVessel.cs](../src/CupriNet.Vessel/TcpVessel.cs) |
| Reliable UDP (a hole-punched path) | `UdpVessel.Over(IPacketLink)` | [TcpVessel.cs](../src/CupriNet.Vessel/TcpVessel.cs) |
| Tor (an onion stream) | `StreamVessel.Over(stream)` | [StreamVessel.cs](../src/CupriNet.Vessel/StreamVessel.cs) |
| Anything else stream-shaped | `StreamVessel.Over(stream)` | [StreamVessel.cs](../src/CupriNet.Vessel/StreamVessel.cs) |

The reliable-UDP path is an `ArqStream` over the packet link, so it is a byte stream by the time the framing sees it —
which is why it inherits exactly the same ceiling as TCP. Tor is the same story: an onion stream handed to
`StreamVessel.Over`, after which nothing downstream knows it is Tor.

**Frame ceiling: 16 MiB** (`FrameCodec.DefaultMaxFrameSize`), and it is a *parameter* — every factory takes
`maxFrameSize`, so a deployment that wants a smaller one may say so. Both ends must agree; a frame larger than the
receiver's limit is refused, not truncated.

**Why there is a cap at all**, given a TCP stream would happily carry more: the length prefix is written by the
*peer*, and the very next thing the reader does with it is allocate. [`FrameCodec.ReadAsync`](../src/CupriNet.Codex/FrameCodec.cs)
checks the declared length against the limit *before* `new byte[length]`, so an unbounded prefix would be an
unauthenticated peer choosing how much memory you commit — one four-byte lie for an out-of-memory kill. The cap is an
allocation guard first and a size limit second, which is also why 16 MiB is not a carefully tuned figure: it wants to
be far above anything legitimate and far below anything ruinous, and anywhere in that range will do.

### 2. The message family — WebRTC

[`DataChannelVessel`](../src/CupriNet.Vessel/DataChannelVessel.cs) over an `IDataChannel`. A DataChannel already
preserves message boundaries, so there is **no length prefix**: one CupriNet frame is exactly one channel message,
shaped `[varuint streamId][payload]`. That is precisely why a browser drops into the same
`ConjoinOverVesselAsync` seam with no adapter — and it is also where the tight ceiling comes from.

**Message ceiling: negotiated, and 256 KiB is only what *we* offer.** A DataChannel message cannot exceed the
`a=max-message-size` the two ends agree on. CupriWebRTC's SCTP association offers 256 KiB
(`SctpAssociation.MaxMessageBytes`) and **rejects** anything larger rather than truncating or fragmenting it, but the
effective limit is the **minimum of the two ends** — and the interoperable floor is **64 KiB**
(`RTCSctpTransport.maxMessageSize`), far below what the rites use. Every pairing measured so far agrees to 256 KiB;
that is a property of those pairings, not a guarantee.

> This document previously called the 256 KiB "fixed". That was wrong, and worth correcting in place because the
> advice below — read the ceiling from the session rather than hard-coding it — was given on the strength of it.
> Nothing between a rite and the wire reconciles the two numbers: `DataChannelVessel` sends exactly one message per
> frame and **never fragments**. So a peer negotiating low would have a legal frame refused by the transport.
>
> `IDataChannel.MaxMessageBytes` now carries the negotiated figure, and `RiteTransport.EnsureCarriesRiteFrames`
> refuses such a transport **when you connect**, naming both numbers — the same bargain every other ceiling makes.
> An adapter that cannot learn the figure reports 0 and is not checked, which is the honest answer rather than a
> guess. The CupriWebRTC adapter reports 0 today: the value lives on the SCTP association and the channel does not
> surface it, so the check is inert on that path until it does.

There is no `maxFrameSize` knob here. This is the constraint the whole Shrine path is sized against.

> **Which vessel does a Shrine accept?** Any of them — but the vessel has to be handed to
> `AcceptPilgrimageOverVesselAsync` / `Pilgrimage.OverVesselAsync` by a caller who owns it. A node's own L1 listen port
> does **not** serve Shrines: it presents the node's Sigil, so a visit there pairs you as an overlay peer and every
> rite goes quiet. See *How a Pilgrim actually reaches a Shrine* in [shrines.md](shrines.md#how-a-pilgrim-actually-reaches-a-shrine)
> — that silence is the single most misread symptom in this stack.

### And one wrapper that is not a transport

[`NoiseVessel`](../src/CupriNet.Conjunction/NoiseVessel.cs) is an `IVessel` that wraps another and encrypts every
frame with a completed Noise transport. Stream ids are preserved; only payloads are sealed. Every authenticated
session — an Arcanum channel, a Pilgrimage — runs over one, whatever transport is underneath.

**It serialises sends**, because the Noise nonce order must match the wire order — one lock for the whole vessel, all
streams through it. Receives are single-consumer, the mux pump, so decryption stays in step.

**What `IVessel` does and does not promise.** It promises nothing about concurrent sends, deliberately. Several
implementations serialise, each for a reason of its own: a byte-stream `Vessel` must or frames would interleave into
one unparseable stream; `NoiseVessel` must or the nonce order would drift; `DataChannelVessel` need not, because SCTP
preserves its message boundaries for it. Those are three different reasons, not one shared guarantee, and **reading
the promise off whichever implementation you are looking at is the mistake the interface now warns against.**

A caller that sends from several tasks serialises them itself — which is exactly what `AuspiceSession`,
`RelicSession` and `ConduitSession` each do with a lock of their own, and why `OracleSession` needs none (its
exchange carries no correlation id, so concurrent consults would take each other's responses whatever the transport
did underneath). That places the protection on the party that suffers if it is missing, which is the same rule the
decode-side ceilings follow: **the Ward belongs where the damage lands, not upstream on trust.**

**So where does the guarantee actually come from?** Two independent places, and this is the authoritative account —
[CupriNet#4](https://github.com/Wixely/CupriNet/issues/4) reached it in stages and is superseded by what follows.

1. **`NoiseVessel`, on every authenticated path.** Its lock is not optional and not defensive: the Noise nonce order
   must match the wire order or the far side cannot decrypt. Every Arcanum channel and every Pilgrimage runs over one,
   so on any shipped path sends are already strictly serial before they reach a transport at all.
2. **Each rite, for itself.** The per-rite locks are what hold when a rite is driven over a *bare* vessel with no
   Noise above it — direct or embedded use, and every test in this repo that constructs a session over a raw
   `TcpVessel`.

Neither makes the other pointless. (1) covers production and (2) covers everything else, and (2) is the one that
survives a future `IVessel` implementation that quietly does the wrong thing. What is *not* true, and was briefly
believed here, is that `IVessel` owes serialisation: it does not, it never did, and requiring it would have added an
unenforceable promise that rites might then have stopped protecting themselves against.

## Which ceiling applies to you

Read down the column for the path you are on:

| | Arcanum channel over TCP / UDP / Tor | Pilgrimage (Shrine) — see note | Pilgrimage from a **browser** |
|---|---|---|---|
| Transport frame ceiling | 16 MiB | 16 MiB or 256 KiB, per transport | **256 KiB, hard** |
| Epistle (message) | vessel frame | — | — |
| Conduit (raw duplex) | **16 MiB** | 192 KiB | 192 KiB |
| Oracle (request/response) | — | 192 KiB body | 192 KiB body |
| Auspice (live feed) | — | 192 KiB payload | 192 KiB payload |
| Relic (bulk) | — | 192 KiB per chunk | 192 KiB per chunk |
| Reliquary (file transfer) | 8 MiB per chunk | — | — |

The rites in the middle column enforce **192 KiB** whether or not a browser is actually on the connection. That is
deliberate: a Shrine does not know how its next Pilgrim will arrive, and a site that only works over TCP is a site
that breaks the first time someone opens it in a browser. One number, enforced everywhere, so a payload that works
in a test works in Chromium.

The Arcanum column has no browser on it and keeps the transport's own ceiling.

> **"Any transport" is true of the rite, not of how you get one.** A Pilgrimage runs over any vessel, but a node
> answers three different ways depending on where you knock. `node.ListenForPilgrims()` opens a **port of its own**
> that presents the Signet to everything arriving — dial it and pin the address, no accept loop required. A browser
> DataChannel is routed into the Shrine automatically. Anything else — Tor, a test pair, a WASM client — is a vessel
> the **caller** owns and hands to `AcceptPilgrimageOverVesselAsync` / `Pilgrimage.OverVesselAsync`.
>
> The node's **L1 listen port is not one of the three**: it answers with
> `NoiseConjunction.AcceptAsync(vessel, Identity, …)`, presenting the node's own Sigil, because it exists to pair
> overlay peers. Knock there for a site and the failure is quiet rather than loud — pinning the Signet fails
> correctly, but pinning the *node's* Sigil **succeeds**, into a healthy session with nothing hosting behind it and
> every rite answering with a closed stream. That reads as a broken rite and is not one. On the Shrine port the same
> mistake cannot complete at all. Full write-up:
> [*How a Pilgrim actually reaches a Shrine*](shrines.md#how-a-pilgrim-actually-reaches-a-shrine).

### Why 192 and not 256

The 64 KiB difference is headroom, not caution. A rite payload is not what goes on the wire — around it sit the rite's
own framing, the vessel's `[varuint streamId]` header, and Noise's ciphertext expansion. 192 KiB of payload leaves
room for all three inside a 256 KiB message. `MaxPaddedBytes` (224 KiB) is the related figure for the two rites that
pad: it bounds the *encoded frame after padding*, so quantising a size can never grow a message into one the transport
would refuse.

### Where each number lives

Three kinds of number live here, and they are not equally negotiable:

- **Fixed** — a `const`. Changing it is a wire change for everyone. Do not.
- **Per-connection** — a constructor parameter with a default. Both ends must agree.
- **Per-call** — an argument you pass, or a record property you override. Yours to set.

| Where | Value | Kind | Meaning |
|---|---|---|---|
| `IDataChannel.MaxMessageBytes` | negotiated | **not ours, and not constant** | what the two ends agreed; 256 KiB is what CupriWebRTC offers, 64 KiB the interoperable floor |
| `RiteTransport.RequiredMessageBytes` | 232 KiB | fixed | what a message transport must carry for the site rites; checked at connect |
| `FrameCodec.DefaultMaxFrameSize` | 16 MiB | per-connection | byte-stream frame ceiling; every factory takes `maxFrameSize` |
| `OracleCodec.MaxBodyBytes` | 192 KiB | fixed | one request or response body |
| `AuspiceCodec.MaxPayloadBytes` | 192 KiB | fixed | one snapshot or update |
| `AuspiceCodec.MaxPaddedBytes` | 224 KiB | fixed | encoded Auspice frame after padding |
| `RelicCodec.MaxPayloadBytes` | 192 KiB | fixed | one chunk, manifest, or reason |
| `ConduitCodec.MaxPayloadBytes` | 192 KiB | fixed (the default) | one Pilgrimage conduit frame |
| `ConduitCodec.MaxChannelPayloadBytes` | 16 MiB − 64 KiB | fixed (the default) | one Arcanum conduit frame |
| `ConduitCodec.MaxPaddedBytes` | 224 KiB | fixed | encoded conduit frame after padding |
| `ConduitSession.MaxPayloadBytes` | per session | **per-connection** | what *this* conduit accepts; set via the constructor's `maxPayloadBytes` |
| `ReliquaryLimits.MaxChunkSize` | 8 MiB | **per-call** | largest Reliquary chunk (`DefaultChunkSize` is 64 KiB) |
| `ReliquaryLimits.MaxTotalBytes` | 8 GiB | **per-call** | largest Reliquary transfer |
| `ReliquaryLimits.MaxChunksPerFile` | ~1M | **per-call** | Ward against a hostile manifest |
| `RelicSession.DefaultMaxFileBytes` | 256 MiB | **per-call** | client-side guard on one `FetchRelicAsync`, via its `maxBytes` argument |

`ReliquaryLimits` is a record of `init` properties, so a caller constructs its own to raise or lower any of them —
they are Wards a *receiver* applies to what a peer sends, not protocol constants. The rite ceilings above them are
`const` precisely because both ends must agree without negotiating.

**With one exception worth knowing before you rely on it.** `ReliquaryBuilder` and `ReliquaryManifest.Decode` take an
optional `limits`, so the Reliquary path is genuinely tunable. `RelicCodec.Decode` does **not** — it constructs
`new ReliquaryLimits()` internally to bound the file and chunk indices a hostile frame may carry. So over a
Pilgrimage those two Wards are fixed at their defaults whatever you pass elsewhere. That is deliberate (a decoder
that let the sender pick its own bounds would not be bounding anything), but it means "per-call" applies to building
and reading a manifest, not to decoding a relic frame.

## Getting past the ceiling

**The ceiling is on one message, never on the content.** Nothing here caps what you can move; it caps how much of it
may travel in a single frame. Three ways past, in order of preference:

### 1. Send it as a Relic — the built path

The rite designed for exactly this. A named relic resolves to a `ReliquaryManifest`, then travels chunk by chunk as
its own request/responses on its own stream — no head-of-line blocking of the Oracle or the Auspice. Every chunk is
verified against the manifest as it arrives and the whole file before the bytes are returned, so **a client can prove
a blob's integrity before executing it** — a property a single large response could never have.

```csharp
// Host: name it once, before hosting. Manifest fit is proven at Add time, not at fetch time.
var relics = new StaticRelicSource(node.Suite).Add("app/client.wasm", wasmBytes);
node.HostShrine(signet, handler, feeds, relics);

// Pilgrim: verified end to end, concurrent with everything else on the visit.
var wasm = await shrine.FetchRelicAsync("app/client.wasm", cancellationToken: ct);
```

A relic tops out around **1.1 GiB**, because the manifest must itself fit in one 192 KiB frame: 192 KiB of 32-byte
chunk hashes is ~6,100 chunks, and 6,100 × 192 KiB ≈ 1.1 GiB. Past that, split it into several relics.

### 2. Chunk it yourself, over a Conduit

For a protocol with its own framing that simply needs to move something larger than one frame, chunk above the rite.
The Conduit gives no help here and is not trying to: `ProtocolId` and `SchemaVersion` are yours, so sequencing and
reassembly are yours too. Use `ConduitSession.MaxPayloadBytes` as the size to chunk against rather than hard-coding
192 KiB — it reports what *this* session will actually accept, which differs between the Pilgrimage and the Arcanum
constructor.

### 3. Stay on a channel, if a browser genuinely never will

An `ArcanumSession.Conduits` frame may be up to `ConduitCodec.MaxChannelPayloadBytes` — a vessel frame less 64 KiB of
headroom, so **16 MiB − 64 KiB**, not a flat 16 MiB. The headroom is there for the same reason the Pilgrimage path
leaves 64 KiB under the browser's 256 KiB: the payload is not what goes on the wire, and this rite's framing, the
Veil's nonce and tag, the vessel header and Noise's expansion all sit around it.

> Before 0.3.7 this constant was set to the frame size exactly, which meant the documented maximum could not actually
> be sent: a payload at 16 MiB encoded to 17 bytes more than a frame may carry, and the refusal came from `FrameCodec`
> talking about frame bytes — at the transport, naming a number the caller never chose. That was the one place the
> library broke its own rule about failing at the rite. Read the figure from
> `ConduitSession.MaxPayloadBytes` rather than hard-coding it and the change is invisible to you.

This path is the right answer only when the peers are channel members on a stream transport and the traffic will never
be served to a browser. It is the wrong answer for anything a Shrine hosts.

### Why the rite refuses instead of the transport

Every ceiling here could have been left to the transport to enforce — it will refuse an oversize frame regardless. The
rites check anyway, and check on **both** encode and decode, for three separate reasons.

**A transport failure cannot say anything useful.** By the time an SCTP association rejects a message, the context
that would make the error actionable — that this was an Oracle body, that a Relic exists for exactly this case — is
several layers below the code that could act on it. What surfaces is a dead connection. Checking at the rite turns
that into a typed exception naming the limit and the alternative, thrown before any I/O happens, which is the
difference between a bug someone fixes in a minute and one they file.

**A transport failure is disproportionate.** Losing the vessel loses the whole visit: the page fetch, the live feed,
the transfer in flight. One oversize response should cost one response. That is why `OracleHost` turns an oversize
handler return into a 500 rather than letting it reach the wire, and why `StaticFileOracleHandler` refuses an
over-ceiling file *before reading it* — the cheapest possible failure, at the earliest possible point.

**Encode-side alone would be trusting the peer.** A sender that checks its own output protects a well-behaved network
from itself. It does nothing about a hostile or simply older peer that sends 200 MiB, and the decode side is where
that lands — again, immediately before an allocation. The decode check is the Ward; the encode check is the courtesy.

### What failure looks like

Every rite checks its own ceiling on **encode and decode**, before any I/O, so exceeding one is a
`CodexFormatException` naming the limit and (for the Oracle and the Conduit) pointing at the Relic. It is never a
truncated message, a torn-down visit, or a failure deep inside an SCTP association. `StaticFileOracleHandler` goes
further and refuses an over-ceiling file *before reading it*, and `OracleHost` turns an oversize handler response into
a clear 500 rather than dropping the Shrine.

## The stream map

One vessel carries many logical streams, demultiplexed by [`VesselMux`](../src/CupriNet.Vessel/VesselMux.cs) so a page
fetch, a live feed, a blob and a raw session all proceed at once on one connection.

**Why multiplex rather than open a second connection.** On this stack a connection is expensive in ways a socket is
not: each one costs a Toll, a Noise handshake, and — on a NAT'd path — its own hole punch, and on the browser path a
whole second DataChannel. Worse, a second connection is a second thing an observer can correlate, and a second
identity decision to get right. Multiplexing pays that once. The per-rite streams then buy something the single
connection would otherwise lose: **head-of-line isolation.** A relic transferring a hundred megabytes sits on stream 8,
so it cannot stall a page fetch on stream 5 — which is exactly why the Relic rite was given its own stream rather than
being carried as large Oracle responses.

| Stream | Carries | Context |
|---|---|---|
| 0 | Conjunction handshake | pairing |
| 1 | peer exchange | overlay |
| 2 | Consecration handshake | channel |
| 3 | Epistles (and Effigy cover traffic, deliberately alike) | channel |
| 4 | **Conduit** | channel *and* Pilgrimage |
| 5 | **Oracle** (Pilgrimage) · reflexive endpoint (paired peer) | context-dependent |
| 6 | overlay control | overlay |
| 7 | **Auspice** | Pilgrimage |
| 8 | **Relic** | Pilgrimage |

Two apparent collisions in that table are deliberate, and for different reasons.

**Stream 3 carries both Epistles and Effigy cover traffic — because it must.** An effigy is a decoy channel session
whose only purpose is to look exactly like a real one. Giving decoy traffic its own stream id would hand an observer a
one-field test for telling real conversations from cover, which is precisely the distinction the cover exists to
destroy. Indistinguishability is the feature; the shared id is how it is achieved.

**Stream 5 carries the Oracle on a Pilgrimage and the reflexive endpoint on a paired peer — because the two never
share a vessel.** A Pilgrimage is not a pairing: different handshake, different identity, different lifetime. The ids
are scoped by connection kind, so reusing a number across kinds costs nothing.

### What is guaranteed to arrive, and what is not

**Ordered, never torn, not retried.** The transports underneath are all ordered and each frame is one whole message,
so frames arrive in the order they were sent and never interleave or duplicate. What no rite except the Epistle adds
is *recovery*: only the Epistle has the **Vigil**, which acknowledges and retries. The Oracle, Auspice, Relic and
Conduit have no acknowledgement and no sequence number.

That matters because of the Ward below. When a stream's inbound queue is full, further frames are **dropped**, and
dropped silently — the demultiplexer's write reports success, and nothing in a `ConduitFrame` or an `AuspiceFrame`
lets a receiver notice a gap. An Epistle lost that way comes back on the next retry. **A conduit frame lost that way
is gone**, and neither end finds out.

In practice this is reachable only by a receiver that stops draining: a thousand frames must already be queued for
that stream. The shape that cannot lose anything is the obvious one — take each frame and come straight back, handing
the work to something else, rather than processing inline in the receive loop. If a protocol genuinely must *detect*
loss rather than avoid it, it carries its own sequence number in its own payload; the rite will not do it for you.

The Relic is the exception worth noting, and not by having retries: every chunk is verified against the manifest and
the whole file before bytes are returned, so a lost chunk surfaces as a failed fetch rather than a silently short
file.

Wards: at most 64 concurrent streams per connection and 1,024 queued frames per stream, after which inbound frames
are dropped rather than growing memory without limit — the reliable rites recover anything dropped that way. **Dropping
rather than blocking is the point:** a queue that blocks under flood lets one hostile stream stall the mux pump and so
every other rite on the connection, and a queue that grows lets it exhaust memory instead. Dropping bounds both, and
pushes recovery up to the rites that already know how to retry.

## Naming

The ceremonial vocabulary maps one-to-one onto ordinary components. The transport and rite halves:

| Lexicon | Plainly |
|---|---|
| **Vessel** | one transport connection (framed, multiplexed) |
| **Concordance** | the L1 overlay — routable, relayed, metadata only |
| **Arcanum** | an L2 channel between members — direct-only, never relayed |
| **Shrine** | a site hosted over L2 at a `cupri1…` **Signet** address |
| **Pilgrimage** | the connect handshake to a Shrine; a **Pilgrim** is the ephemeral visitor |
| **Conjunction** | the Noise_XX first-contact handshake |
| **Consecration** | the channel key-confirmation handshake |
| **Toll** | the stateless pre-handshake anti-DoS cookie |
| **Veil** | the inner per-rite seal (used on a channel; *not* on a Pilgrimage — see below) |
| **Epistle** | a message · **Vigil** its reliable retry |
| **Conduit** | a raw duplex session under an application's own `ProtocolId` |
| **Oracle** | HTTP-shaped request/response |
| **Auspice** | a named live feed — attend, snapshot, then updates |
| **Relic** | bulk content, chunked and hash-verified, over a Pilgrimage |
| **Reliquary** | the underlying chunked file-transfer format |

### One asymmetry worth knowing, and why it exists

On an **Arcanum channel** the rites are Veil-sealed: an inner encryption layer keyed from the channel's session key,
which is why those constructors take a session key and a crypto suite. On a **Pilgrimage** they are not — every rite
has a **keyless** constructor for that path, and no session key is ever plumbed out of `Pilgrimage.OverVesselAsync`.

*(Every rite, as of 0.3.6. The Conduit was the exception until then: it had only key-taking constructors, which is why
it could be reached on a channel and not from a visit at all —
[CupriNet#3](https://github.com/Wixely/CupriNet/issues/3). The rule below is the one it should always have followed.)*

The rule of thumb: *if you are holding a channel key to construct a rite, you are on the channel path; if you are
handing it only a stream, you are on the Shrine path.*

**Why the Veil is not simply redundant on a channel.** The two layers make genuinely different claims. The Noise
vessel authenticates the **node** you are connected to — its Sigil. The Veil is keyed from the **Watchword**, so
opening a frame proves the sender holds the channel secret, which is a claim about *membership*, not about which box
is at the other end. The two are deliberately decoupled: `ConsecrateAsync` takes a `ChannelIdentity` precisely so a
member may speak under a persona that is not their overlay identity. Binding content to the transport alone would
re-couple exactly what that option exists to separate.

**Why a Pilgrimage needs only one layer.** There is no second claim to make. The host presents the Signet *as* its
handshake identity — `NoiseConjunction.AcceptAsync` is handed `NodeIdentity.FromSeal(signet.Seal)` — so pinning the
Signet during the handshake already proves everything a Shrine can prove about itself. And the Pilgrim is anonymous
by design, so there is no membership to demonstrate in the other direction. A Veil there would encrypt the same claim
twice and hand a caller a key it has no reason to hold.

The Conduit carries the split one step further: its Pilgrimage constructor takes **no author identity** either.
Authenticated authorship is right on a channel, where members are durable and attribution is the point; on a visit
the Pilgrim is meant to be unlinkable, and signing frames under a lasting key would quietly undo that. Where a rite
cannot be misused, it should not be *possible* to misuse — hence a separate constructor rather than a nullable
argument.

### Why some rites pad and others do not

`AuspicePadding` and `ConduitPadding` quantise a message's length before it is sealed; the Oracle and the Relic have
no equivalent. That is not an omission in either direction.

Encryption hides the bytes, not how many there are. A **live feed** is the richest possible version of that leak:
update sizes track content and snapshot size tracks how much state exists, sampled continuously for as long as
someone watches. A **duplex conduit** is at least as rich, and bidirectional. Both pad by default.

A **relic** needs no padding because it is already uniform by construction — `RelicCodec.DefaultChunkBytes` *is*
`MaxPayloadBytes`, so every chunk but the last is exactly the same size, and quantising identical values achieves
nothing. An **Oracle** exchange is a single request/response burst rather than a continuous stream, so it offers an
observer far fewer samples to work with; padding there would cost bandwidth on every page fetch to blur a signal that
is weak to begin with.

Note what padding does *not* fix, in every case: it hides size, never **timing**. A feed emitting one update per
event still reveals the event rate, and a source whose cadence is itself sensitive has to smooth it upstream.

### Two smaller decisions, since they surprise people

**No `ProtocolId` registry.** The Conduit never dispatches on it and CupriNet keeps no list. Owning that namespace
would make the library an authority on applications it knows nothing about, and force a version bump every time
someone wanted a number. The cost is that an unrecognised id has no library-defined behaviour, so `IConduitHandler`
documents the convention instead — check it, and seal an unknown one rather than ignoring the frame.

**The Arcanum conduit carries no CupriMark version byte, and the Pilgrimage one does.** Every rite added *after* a
release carries its own component, because it cannot inherit compatibility from a handshake that shipped before it
existed. The Conduit predates the catalogue on the channel path, where it inherits from the Consecration handshake,
so adding a byte there would be a flag day for every deployed peer — the exact thing CupriMark exists to avoid. The
Pilgrimage conduit is new wire with no deployed peers to break, so it carries the component from the start. Same
reasoning produced the other asymmetry on that path: the reserved top flag bit is honoured on the Pilgrimage wire
only, because a channel application may already be using it.
