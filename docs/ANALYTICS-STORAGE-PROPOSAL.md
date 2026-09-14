# Compact match observations: storage proposal

Design assessment, 2026-09-08. No app or hosted database implementation is included.
All MB calculations use 1 MB = 1,000,000 bytes. This concerns persistent storage,
not runtime RAM. Capacity estimates below are modeled, not PostgreSQL benchmarks.

## Findings from the local build

- `cache/gwent-one-cards.json` contains 1,381 distinct card IDs, from 112101 to
  203286. Its source request identifies version 14.9.0. This is cached catalog
  metadata, not proof of which balance rules applied to a historical match.
- `Data/ObservedDeckStore.cs` repeats card names, factions, provisions, floating
  point confidence and JSON property names. None need repeating in each upload.
- `Data/OpponentDeckMemoryStore.cs` records individual encounters but also combines
  them into learned decks. Export the individual encounter, not the merged deck
  evidence as if it had all been observed in that match.
- `Data/PlayEventStore.cs` records detection times, card identity, inferred side,
  confidence and diagnostic image references. The analytics record excludes images.
- `GameState/GameStateTracker.cs` caps RecentEvents at 128; these include board
  contacts and other observations, not only plays. Forty local final snapshots
  contained 0-128 recent events (mean 105.65), which is NOT an estimate of plays per
  match. A complete analytics sequence must be appended as events occur.
- `GameState/GameStateModels.cs` stores current scores; add confirmed end-of-round
  score snapshots. Do not substitute a last observed score for a verified final score.
- `Data/DeckPatchMetadata.cs` infers monthly labels. Preserve inference status and
  use a maintained effective-time patch table for authoritative filtering.

## Recommended compact representation

Use a versioned little-endian binary record, stored in PostgreSQL bytea with only
the deduplication key and revision as ordinary columns. Do not store JSON/Base64
alongside it or make every card/event its own database row. Decode for private
batch analytics. This trades convenient ad hoc SQL for small storage.

Use a shared, immutable, versioned dictionary mapping dense card codes to actual
game IDs. Store card definitions and balance-version metadata once. With the
present catalog, 11 bits address every card (2,048 possible codes). Reserve code
0 for unknown, codes near the upper end for non-card events, and 2047 for an
extended identifier. Never silently renumber an existing dictionary.

This is a compact practical format, not a claim of a mathematical minimum.
It retains the requested analytics, not a lossless copy of every local diagnostic.

### Fixed match header: 64 bytes

| Field | Bytes |
| --- | ---: |
| Server-assigned contributor integer | 4 |
| Persistent per-contributor match counter | 8 |
| Match correction revision | 2 |
| UTC start time, unsigned Unix seconds | 4 |
| Duration in seconds | 2 |
| Patch dictionary ID | 2 |
| Card dictionary ID | 2 |
| App/recognizer build dictionary ID | 2 |
| Mode, result, first player, round count, sequence/timing flags | 2 |
| MMR context, MMR after, signed change, season peak | 8 |
| Standard rank | 1 |
| Both leader codes | 4 |
| Both stratagem codes | 4 |
| Six end-of-round scores, three rounds times two sides | 12 |
| Number of card-copy entries on each side | 2 |
| Sequence event count | 2 |
| Format version | 1 |
| Both factions, packed into one byte | 1 |
| Quality flags | 1 |
| **Total** | **64** |

MMR fields describe the reporting player's known rating, not an inferred opponent
rating. Quality flags distinguish inferred patch, incomplete sequence, uncertain
ordering, reviewed scores, deck completeness on each side, MMR scope and manual
MMR context. Missing integer values use reserved sentinels. Before-MMR is derived
only when both after and change are known and appropriately scoped.

Ordinary compact fields have documented ranges. Reserve overflow sentinels and
append tagged, length-prefixed extension values for unusually large scores,
durations, counts or later dictionary growth. Never clamp a real value to fit.
Extensions and extra optional fields increase the estimates below. Times are
second-resolution; confidence is quantized, not retained as full doubles.

The contributor's authentication identity lives in a separate table once, mapped
to the four-byte integer. The server supplies it from authentication. Maintain a
unique constraint on (contributor_id, match_counter), accept only newer revisions,
and skip writes for identical retries. Persist the counter atomically across app
updates; identity recovery must also restore/advance its counter safely. If identity
is reset, register a new identity rather than restarting an old counter namespace.
Existing local session IDs require a persistent migration map.

### Decks: 2 bytes per recorded card copy

Each entry has 11 bits of card code, 2 bits of evidence class (unknown, observed,
reviewed exact, inferred), and 3 bits of confidence (seven levels plus unknown).
Copies repeat the two-byte entry. Sort each side's entries canonically. Unknown
slots can be represented explicitly; do not fill them with inferred cards without
marking them. A two-byte entry does not retain detailed origin/provenance: event
origin is retained in the sequence, and only starting-list evidence belongs here.

Two 25-card lists use 100 bytes; base match size is therefore 164 bytes. This is a
planning scenario, not a limit of 25 or a claim that the opponent list is known.
Each additional stored copy costs two bytes. Shared deck references could reduce
this further when identical lists recur often, but are not credited in capacity:
unique partial lists and their index/dictionary overhead can erase those savings.

### Sequence: 3 bytes per event, or 5 with elapsed time

Each 24-bit event consists of:

- 11-bit card code;
- 2-bit acting side, including unknown;
- 2-bit round, including unknown;
- 4-bit route/origin code (normal play, summon, create, spawn, copy, transform,
  replay, stolen, unresolved, etc.);
- 5-bit confidence (31 levels plus unknown).

Array position is the event order, so no separate sequence number is needed.
Reserved non-card codes allow pass, round boundary, or capture-gap markers.
Do not force alternating sides: chained plays and summons can occur on one side.
Do not promote a board contact into a confirmed play. Order is observed order,
and unknown/ambiguous order must remain marked. Explicit causal/turn grouping or
target tracking would require additional fields and is outside this size model.

Optional elapsed seconds cost two more bytes per event, with an overflow escape.
For 40 events across both players, order adds 120 bytes; order plus times adds
200 bytes. These counts are sensitivity assumptions, not measured average plays.
If a chronological event array already existed, its order itself would cost zero
extra; these figures are the cost of adding the event array to a summary/decks record.

## Capacity model

Reserve 100 MB of the 500 MB quota for the initial database, shared dictionaries,
contributor/auth records and operational headroom. Supabase documents approximately
40-60 MB occupied by a new project. The 100 MB reserve is a planning allowance and
must be increased if contributor/auth/log storage grows beyond it.

For each match budget another 156 bytes beyond the logical binary record for
PostgreSQL tuple/bytea headers, its one deduplication index, page/alignment slack and
routine update space. This is a conservative allowance, not a measured constant.
Store contributor/counter/revision outside bytea without repeating them inside it;
the 64-byte logical header still counts those fields exactly once. Avoid other
per-match indexes, raw upload archives and large materialized views in this budget.

Formula for 50 stored card copies across both sides:

    raw_bytes = 64 + 2 * 50 + event_count * bytes_per_event
    planned_bytes = raw_bytes + 156
    retained_matches = floor(400,000,000 / planned_bytes)

| Scenario | Raw bytes/match | Planned database bytes/match | Retained observations |
| --- | ---: | ---: | ---: |
| Summary and decks, no sequence | 164 | 320 | 1,250,000 |
| Plus 40 ordered events | 284 | 440 | 909,090 |
| Plus 40 ordered and timed events | 364 | 520 | 769,230 |
| Plus 80 ordered events | 404 | 560 | 714,285 |
| Plus 80 ordered and timed events | 564 | 720 | 555,555 |

These are storage capacities, not free-tier throughput or query-performance
guarantees. One observation is one contributor's match report, with both sides in
the same record. Two contributors reporting the same game remain two observations
unless reliably reconciled. Compression gains are not assumed: small bytea values
do not automatically benefit from compressed HTTP uploads. Larger compressed blocks
may save more but complicate corrections, deletions and efficient deduplication.

For a one-month retained season, these are per-month counts. If two months remain
online, halve the sustainable monthly volume. For k months, divide capacity by k.
Keep old archives outside this quota and private, if retained at all.

Season retirement should drop a season partition or truncate a dedicated season
table after any approved archive, rather than rely on DELETE immediately shrinking
the database. Reject uploads for retired seasons, even with new IDs, so cleared
records cannot be resubmitted and counted again. Keep contributor identity state
across seasons. Reclaim storage before the next season's records fill the quota.

## Validation before launch

Implement the codec and test round-trip identity, unknown/overflow encodings,
retries and revisions. Import at least 10,000 representative completed matches
into a test PostgreSQL database; measure table plus index with
`pg_total_relation_size`, and measure total database/auth growth separately.
Derive actual bytes/match and event-count distribution after a correction workload.
Recalculate the quota using those results. The present diagnostic snapshot sample
cannot establish the average length of a complete canonical play sequence.

## Sources

- Supabase database size, initial allocation and quota:
  https://supabase.com/docs/guides/platform/database-size
- PostgreSQL row headers, item pointers and page structure:
  https://www.postgresql.org/docs/17/storage-page-layout.html
- PostgreSQL deletion, vacuuming and space reclamation:
  https://www.postgresql.org/docs/current/routine-vacuuming.html
