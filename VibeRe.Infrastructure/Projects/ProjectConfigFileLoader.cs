using System.Text.Json;
using System.Text.Json.Serialization;
using VibeRe.Application.Projects;
using VibeRe.Domain.Projects;

namespace VibeRe.Infrastructure.Projects;

/// <summary>
/// Reads <c>vibe.re</c> files: JSON, with comments and trailing commas allowed. Unknown properties are rejected
/// so a typo like <c>runOnWorktreeCreated</c> doesn't silently do nothing.
/// </summary>
public sealed class ProjectConfigFileLoader : IProjectConfigLoader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    public async Task<ProjectConfig?> FindAsync(string directory, CancellationToken cancellationToken = default)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
        {
            var path = Path.Combine(current.FullName, ProjectConfig.FileName);
            if (File.Exists(path))
            {
                return Parse(await File.ReadAllTextAsync(path, cancellationToken), current.FullName, path);
            }

            // A worktree has a `.git` file, a main checkout a `.git` directory: either way, the repository ends here.
            if (Path.Exists(Path.Combine(current.FullName, ".git")))
            {
                return null;
            }
        }

        return null;
    }

    internal static ProjectConfig Parse(string json, string rootDirectory, string path)
    {
        try
        {
            var file = JsonSerializer.Deserialize<FileDto>(json, Options)
                ?? throw new JsonException("The file is empty.");
            return new ProjectConfig(rootDirectory, (file.Actions ?? []).Select(action => new ProjectAction(
                action.Name,
                action.Command,
                action.Description,
                action.RunOnWorktreeCreate,
                action.Tool)));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new ProjectConfigException($"{path} is invalid: {exception.Message}", exception);
        }
    }

    private sealed record FileDto(
        [property: JsonPropertyName("$schema")] string? Schema = null,
        List<ActionDto>? Actions = null);

    private sealed record ActionDto(
        string Name,
        string Command,
        string? Description = null,
        bool RunOnWorktreeCreate = false,
        bool Tool = true);
}
