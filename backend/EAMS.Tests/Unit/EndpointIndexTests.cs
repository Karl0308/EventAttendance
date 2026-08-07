using System.Text.Json;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// <b><c>docs/api/endpoints.md</c> lists every endpoint the contract publishes.</b>
///
/// <para>
/// The index is generated from <c>docs/api/openapi.json</c> by
/// <c>scripts/generate-endpoint-index.mjs</c>, which is what keeps it honest — but a generated file
/// checked in beside the code can go stale with nothing failing, and that is exactly the drift Phase
/// 4e spent 443 deleted lines ending. <c>OpenApiDocumentTests</c> puts that guard on
/// <c>openapi.json</c>; this puts it on the index derived from it.
/// </para>
///
/// <para>
/// <b>What is asserted is coverage, not rendering.</b> This checks that every operation has a row and
/// that no row survives an endpoint's deletion — the two failures that make the index lie. It
/// deliberately does not re-implement the generator's prose extraction: a test that reproduced the
/// blurb logic would pass by construction and pin wording nobody agreed to freeze. Regenerating is
/// how you fix a failure here:
/// <c>node scripts/generate-endpoint-index.mjs --write</c>.
/// </para>
///
/// <para>
/// Pure file reads, so this is a unit test — it needs no SQL Server and no host. It also does not
/// shell out to Node: the .NET suite must not acquire a JavaScript toolchain as a dependency, and
/// the invariant is checkable without one.
/// </para>
/// </summary>
public class EndpointIndexTests
{
    private const string Contract = "docs/api/openapi.json";
    private const string Index = "docs/api/endpoints.md";

    private const string Regenerate = "node scripts/generate-endpoint-index.mjs --write";

    /// <summary>Methods the index renders, matching the generator's list.</summary>
    private static readonly string[] Methods =
        ["get", "post", "put", "patch", "delete", "head", "options", "trace"];

    [Fact]
    public void Every_published_endpoint_has_a_row_in_the_index()
    {
        var index = ReadIndex();

        foreach (var (method, route) in PublishedOperations())
        {
            Assert.True(
                index.Contains(Row(method, route), StringComparison.Ordinal),
                $"{Index} has no row for `{method}` `{route}`, which {Contract} publishes. The " +
                $"endpoint index is incomplete, so a reader looking for that endpoint concludes it " +
                $"does not exist. Regenerate it in the same change that added the endpoint: {Regenerate}");
        }
    }

    /// <summary>
    /// The other direction: a row for an endpoint that no longer exists. Worse than a missing row —
    /// a caller builds against it and gets a 404 from a documented route.
    /// </summary>
    [Fact]
    public void The_index_lists_no_endpoint_the_contract_does_not_publish()
    {
        var published = PublishedOperations()
            .Select(o => Row(o.Method, o.Route))
            .ToHashSet(StringComparer.Ordinal);

        var listed = ReadIndex()
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("| `", StringComparison.Ordinal));

        foreach (var line in listed)
        {
            var cells = line.Split('|', StringSplitOptions.TrimEntries);

            // A rendered row is `| method | route | auth | errors | blurb |` — six cells once the
            // leading and trailing pipes produce empties. Anything shorter is a header separator or
            // prose that merely opens with a pipe, and is not a claim about an endpoint.
            if (cells.Length < 4)
            {
                continue;
            }

            var row = Row(cells[1].Trim('`'), cells[2].Trim('`'));

            Assert.True(
                published.Contains(row),
                $"{Index} lists {cells[1]} {cells[2]}, which {Contract} does not publish. A " +
                $"documented route that 404s is worse than an undocumented one. Regenerate the " +
                $"index in the same change that removed the endpoint: {Regenerate}");
        }
    }

    /// <summary>
    /// Every operation in the committed contract, as (METHOD, route-relative-to-`/api/v1`) — the
    /// form the index renders, so comparison needs no second transformation.
    /// </summary>
    private static IEnumerable<(string Method, string Route)> PublishedOperations()
    {
        var path = RepoPath(Contract);

        Assert.True(File.Exists(path), $"{Contract} is missing; the endpoint index cannot be checked.");

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var paths = document.RootElement.GetProperty("paths");
        var operations = new List<(string, string)>();

        foreach (var route in paths.EnumerateObject())
        {
            foreach (var method in Methods)
            {
                if (route.Value.TryGetProperty(method, out _))
                {
                    operations.Add((method.ToUpperInvariant(), Relative(route.Name)));
                }
            }
        }

        Assert.NotEmpty(operations);

        return operations;
    }

    /// <summary>The `/api/v1` mount is stated once in the index preamble, not on every row.</summary>
    private static string Relative(string route)
    {
        var trimmed = route.StartsWith("/api/v1", StringComparison.Ordinal)
            ? route["/api/v1".Length..]
            : route;

        return trimmed.Length == 0 ? "/" : trimmed;
    }

    /// <summary>The identity of a row: its method and route cells, ignoring everything rendered after.</summary>
    private static string Row(string method, string route) => $"| `{method}` | `{route}` |";

    private static string ReadIndex()
    {
        var path = RepoPath(Index);

        Assert.True(File.Exists(path), $"{Index} is missing. Generate it: {Regenerate}");

        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    /// <summary>
    /// A repository-relative path resolved by walking up from the test binary to the directory
    /// holding <c>EAMS.sln</c> — the same anchor <c>OpenApiDocumentTests</c> uses, so neither depends
    /// on the working directory a runner happens to choose.
    /// </summary>
    private static string RepoPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EAMS.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "EAMS.sln is not above the test binary; cannot locate the repository.");

        return Path.Combine(directory!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
