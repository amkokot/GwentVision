# Implementation notes

Gwent Vision is a .NET 8 WPF application split into three runtime projects. `GwentCompanion.App` owns the windows and interaction state, `GwentCompanion.Platform.Windows` handles Windows capture and pixel recognition, and `GwentCompanion.Core` contains card data, inference, and persistence rules. The internal project names predate the public name; the installed program and package are `GwentVision.exe` and `GwentVision`.

Before recognition, `VisionFrameNormalizer` retains ordinary 16:9 frames, reduces captures larger than 1920×1080 for bounded feature-search cost, and removes centered black letterboxing when it can do so unambiguously. Unsupported aspect ratios and frames below 960×540 produce no recognition evidence and surface a visible warning. Board scans additionally require the fixed match HUD to be visible, so a menu, transition, unfamiliar layout, or cosmetic background cannot make an empty/incorrect scan authoritative.

## Runtime path

The app locates `Gwent.exe` by walking up from the working directory and executable directory. Release data is loaded from the `cache` folder beside `GwentVision.exe`. Development builds fall back to the existing `GwentCompanion` workspace below the game directory. Decks, snapshots, and recognition caches stay under that data root. Installation identity, sharing preferences, match records, and automatic diagnostic sessions live under `%LOCALAPPDATA%/GwentVision` so an update cannot replace them. All runtime state is excluded from the public repository.

## Recognition and inference

Clicking Play creates the capture and recognition pipeline; simply opening the app does not load it. The pipeline captures the GWENT window, classifies the current screen, and only invokes recognizers that are useful for that screen. Title regions, tooltips, board locations, choices, counters, and known artwork contribute evidence with a source and confidence rather than directly editing a deck.

Recognition is staged from cheapest to most expensive. Every retained frame can update screen, title and HUD state; hand, deck, score, motion and unresolved-title changes then protect a bounded set of artwork frames from queue eviction. A board pass uses one shared SIFT scene extraction, side-specific candidate indexes for localization, and an aligned colour/layout comparison for identity. Card-specific observed-art references are admitted only after manual verification and are evaluated on different frames. This keeps artwork adaptation separate from event inference and avoids teaching the matcher from its own guesses.

The match ledger groups repeated sightings into episodes and is the only layer that assigns origin. Resolution trackers account for created, spawned, stolen, tutored and replayed cards before the live deck tracker updates copy bounds. Printed rules constrain legal targets, while hand/deck conservation supplies typed, bounded causal credits. Recurring broad summons such as Saskia: Commander remain active across the round: Deploy can credit one strong legal far-right arrival, and later timer targets must join an unconsumed same-hand deck departure. Artwork may slightly precede a lagging HUD digit, but a sparse multi-card delta confirms only the source-scoped target and cannot be reused for another identity. An old activation cannot lend its decrement to a later body.

The opponent-deck projector and library associations remain internal recognition aids and supply explicit inferred evidence to completed match records. The public live display filters the opponent list to observed cards only. Its Candidates pane is an eligible-card catalogue in normal deck-builder order and does not expose the internal ranking or let saved unseen cards enter the live list.

Work is kept off the game loop where possible. OpenCV uses a small fixed worker count, expensive reference data is loaded lazily, and recognition is scoped to likely regions or candidates. The default Live layout renders Opponent Cards, Candidates, and Snapshots.

## Stream archive path

Stream mode stores source identities, compact per-game JSON records, and public
deck-page cache entries in separate folders from live match data. Source video
and audio are never downloaded. A first sequential pass advances the decoder
without copying skipped frames and evaluates one state sample per second. It
finds game intervals, corroborates short result and first-round cues, and
schedules the existing `DeckBuilderScanner` at most once every 20 seconds while
a sustained non-match deck page is visible. Multiple successful pages merge into
one deck observation. A canonical PlayGwent link in public video metadata is a
higher-confidence fallback for custom deck graphics that are not GWENT UI.

The second pass runs the shared card pipeline only inside discovered games. Like
live detection, it starts from the candidate-scoped artwork index, seeds known
player cards from deck evidence, learns opponent candidates from resolved actions,
and releases transient opponent references between games. It
temporarily increases cadence around motion, titles, HUD changes, and unresolved
artwork; score changes can therefore trigger a timely board scan for automatic
arrivals. Stable corner overlays mask only their unsafe lane. Unfamiliar central
overlays are skipped instead of generating weak evidence. Automatically retained
hard frames use anonymous source hashes, irreversible identity/display masks,
metadata-free JPEG re-encoding, per-game/per-source limits, and a 64 MiB global
cap. The private manually reviewed stream suite is documented in
`tests/stream-validation`; tournament layouts are currently allowed to skip.

## UI and saved state

Local analytics acquisition consumes accepted game-state deltas and writes atomic
Brotli-compressed match checkpoints, independently of screenshot recording. Visual
observations, selected references and inferred deck hypotheses remain separate.
See [Local match data](LOCAL-MATCH-DATA.md) for the format, identity, lifecycle and decoder.

Automatic diagnostic sessions are kept under
`%LOCALAPPDATA%/GwentVision/diagnostics/sessions`. A session retains the compact
vision/state journals and JPEG before/during/after evidence for recognized plays.
At startup and after a clean stop, oldest-first pruning retains no more than five
sessions or 256 MiB in total. The newest session is always kept, so one explicitly
enabled training recording may temporarily exceed the byte ceiling until a later
session is available. This policy does not delete `.gvm` match history.

`MainWindow` is divided into partial classes by responsibility: navigation, analysis, deck projection, candidates, snapshots, settings, and vision lifecycle. Compact mode shows one Live pane at a time; wide mode places the three default panes left to right. Snapshot capture always requests a fresh game frame and writes a PNG before selecting it in the review pane.

The deck library shipped in releases contains only public PlayGWENT imports. Local names, observed opponents, editor drafts, settings, browser state, and generated recognition caches are filtered from the release export.

## Validation

The large baseline corpus is described by `tests/recording-validation/manifest.json`. Its public export is re-encoded, stripped of metadata, masked in player-identity regions, and verified by path, size, and SHA-256 digest.

New bugs use append-only folders below `tests/recording-validation/cases`. Each folder has a `case.json`, numbered anonymized evidence images, and one matching `IRecordingValidationCase` implementation in `tests/GwentCompanion.Tests/RecordingCases`. Reflection discovers the pair, so contributors do not edit a common registry or increment a validation version. CI rejects missing evidence, changed hashes, orphan tests, duplicate IDs, or unimplemented assertions, then runs the baseline and every add-on case.

Feature, layout, data, accessibility, performance, and integration checks that do not need captured media implement `IContributorValidationCase` under `tests/GwentCompanion.Tests/ValidationCases`. They are also discovered by reflection. The `Kind` value is an open kebab-case label, allowing new test categories without changing the runner. Both systems automate discovery and execution, not the human decision about correct expected behavior.

## Build and release

Pull requests build the solution, verify the public payload, run the recording suite, and run the compact-layout regression. A separate workflow scans the repository. Version tags publish a self-contained Windows x64 ZIP; the package is scanned again before the GitHub release is created. `tools/Export-ReleaseRepository.ps1` is the only supported way to assemble the public repository because it also removes private runtime state and regenerates the anonymized baseline corpus.

## Naming

Use **Gwent Vision** in prose, `GwentVision` for package and executable names, PascalCase for C# types, and lowercase kebab-case for validation IDs. Existing `GwentCompanion.*` namespaces and project filenames are retained for source compatibility.
