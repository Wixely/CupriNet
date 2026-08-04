# CupriNet vs I2P

**[I2P](https://geti2p.net/)** (the Invisible Internet Project) is, like Tor, an **anonymity overlay network** — not
really a direct *alternative* to CupriNet but a **layer you run apps over**. This page compares them where a
comparison is meaningful (layer, trust root, relaying, what each gives you), notes a genuine technical kinship
(both build on the **Noise** framework), and then explains how they'd **compose**.

> *Grounded in the I2P project's documentation and the long-standing I2P design (garlic routing, unidirectional
> tunnels, the distributed netDb on floodfill routers, NTCP2/SSU2 transports, self-authenticating destinations).
> Checked 2026-08. These are stable fundamentals, not a moving target.*

## TL;DR

I2P **anonymizes connections inside its own network**: it hides your IP by sending traffic through **unidirectional
tunnels** built across other participants' routers, and it is optimized for **anonymous in-network services**
(eepsites, messaging, torrents) reachable by a self-authenticating **`.b32.i2p`** address. It is a *network and
transport*, not an application protocol — you still run something on top. CupriNet is that "something on top": an
app-layer private-communication library. So, as with Tor, the honest framing isn't "CupriNet vs I2P" but
potentially **"CupriNet over I2P"** — I2P would supply IP-level anonymity and NAT-free reachability; CupriNet supplies
identities, channels, messages, and metadata resistance on top. (Today CupriNet ships a **Tor** transport, not an I2P
one — see [composition](#how-theyd-compose) below.)

## What I2P is

- **Garlic routing over unidirectional tunnels.** Each router builds its own **inbound** and **outbound** tunnels
  (separate, typically ~3 hops each), so a request and its reply travel **different paths** through different peers.
  "Garlic" routing bundles several messages ("cloves") under layered end-to-end encryption. This differs from Tor's
  single **bidirectional** 3-relay circuit.
- **Everyone is a router.** By default an I2P node **relays other participants' tunnels** — routing is not a
  volunteer minority (as with Tor relays) but the normal state of every peer, which grows the anonymity set and the
  network's capacity together.
- **Destinations & LeaseSets.** A service is a cryptographic **Destination** (public keys), addressed as a
  self-authenticating **`.b32.i2p`** (Base32 of the key) — no CA, like a Tor `.onion`. To be reachable it publishes a
  **LeaseSet** (its current inbound-tunnel gateways) into the network database. Human-readable `.i2p` names come from
  **subscription address books / jump services**, not a global registry.
- **Trust root: a distributed netDb, not directory authorities.** Reachability info (**RouterInfos**) and service
  info (**LeaseSets**) live in a **Kademlia-style distributed database (netDb)** held by **floodfill** routers — so
  I2P has **no Tor-style directory-authority consensus**. The one central-ish dependency is **reseed** (a hardcoded
  set of HTTPS servers) to fetch the *first* RouterInfos when a brand-new node has no peers; after that, discovery is
  distributed.
- **Threat model.** Strong anonymity against a **non-global** adversary; like every low-latency network it **does not**
  defend against a global passive adversary who can watch enough of the network and **correlate** by timing/volume —
  a documented, fundamental limitation. Its packet-switched, unidirectional-tunnel design gives *different* (not
  strictly stronger) traffic-analysis properties than Tor.
- **Transports — built on Noise.** I2P's **NTCP2** (over TCP) and **SSU2** (over UDP) transports are built on the
  **Noise Protocol Framework** and are obfuscated/authenticated. (This is a real point of kinship with CupriNet,
  whose transport is also Noise — see below.)
- **Implementation.** The reference router is **Java** (`i2p.i2p`); **[i2pd](https://i2pd.website/)** is a C++
  reimplementation. A mix of open-source licenses (much of it public-domain / BSD, some GPL). ~20 years old
  (since 2003), production, a smaller user base than Tor but well studied; popular for **high-bandwidth in-network
  P2P** (e.g. torrents) that Tor discourages.

## Side by side

| | I2P | CupriNet |
|---|---|---|
| **What it is** | An **anonymity overlay network / transport** (anonymized in-network streams + hidden services) | An **application-layer** comms library (identities, channels, messages, files) |
| **Layer** | Below your app — you run a protocol *over* it (streaming/datagram API, SAM, I2PTunnel) | The app protocol itself — no virtual network, no SOCKS |
| **Anonymity mechanism** | **Garlic routing** over unidirectional tunnels; every node relays | **Native**: pseudonymity, per-channel unlinkability, traffic-analysis cover — but **not IP-hiding by itself** |
| **Relaying** | **Fundamental** — anonymity *comes from* relaying through peers' tunnels | **L2 content is never relayed by CupriNet nodes**; L1 metadata may hop, and a **Ferryman** may broker a NAT hole-punch (signalling only) |
| **Trust root / bootstrap** | **No directory authorities** — distributed netDb on floodfill routers; **reseed** servers only for first contact | **None** — discovery via links / same-LAN / member introductions (a seed/link for first contact) |
| **Hidden-service analogue** | **Destinations** (`.b32.i2p`, self-authenticating) — native and primary | Uses **Tor's** onion services (via CupriTor) for its onion transport |
| **Transport crypto** | **Noise-based** NTCP2 / SSU2 | **Noise-over-TCP** (XX/IK) on BouncyCastle |
| **What you get** | Anonymized in-network transport + hidden services; you still build the app | The whole app layer; borrow Tor (today) for IP anonymity |
| **Language** | **Java** (reference) / **C++** (i2pd) | **100% managed C#** (.NET 10) |
| **License** | Mixed OSS (public-domain / BSD / GPL) | MIT |
| **Maturity** | Production, ~20 yrs, well studied | **Pre-1.0**, tested, unaudited, no public network yet |

## Where they actually differ

The core distinction is the **same one as with Tor**: I2P is a **network you run over**; CupriNet is the **app you
build with**. Two consequences matter most:

- **Relaying vs direct.** I2P's anonymity *requires* relaying your (encrypted) traffic through other peers' tunnels —
  that's the mechanism. CupriNet's core invariant is the opposite: **no CupriNet node ever relays your L2 channel
  content**, so no intermediary can record — or even confirm — that a conversation happened. The trade is symmetric:
  I2P *gets* IP-anonymity by relaying; CupriNet *forgoes* native IP-anonymity to keep content strictly direct (and
  leans on Tor when IP-hiding is needed). CupriNet's only relay is the **Ferryman**, and it merely **brokers a NAT
  hole-punch** (signalling) so two peers can connect *directly* — it never carries channel bytes.
- **What "identity" means.** In I2P a **Destination** is a network endpoint you host; anonymity is about hiding the
  *IP/location* of routers and services. In CupriNet an identity is a **Sigil** (a key hash) with **per-channel
  unlinkable** sub-identities, and the privacy properties are about **who-talks-to-whom / which-group** at the app
  layer — a different axis than IP-hiding.

## Genuine kinship (worth stating)

Unlike the Tor comparison, I2P and CupriNet share real design DNA in two places:

- **Both build on Noise.** I2P's NTCP2/SSU2 and CupriNet's transport are both **Noise Protocol Framework**
  handshakes — the same modern, well-analyzed crypto foundation (as opposed to bespoke or TLS-based transports).
- **Both reject a trusted directory.** I2P has **no directory authorities** (a distributed netDb), which is
  philosophically closer to CupriNet's **no-trusted-root** stance than Tor's ≈9 signing authorities. Both still need
  *some* first contact — I2P via reseed servers, CupriNet via a link/seed — an honest, documented bootstrap cost on
  both sides.

## How they'd compose

Because I2P is a transport/overlay, CupriNet could in principle run **over** I2P exactly as it runs over Tor: a
CupriNet node would reach a peer's **Destination** through I2P's tunnels, getting IP-anonymity and NAT-free
reachability for free while CupriNet layers authenticated channels, unlinkable identities, and messaging on top.
**This binding does not exist today** — CupriNet ships **[CupriTor](https://github.com/Wixely/CupriTor)** (a managed
Tor client), not an I2P one. Why Tor first:

- a **larger anonymity set** and more mature, widely-studied onion services;
- an existing **managed** client path (CupriTor) that fits CupriNet's 100%-C# goal;
- Tor's exit ecosystem, which CupriNet doesn't need but which reflects Tor's larger operational surface.

An "I2P mode" is a reasonable **future** transport (its garlic tunnels and self-authenticating destinations map
cleanly onto how CupriNet already thinks about onion transports), but it's not on the near-term roadmap.

## Clearnet CupriNet ≠ "no privacy"

As with the [Tor comparison](cuprinet-vs-tor.md#clearnet-cuprinet--no-privacy), running CupriNet on clearnet (no I2P,
no Tor) still gives real **non-IP** anonymity that I2P by itself is not aimed at: **pseudonymity** (Sigils, no
accounts), **unlinkability** (channel identities separable from the overlay Sigil), **opaque rotating channel
adverts** (Glyph tokens, never the channel name), **cover traffic** (gossip fuzzing, hot fuzz, effigies, pageants),
and **direct-only content**. What clearnet CupriNet does *not* give you is IP/location hiding — that's the job of an
anonymity network like I2P or Tor, and it's exactly the layer you'd add underneath. *(Caveat: CupriNet's cover-traffic
machinery is young and unaudited — a design goal being hardened, not a guarantee.)*

## Maturity — I2P is far ahead here

No contest: I2P is ~two decades of production use, two independent implementations (Java + i2pd), sustained research
on its anonymity properties and their limits, and a live network. CupriNet is pre-1.0, unit-tested but **not
security-audited**, with **no public network of its own** — and for IP anonymity it deliberately **relies on Tor**
today rather than reinventing an I2P-style overlay. Treat CupriNet's *native* traffic-analysis cover as young and
unproven; if you need battle-tested IP-anonymity now, that's I2P or Tor, not CupriNet.

## When to think about which

- **You want anonymous, high-bandwidth services *inside* an established anonymity network** (eepsites, in-network
  P2P/torrents, hidden messaging) with no central directory: that's **I2P**, directly.
- **You're building an app that needs private, authenticated group communication** with unlinkable identities and
  direct-only content: that's **CupriNet** — on clearnet today, or over **Tor** when you also need to hide IPs.
- **You specifically want CupriNet with I2P underneath:** not available yet — it's a plausible future transport, but
  today the IP-anonymity option CupriNet ships is **Tor mode**.
