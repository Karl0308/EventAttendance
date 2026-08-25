using System.Text.RegularExpressions;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// <b><c>IgnoreQueryFilters</c> in the authentication path is contained to exactly two methods, and
/// this is what keeps it there.</b>
///
/// <para>
/// The hazard is specific and it misbehaves in opposite directions depending on the deployment.
/// <c>Users</c> and <c>UserRoles</c> both carry a <c>SchoolId</c> query filter, and the login query
/// is what <em>determines</em> the tenant — there is no claim yet when a credential is presented.
/// During the staged cutover <c>ClaimsSchoolContext</c> falls back to the pinned development school
/// for a request with no credentials, so a filtered login lookup would see only that school's users
/// and every other school's operator would get a perfectly correct-looking <c>401</c>. Once the pin is
/// deleted the tenant is <c>null</c>, the filter goes inactive, and the identical code starts working.
/// <b>In a one-school development database the two are indistinguishable and both look right.</b>
/// </para>
///
/// <para>
/// So the exemption has to be taken, and taking it is exactly how a tenant boundary gets left open by
/// accident. Two constraints make it safe, and both are asserted here rather than described:
/// <b>exactly two methods</b>, so the surface is enumerable; and <b>each projects immediately</b>, so
/// nothing that is still an entity — and therefore nothing that could be tracked, navigated from, or
/// handed to a caller — escapes an unfiltered query.
/// </para>
///
/// <para>
/// <b>Why this is a source scan and not a behavioural test.</b> The failure it prevents has no
/// symptom: an <c>IgnoreQueryFilters</c> added to a third method returns <em>more</em> rows, so
/// every existing test still passes and the only visible consequence is one school reading another's
/// data. There is nothing to assert on except the code itself.
/// </para>
///
/// <para>
/// <b>Scope, stated precisely.</b> This governs the Phase 6b authentication path — <c>AuthService</c>
/// — and not the whole backend. <c>IgnoreQueryFilters</c> is used widely and legitimately elsewhere
/// (<c>SisImportService</c> and <c>StudentGroupProjection</c> read across the term structure during an
/// import; <c>DeviceAuthenticator</c> and <c>UserCredentialVerifier</c> take the same
/// credential-before-tenant exemption this file describes). A repository-wide ban would be a rule
/// nobody could keep, and a rule nobody keeps is deleted rather than obeyed.
/// </para>
/// </summary>
public class AuthTenantContainmentTests
{
    private const string AuthServiceSource =
        "backend/EAMS.Infrastructure/Identity/AuthService.cs";

    /// <summary>
    /// The two methods that may take the exemption, named. Adding a third is a decision, and a
    /// decision belongs in this list beside a reason rather than in a diff nobody reads twice.
    /// </summary>
    private static readonly string[] PermittedMethods =
    [
        "LoadProfileAsync",
        "LoadPermissionsAsync",
    ];

    [Fact]
    public void The_auth_service_ignores_query_filters_in_exactly_two_methods()
    {
        var source = ReadSource();
        var found = MethodsCalling(source, "IgnoreQueryFilters");

        Assert.Equal(PermittedMethods.Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Each_exempt_method_projects_before_it_materializes()
    {
        var source = ReadSource();

        foreach (var name in PermittedMethods)
        {
            var body = BodyOf(source, name);

            var ignoreAt = body.IndexOf("IgnoreQueryFilters", StringComparison.Ordinal);
            var selectAt = body.IndexOf(".Select(", StringComparison.Ordinal);

            Assert.True(ignoreAt >= 0, $"{name} no longer calls IgnoreQueryFilters.");
            Assert.True(
                selectAt > ignoreAt,
                $"{name} calls IgnoreQueryFilters without a .Select() after it. An unfiltered query " +
                $"that materializes an entity puts a tracked row from an arbitrary tenant into the " +
                $"change tracker, where anything holding that DbContext can navigate from it.");

            foreach (var materializer in new[] { "ToListAsync", "FirstOrDefaultAsync", "SingleAsync" })
            {
                var at = body.IndexOf(materializer, StringComparison.Ordinal);
                if (at < 0) continue;

                Assert.True(
                    at > selectAt,
                    $"{name} materializes with {materializer} before projecting. The projection has " +
                    $"to come first, or the unfiltered rows are entities by the time they are read.");
            }
        }
    }

    [Fact]
    public void The_authenticated_read_behind_auth_me_stays_filtered()
    {
        // GetProfileAsync is the deliberate counterpart of the two exemptions above: by the time
        // /auth/me runs, the caller's token has named a tenant, so the filter is active AND correct.
        // A token minted for one school must not be able to read a user row in another, however it
        // came to name one — which is the property that would silently vanish if someone "made it
        // consistent" with the two methods beside it.
        var body = BodyOf(ReadSource(), "GetProfileAsync");

        Assert.DoesNotContain("IgnoreQueryFilters", body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------ scanning

    private static string ReadSource()
    {
        var path = RepoPath(AuthServiceSource);
        Assert.True(File.Exists(path), $"{AuthServiceSource} is missing; the containment cannot be checked.");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The names of the methods whose bodies contain <paramref name="call"/>.
    ///
    /// <para>
    /// Comment text is stripped first, and that is not cosmetic: this file's own class remarks name
    /// <c>IgnoreQueryFilters</c> several times to explain the containment, and a scan that counted
    /// prose would fail on its own documentation.
    /// </para>
    /// </summary>
    private static IEnumerable<string> MethodsCalling(string source, string call)
    {
        var code = StripComments(source);

        return Members(code)
            .Where(m => m.Body.Contains(call, StringComparison.Ordinal))
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal);
    }

    private static string BodyOf(string source, string name)
    {
        var members = Members(StripComments(source)).Where(m => m.Name == name).ToArray();

        Assert.True(members.Length > 0, $"{AuthServiceSource} declares no member named '{name}'.");
        return string.Concat(members.Select(m => m.Body));
    }

    /// <summary>
    /// Every method declaration and the text up to the next one.
    ///
    /// <para>
    /// A regular expression rather than a Roslyn parse, deliberately. Adding
    /// <c>Microsoft.CodeAnalysis</c> to the test project to police one file would be a heavier
    /// dependency than the rule is worth, and the shape being matched is entirely under this
    /// repository's control: a member declaration on one line, which is how every method in
    /// <c>AuthService</c> is written.
    /// </para>
    /// </summary>
    private static IEnumerable<(string Name, string Body)> Members(string code)
    {
        var declarations = Regex.Matches(
            code,
            @"^\s{4}(?:public|private|internal|protected)[^\r\n(]*?\b(?<name>\w+)\s*\(",
            RegexOptions.Multiline);

        for (var i = 0; i < declarations.Count; i++)
        {
            var start = declarations[i].Index;
            var end = i + 1 < declarations.Count ? declarations[i + 1].Index : code.Length;

            yield return (declarations[i].Groups["name"].Value, code[start..end]);
        }
    }

    private static string StripComments(string source) =>
        Regex.Replace(source, @"^\s*(///|//).*$", "", RegexOptions.Multiline);

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
