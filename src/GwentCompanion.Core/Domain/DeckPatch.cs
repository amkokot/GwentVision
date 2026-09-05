namespace GwentCompanion.Core.Domain;

/// <summary>Patch provenance, not a claim that an old list is legal under today's card costs.</summary>
public sealed record DeckPatch(string Label, bool Inferred, string Source);

public sealed record DeckOccurrence(string Id, string Patch, string Kind, string Source,
    DateTimeOffset? At = null, bool Inferred = false);
