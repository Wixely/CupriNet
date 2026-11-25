# 3. Transport (Vessel)

> **Status: Draft.** Describes the reference implementation at commit `8a35e12`. Rationale and the full size-limit
> table are in [design/transports-and-limits.md](../design/transports-and-limits.md).

A **Vessel** is one bidirectional connection carrying **frames**. Each frame is tagged with a **stream id**. There
are two families:

| Family | Carriers | Frame boundary | Max frame |
|---|---|---|---|
| byte-stream | TCP, reliable UDP (§3.4), Tor | Codex frame codec (01 §1.4) | 16 MiB |
| message | WebRTC DataChannel | one DataChannel message | the channel's negotiated max message size |

After the Conjunction (04), every frame payload is a Noise transport ciphertext (§3.5).

## 3.1 Byte-stream frame

Content of one Codex frame:

| Field | Type | Notes |
|---|---|---|
| streamId | `varuint` | 0–65,535 |
| payload | `bytes<len>` | |

The payload is length-prefixed twice: once by the outer `u32` frame length and once by `bytes<len>`.

A receiver MUST reject a `streamId` above 65,535 and MUST reject a `payload` length that runs past the frame.

*Reference:* `src/CupriNet.Vessel/Vessel.cs:49-81`.

> **Open (OI-03.1):** bytes after `payload` inside a frame are ignored. Decide whether the inner length must exactly
> fill the frame. The double length prefix is also redundant; consider dropping one in a future version.

## 3.2 Message frame (DataChannel)

One DataChannel message is:

| Field | Type |
|---|---|
| streamId | `varuint` (0–65,535) |
| payload | remainder of the message |

There is no inner length and no fragmentation. An empty message is malformed. A sender MUST NOT send a message larger
than the channel's negotiated maximum. The interoperable floor is 64 KiB. CupriWebRTC offers 256 KiB.

*Reference:* `src/CupriNet.Vessel/DataChannelVessel.cs:56-82`.

> **Open (OI-03.2):** the reference sender does not check the size before sending.

## 3.3 Streams

There are no stream open, close, reset or flow-control messages. A stream exists from the first frame that carries
its id and lasts as long as the Vessel. Order is preserved within the Vessel, and therefore within each stream.

### 3.3.1 Stream id registry

| Id | Pairing (node ↔ node) | Pilgrimage (visitor ↔ Shrine) |
|---|---|---|
| 0 | Toll, Noise handshake, identity binding | same |
| 1 | peer exchange ([07](07-overlay.md)) | — |
| 2 | Consecration ([08](08-channels.md)) | — |
| 3 | Epistles **and** Effigy cover traffic ([09](09-rites.md)) | — |
| 4 | Conduit (Veil-sealed) | Conduit (unsealed) |
| 5 | reflexive endpoint ([11](11-traversal.md)) | Oracle |
| 6 | overlay control: session kind ([07](07-overlay.md)) | — |
| 7 | — | Auspice |
| 8 | — | Relic |

The same id carries different things in the two connection kinds, and the kinds never share a Vessel. Stream 3 is
deliberately shared by Epistles and cover traffic, so that the stream id does not distinguish real traffic from cover.

Ids 9–65,535 are unassigned. **Open (OI-03.3):** define a reserved or private-use range.

### 3.3.2 Receiver limits

A receiver MUST bound its resources per Vessel. The reference implementation:

- admits at most **64** distinct stream ids per Vessel. Frames for further new ids are dropped;
- queues at most **1,024** undelivered frames per stream. When the queue is full, the newest frame is dropped.

Drops are silent: the sender is not told. Only the Epistle/Vigil rite recovers from a drop. Every other rite either
detects loss itself (the Relic, by hash) or loses the frame.

*Reference:* `src/CupriNet.Vessel/VesselMux.cs:14-93`.

> **Open (OI-03.4):** the protocol has no close reason or error signal. A malformed frame on any stream, or a Noise
> authentication failure, tears down every stream on the Vessel with no indication to the peer.

## 3.4 Reliable UDP

Reliable UDP is a simple selective-repeat ARQ carried in UDP datagrams. Its byte stream carries byte-stream frames
(§3.1) unchanged.

### 3.4.1 Segment

| Offset | Field | Type | Meaning |
|---|---|---|---|
| 0 | cmd | `u8` | `1` PUSH · `2` ACK · `3` CLOSE |
| 1 | conn | `u64` | connection id, the same in both directions |
| 9 | seq | `u32` | sequence number of this segment (0 for ACK) |
| 13 | una | `u32` | cumulative ack: the next sequence number the sender expects to receive |
| 17 | len | `u16` | payload length |
| 19 | payload | `bytes[len]` | PUSH only |

- MSS is 1,152 payload bytes, so the largest datagram is 1,171 bytes.
- The connection id is fixed for the life of the stream. On a hole-punched path both ends derive it from the
  punch session id: `SHA-256("cuprinet/arq/connection/v1" ‖ sessionId)[0..8]`, read big-endian. A node dialling a
  listener picks 8 random bytes; the listener adopts the id of the first well-formed segment from a new source and
  sends nothing to that source before then.
- Each direction starts at seq 0. Sequence numbers compare with 32-bit serial arithmetic (RFC 1982).
- CLOSE carries no payload, consumes one sequence number and is delivered in order as end-of-stream.
- Every segment's `una` acknowledges everything below it.

A receiver MUST ignore:

- a datagram shorter than 19 bytes;
- a datagram shorter than `19 + len`;
- a datagram whose `cmd` is not 1, 2 or 3. Values `0xF1` and `0xF2` are hole-punch probes sharing the socket
  ([11](11-traversal.md)).

A receiver MUST also ignore:

- a segment whose `conn` is not the stream's connection id. It MUST NOT acknowledge it. This is what stops a host
  that can forge a source address, but is not on the path and so cannot see the id, from injecting into a stream;
- a `una` beyond the highest sequence number it has sent (`una > snd_next`): it cannot be an honest
  acknowledgement;
- any sequenced segment that arrives after the peer's CLOSE has been delivered.

Segments outside `[rcv_next, rcv_next + 256)` are dropped but still trigger an ACK.

A listener MUST NOT create state for an unknown source unless the datagram is a well-formed segment.

*Reference:* `src/CupriNet.Vessel/ReliableArq.cs:21-226`.

### 3.4.2 Sender behaviour (informative)

| Parameter | Reference value |
|---|---|
| send window | 128 segments |
| receive window | 256 segments |
| RTO | starts at 100 ms, doubles on every (re)transmission, capped at 4,000 ms |
| retransmission limit | 12 per segment (about 30 s), then the stream is dead |
| idle timeout | 60 s with nothing received, then the stream is dead |
| keep-alive | a bare ACK after 15 s with nothing sent |
| congestion control | none (omitted deliberately; it needs no wire change) |
| close linger | until the CLOSE is acknowledged, at most 1 s (80 ms if the peer has already closed) |

A sender MUST retransmit CLOSE like any other sequenced segment until it is acknowledged. An endpoint SHOULD send an
ACK when it has sent nothing for a while, and MUST NOT treat a peer as dead sooner than four such intervals. A dead
stream reads as end-of-stream and fails writes.

The connection id is not authentication: anything on the path can read it. Tampering is detected by the Noise
session above, which ends the connection.

> **Resolved (OI-03.5):** reliable UDP had no connection id, sent CLOSE without waiting for an acknowledgement, and
> never gave up on a silent peer. All three are fixed. It is still IPv4-only (OI-11.3).

## 3.5 Noise transport framing

After the handshake (04), the stream id is sealed together with the payload. Every frame is sent on **stream 0** of
the underlying Vessel (the *carrier*), and its payload is:

```
sealed = ChaCha20-Poly1305(key = k_send, nonce = 0x00000000 ‖ u64le(n), ad = "",
                           plaintext = varuint(streamId) ‖ payload)
```

`n` is a single counter for each direction across all streams. It starts at 0 after Split, increments by one per
frame, and is never sent.

The real stream id is therefore both authenticated and hidden: it cannot be changed on the way without the frame
failing to open, and an observer sees only stream 0 and the frame's length.

- Senders MUST serialise frames so that nonce order matches wire order.
- A receiver MUST reject a frame that arrives on any outer stream other than 0.
- A receiver MUST reject a sealed frame whose plaintext does not begin with a `varuint` of at most 65,535.
- A receiver MUST treat any of these failures, and any authentication failure, as fatal to the Vessel.
- An implementation MUST NOT use nonce `2^64 − 1`. There is no rekey; the Vessel must be replaced first.

This applies from the first frame after Split, including the two identity-binding messages.

*Reference:* `src/CupriNet.Conjunction/NoiseVessel.cs`, `src/CupriNet.Noise/NoiseStates.cs:36-60`.

> **Resolved (OI-03.6):** in Conjunction v1 the stream id travelled outside the ciphertext with empty AD, so it could
> be rewritten on-path and it revealed the session kind. Conjunction v2 seals it with the payload.
>
> **Open (OI-03.7):** transport messages may be up to 16 MiB. That deviates from the Noise specification's 65,535-byte
> limit (Noise rev. 34 §3). State the deviation as normative, or fragment.
