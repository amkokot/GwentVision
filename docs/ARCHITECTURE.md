# Implementation notes

Gwent Vision is a .NET 8 WPF application split into three runtime projects. `GwentCompanion.App` owns the windows and interaction state, `GwentCompanion.Platform.Windows` handles Windows capture and pixel recognition, and `GwentCompanion.Core` contains card data, inference, and persistence rules. The internal project names predate the public name; the installed program and package are `GwentVision.exe` and `GwentVision`.

Before recognition, `VisionFrameNormalizer` retains ordinary 16:9 frames, reduces captures larger than 1920×1080 for bounded feature-search cost, and removes centered black letterboxing when it can do so unambiguously. Unsupported aspect ratios and frames below 960×540 produce no recognition evidence and surface a visible warning. Board scans additionally require the fixed match HUD to be visible, so a menu, transition, unfamiliar layout, or cosmetic background cannot make an empty/incorrect scan authoritative.

## Runtime path

The app locates `Gwent.exe` by walking up from the working directory and executable directory. Release data is loaded from the `cache` folder beside `GwentVision.exe`. Development builds fall back to the existing `GwentCompanion` workspace below the game directory. User-created snapshots, settings, observed decks, and diagnostics stay under that data root and are excluded from the public repository.

## Recognition and inference

Clicking Play creates the capture and recognition pipeline; simply opening the app does not load it. The pipeline captures the GWENT window, classifies the current screen, and only invokes recognizers that are useful for that screen. Title regions, tooltips, board locations, choices, counters, and known artwork contribute evidence with a source and confidence rather than directly editing a deck.

The match ledger groups repeated sightings into episodes. Resolution trackers account for created, spawned, stolen, and replayed cards before the live deck tracker updates copy bounds. The opponent-deck projector compares that evidence with the public deck library and exposes a cautious hypothesis. Candidate cards come from the same evidence and faction constraints. Ambiguous evidence remains visible as a candidate instead of becoming a confirmed play.

Work is kept off the game loop where possible. OpenCV uses a small fixed worker count, expensive reference data is loaded lazily, and recognition is scoped to likely regions or candidates. The default Live layout renders the opponent deck, candidates, and snapshots. The experimental Overview layout is opt-in, and its presentation model is not rebuilt while hidden.

## UI and saved state

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
