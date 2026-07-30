# CupriNet vs Tox

**[Tox](https://tox.chat/)** is the closest thing here to a like-for-like: a **P2P instant messenger** with
public-key identities, no account servers, and end-to-end encryption — the same space our sample app
**[CupriChat](../samples/CupriChat/)** lives in. So this one is best read at the **application level**: *CupriChat
(on CupriNet) vs a Tox client like qTox (on toxcore)*.

> *Fact-checked against the [Tox FAQ](https://tox.chat/faq.html) (checked 2026-07). Toxcore (the C reference
> library, `c-toxcore`) is GPL-3.0; the tox.chat docs are CC BY-SA 4.0.*

## TL;DR

Tox is a **mature, shipping P2P messenger** — text, **voice and video calls**, file transfer, groups — where you
are **globally findable by a static Tox ID** via a public **DHT**, and which **does not hide your IP** from
contacts by design (Tor is a bolt-on workaround). CupriNet is a **younger library** whose sample **CupriChat**
does text/file/group chat with a **stricter privacy model**: **no DHT / not globally findable** (you're reached
via an expiring link), **first-class Tor**, and native metadata resistance (unlinkability + cover traffic). Tox
wins today on **features and maturity**; CupriNet leans harder into **privacy-by-design and embeddability**.

## What Tox is

- **Identity.** A **Tox ID** is a "76-character hexadecimal string" — your **curve25519** public key + a *nospam*
  value + checksum. It's a **static, long-lived** identifier you hand out; contacts add you by it.
- **Discovery.** A public **DHT** with "publicly listed **bootstrap nodes**." You're **findable** on the network
  by your key (friend-finding lookups are onion-routed so DHT nodes don't learn who's searching for whom).
- **IP privacy.** Explicit: "Tox makes no attempt to cloak your IP address when communicating with friends… you
  reveal your IP address to someone only when you add them to your contacts list." Tor is offered as a
  **"workaround"** (tunnel Tox through Tor — which loses UDP and is awkward).
- **Crypto.** libsodium/NaCl — "curve25519 for key exchanges, xsalsa20 for symmetric encryption, and poly1305 for
  MACs," with **perfect forward secrecy** as "the default and only mode of operation."
- **Transports & features.** UDP primary with **TCP relays** when UDP is blocked; **text, voice (Opus), video
  (VP8), file transfer, and group chats**. Reference core is **C** (`c-toxcore`, GPL-3.0); mature clients include
  qTox, aTox, Toxic.

## Side by side (CupriChat/CupriNet vs Tox)

| | Tox (qTox / toxcore) | CupriChat (CupriNet) |
|---|---|---|
| **Kind** | P2P messenger — text, **voice/video**, files, groups | P2P messenger — text, data, **files**, group channels (no A/V) |
| **Discovery** | **Public DHT** + bootstrap nodes; findable by Tox ID | **No DHT** — reached via a signed, **expiring link** (or LAN / introductions) |
| **Identity** | **Static** Tox ID (curve25519 key + nospam + checksum) | Sigil (Ed25519 key hash); **per-channel identities unlinkable** from it |
| **Add someone** | Send/accept a friend request to a Tox ID | Share/paste a `cuprinet://intone/…` link (or QR); join a channel by `Name#Salt` |
| **IP privacy** | **None by default** — contacts see your IP; Tor is a workaround | Clearnet also shows IP to a direct peer, but **first-class Tor** (dual-stack / onion-only) is built in |
| **Metadata resistance** | Onion-routed friend lookup; but DHT presence + static ID are surfaces | **Opaque rotating adverts**, no DHT, **unlinkability**, **cover traffic** (fuzz/effigies/pageants) |
| **Relaying** | **TCP relays** carry (E2E-encrypted) traffic when UDP fails | **L2 content never relayed** by CupriNet nodes (direct-only); Tor is the anonymised lane |
| **Encryption** | NaCl (curve25519/xsalsa20/poly1305), **PFS** | Noise XX/IK (Ed25519/X25519/ChaCha20-Poly1305), **PFS** |
| **Groups** | Conferences / new group chats (ad-hoc) | Channels with **owner-signed membership** (Investiture), access modes, ownership transfer |
| **Runtime / license** | **C** core, **GPL-3.0** | **100% managed C#** (.NET 10), **MIT** |
| **Maturity** | ~2013, real clients + users, A/V shipping | Pre-1.0; **CupriChat is a sample/demo**; unaudited |

## Where they diverge

**1. Findable-by-ID vs reachable-by-link.** Tox puts you on a **public DHT**: anyone with your Tox ID can find and
message you — convenient and open, but it means a **static, globally-queryable identifier** and a discovery
surface. CupriNet has **no DHT**: you're reached only by a **signed, expiring Intonation link** you chose to share
(or same-LAN / a member introduction). That's worse for "be reachable by anyone" and better for **closed, private
groups** where non-discoverability is the point.

**2. IP privacy: bolt-on vs built-in.** Tox is upfront that it **doesn't cloak your IP** from contacts, and Tor is
a documented workaround (that sacrifices UDP). CupriNet clearnet *also* exposes your IP to a direct peer — but
Tor is a **first-class mode** (dual-stack keeps clearnet + onion at once, or onion-only hides the IP entirely),
and even on clearnet CupriNet adds **cover traffic, unlinkable per-channel identities, and opaque rotating channel
adverts** — non-IP anonymity Tox doesn't attempt. (See [cuprinet-vs-tor.md](cuprinet-vs-tor.md) for the clearnet
privacy detail.)

**3. Relaying content vs strictly direct.** When UDP is blocked, Tox falls back to **TCP relays** that carry your
(E2E-encrypted) traffic — more likely to connect, at the cost of a relay seeing that a connection exists.
CupriNet **never relays L2 content** through its own nodes: a pair either connects directly (hole-punched) or
uses Tor; if neither works, there's no session. Stricter, but it can fail to connect where Tox's relay would have
succeeded.

**4. Groups: ad-hoc vs governed.** Tox groups are lightweight and ad-hoc. CupriNet channels have **owner-signed
membership credentials** (Investiture), explicit access modes, and an ownership/transfer chain (with tolerated
forks) — a more governed model, at the cost of simplicity.

**5. Runtime & license.** Tox is a C library under **GPL-3.0**; CupriNet is **managed C# under MIT**. For
embedding in a proprietary or permissively-licensed .NET app, MIT + no native deps is materially easier; for a
native/systems client, toxcore's C is the more natural fit.

## Where they're kin

- **P2P, no account servers, self-generated key identities**, friend/link-based rather than phone/email.
- **End-to-end encryption with perfect forward secrecy** on both.
- **NAT traversal** (UDP hole-punching) and **local discovery**.
- **Tor-capable** (Tox as a workaround; CupriNet first-class).
- **FOSS**, embeddable core libraries (toxcore / CupriNet.Hosting).

## Maturity — Tox is the shipping product

Be clear-eyed: **Tox is a real, years-old messenger** with multiple mature clients, a user base, and **working
voice/video** — things CupriNet does **not** have. **CupriChat is a sample app**, CupriNet is pre-1.0 and
unaudited, and there's **no A/V** and no public network yet. Tox has its own history of uneven development and
security scrutiny, but as *something you can install and talk on today*, it's far ahead. CupriNet's bet is a
**stricter privacy model** (no DHT, integrated Tor, cover traffic, unlinkability) delivered as an **MIT,
fully-managed, embeddable** library — promising for private/closed-group use, but younger.

## When to pick which

- **Pick Tox** if you want a **ready-to-use P2P messenger with voice/video**, to be **reachable by an ID** anyone
  can add, and you're fine with your IP being visible to contacts (or you'll tunnel it yourself).
- **Pick CupriNet/CupriChat** if you're **building** a messaging feature into a .NET app, want **non-discoverable,
  link-gated** private groups, **integrated Tor** and metadata resistance out of the box, and an **MIT** dependency
  — accepting that it's younger, text/file only (no A/V yet), and not audited.
