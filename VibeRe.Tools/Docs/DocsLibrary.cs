using System.Text.RegularExpressions;

namespace VibeRe.Tools.Docs;

/// <summary>One Markdown page of the user guide, addressed by its path (e.g. <c>tools.md</c>).</summary>
public sealed record DocPage(string Path, string Content)
{
    public string Title { get; } =
        Content.Split('\n').FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim()
        ?? Path;
}

/// <summary>A heading and the text under it, the unit that <see cref="DocsLibrary.Search"/> ranks.</summary>
public sealed record DocSection(DocPage Page, string Heading, string Content);

/// <summary>
/// VibeRe's own documentation, embedded from <c>docs/guide</c>. Search is plain keyword ranking: the guide is
/// small, and exact matches on command names and settings matter more than fuzzy similarity.
/// </summary>
public sealed partial class DocsLibrary
{
    private const string ResourcePrefix = "docs/";

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "can", "do", "does", "for", "how", "i", "in", "is", "it", "me", "my", "of", "on",
        "or", "the", "to", "what", "when", "where", "which", "why", "with", "you",
    ];

    private readonly IReadOnlyList<DocSection> _sections;

    public DocsLibrary(IEnumerable<DocPage> pages)
    {
        Pages = pages.OrderBy(page => page.Path, StringComparer.Ordinal).ToList();
        _sections = Pages.SelectMany(Split).ToList();
    }

    public IReadOnlyList<DocPage> Pages { get; }

    public static DocsLibrary LoadEmbedded()
    {
        var assembly = typeof(DocsLibrary).Assembly;
        return new DocsLibrary(assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(name =>
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
                return new DocPage(name[ResourcePrefix.Length..], reader.ReadToEnd().ReplaceLineEndings("\n"));
            }));
    }

    /// <summary>Finds a page by path, with or without <c>docs/</c> and <c>.md</c>.</summary>
    public DocPage? Find(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        normalized = normalized.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[ResourcePrefix.Length..]
            : normalized;
        normalized = normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? normalized : normalized + ".md";
        return Pages.FirstOrDefault(page => string.Equals(page.Path, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Ranks sections by how often the query's terms occur, weighting headings and sections that match more
    /// distinct terms. Terms are cut to 6 characters so "configure" also finds "configuration".
    /// </summary>
    public IReadOnlyList<DocSection> Search(string query, int limit)
    {
        var terms = WordPattern().Matches(query.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(term => term.Length > 1 && !StopWords.Contains(term))
            .Select(term => term.Length > 6 ? term[..6] : term)
            .Distinct()
            .ToList();
        if (terms.Count == 0)
        {
            return [];
        }

        return _sections
            .Select(section =>
            {
                var heading = section.Heading.ToLowerInvariant();
                var body = section.Content.ToLowerInvariant();
                var counts = terms.Select(term => 5 * Occurrences(heading, term) + Occurrences(body, term)).ToList();
                return (Section: section, Score: counts.Sum() * counts.Count(count => count > 0));
            })
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .Take(limit)
            .Select(result => result.Section)
            .ToList();
    }

    private static IEnumerable<DocSection> Split(DocPage page)
    {
        var heading = page.Title;
        var lines = new List<string>();
        var inCode = false;
        foreach (var line in page.Content.Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
            }

            if (!inCode && line.StartsWith('#'))
            {
                if (lines.Any(text => text.Length > 0))
                {
                    yield return new DocSection(page, heading, string.Join('\n', lines).Trim());
                }

                heading = line.TrimStart('#').Trim();
                lines.Clear();
                continue;
            }

            lines.Add(line);
        }

        if (lines.Any(text => text.Length > 0))
        {
            yield return new DocSection(page, heading, string.Join('\n', lines).Trim());
        }
    }

    private static int Occurrences(string text, string term)
    {
        var count = 0;
        for (var index = text.IndexOf(term, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(term, index + term.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex WordPattern();
}
