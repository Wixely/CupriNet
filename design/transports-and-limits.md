# Transports and limits — what each part is, what it is called, and what it will carry

**Every size limit in CupriNet comes from one of two places: the frame ceiling of the transport underneath you, or a
rite's own cap.** They are different numbers for different reasons, and the most common mistake is to read a rite's
192 KiB as a CupriNet-wide rule. It is not. It is the browser's number, and it applies where a browser can reach.

This is the reference for which parts are which, what they are called, and what happens when you exceed them.

## The two vessel families

A **Vessel** is one transport connection. Everything above it — Noise, the mux, the rites — is written against
`IVessel` and does not know or care which family it is on. There are exactly two, and the difference between them is
the source of every ceiling in this document.

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

### 2. The message family — WebRTC

[`DataChannelVessel`](../src/CupriNet.Vessel/DataChannelVessel.cs) over an `IDataChannel`. A DataChannel already
preserves message boundaries, so there is **no length prefix**: one CupriNet frame is exactly one channel message,
shaped `[varuint streamId][payload]`. That is precisely why a browser drops into the same
`ConjoinOverVesselAsync` seam with no adapter — and it is also where the tight ceiling comes from.

**Message ceiling: 256 KiB, and it is not ours to raise.** A DataChannel message cannot exceed the
`a=max-message-size` an endpoint advertises; CupriWebRTC's SCTP association fixes that at 256 KiB
(`SctpAssociation.MaxMessageBytes`) and **rejects** anything larger in both directions rather than truncating or
fragmenting it. There is no `maxFrameSize` knob here. This is the constraint the whole Shrine path is sized against.

### And one wrapper that is not a transport

[`NoiseVessel`](../src/CupriNet.Conjunction/NoiseVessel.cs) is an `IVessel` that wraps another and encrypts every
frame with a completed Noise transport. Stream ids are preserved; only payloads are sealed. Every authenticated
session — an Arcanum channel, a Pilgrimage — runs over one, whatever transport is underneath.

**It serialises sends**, because the Noise nonce order must match the wire order. That has a consequence worth
stating plainly: **concurrent sends on an authenticated session are safe on every transport**, not because each
transport guarantees it (`Vessel` does, `DataChannelVessel` leaves it to the channel) but because the Noise layer
above them both does. Receives are single-consumer — the mux pump — so decryption stays in step.

## Which ceiling applies to you

Read down the column for the path you are on:

| | Arcanum channel over TCP / UDP / Tor | Pilgrimage (Shrine), any transport | Pilgrimage from a **browser** |
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
| `SctpAssociation.MaxMessageBytes` (CupriWebRTC) | 256 KiB | **fixed, and not ours** | WebRTC message ceiling |
| `FrameCodec.DefaultMaxFrameSize` | 16 MiB | per-connection | byte-stream frame ceiling; every factory takes `maxFrameSize` |
| `OracleCodec.MaxBodyBytes` | 192 KiB | fixed | one request or response body |
| `AuspiceCodec.MaxPayloadBytes` | 192 KiB | fixed | one snapshot or update |
| `AuspiceCodec.MaxPaddedBytes` | 224 KiB | fixed | encoded Auspice frame after padding |
| `RelicCodec.MaxPayloadBytes` | 192 KiB | fixed | one chunk, manifest, or reason |
| `ConduitCodec.MaxPayloadBytes` | 192 KiB | fixed (the default) | one Pilgrimage conduit frame |
| `ConduitCodec.MaxChannelPayloadBytes` | 16 MiB | fixed (the default) | one Arcanum conduit frame |
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

An `ArcanumSession.Conduits` frame may be up to the vessel's 16 MiB. This is the right answer only when the peers are
channel members on a stream transport and the traffic will never be served to a browser. It is the wrong answer for
anything a Shrine hosts.

### What failure looks like

Every rite checks its own ceiling on **encode and decode**, before any I/O, so exceeding one is a
`CodexFormatException` naming the limit and (for the Oracle and the Conduit) pointing at the Relic. It is never a
truncated message, a torn-down visit, or a failure deep inside an SCTP association. `StaticFileOracleHandler` goes
further and refuses an over-ceiling file *before reading it*, and `OracleHost` turns an oversize handler response into
a clear 500 rather than dropping the Shrine.

## The stream map

One vessel carries many logical streams, demultiplexed by [`VesselMux`](../src/CupriNet.Vessel/VesselMux.cs) so a page
fetch, a live feed, a blob and a raw session all proceed at once on one connection.

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

Wards: at most 64 concurrent streams per connection and 1,024 queued frames per stream, after which inbound frames
are dropped rather than growing memory without limit — the reliable rites recover anything dropped that way.

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

### One asymmetry worth knowing

On an **Arcanum channel** the rites are Veil-sealed: an inner encryption layer keyed from the channel's session key,
which is why those constructors take a session key and a crypto suite.

On a **Pilgrimage** they are not. The Noise vessel is already the confidentiality and authenticity boundary, so a
second layer would be redundant — which is why every rite has a **keyless** constructor for that path, and why no
session key is ever plumbed out of `Pilgrimage.OverVesselAsync`. If you are holding a channel key to construct a rite,
you are on the channel path; if you are handing it only a stream, you are on the Shrine path.

The Conduit carries the same split one step further: its Pilgrimage constructor takes **no author identity** either,
because a Pilgrim is meant to be unlinkable and signing frames under a durable key would undo that. Authenticated
authorship belongs on a channel, where members have durable identities.
