using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Platform.Windows.Data;
using GwentCompanion.Platform.Windows.Security;

internal sealed class DataServiceContractValidationCase : IContributorValidationCase
{
    public string Id => "data-service-contract";
    public string Kind => "data-integrity";
    public string Summary => "Keep uploads signed and idempotent, raw tables private, public curves sanitized, and deployments secret-free.";

    public async Task RunAsync(ContributorValidationContext context)
    {
        void Check(bool condition, string message) => ContributorValidationContext.Check(condition, message);
        string Read(params string[] parts) => File.ReadAllText(context.PathFromRoot(parts));

        var schema = Read("backend", "supabase", "migrations", "202609130001_initial.sql");
        var routines = Read("backend", "supabase", "migrations", "202609130002_service_routines.sql");
        var automaticSeasons = Read("backend", "supabase", "migrations", "202609130003_automatic_patch_seasons.sql");
        var serviceProjection = Read("backend", "supabase", "migrations", "202609130004_service_role_public_projection.sql");
        var challengeFix = Read("backend", "supabase", "migrations", "202609130005_registration_challenge_column_fix.sql");
        var publicKeyFix = Read("backend", "supabase", "migrations", "202609130006_canonical_public_key_base64.sql");
        var seasonActivationFix = Read("backend", "supabase", "migrations", "202609130007_safe_season_activation.sql");
        var collaboratorMigration = Read("backend", "supabase", "migrations",
            "202609140001_collaborator_match_import.sql");
        var collaboratorRateLimitMigration = Read("backend", "supabase", "migrations",
            "202609140002_collaborator_rate_limit_route.sql");
        var edge = Read("backend", "supabase", "functions", "gw-api", "index.ts");
        var collaboratorDocs = Read("docs", "COLLABORATOR-IMPORT.md");
        var collaboratorUtility = Read("backend", "collaborator", "Import-GwentVisionMatches.ps1");
        var clientSource = Read("src", "GwentCompanion.Platform.Windows", "Data", "DataContributionClient.cs");
        var workflow = Read(".github", "workflows", "data-service.yml");
        var pagesWorkflow = Read(".github", "workflows", "pages.yml");
        var releaseWorkflow = Read(".github", "workflows", "release.yml");
        var site = Read("site", "index.html") + Read("site", "app.js");
        var appShell = Read("src", "GwentCompanion.App", "MainWindow.xaml") +
            Read("src", "GwentCompanion.App", "MainWindow.MatchStorage.cs");
        var launcher = Read("packaging", "Gwent Vision.cmd");
        var runbook = Read("docs", "DATABASE-SETUP-RUNBOOK.md");

        Check(schema.Contains("partition by list (season_id)", StringComparison.OrdinalIgnoreCase) &&
            schema.Contains("unique (source_id, season_id, observation_key)", StringComparison.OrdinalIgnoreCase),
            "Match storage is not season-partitioned and deduplicated by source identity.");
        Check(schema.Contains("revoke all on all tables in schema private from public, anon, authenticated",
                StringComparison.OrdinalIgnoreCase) &&
            serviceProjection.Contains("grant select on public.published_seasons, public.published_curve_points to service_role",
                StringComparison.OrdinalIgnoreCase) &&
            routines.Contains("revoke all on function public.gw_accept_live_upload", StringComparison.OrdinalIgnoreCase) &&
            routines.Contains("grant execute on function public.gw_accept_live_upload", StringComparison.OrdinalIgnoreCase),
            "The private database or upload routine lacks an explicit role boundary.");
        Check(routines.Contains("excluded.revision > private.matches.revision", StringComparison.OrdinalIgnoreCase) &&
            routines.Contains("batch id was reused with different content", StringComparison.OrdinalIgnoreCase) &&
            routines.Contains("installation season match limit exceeded", StringComparison.OrdinalIgnoreCase),
            "The upload transaction lacks revision, replay, or seasonal-volume protection.");
        Check(routines.Contains("coalesce(sum(peak), 0)::integer", StringComparison.OrdinalIgnoreCase) &&
            !routines.Contains("count(*) = 4", StringComparison.OrdinalIgnoreCase) &&
            routines.Contains("value > previous", StringComparison.OrdinalIgnoreCase) &&
            routines.Contains("m.mmr_confirmed", StringComparison.OrdinalIgnoreCase),
            "The public total-MMR projection does not treat unseen factions as zero or keep strictly increasing points.");
        Check(routines.Contains("archive_verified_at is not null", StringComparison.OrdinalIgnoreCase) &&
            routines.Contains("400 * 1024 * 1024", StringComparison.OrdinalIgnoreCase) &&
            routines.Contains("interval '27 days'", StringComparison.OrdinalIgnoreCase) &&
            routines.Contains("interval '6 days'", StringComparison.OrdinalIgnoreCase),
            "Archive-gated monthly/weekly storage pressure logic is missing.");
        Check(automaticSeasons.Contains("private.ensure_patch_season", StringComparison.OrdinalIgnoreCase) &&
            automaticSeasons.Contains("create table if not exists private.%I partition", StringComparison.OrdinalIgnoreCase) &&
            automaticSeasons.Contains("p_season_slug <> v_patch", StringComparison.OrdinalIgnoreCase) &&
            automaticSeasons.Contains("Europe/Warsaw", StringComparison.Ordinal) &&
            clientSource.Contains("TryMonthlyPatch", StringComparison.Ordinal) &&
            !clientSource.Contains("No upload season is currently open", StringComparison.Ordinal),
            "Patch seasons are not created and selected automatically from validated match data.");

        var publicTableStart = schema.IndexOf("create table public.published_curve_points", StringComparison.OrdinalIgnoreCase);
        var publicTableEnd = schema.IndexOf(");", publicTableStart, StringComparison.Ordinal);
        Check(publicTableStart >= 0 && publicTableEnd > publicTableStart, "Public curve table was not found.");
        var publicTable = schema[publicTableStart..publicTableEnd];
        foreach (var forbidden in new[] { "installation_id", "match_id", "opponent", "deck", "season_code" })
            Check(!publicTable.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                "Public curve table contains private field: " + forbidden);

        Check(edge.Contains("verifySignature", StringComparison.Ordinal) &&
            edge.Contains("exactKeys(record", StringComparison.Ordinal) &&
            edge.Contains("gw_consume_rate_limit", StringComparison.Ordinal) &&
            edge.Contains("\"apikey\": key", StringComparison.Ordinal) &&
            !edge.Contains("`Bearer ${key}`", StringComparison.Ordinal) &&
            edge.Contains("nextOffset", StringComparison.Ordinal) &&
            edge.Contains("/public/faction-daily", StringComparison.Ordinal) &&
            routines.Contains("count(distinct ds.installation_id) >= 5", StringComparison.OrdinalIgnoreCase) &&
            routines.Contains("installation_season.publish_anonymous_curve", StringComparison.OrdinalIgnoreCase) &&
            !edge.Contains("console.log", StringComparison.Ordinal),
            "The Edge Function lacks signature, strict-shape, rate, pagination, or log protections.");
        Check(collaboratorMigration.Contains("private.collaborator_producers", StringComparison.OrdinalIgnoreCase) &&
            collaboratorMigration.Contains("source_kind = 'collaborator_import'", StringComparison.OrdinalIgnoreCase) &&
            collaboratorMigration.Contains("excluded.revision > private.matches.revision", StringComparison.OrdinalIgnoreCase) &&
            collaboratorMigration.Contains("grant execute on function public.gw_accept_collaborator_match_upload",
                StringComparison.OrdinalIgnoreCase) &&
            collaboratorRateLimitMigration.Contains("request_windows_route_check", StringComparison.OrdinalIgnoreCase) &&
            collaboratorRateLimitMigration.Contains("'collaborator'", StringComparison.OrdinalIgnoreCase) &&
            edge.Contains("/collaborator/import", StringComparison.Ordinal) &&
            edge.Contains("authenticatedUser(request)", StringComparison.Ordinal) &&
            edge.Contains("keyedDigest(\"collaborator-source\"", StringComparison.Ordinal) &&
            collaboratorDocs.Contains("Authorization: Bearer TOKEN", StringComparison.Ordinal) &&
            collaboratorDocs.Contains("collaborator_import", StringComparison.Ordinal) &&
            collaboratorUtility.Contains("Get-Credential", StringComparison.Ordinal) &&
            !collaboratorUtility.Contains("GV_SERVER_KEY", StringComparison.Ordinal),
            "Collaborator imports lack per-user authorization, provenance, idempotency, or a secret-free client contract.");
        Check(workflow.Contains("workflow_dispatch", StringComparison.Ordinal) &&
            workflow.Contains("data-staging", StringComparison.Ordinal) &&
            workflow.Contains("data-production", StringComparison.Ordinal) &&
            workflow.Contains("secrets.SUPABASE_DB_PASSWORD", StringComparison.Ordinal) &&
            !workflow.Contains("pull_request:", StringComparison.Ordinal),
            "Database deployment is not manual and protected from pull-request secret exposure.");
        Check(pagesWorkflow.Contains("vars.PUBLIC_DATA_API", StringComparison.Ordinal) &&
            pagesWorkflow.Contains("actions/deploy-pages", StringComparison.Ordinal) &&
            site.Contains("all-factions", StringComparison.Ordinal) &&
            site.Contains("population-measure", StringComparison.Ordinal) &&
            site.Contains("https://github.com/amkokot/GwentVision", StringComparison.Ordinal) &&
            !site.Contains("localStorage", StringComparison.Ordinal),
            "The public dashboard lacks all-faction curves, aggregate comparisons, safe configuration, or code privacy.");
        Check(appShell.Contains("PublicMmrSiteButton", StringComparison.Ordinal) &&
            appShell.Contains("https://amkokot.github.io/GwentVision/", StringComparison.Ordinal) &&
            launcher.Contains("%~dp0GwentVision\\GwentVision.exe", StringComparison.OrdinalIgnoreCase) &&
            releaseWorkflow.Contains("dist/package/GwentVision", StringComparison.Ordinal) &&
            releaseWorkflow.Contains("packaging/Gwent Vision.cmd", StringComparison.Ordinal),
            "The desktop site link or portable release launcher is missing.");
        Check(runbook.Contains("Do not enable the production configuration yet", StringComparison.Ordinal) &&
            runbook.Contains("normal users never", StringComparison.Ordinal) &&
            runbook.Contains("Repeated Pushes", StringComparison.Ordinal),
            "The owner runbook does not preserve staged rollout or secret separation.");

        var installationId = Guid.NewGuid();
        var fixture = new CompactMatch(installationId, Guid.NewGuid(), new DateOnly(2026, 9, 13),
            "0.3.0", "rules-v1", "14.9", false, 4, true, true, "VICTORY", 2470, 8, 2490,
            true, null,
            new MatchPlayer("Skellige", "Onslaught", "202497", [],
                [new("200018", 1, MatchCardEvidence.SelectedReference, CardProvenance.Unknown, byte.MaxValue)], []),
            new MatchPlayer("Nilfgaard", "Imprisonment", null,
                [new("162208", 1, MatchCardEvidence.Observed, CardProvenance.ConfirmedStartingDeck, 250)], [],
                [new("162209", 1, MatchCardEvidence.Inferred, CardProvenance.Unknown, 180)]),
            [new(1, 34, 22, true), new(2, 18, 7, true)], [], false,
            new DateTimeOffset(2026, 9, 13, 18, 30, 0, TimeSpan.Zero), false);
        var recordBytes = DataContributionTransport.SerializeMatch(fixture);
        using (var record = JsonDocument.Parse(recordBytes))
        {
            Check(record.RootElement.GetProperty("matchId").GetGuid() == fixture.MatchId &&
                record.RootElement.GetProperty("opponent").GetProperty("observations")[0]
                    .GetProperty("evidence").GetString() == "Observed" &&
                record.RootElement.GetProperty("opponent").GetProperty("hypothesis")[0]
                    .GetProperty("evidence").GetString() == "Inferred" &&
                record.RootElement.GetProperty("opponent").GetProperty("hypothesis")[0]
                    .GetProperty("copyCountIsEstimate").GetBoolean(),
                "The app transport does not preserve lower-camel identity or the observed/inferred evidence distinction.");
        }
        var handle = Guid.NewGuid(); var batch = Guid.NewGuid();
        var payload = DataContributionTransport.SerializeUploadPayload(handle, batch, "14.9", "0.3.0", 1, true, [fixture]);
        using (var envelope = JsonDocument.Parse(payload))
            Check(envelope.RootElement.GetProperty("schema").GetInt32() == 1 &&
                envelope.RootElement.GetProperty("matches")[0].GetProperty("format").GetString() == "json-v1",
                "The app upload envelope disagrees with the Edge Function's API-v1 transport.");
        var canonical = DataContributionTransport.CanonicalDeviceRequest("upload", handle, batch, "14.9",
            payload, new DateTimeOffset(2026, 9, 13, 20, 1, 2, 345, TimeSpan.Zero));
        Check(canonical.Split('\n').Length == 8 && canonical.EndsWith("2026-09-13T20:01:02.345Z", StringComparison.Ordinal),
            "The app and Edge Function cannot share a stable UTC signing contract.");
        var mixedIdentityRejected = false;
        try
        {
            DataContributionTransport.SerializeUploadPayload(handle, Guid.NewGuid(), "14.9", "0.3.0", 1, true,
                [fixture, fixture with { InstallationId = Guid.NewGuid(), MatchId = Guid.NewGuid() }]);
        }
        catch (InvalidDataException) { mixedIdentityRejected = true; }
        Check(mixedIdentityRejected, "One signed source could mix records from unrelated local installations.");

        var clientDirectory = context.PathFromRoot("diagnostics", "data-service-client-tests", Guid.NewGuid().ToString("N"));
        new LocalMatchStore(Path.Combine(clientDirectory, "matches")).Save(fixture);
        var clientSigning = InstallationSigningIdentity.LoadOrCreate(Path.Combine(clientDirectory, "signing.json"), installationId);
        var clientConfig = new DataServiceConfiguration(1, true, Guid.NewGuid(),
            "https://fixture.supabase.co/functions/v1/gw-api");
        var handler = new FixtureDataServiceHandler();
        using (var client = new DataContributionClient(clientConfig, clientSigning,
                   Path.Combine(clientDirectory, "state.json"), handler,
                   () => new DateTimeOffset(2026, 9, 13, 20, 0, 0, TimeSpan.Zero)))
        {
            var uploaded = await client.UploadCurrentSeasonAsync(Path.Combine(clientDirectory, "matches"),
                installationId, "0.3.0", 1, true);
            Check(uploaded.Uploaded == 1 && uploaded.SeasonCode == FixtureDataServiceHandler.SeasonCode &&
                uploaded.ReceiptId == FixtureDataServiceHandler.ReceiptId,
                "The one-click client did not register, upload, or retain its server receipt and season code.");
        }
        using (var repeated = new DataContributionClient(clientConfig, clientSigning,
                   Path.Combine(clientDirectory, "state.json"), handler,
                   () => new DateTimeOffset(2026, 9, 13, 20, 0, 0, TimeSpan.Zero)))
        {
            var uploaded = await repeated.UploadCurrentSeasonAsync(Path.Combine(clientDirectory, "matches"),
                installationId, "0.3.0", 1, false);
            Check(uploaded.Uploaded == 0 && handler.UploadCalls == 1 && handler.RegisterCalls == 1 &&
                uploaded.SeasonCode == FixtureDataServiceHandler.SeasonCode,
                "A repeated click re-registered the device, resent an unchanged revision, or lost its season code.");
        }

        Check(challengeFix.Contains("registration_challenges challenge", StringComparison.OrdinalIgnoreCase) &&
            challengeFix.Contains("challenge.expires_at", StringComparison.OrdinalIgnoreCase),
            "Registration challenge cleanup leaves a PL/pgSQL output-column ambiguity.");
        Check(publicKeyFix.Contains("replace(replace(encode(public_key_spki, 'base64')", StringComparison.OrdinalIgnoreCase),
            "Stored installation public keys are not returned as canonical single-line base64.");
        Check(seasonActivationFix.Contains("set active = (season_id = v_active_id) where true", StringComparison.OrdinalIgnoreCase),
            "Intentional all-season activation update lacks the explicit protected-database predicate.");

        var backendText = schema + routines + automaticSeasons + serviceProjection + challengeFix + publicKeyFix +
            seasonActivationFix + collaboratorMigration + collaboratorRateLimitMigration + edge + workflow;
        Check(!Regex.IsMatch(backendText, @"sb_secret_(?!REPLACE)[A-Za-z0-9_-]{20,}", RegexOptions.IgnoreCase) &&
            !Regex.IsMatch(backendText, @"postgres(?:ql)?://[^\s]+:[^\s]+@", RegexOptions.IgnoreCase),
            "A plausible deployed secret or database connection string is present in source.");
    }

    private sealed class FixtureDataServiceHandler : HttpMessageHandler
    {
        public static readonly Guid ReceiptId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        public const string SeasonCode = "23456789ABCD";
        public int RegisterCalls { get; private set; }
        public int UploadCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var apiIndex = request.RequestUri!.AbsolutePath.IndexOf("/gw-api", StringComparison.Ordinal);
            var route = request.RequestUri.AbsolutePath[(apiIndex + 7)..];
            object body = route switch
            {
                "/register/challenge" => new { schema = 1, challengeId = Guid.Parse("22222222-2222-4222-8222-222222222222"),
                    nonce = "AQIDBA", expiresAt = new DateTimeOffset(2026, 9, 13, 20, 5, 0, TimeSpan.Zero) },
                "/register" => Register(),
                "/upload" => Upload(),
                "/curve/visibility" => new { schema = 1, curveId = Guid.Parse("44444444-4444-4444-8444-444444444444"),
                    publishAnonymousCurve = true },
                _ => throw new InvalidOperationException("Unexpected fixture route: " + route),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
        }

        private object Register()
        {
            RegisterCalls++;
            return new { schema = 1, installationHandle = Guid.Parse("33333333-3333-4333-8333-333333333333"),
                registeredAt = DateTimeOffset.UtcNow };
        }

        private object Upload()
        {
            UploadCalls++;
            return new { schema = 1, receiptId = ReceiptId, accepted = 1, unchanged = 0,
                curveId = Guid.Parse("44444444-4444-4444-8444-444444444444"), seasonCode = SeasonCode };
        }
    }
}
