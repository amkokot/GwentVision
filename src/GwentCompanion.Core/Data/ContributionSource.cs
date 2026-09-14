using System.Text.RegularExpressions;

namespace GwentCompanion.Core.Data;

/// <summary>Stable ingestion categories. Analyses must select or stratify by this field.</summary>
public enum ContributionSourceKind : byte
{
    LiveGame = 1,
    StreamArchive = 2,
    CollaboratorImport = 3,
}

/// <summary>
/// Provenance sent once per upload batch. SourceLocator is a private, canonical
/// identifier or digest; it is never copied into the public MMR projection.
/// </summary>
public sealed record ContributionSource(
    ContributionSourceKind Kind,
    string DatasetNamespace,
    int MetadataSchemaVersion,
    string Producer,
    string ProducerVersion,
    string? SourceLocator = null)
{
    private static readonly Regex Slug = new("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    public static ContributionSource LiveGame(string version) =>
        new(ContributionSourceKind.LiveGame, "gwent-vision-live", 1, "GwentVision", version);

    public static ContributionSource StreamArchive(string version, string sourceKey) =>
        new(ContributionSourceKind.StreamArchive, "gwent-vision-stream", 1, "GwentVision", version, sourceKey);

    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || string.IsNullOrWhiteSpace(DatasetNamespace) || !Slug.IsMatch(DatasetNamespace) ||
            MetadataSchemaVersion is < 1 or > 1000 ||
            string.IsNullOrWhiteSpace(Producer) || Producer.Length > 80 ||
            string.IsNullOrWhiteSpace(ProducerVersion) || ProducerVersion.Length > 80 ||
            SourceLocator?.Length > 240)
            throw new InvalidDataException("Invalid contribution source provenance.");
        if (Kind == ContributionSourceKind.LiveGame && SourceLocator is not null)
            throw new InvalidDataException("Live-game uploads cannot include a source locator.");
        if (Kind == ContributionSourceKind.StreamArchive && string.IsNullOrWhiteSpace(SourceLocator))
            throw new InvalidDataException("Stream uploads require a private source locator.");
    }
}
