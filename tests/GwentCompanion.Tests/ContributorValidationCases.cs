using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

internal interface IContributorValidationCase
{
    string Id { get; }
    string Kind { get; }
    string Summary { get; }
    Task RunAsync(ContributorValidationContext context);
}

internal sealed class ContributorValidationContext
{
    public ContributorValidationContext(string projectRoot)
    {
        ProjectRoot = Path.GetFullPath(projectRoot);
        Check(File.Exists(Path.Combine(ProjectRoot, "GwentCompanion.sln")),
            "Contributor validation must run from a Gwent Vision project root.");
    }

    public string ProjectRoot { get; }

    public string PathFromRoot(params string[] parts)
    {
        var path = parts.Aggregate(ProjectRoot, Path.Combine);
        var fullPath = Path.GetFullPath(path);
        var prefix = ProjectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Check(fullPath.Equals(ProjectRoot, StringComparison.OrdinalIgnoreCase) ||
              fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
            "Contributor validation path escapes the project root: " + fullPath);
        return fullPath;
    }

    public static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal static class ContributorValidationCases
{
    private static readonly Regex Slug = new("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    public static async Task RunAsync(string projectRoot)
    {
        var context = new ContributorValidationContext(projectRoot);
        var cases = Assembly.GetExecutingAssembly().GetTypes()
            .Where(type => !type.IsAbstract && typeof(IContributorValidationCase).IsAssignableFrom(type))
            .Select(type => (IContributorValidationCase)(Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Could not create contributor validation case " + type.Name)))
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

        ContributorValidationContext.Check(cases.Length > 0, "No contributor validation cases were discovered.");
        ContributorValidationContext.Check(cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == cases.Length,
            "Contributor validation case IDs must be unique.");

        foreach (var item in cases)
        {
            ContributorValidationContext.Check(Slug.IsMatch(item.Id),
                $"Contributor validation ID must be lowercase kebab-case: {item.Id}");
            ContributorValidationContext.Check(Slug.IsMatch(item.Kind),
                $"Contributor validation kind must be lowercase kebab-case: {item.Kind}");
            ContributorValidationContext.Check(!string.IsNullOrWhiteSpace(item.Summary),
                $"Contributor validation case {item.Id} needs a summary.");
            try
            {
                await item.RunAsync(context);
                Console.WriteLine($"PASS [{item.Kind}] {item.Id}: {item.Summary}");
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"Contributor validation failed [{item.Kind}] {item.Id}: {item.Summary}", error);
            }
        }

        Console.WriteLine($"PASS {cases.Length} automatically discovered contributor validation case(s).");
    }
}
