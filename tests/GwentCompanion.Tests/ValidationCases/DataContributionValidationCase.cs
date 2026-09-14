using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using GwentCompanion.Core.Data;
using GwentCompanion.Platform.Windows.Security;

internal sealed class DataContributionValidationCase : IContributorValidationCase
{
    public string Id => "data-contribution-preferences";
    public string Kind => "data-integrity";
    public string Summary => "Persist explicit sharing consent, default anonymous publication, schedule after the 18th, and preserve source provenance.";

    public Task RunAsync(ContributorValidationContext context)
    {
        void Check(bool condition, string message) => ContributorValidationContext.Check(condition, message);
        var directory = context.PathFromRoot("diagnostics", "data-contribution-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "data-contribution.json");
        var initial = DataContributionPreferences.Load(path);
        Check(initial.Consent == DataContributionConsent.Undecided && initial.PublishAnonymousCurve &&
            initial.AutomaticMonthlyUpload && !initial.CanShare, "New-install sharing defaults are incorrect or sharing began before consent.");

        var declined = initial.Decide(false, DateTimeOffset.UtcNow);
        Check(!declined.CanShare && !declined.AutomaticMonthlyUpload && declined.PublishAnonymousCurve,
            "Declining consent did not keep data local or changed the independent curve default.");
        DataContributionPreferences.Save(path, declined);
        Check(DataContributionPreferences.Load(path) == declined, "Consent preferences did not round-trip.");

        var accepted = initial.Decide(true, DateTimeOffset.UtcNow);
        Check(accepted.CanShare && accepted.AutomaticMonthlyUpload, "Explicit opt-in did not enable the disclosed monthly schedule.");
        Check(!DataContributionSchedule.IsDue(accepted, new(2026, 9, 18), null) &&
            DataContributionSchedule.IsDue(accepted, new(2026, 9, 19), null) &&
            !DataContributionSchedule.IsDue(accepted, new(2026, 9, 19), "2026-09") &&
            DataContributionSchedule.IsDue(accepted, new(2026, 10, 19), "2026-09"),
            "Monthly first-game scheduling does not respect the 18th or successful receipt period.");
        var receiptPath = Path.Combine(directory, "data-contribution-state.json");
        var receipt = DataContributionSyncState.Empty.RecordSuccess("2026-09", Guid.NewGuid(), DateTimeOffset.UtcNow);
        DataContributionSyncState.Save(receiptPath, receipt);
        Check(DataContributionSyncState.Load(receiptPath) == receipt &&
            !DataContributionSchedule.IsDue(accepted, new(2026, 9, 20), receipt.LastSuccessfulAutomaticPeriod),
            "A successful server receipt did not suppress another automatic upload in the same month.");

        var live = ContributionSource.LiveGame("0.2.103"); live.Validate();
        var stream = ContributionSource.StreamArchive("0.2.103", "youtube:fixture"); stream.Validate();
        new ContributionSource(ContributionSourceKind.CollaboratorImport, "council-survey", 1,
            "FixtureImporter", "1").Validate();
        var badLiveRejected = false;
        try { (live with { SourceLocator = "account-or-video-id" }).Validate(); }
        catch (InvalidDataException) { badLiveRejected = true; }
        Check(badLiveRejected, "A live-game source accepted an identifying locator.");

        var installation = Guid.NewGuid();
        var signingPath = Path.Combine(directory, "upload-identity.json");
        var signing = InstallationSigningIdentity.LoadOrCreate(signingPath, installation);
        var restoredSigning = InstallationSigningIdentity.LoadOrCreate(signingPath, installation);
        var canonicalPayload = System.Text.Encoding.UTF8.GetBytes("api-v1\nfixture-batch\nfixture-digest");
        var signature = signing.Sign(canonicalPayload);
        Check(signing.Fingerprint.SequenceEqual(restoredSigning.Fingerprint) &&
            restoredSigning.Verify(canonicalPayload, signature) &&
            !restoredSigning.Verify(System.Text.Encoding.UTF8.GetBytes("changed"), signature),
            "DPAPI-protected installation signing identity did not persist or authenticate a canonical payload.");
        var signingJson = File.ReadAllText(signingPath);
        Check(signingJson.Contains("ProtectedPrivateKey", StringComparison.Ordinal) &&
            !signingJson.Contains("PRIVATE KEY", StringComparison.Ordinal),
            "Installation signing identity was not stored in its protected form.");

        var xaml = XDocument.Load(context.PathFromRoot("src", "GwentCompanion.App", "MainWindow.xaml"));
        var names = xaml.Descendants().Select(element => (string?)element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == "Name")).Where(name => name is not null).ToHashSet();
        Check(names.Contains("PublishAnonymousCurveChoice") && names.Contains("AutomaticMonthlyUploadChoice") &&
            names.Contains("PushToDatabaseButton"), "Settings is missing a database-sharing control.");
        Check(xaml.Descendants().Any(element => element.Name.LocalName == "CheckBox" &&
            (string?)element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Name") == "PublishAnonymousCurveChoice" &&
            (string?)element.Attribute("IsChecked") == "True"), "Anonymous curve display is not on by default.");

        var consentXaml = XDocument.Load(context.PathFromRoot("src", "GwentCompanion.App", "DataContributionConsentWindow.xaml"));
        var consentElements = consentXaml.Descendants().ToArray();
        Check(consentElements.Any(element => (string?)element.Attribute("Text") is { } text &&
                text.Contains("Balance Council", StringComparison.Ordinal)) &&
            consentElements.Any(element => (string?)element.Attribute("Text") is { } text &&
                text.Contains("anonymized", StringComparison.OrdinalIgnoreCase) && text.Contains("never published", StringComparison.OrdinalIgnoreCase)) &&
            consentElements.Any(element => element.Name.LocalName == "Expander" &&
                (string?)element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Name") == "PrivacyDetails" &&
                (string?)element.Attribute("IsExpanded") == "False"),
            "First-start consent must lead with Balance Council value and private anonymized records, with concrete details collapsed.");

        var appProject = File.ReadAllText(context.PathFromRoot("src", "GwentCompanion.App", "GwentCompanion.App.csproj"));
        Check(appProject.Contains("cache\\data-service.json", StringComparison.OrdinalIgnoreCase) &&
            appProject.Contains("CopyToPublishDirectory=\"PreserveNewest\"", StringComparison.Ordinal),
            "Published installations do not include the public data-service connection file.");

        var releaseConfigPath = context.PathFromRoot("cache", "data-service.json");
        var releaseConfig = DataServiceConfiguration.Load(releaseConfigPath);
        Check(releaseConfig.Enabled && releaseConfig.ServiceId != Guid.Empty &&
            releaseConfig.Endpoint is { Scheme: "https", UserInfo.Length: 0, Query.Length: 0, Fragment.Length: 0 },
            "The production build must contain only an HTTPS service address and non-secret service identifier.");
        var serviceId = Guid.NewGuid();
        var serviceStatePath = Path.Combine(directory, "data-service-state.json");
        var serviceState = DataContributionServerState.Empty(serviceId) with
        {
            InstallationHandle = Guid.NewGuid(), LastReceiptSeason = "2026-09",
            LastReceiptId = Guid.NewGuid(), LastReceiptAtUtc = DateTimeOffset.UtcNow,
        };
        DataContributionServerState.Save(serviceStatePath, serviceState);
        Check(DataContributionServerState.Load(serviceStatePath, serviceId).InstallationHandle == serviceState.InstallationHandle &&
            DataContributionServerState.Load(serviceStatePath, Guid.NewGuid()).InstallationHandle is null,
            "Server registration state did not persist or was reused across different services.");

        var migration = File.ReadAllText(context.PathFromRoot("backend", "supabase", "migrations", "202609130001_initial.sql"));
        foreach (var sourceKind in new[] { "live_game", "stream_archive", "collaborator_import" })
            Check(migration.Contains("'" + sourceKind + "'", StringComparison.Ordinal),
                "Database schema is missing source provenance: " + sourceKind);
        Check(migration.Contains("private.data_sources", StringComparison.Ordinal) &&
            migration.Contains("public.published_curve_points", StringComparison.Ordinal) &&
            migration.Contains("revoke all on all tables in schema private", StringComparison.OrdinalIgnoreCase),
            "Database schema does not preserve the private/public boundary.");

        Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(declined with { Schema = 99 }));
        var corruptRejected = false;
        try { DataContributionPreferences.Load(path); }
        catch (InvalidDataException) { corruptRejected = true; }
        Check(corruptRejected, "Unknown sharing-preference schema did not fail closed.");
        return Task.CompletedTask;
    }
}
