# 5. Versioning (CupriMark)

> **Status: Draft.** Describes the reference implementation at commit `8a35e12`, using
> [CupriMark](https://github.com/Wixely/CupriMark) v0.2.0.

CupriNet versions each wire **component** separately rather than having one protocol version. A component's
versions are **ordinals** (u16, 0–65,535). Each one is marked with why it was introduced and where it is in its
lifecycle.

## 5.1 Model

Each version of a component has:

- `ordinal`: a u16;
- `bump`: `Functionality` (0) or `Security` (1). Never changes after release;
- `status`: `Active` (0) → `Deprecated` (1) → `Buried` (2). Status only moves forward. A Buried version is a
  tombstone and MUST NOT be accepted.

From these each component derives:

```
security_floor  = highest ordinal with bump = Security, else 0
buried_floor    = (highest Buried ordinal) + 1, else 0
effective_floor = max(security_floor, buried_floor)
supported       = the highest contiguous run of non-Buried ordinals ≥ buried_floor
```

An operator MAY lower `effective_floor` locally, but never below `buried_floor`. The reference implementation never
does.

## 5.2 Two patterns

**Negotiated** (handshakes). Each side advertises `supported` as a range `[min, max]`. The selected version is the
highest ordinal in both ranges. The result is a rejection if:

| Condition | Reject reason |
|---|---|
| no local versions | `NothingToOffer` |
| peer range empty, or `peer.max < local.min` | `PeerTooOld` |
| `local.max < peer.min` | `SelfTooOld` |
| no overlap otherwise | `NoCommonVersion` |
| selected < `effective_floor` | `BelowFloor` |

**Accepted** (one-way documents). The sender stamps one version. The receiver accepts it only if negotiating
`[v, v]` against its own range would succeed. That rejects versions that are too new as well as ones that are too
old.

## 5.3 Binding into transcripts

- **Conjunction:** each side signs its own advertised range into its identity binding (04 §4.3.2). The selection is
  a deterministic function of the two signed ranges, so neither side can be downgraded without breaking a signature.
- **Consecration:** the negotiation is hashed into the transcript using CupriMark's `TranscriptBinding`:

```
binding = string("cuprimark/negotiation/v1")
          ‖ varuint(N)
          ‖ for each component, sorted by ordinal byte order of name:
              string(name) ‖ OrdinalSet(initiator) ‖ OrdinalSet(responder) ‖ u16(selected)
digest  = SHA-256(binding)

OrdinalSet = 0x00                                        ; empty
           | 0x01 ‖ u16(min) ‖ u16(max)                  ; contiguous
           | 0x02 ‖ varuint(count) ‖ count × u16          ; sparse
```

Worked value for Consecration v1 (both sides advertise `[1, 1]`, selected 1):
`SHA-256 = dce3c409148477c53465b20ea995044816736ddaebde8d82b437c031dfe34b35`. *(To be confirmed by a generated test
vector.)*

- **Toll:** negotiated in cleartext; see OI-04.3.

## 5.4 Component registry

Catalogue `"cuprinet"`. Every component is currently at ordinal 1 (Functionality, Active), so every floor is 0 and
every range is `[1, 1]`.

| Component | Pattern | Wire width | Spec |
|---|---|---|---|
| `toll` | negotiated | varuint | [04 §4.1](04-conjunction.md) |
| `conjunction` | negotiated | varuint | [04 §4.3](04-conjunction.md) |
| `consecration` | negotiated | varuint | [08](08-channels.md) |
| `intonation` | accepted | u8 | [06](06-identity-and-documents.md) |
| `webrtc-endpoint` | accepted | u8 | [06](06-identity-and-documents.md), [11](11-traversal.md) |
| `shrine-address` | accepted | u8 | [10 §10.8](10-shrines.md) |
| `lan-presence` | accepted | u8 | [11](11-traversal.md) |
| `decree` | accepted | u8 | [08](08-channels.md) |
| `channel-descriptor` | accepted | u8 | [08](08-channels.md) |
| `investiture` | accepted | u8 | [08](08-channels.md) |
| `oracle` | accepted | u8 | [10 §10.4](10-shrines.md) |
| `auspice` | accepted | u8 | [10 §10.5](10-shrines.md) |
| `relic` | accepted | u8 | [10 §10.6](10-shrines.md) |
| `conduit` | accepted (Pilgrimage path only) | u8 | [10 §10.7](10-shrines.md) |
| `known-peer` | accepted | u8 | local storage only; not on the wire |

*Reference:* `src/CupriNet.Marks/CupriMarks.cs:34-200`.

> **Open (OI-05.1):** documents stamp the version as one byte, which caps those components at ordinal 255.
> Handshakes use varuint. Decide on one width per component.
>
> **Open (OI-05.2):** the catalogue's identity hash and signing envelope are implemented but never exchanged.
> Decide whether peers need to agree on catalogue content, or only on ordinals.
>
> **Open (OI-05.3):** some messages also carry a separate *format* byte (Conjunction `bindingVersion`, the
> Consecration envelope version). Its relationship to the CupriMark ordinal should be stated, or the byte removed.

## 5.5 Retiring a version

A version is retired by moving it to Buried in a new release. Peers that only speak Buried versions then get
`PeerTooOld` or `SelfTooOld`, never a silent downgrade. A Security bump raises the floor immediately, without waiting
for a version to be buried.
