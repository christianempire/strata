namespace Strata.Host;

/// <summary>Paths kept out of the change log by default.</summary>
/// <remarks>
/// Strata's own data directory is excluded because it is rewritten by every tick. Left in, a
/// database this size is permanently the largest single change on the machine, which makes the feed
/// useless for the thing it exists to show. The filtering itself lives in
/// <see cref="Strata.Core.Diffing.PathExclusions"/>; only the default belongs here, because it is
/// the only part that knows where the database lives.
/// </remarks>
internal static class Exclusions
{
    internal static IReadOnlyList<string> Defaults { get; } = [Strata.Storage.StrataDatabase.DefaultDirectory];
}
