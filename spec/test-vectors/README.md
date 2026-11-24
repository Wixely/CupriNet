# Test vectors

> **Status: not started.**

Every structure in a section must have vectors here before that section can move from Draft to Candidate.

## Plan

Vectors are **generated from the reference implementation**, not written by hand. That way the spec and the code
can't quietly drift apart: a test regenerates them and fails if the committed files change.

- One JSON file per spec document (`01-encoding.json`, `04-conjunction.json`, …).
- Every vector carries fixed inputs (keys, nonces, timestamps, randomness seeded deterministically) and the exact
  output bytes as lowercase hex.
- Handshake vectors record the full transcript: each message on the wire and the resulting handshake hash and
  transport keys.
- Negative vectors (malformed input → the required rejection) are as important as positive ones. They mark the
  parse paths the roadmap flags as never fuzzed.

```json
{
  "spec": "04-conjunction",
  "section": "4.3",
  "name": "identity-binding/basic",
  "inputs": { "...": "hex" },
  "output": "hex",
  "expect": "accept"
}
```

## Priority order

1. Codex primitives (01)
2. Intonation and Signet encodings (06, 10): the formats humans copy and paste between implementations
3. Conjunction transcript, including the target field and refusal marker (04)
4. CupriMark negotiation outcomes (05)
5. Watchword → key schedule (08)
