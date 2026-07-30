# Comparisons

Side-by-side looks at how **CupriNet** relates to other decentralized / P2P / mesh projects, so the picture is
clear: what's genuinely similar, where the designs diverge, and when you'd reach for one over the other.

Each comparison is **fact-checked against the other project's own docs** (with the version/date noted at the top
of the file) and aims to be **fair, not a sales pitch** — including the places another project is ahead of us.

> Quick reminder of what CupriNet is: a 100% managed-C# (.NET 10, MIT) **application-layer** private-communication
> library — a pseudonymous L1 overlay (Concordance) plus **direct-only**, authenticated, end-to-end-encrypted L2
> channels (Arcanum). It's what an app *talks through*, not a network you run other apps *over*. See the top-level
> [README](../README.md) and [ROADMAP](../ROADMAP.md).

## Index

| Project | What it is | Comparison |
|---|---|---|
| [FIPS](https://github.com/jmcorgan/fips) | Rust, Nostr-identified, **routed encrypted IP mesh** (IPv6 adapter + DNS) — closest to Tailscale/Yggdrasil | [cuprinet-vs-fips.md](cuprinet-vs-fips.md) |
| [Tor](https://www.torproject.org/) | **Anonymity network / transport** — onion routing + onion services. Not an alternative; CupriNet runs *over* it | [cuprinet-vs-tor.md](cuprinet-vs-tor.md) |
| [Tox](https://tox.chat/) | **P2P instant messenger** (DHT-findable, voice/video) — the closest like-for-like; compared via our **CupriChat** sample | [cuprinet-vs-tox.md](cuprinet-vs-tox.md) |
| [Freenet](https://freenet.org/) (new) | Decentralized **app platform / shared datastore** (WASM-contract key-value store) — build apps *on* it | [cuprinet-vs-freenet.md](cuprinet-vs-freenet.md) |

*(More to come.)*

> Note: not every entry is an *alternative*. Some (like **Tor**) are things CupriNet **composes with** — the
> comparison then explains the layering rather than picking a winner.

## How to read these

- **"Layer"** is usually the biggest difference: an **IP-mesh** gives you addresses and carries arbitrary traffic
  (you run apps *over* it); an **application-layer** library gives apps an API for sessions/messages (you build *with* it).
- **"Routed vs direct"** matters for privacy: a routed mesh relays your (encrypted) traffic *through* other nodes;
  CupriNet's core invariant is that private content is **never** relayed.
- **"Anonymity: native vs delegated"** — some projects get anonymity only by running over Tor/Nym; CupriNet bakes
  unlinkability + traffic-analysis cover into the design *and* can also use Tor.
