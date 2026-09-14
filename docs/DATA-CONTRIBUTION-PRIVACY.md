# Match-data contribution and privacy

Design version 1, 2026-09-13. Uploading is not active until the database service
and deletion route have passed staging review.

## Choice and timing

- The first ordinary app startup asks whether to contribute saved matches for
  Balance Council recommendations. No network request occurs before a clear Yes.
- A per-installation ECDSA signing key is generated locally. Its private bytes are
  protected for the current Windows user with DPAPI and never uploaded; the public
  key is registered only on the first consented request.
- Yes enables one automatic contribution on the first actual match played after
  the 18th of each local calendar month. Starting tracking from a menu is not a
  played match. A successful server receipt, rather than an attempted request,
  marks that month complete.
- No keeps automatic sharing off. The choice persists across app updates and can
  be changed in Settings. Manual Push to database remains a separate deliberate
  action once available.
- Anonymous public publication is a separate toggle and is on by default.
  Turning it off excludes the contributor from individual curve feeds and public
  faction aggregates without preventing private research contribution.

## What is private

The service may store a canonical compressed match record, including date and
private exact start time, patch, result, round scores, standard rank or confirmed
faction MMR, the user's selected reference deck, opponent observations, inferred
cards with their evidence markers, detector version, upload receipts, and source
provenance. It does not retain the local play-order array or redundant local
installation UUID; the private signing-key registration still links uploads for
deduplication and longitudinal analysis.

Gwent Vision does not intentionally collect a Gwent account identifier, player
name, email address, screenshots, raw video, diagnostic text, or IP address in the
match payload. The service uses a short-lived keyed digest of network metadata for
abuse prevention; the implemented counters are retained for at most ten minutes.

Raw data is pseudonymous because a private random installation identity must link
that installation's uploads for deduplication and longitudinal analysis. It must
not be described as completely anonymous. Only individually allowlisted analysts
may access it, and their access is logged.

## What is public

For contributors who leave public curves enabled, the public site receives only
a per-season random curve ID, 15-minute time bucket, metric, faction, rating, and
point order. It receives no installation identity, seasonal lookup code, deck,
opponent, leader, match ID, standard rank, exact timestamp, or linkable
cross-season identity. The seasonal code resolves to the curve server-side and is
never included in the public feed.

The site also receives daily faction-level match, contributor, win, loss, and
draw counts from contributors who leave public publication enabled. It suppresses
each faction/day cell until at least five distinct installations contributed. No
curve or installation identifier appears in those aggregates, and turning public
publication off removes that installation from future aggregate responses.

## Provenance

Every stored observation has one immutable source category:

- direct `live_game` detector data;
- `stream_archive` detector data from a broadcast or VOD;
- a namespaced, versioned `collaborator_import`.

Analyses must filter, stratify, or explicitly model this field. Data from
different sampling processes must never be silently combined. Source-specific
identifiers and metadata remain private and are excluded from public curves.

## Retention and control

- Publish the active season and allow a closed season to remain visible briefly.
  Remove its public curve rows and code lookup no later than 30 days after close,
  unless a new explicit notice and consent covers longer publication.
- Retain raw match records for at most 13 months. Capacity pressure may retire the
  oldest closed season sooner after an encrypted, verified research archive is
  produced. Never remove the active season to make space.
- Retain detailed operational logs for no more than 30 days and keyed
  abuse-prevention counters for no more than ten minutes. Logs must not contain
  payloads, seasonal codes, signatures, or private keys.
- The local consent switch stops future contributions. A signed installation
  request can change public-curve visibility, rotate a seasonal code, or delete
  that installation's identifiable raw records and public curve.
  Document any aggregate statistics that cannot be withdrawn because they no
  longer contain a contributor-level record.
- A materially changed privacy notice increments its version and requires a new
  decision. Sharing fails closed when the consent file cannot be validated.
