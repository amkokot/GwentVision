using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class TooltipCorroboratedAutomaticSummonsRegressionCase : IRecordingValidationCase
{
    public string Id => "tooltip-corroborated-automatic-summons";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var brigade = catalog.Single(card => card.Id == "162310");
        var affan = catalog.Single(card => card.Id == "202445");
        var sergeant = catalog.Single(card => card.Id == "162309");
        if (!CompanionCardRules.IsInherentDeckArrival(brigade) || !CompanionCardRules.IsInherentDeckArrival(affan) ||
            CompanionCardRules.IsInherentDeckArrival(sergeant))
            throw new InvalidOperationException("The automatic-arrival rule boundary for the retained Nilfgaard cards changed.");

        var references = VisionReferenceLibrary.Load(catalog, cache);
        using var pipeline = new CardVisionPipeline(references, catalog,
            Path.Combine(cache, "recognition-features"), VisionReferenceScope.CandidateDecks);
        pipeline.SetKnownPlayerDeck([]);
        var likelyNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Nauzicaa Brigade", "Affan Hillergrand", "Nauzicaa Sergeant", "Alba Armored Cavalry",
            "Ard Feainn Light Cavalry", "Baccalà", "Bearification", "Dead Man's Tongue", "Illusionist",
            "Imperial Marine", "Offering", "Ramon Tyrconnel", "The Mushy Truffle", "War Council"
        };
        pipeline.SetLikelyOpponentCards(catalog.Where(card => likelyNames.Contains(card.Name)).Select(card => card.Id));

        var at = DateTimeOffset.UnixEpoch;
        var brigadeTitle = await pipeline.PrepareAsync(definition.Load("evidence-01.png"), at.AddMilliseconds(200));
        var affanTitle1 = await pipeline.PrepareAsync(definition.Load("evidence-03.png"), at.AddSeconds(10.2));
        var affanTitle2 = await pipeline.PrepareAsync(definition.Load("evidence-04.png"), at.AddSeconds(10.4));
        if (brigadeTitle.HoveredCard?.Id != brigade.Id || affanTitle1.HoveredCard?.Id != affan.Id ||
            affanTitle2.HoveredCard?.Id != affan.Id)
            throw new InvalidOperationException("The retained exact Nauzicaa Brigade/Affan tooltip titles no longer resolve.");

        GwentVisualObservation TooltipScreen(PreparedVisionFrame prepared) => prepared.Screen with
        {
            View = GwentViewKind.Board,
            MatchHudVisible = true,
            IsCardSelectionOverlay = false,
            HasCardTooltip = true,
            TooltipRegion = prepared.Screen.TooltipRegion ?? throw new InvalidOperationException("Retained tooltip geometry was not found.")
        };
        var cleanBoard = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null)
            { MatchHudVisible = true };
        CardSighting Candidate(CardVisionResult result, CardDefinition card, string label) =>
            result.Sightings.Where(sighting => sighting.Side == PlayerSide.Opponent &&
                sighting.Source == CardSightSource.Board && sighting.Card.Id == card.Id &&
                sighting.NeedsTemporalConfirmation &&
                (sighting.Evidence ?? "").Contains("bounded automatic-arrival board fallback", StringComparison.Ordinal))
                .OrderBy(sighting => sighting.Distance).FirstOrDefault() ??
            throw new InvalidOperationException($"The retained {label} board frame no longer produces its guarded automatic-arrival candidate. Sightings: " +
                string.Join(" | ", result.Sightings.Select(item => $"{item.Card.Id}/{item.Side}/{item.Source}/d={item.Distance:F3}/m={item.Margin:F3}/{item.Evidence}")));

        // This late Brigade observation was produced by the board fallback's
        // temporal localization cache in the live run. Verify the retained crop
        // directly, then preserve that guarded production representation so this
        // case does not manufacture the missing earlier cache frames.
        using var brigadeFeatures = new FeatureCardRecognizer(references.Where(reference => reference.Card.Id == brigade.Id),
            Path.Combine(cache, "recognition-features"));
        var brigadeRegion = new NormalizedRegion(.603, .320, .656, .447);
        var brigadeDistance = new CardArtMatcher(brigadeFeatures.ArtReferences)
            .IdentityDistance(definition.Load("evidence-02.png"), brigadeRegion, brigade.Id);
        if (brigadeDistance > .60)
            throw new InvalidOperationException($"The retained settled Nauzicaa Brigade crop no longer resembles the card ({brigadeDistance:F3}).");
        var brigadeCandidate = new CardSighting(brigade, PlayerSide.Opponent, CardSightSource.Board,
            brigadeRegion, .40, 1,
            $"Localized board appearance persisted in an independent scan at distance {brigadeDistance:F3} after a scale-agreeing feature candidate · bounded automatic-arrival board fallback; repetition and deck conservation still required · opponent candidate reference index",
            NeedsTemporalConfirmation: true);
        var affanScreen = TooltipScreen(affanTitle1);
        var affanArtwork = pipeline.RecognizePrepared(affanTitle1 with { Screen = affanScreen, Titles = [] }, includeBoard: true);
        var affanCandidate = Candidate(affanArtwork, affan, affan.Name);

        void Confirm(CardDefinition card, CardSighting candidate, GwentVisualObservation titleScreen, double seconds,
            bool sparseGap = false)
        {
            var ledger = new MatchVisionLedger();
            if (ledger.Observe(at.AddSeconds(seconds), cleanBoard, [candidate], boardWasScanned: true).Count != 0)
                throw new InvalidOperationException($"One guarded {card.Name} art frame committed without title corroboration.");
            var titleAt = seconds + .2;
            if (sparseGap)
            {
                if (ledger.Observe(at.AddSeconds(seconds + 4), cleanBoard, [candidate], boardWasScanned: true).Count != 0)
                    throw new InvalidOperationException($"Repeated guarded {card.Name} art committed without exact title corroboration.");
                titleAt = seconds + 19;
            }
            if (ledger.Observe(at.AddSeconds(titleAt), titleScreen, [], boardWasScanned: false,
                    confirmedHover: card, artworkWasScanned: false).Count != 0)
                throw new InvalidOperationException($"One exact {card.Name} title committed the guarded artwork candidate.");
            var arrival = ledger.Observe(at.AddSeconds(titleAt + .2), titleScreen, [], boardWasScanned: false,
                confirmedHover: card, artworkWasScanned: false);
            if (arrival.Count != 1 || arrival[0].Sighting.Card.Id != card.Id ||
                !(arrival[0].Description.Contains("guarded inherent deck-arrival", StringComparison.Ordinal) ||
                  arrival[0].Description.Contains("sparse scan gap", StringComparison.Ordinal)))
                throw new InvalidOperationException($"Repeated exact {card.Name} title did not join the prior same-body guarded artwork candidate. " +
                    $"Candidate={candidate.Region}; tooltip={titleScreen.TooltipRegion}; events={arrival.Count}.");
            if (new OpponentKnowledge().BoardOrigin(arrival[0], "Nilfgaard")?.Provenance != CardProvenance.ProbableStartingDeck)
                throw new InvalidOperationException($"Confirmed {card.Name} arrival did not enter starting-deck accounting.");
        }

        Confirm(brigade, brigadeCandidate, TooltipScreen(brigadeTitle), 0, sparseGap: true);
        Confirm(affan, affanCandidate, TooltipScreen(affanTitle2), 10);

        // A printed self-summon rule is mandatory. Even otherwise identical
        // guarded artwork/title evidence must not manufacture an ordinary play.
        var ordinary = new MatchVisionLedger();
        var falseCandidate = affanCandidate with { Card = sergeant };
        ordinary.Observe(at.AddSeconds(20), cleanBoard, [falseCandidate], boardWasScanned: true);
        ordinary.Observe(at.AddSeconds(20.2), affanScreen, [], false, sergeant, false);
        if (ordinary.Observe(at.AddSeconds(20.4), affanScreen, [], false, sergeant, false).Count != 0)
            throw new InvalidOperationException("Repeated title/art evidence bypassed the inherent deck-arrival rule boundary.");
    }
}
