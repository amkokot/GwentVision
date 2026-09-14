# Local match data

Live recognition now checkpoints a compressed match file under
`%LOCALAPPDATA%/GwentVision/match-data/YYYY-MM/`, outside the replaceable application
folder. The chart icon in the top bar opens the maximized Match Data window for these files.
Settings contains **Push to database**, which sends eligible completed matches from the
current patch only after the user has agreed to contribute. A monthly option can perform
one automatic upload on or after the 18th. Normal local saving requires no screenshots
and no network connection.

## Analysis window

- The patch dropdown uses the patch saved inside each record and defaults to the
  latest available patch. Date-inferred labels display without a tilde; the raw
  record retains `PatchInferred` for provenance. Choose
  All patches to combine periods explicitly; reading old games never relabels them.
- Match history shows wins in green, losses in red, draws in gold and unknown
  outcomes in gray. Faction names and cached leader icons identify both sides.
  Expand a row to see your deck on the left and the opponent's on the right,
  using the library's card strips. The complete frozen player reference is primary;
  known opponent identities and inferred additions share one sorted list. SEEN
  marks observations; tentative % ? and PICK ? guesses use faded rows. Their
  evidence classes remain separate in the saved data.
  The opponent composition uses the live starting-deck eligibility and origin
  filters: non-collectible tokens and generated observations are excluded from
  deck slots, but remain available in the raw observations. Catalog-unresolved
  cards cannot be verified for this view and are also omitted.
- Factions & leaders switches between player and opponent, faction and leader, and
  encounter share and win rate. Share includes unknown opponents in its denominator.
  Win rate is wins divided by wins plus losses; draws and unknown results are excluded.
  Every group shows its sample size. No decisive outcomes produces a dash, not 0%.
- Statistics focus on gameplay: separate faction-MMR line graphs by games played,
  recent win rate, change from the previous week, strongest/toughest matchups
  (minimum five decisive games), and draw rate. Faction/leader win rates live in
  the middle display. These describe your matches, not the population metagame.
- Refresh data reloads saved checkpoints. History is paginated and deck details
  are read on expansion. Duplicate installation/match IDs use the newest revision;
  unreadable files are counted in the footer and remain untouched.
- F11 toggles full screen; Escape exits full screen. Standard window controls also
  allow maximizing, restoring and resizing. No match files are edited by the window.

New records store a single UTC first-game-observation time to order matches.
Old GVM1 records remain readable, but multiple games on a date without unambiguous
times leave gaps in the MMR graph. Missing ratings also leave gaps; lines stop at
patch boundaries. Each faction uses its own labelled rating scale. The horizontal
axis counts all recorded games with that faction in the selected patch, including
games without ratings. Named deck archetypes are not inferred for display.

## Identity and lifecycle

An installation identity is generated on first normal startup and stored at
`%LOCALAPPDATA%/GwentVision/installation.json`. It contains schema version 1, a random
UUID and its UTC creation time. This is the first-use date, not a guessed download
date. It contains no account name or hardware identifier. The same Windows user
keeps the identity across upgrades and replacement of the application folder.
Back up this file if continuity across a Windows reinstall is important. A corrupt
or unknown identity is reported, never silently replaced with a new contributor.

Each capture gets an independent random match UUID. The installation UUID is also
inside every match file, so future uploads can deduplicate copied files. Repeated
checkpoints update the same file rather than create new observations. Starting
capture again midway through the same real game creates a separate partial capture;
there is no trustworthy shared game-server ID to merge those automatically yet.

A file is created only after a match HUD or play preview is observed, not for menus.
Its UTC game date is the date of that first accepted observation, not the save date.
No individual action timestamp is stored. Every five seconds at most, immutable
snapshots are compressed on a background task. Slow writes coalesce normal
checkpoints. Stop and application close await final saves. Unexpected termination
can lose observations since the last successful checkpoint (normally up to five
seconds; longer on a slow/failing disk). Errors appear in the Settings section.

Starting tracking in a menu waits for the game; the menu's existing MMR is not
assigned to the upcoming match. After a game, the Standard Mode rating panel is
sampled at most every 200 ms, with three agreeing reads required for confirmation.
The optional rating auto-stop waits for the actual confirmed number, not merely
the menu. If the next game appears first (opening redraw, ROUND 1, or match HUD),
capture closes before accepting its cards, leaders or scores, even with rating
auto-stop disabled. The strongest retained rating is used; an unconfirmed fallback
is explicitly marked `MmrUnconfirmed=true`. Confirmed readings take priority over
unconfirmed candidates; otherwise the greater consecutive-read count wins. Start
tracking again to capture that new game. If a result was missed entirely, repeated
ROUND 1 after a later round remains a fallback for separating local match records.
Stopping does not itself assert a completed game or complete observations.

## Evidence semantics

Each side has three independent collections:

- `Observations`: visually recognized card identities, with recognition confidence,
  tracker-estimated copy counts (`CopyCountIsEstimate=true`) and origin (`Created`, `Replayed`,
  `ProbableStartingDeck`, etc.). Recognition is not proof of original-deck membership
  or exact copies. Reasoned tracker cards without visual identity evidence are
  excluded from this collection.
- `Reference`: the player's selected starting-deck list, explicitly labelled
  `SelectedReference`. Card IDs, exact copy counts, faction, leader and stratagem
  are copied when capture starts (or when a reference first becomes available).
  Later library edits/selections cannot change this match's snapshot. The user
  still needs to select the deck they play. As a safeguard, three distinct player
  cards confidently classified as starting-deck cards but absent from the selected
  list cause the reference to be discarded for that match. Created, spawned,
  copied, transformed, stolen, replayed and unresolved cards do not count toward
  this threshold. Gwent Vision then saves the player cards it detects instead of
  presenting the wrong selected list as complete. At the final save, the same deck
  completion model used for the opponent fills the remaining player slots from the
  detected player cards. These additions stay labelled `Inferred`; they never replace
  or relabel the `Observations`.
- `Hypothesis`: algorithmic guesses labelled `Inferred`, and pinned/selected opponent
  guesses labelled `ManualHypothesis`. Their scores remain separate from visual
  recognition confidence. Unknown confidence is 255; values 0-254 represent 0-1.

Detector build and rules version accompany every record. A new hypothesis replaces
the previous hypothesis without editing the observations. No full hypothesis-history
archive is kept. Future analysis can discard the entire hypothesis collection and
estimate a new deck from retained observations. A missing card remains unknown;
the file does not imply that all 25+ original cards are known.

Acquisition consumes accepted GameStateUpdate.Events to classify known identities,
but new records no longer retain a sequential play-by-play. The legacy Actions
array and SequenceTruncated flag remain decodable for existing records.

Live scores remain the last observed values, labelled `FinalConfirmed=false`.
The settled result table before MMR provides an independent check: two matching
readings from distinct captures correct or confirm the saved round totals and set
`FinalConfirmed=true`. Result panels receive the 100 ms capture cadence rather
than the normal idle throttle; the reader adds no minimum delay between votes.
Confirmed tables are cached through the result panel to avoid repeated digit OCR.
A result-font glyph reference supplements OCR. The unused
third 0–0 row of a two-round match is excluded; ambiguous early-forfeit tables
remain unconfirmed. If the panel is skipped or unreadable, live values remain
unconfirmed. Historical matches without retained result images cannot be repaired
from this change alone.
Unread scores, rank, MMR and outcome remain null. FactionMmr distinguishes labelled
faction ratings from unqualified ratings. Best-read fallback MMR appears labelled
unconfirmed in history and is excluded from confirmed-rating charts and averages.
Patch labels remain calendar-inferred
and explicitly carry PatchInferred=true.

## Format and inspection

`.gvm` version 3 is a self-contained binary payload with a local string dictionary,
variable-length integer encoding and Brotli compression. The outer header is ASCII
GVM3 followed by a 32-byte SHA-256 digest of the uncompressed payload. Version 2
appends a nullable UTC start time (one presence byte and eight tick bytes when
present). Version 3 adds one uncompressed boolean byte for MMR uncertainty;
the decoder also reads GVM1 and GVM2 without rewriting them. A checksum
detects damaged files; it is not authentication or protection against deliberate
forgery. No external card catalog is needed to decode the file. Card descriptions,
art, frame paths, player names and per-play timestamps are omitted.

The trial format deliberately uses explicit evidence collections and self-contained
IDs instead of the earlier hypothetical fixed 64-byte header/global dictionary.
Actual file sizes therefore vary; previous centralized capacity estimates are not
benchmarks of this implementation. Brotli is lossless for this reduced schema;
confidence was intentionally quantized before compression.

Decode a file to readable JSON without starting capture:

```powershell
.\GwentVision.exe --export-match 'C:\path\match.gvm' 'C:\path\match.json'
```

The output must not already exist. The Core library also exposes
`LocalMatchStore.Read(path)` and `CompactMatchCodec.Decode(bytes)` for analysis.
Only the explicitly requested JSON export is written; normal acquisition writes
compressed records only. Decoder checks size limits, checksum and evidence classes.

Atomic replacement avoids exposing partial checkpoints. Older revisions cannot
replace a newer checkpoint. Existing corrupt files are preserved and the save fails
visibly. Local data is excluded from repository/release export. No retention or
automatic deletion is enabled for this local trial.

## Detector integration

### Deck library and release data

The installed deck library and the local match store are separate. The repository's
`cache/deck-library.json` is generated by `tools/Export-PublicDeckLibrary.ps1`. It
contains PlayGWENT imports and manually created decks, but removes every
opponent-match occurrence and local file path. The public repository contains no
`.gvm` files, `match-data` folder, installation identity, upload receipt or user
settings. `tools/Test-PublicRelease.ps1` enforces these rules before publishing.

The images under `sessions/` and `tests/recording-validation/` are anonymized detector
fixtures. They reproduce recognition behavior and are not saved match records: their
session journals, player identifiers and other non-image files are excluded.

### Current acquisition boundary

`MainWindow.MatchStorage.cs` consumes the accepted shared game-state update after
the normal origin/deck trackers have processed the detector result. Changes to
recognizers therefore flow through the same acquisition boundary without a second
OCR implementation. Projection callbacks refresh the separate hypothesis. Later
detector changes improve newly acquired data; old compressed files cannot be
re-recognized without the optional original recordings. Confirmed final round
scores use the same detector-to-storage boundary; new data types still require
an appropriate schema/adapter extension. Opponent card matchers run before the
player matcher. Rejecting a stale player reference does not add a second board scan;
it only removes the stale player-card restriction from later ordinary frames. The
player deck completion runs after capture stops, so it cannot delay opponent detection
during play.

Validation is automatically discovered as `local-match-storage` by the contributor
runner. It covers detector consumption beyond 128 events, frozen reference copies, observed
versus inferred evidence, stale player-reference rejection, replacement of hypotheses, missing values, UTC dates,
compression round trips, checksum failures, stable installation identity, atomic
checkpoint replacement and stale-write/corruption protection.

The MMR reader confirms three numeric readings at its actual 600-ms OCR cadence.
The shared RANKED label alone cannot identify a standard-rank result or stop capture;
automatic stopping requires a confirmed numeric faction rating or ladder rank.
History shows rank where recorded, and unqualified MMR has an explicit scope marker.
Historical missing values cannot be recovered from the compact record alone.
