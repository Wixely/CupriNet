# CupriNet.Marks

CupriNet's [CupriMark](https://github.com/Wixely/CupriMark) integration: the one place that says
*which protocol versions this build speaks*, so peers **range-negotiate** a shared version instead of
hard-failing on a `!= Version` equality check. A newer node keeps talking to an older one through a
deliberate, security-aware window rather than partitioning on a flag day.

## The catalogue

[`CupriMarks`](CupriMarks.cs) holds the single built-in catalogue (`cuprinet`). Each **component** is an
independently-versioned protocol point; on the wire peers exchange only an ordinal *range*, and each side
resolves the agreed ordinal against this same catalogue (its SHA-256 `Id` pins exactly this set of
definitions).

| Component | What it versions | Pattern |
|---|---|---|
| `conjunction` | L1 transport pairing handshake (`NoiseConjunction`) | negotiate |
| `consecration` | L2 Arcanum channel handshake (`ConsecrationHandshake`) | negotiate |
| `decree` | signed channel advertisement (`Decree`) | accept |
| `intonation` | signed connection link (`Intonation`) | accept |
| `channel-descriptor` | owner-signed channel root (`ChannelDescriptor`) | accept |
| `investiture` | owner-signed membership credential (`Investiture`) | accept |

## Two patterns

**Negotiate** — for a *handshake*, where both sides exchange ranges and pick a common version:

```csharp
var mine = CupriMarks.Supported(CupriMarks.Conjunction);   // advertise mine.Min..mine.Max on the wire
var result = CupriMarks.Negotiate(CupriMarks.Conjunction, peerAdvertisedRange);
if (!result.Accepted) throw ...;                            // typed RejectReason + EffectiveFloor
var agreed = result.SelectedOrdinal;                        // highest both speak, at/above our floor
```

Bind the outcome into the handshake's own transcript so a downgrade can't survive: the L1 handshake signs
the advertised range together with the Noise handshake hash; the L2 handshake folds
`TranscriptBinding.Digest(NegotiationBinding.FromLocal(...))` into its key-confirmation transcript.

**Accept** — for a one-way *document* that carries no exchange (a link, an advert, a credential). Accepting
a document stamped at version `V` is just negotiating the single-point range `[V..V]`, so it reuses the exact
floor / lifecycle rules:

```csharp
producedDoc.Version = (byte)CupriMarks.Supported(CupriMarks.Decree).Max;   // stamp from the catalogue
...
if (!CupriMarks.Accepts(CupriMarks.Decree, receivedDoc.Version)) reject(); // support + floor + not buried
```

## Adding a version or a component

Append a `ComponentVersion` to the component in `CupriMarks.Build()` and ship the code path that handles
it — producers then stamp / advertise the new max automatically and peers range-negotiate it. A version
that fixes a security issue is a `BumpReason.Security` bump (it raises the floor, so old versions stop being
accepted); a version whose code is deleted becomes `VersionStatus.Buried` (an inviolable hard floor — no
override can revive it). A new protocol point is a new `Component`.

## Signing a catalogue with our own crypto

[`AlembicCatalogueSigning`](AlembicCatalogueSigning.cs) bridges CupriMark's `ICatalogueSigner` /
`ICatalogueVerifier` to CupriNet's Alembic Ed25519 (the Seal), so a catalogue can be release-signed and
verified without a second crypto implementation.

## Build note

CupriMark is restored from the **Wixely GitHub Packages feed** (see the repo `nuget.config`). Because
`CupriNet.Marks` is referenced by core projects, the whole build needs that feed + a `read:packages` token
(CI authenticates with `PACKAGES_TOKEN`).
