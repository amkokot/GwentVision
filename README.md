# Gwent Vision

Gwent Vision is a free, open-source Windows companion for *GWENT: The Witcher Card Game*. Version 0.3 tracks the opponent cards shown during a match, adds an extended deck library with new quality-of-life features, saves your match history, and displays matchup and MMR charts.

It is an unofficial, not-for-profit fan project. Gwent Vision provides no predictive gameplay features.

[Download the latest Windows release](../../releases/latest) · [View public season MMR curves](https://amkokot.github.io/GwentVision/) · [Open the interface tour](docs/SCREENSHOTS.md) · [Report a bug](../../issues/new?template=bug_report.yml) · [Request a feature](../../issues/new?template=feature_request.yml)

## Live tracking

The **Opponent Cards** view records cards as they appear on the game screen. It marks cards created or spawned during the match so they are not mistaken for cards from the opponent's starting deck. The Candidates view provides a searchable list of every card in the usual deck-builder order.

In the wide layout, Opponent Cards, Candidates, and Snapshots appear in three columns. Compact mode shows the same tools one at a time beside a windowed game. The selected player deck shares the Library row in wide mode, so it remains obvious without taking space from opponent cards.

![Wide Gwent Vision view with Opponent Cards, Candidates, and Snapshots](docs/images/live-opponent-cards.png)

## Match Data

Every completed match is saved on your computer with its date, game patch, result, round scores, faction MMR when available, your selected deck, and the opponent cards that were detected. Match Data opens in its own resizable or full-screen window and initially shows the latest saved patch.

- **Match history** shows wins in green and losses in red, together with both factions and leader abilities. Expanding a match shows your complete saved deck on the left and the opponent deck information on the right. Detected cards and estimated cards appear in one list, with labels and a slight fade to show which is which.
- **Factions and leaders** shows how often you face each faction or leader and your win rate against each one. It can also show the same information for the factions and leaders you play.
- **Your progress** plots each faction's MMR against the number of games played with that faction. It also highlights results that may be useful to review when improving your play.

![Patch-filtered match history](docs/images/match-data-history.png)

![Faction matchup view](docs/images/match-data-matchups.png)

![Faction MMR progress and gameplay statistics](docs/images/match-data-progress.png)

Players below Pro Rank do not have faction MMR, so those matches remain in history and matchup summaries while rating charts leave the unavailable rating blank.

## Deck library, snapshots, and recording

The extended Library can store any number of decks. It adds search and filters, groups similar deck versions together, supports manual deck building, scans decks from the in-game builder, and imports decks from PlayGWENT links or spreadsheets. The deck selected as **Deck you are playing** is saved as your complete deck for each new match. Your deck library and settings are preserved when Gwent Vision is updated.

![The Gwent Vision deck library and selected player deck](docs/images/deck-library.png)

Snapshots keep selected game screens available for quick review. Optional diagnostic recording helps reproduce missed or incorrectly recognized cards. These files stay on your computer unless you deliberately share a copy after removing personal information.

## Optional data contribution

The first time Gwent Vision opens, it asks whether you would like to contribute match data for Balance Council recommendations. You can press **Push to database** more than once; each push sends only new matches or updated results from the current patch. You can also choose to share automatically after your first match played on or after the 18th of each month.

The connection is already included in the download, so there is no account, API key, or database setup for users. Submitted matches are linked to a random installation ID rather than a player name. This research data is not published and can be accessed only by approved analysts. It does not include player names, Gwent account IDs, screenshots, or recordings. Read the [data contribution and privacy policy](docs/DATA-CONTRIBUTION-PRIVACY.md) for the complete details.

The option to display your MMR curve anonymously is separate and is turned on by default. After a successful push, the app gives you a season code. Enter that code on the [public season site](https://amkokot.github.io/GwentVision/) to highlight your own line. The public charts contain only anonymous MMR points grouped into 15-minute time blocks. They do not include decks, opponents, match IDs, installation IDs, or exact times.

The site provides:

- a Total MMR line based on the highest rating reached with up to four factions; factions you have not played count as zero, and the line never moves down;
- separate MMR lines for each faction, with the option to display all factions together; and
- daily faction popularity and win rate once enough users have contributed data to protect privacy.

## Install

1. Open the [latest release](../../releases/latest) and download `GwentVision-Windows-x64.zip`.
2. Extract the ZIP contents into the GWENT installation folder, beside `Gwent.exe`.
3. Double-click **Gwent Vision.cmd** in that folder. This shortcut opens the app stored inside the `GwentVision` folder. You can also run `GwentVision\GwentVision.exe` directly.
4. Start GWENT, then press the play button in Gwent Vision when you want live recognition to begin.

The installed layout is:

```text
GWENT The Witcher Card Game\
├── Gwent.exe
├── Gwent Vision.cmd
└── GwentVision\
    ├── GwentVision.exe
    ├── cache\
    └── vision-assets\
```

Gwent Vision supports 64-bit Windows 10 or later and does not require a separate .NET installation. For reliable card recognition, use a 16:9 GWENT resolution such as 1280×720, 1920×1080, or 3840×2160. Black bars around a 16:9 game image are handled automatically.

## How recognition works

Gwent Vision reads ordinary Windows desktop pixels from the visible game window. All displayed information comes from that screen, and recognition begins only when the user presses the play button.

## Contributing

You do not need to be a programmer to help. A short description of a missed card or an awkward part of the app is useful. Please remove names, profile IDs, file locations, and private deck information from screenshots before attaching them to an issue.

- [Report a bug](../../issues/new?template=bug_report.yml)
- [Request a feature](../../issues/new?template=feature_request.yml)
- [Browse the issue tracker](../../issues)

Code and detector contributors should start with [CONTRIBUTING.md](CONTRIBUTING.md). The [architecture notes](docs/ARCHITECTURE.md), [local match format](docs/LOCAL-MATCH-DATA.md), and [database runbook](docs/DATABASE-SETUP-RUNBOOK.md) describe the main components and release process.

```powershell
dotnet build GwentCompanion.sln -c Release
dotnet run --project tests/GwentCompanion.Tests/GwentCompanion.Tests.csproj -c Release -- --recording-validation-regression
dotnet run --project tests/GwentCompanion.Tests/GwentCompanion.Tests.csproj -c Release -- --contributor-validation-regression
```

GitHub repeats these checks for each proposed change, checks that private files and passwords were not included, and scans every release file for malware.

## License and attribution

Gwent Vision is not affiliated with or endorsed by CD PROJEKT RED. Game names, artwork, and other game assets belong to their respective owners. Code is available under the [MIT License](LICENSE); third-party material is excluded from that grant as described in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
