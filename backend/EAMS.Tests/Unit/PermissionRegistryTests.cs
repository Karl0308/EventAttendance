using System.Reflection;
using EAMS.Api.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// <b><see cref="EamsPermissions"/> is the whole set of §4.11 codes this API declares, and these tests
/// are what make that a fact rather than a comment.</b>
///
/// <para>
/// Before Phase 3b-3 the class held <c>attendance.capture</c> alone and the other ten codes were bare
/// string literals repeated across forty-odd attribute call sites. Moving them onto constants is only
/// half the change: a constant nobody is obliged to use is a convention, and the next endpoint written
/// under deadline gets a literal. These two assertions are the obligation — the forward one fails on a
/// code used but not declared, the reverse one on a code declared but not used.
/// </para>
///
/// <para>
/// <b>The forward direction is what a typo hits.</b> <c>[HasPermissionNotEnforced("student.read")]</c>
/// compiles, enforces nothing (ADR-001 D-6, so no request ever fails on it), and passes every other
/// test in this suite — it mints a permission no role will grant, and the first signal is Phase 6
/// going live with one endpoint reachable by nobody. That failure is invisible until it is expensive,
/// which is the shape this repo writes tests for.
/// </para>
///
/// <para>
/// <b>The reverse direction is what a deletion hits.</b> D-45 removed <c>groups.read</c> because
/// Technical Plan §7.1 had already assigned <c>students.read</c> to that page; a dead constant left
/// behind reads to the next person as a code the product has, so Phase 6 writes it into a role and
/// grants a permission that guards nothing. It also catches the reverse of a rename: change the
/// constant's value at the declaration and every call site follows, but a stale <em>second</em>
/// constant does not.
/// </para>
/// </summary>
public class PermissionRegistryTests
{
    /// <summary>
    /// <b>Scoped to the <c>EAMS.Api</c> assembly's controllers, deliberately, and not to every loaded
    /// controller.</b>
    ///
    /// <para>
    /// <c>PermissionProbeController</c> lives in this test assembly and carries
    /// <c>[HasPermissionNotEnforced("attendance.override")]</c> — a string that exists nowhere else in
    /// the codebase, chosen precisely <em>because</em> it names no real permission. That is the proof
    /// it exists to give: the attribute denies nothing, so an arbitrary code reaches the action anyway.
    /// A test that swept every loaded controller would fail on it, and the two ways to make it pass are
    /// both wrong — declaring <c>attendance.override</c> on the registry invents a permission the
    /// product does not have, and tidying the probe's literal into a real constant destroys the
    /// "arbitrary string" property the probe is the proof of.
    /// </para>
    ///
    /// <para>
    /// Reached through <c>typeof(Program).Assembly</c> rather than by name, so a project rename is a
    /// compile error here — the same reasoning <c>EamsOpenApi.XmlCommentPaths</c> records.
    /// </para>
    /// </summary>
    private static readonly Assembly ApiAssembly = typeof(Program).Assembly;

    /// <summary>
    /// Every declaration of the attribute in <c>EAMS.Api</c> — <b>on controller classes as well as on
    /// their actions.</b>
    ///
    /// <para>
    /// <b>The class-level half is not decoration, and leaving it out was a real hole.</b>
    /// <see cref="HasPermissionNotEnforcedAttribute"/> is declared
    /// <c>AttributeTargets.Class | AttributeTargets.Method</c>, and <c>PermissionProbeController</c>'s
    /// own comment advertises the class form as legal. <c>inherit: true</c> on a <see cref="MethodInfo"/>
    /// walks the base <em>method</em> chain; it never reaches the declaring type. So
    /// <c>[HasPermissionNotEnforced("studnets.read")]</c> on a controller class was invisible to both
    /// assertions below — a typo'd code, applied to every action on that controller, sailing past the
    /// one guard written to catch exactly that. No EAMS.Api controller declares at class level today,
    /// which is what makes it a hole rather than a live defect: the first one to do it would have
    /// arrived unguarded.
    /// </para>
    /// </summary>
    private static IReadOnlyList<(string Site, string Permission)> DeclaredAtCallSites()
    {
        var controllers = ApiAssembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .ToList();

        var onClasses = controllers
            .SelectMany(t => Declared(t).Select(p => ($"{t.Name} (class)", p)));

        var onActions = controllers
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(m => Declared(m).Select(p => ($"{m.DeclaringType?.Name}.{m.Name}", p)));

        return [.. onClasses.Concat(onActions)];

        static IEnumerable<string> Declared(MemberInfo member) => member
            .GetCustomAttributes(typeof(HasPermissionNotEnforcedAttribute), inherit: true)
            .Cast<HasPermissionNotEnforcedAttribute>()
            .Select(a => a.Permission);
    }

    private static IReadOnlyDictionary<string, string> RegistryConstants() =>
        typeof(EamsPermissions)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            // GetRawConstantValue rather than GetValue: a const string is a literal with no storage,
            // and this is the accessor that says so.
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void Every_permission_an_endpoint_declares_is_a_constant_on_the_registry()
    {
        var declared = DeclaredAtCallSites();
        var known = RegistryConstants().Values.ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(declared);
        Assert.NotEmpty(known);

        // Ordinal, not case-insensitive: the codes are compared against claim values by
        // RequireClaim, which is ordinal, so 'Students.Read' and 'students.read' are two permissions
        // to the authorization layer and must be two failures here.
        var unregistered = declared
            .Where(d => !known.Contains(d.Permission))
            .Select(d => $"{d.Site} → '{d.Permission}'")
            .Distinct()
            .Order()
            .ToList();

        Assert.True(
            unregistered.Count == 0,
            "These endpoints declare a permission code that is not a constant on EamsPermissions:\n  " +
            string.Join("\n  ", unregistered) +
            "\nAdd the constant and reference it at the call site. A bare literal here compiles, " +
            "denies nothing (ADR-001 D-6) and passes every other test, so a typo in it stays " +
            "invisible until Phase 6 grants a permission that guards no endpoint.");
    }

    [Fact]
    public void Every_constant_on_the_registry_is_declared_by_at_least_one_endpoint()
    {
        var used = DeclaredAtCallSites().Select(d => d.Permission).ToHashSet(StringComparer.Ordinal);

        var orphans = RegistryConstants()
            .Where(c => !used.Contains(c.Value))
            .Select(c => $"{c.Key} = '{c.Value}'")
            .Order()
            .ToList();

        Assert.True(
            orphans.Count == 0,
            "These constants are on EamsPermissions and no endpoint declares them:\n  " +
            string.Join("\n  ", orphans) +
            "\nDelete the constant, or apply it. A code the registry lists and nothing demands reads " +
            "to the next person as a permission the product has — which is how a role in Phase 6 ends " +
            "up granting something that guards nothing. D-45 deleted 'groups.read' for exactly this.");
    }

    /// <summary>
    /// The one code D-45 removed, pinned by absence.
    ///
    /// <para>
    /// The two assertions above are symmetric and would both stay green if <c>groups.read</c> came
    /// back on the registry <em>and</em> at a call site — which is precisely how it would come back,
    /// since re-minting it means writing both halves. §7.1 assigns <c>students.read</c> to the
    /// <c>/groups</c> page and the Technical Plan is source of truth for the permission map, so this
    /// names the decision rather than leaving it inferable from a diff.
    /// </para>
    /// </summary>
    [Fact]
    public void The_groups_read_code_D_45_deleted_has_not_come_back()
    {
        const string deleted = "groups.read";

        Assert.DoesNotContain(deleted, RegistryConstants().Values);

        var sites = DeclaredAtCallSites()
            .Where(d => string.Equals(d.Permission, deleted, StringComparison.Ordinal))
            .Select(d => d.Site)
            .ToList();

        Assert.True(
            sites.Count == 0,
            $"'{deleted}' is declared again at: {string.Join(", ", sites)}. Technical Plan §7.1 " +
            "guards the /groups page with 'students.read' and the plan is source of truth for the " +
            "permission map (D-45). If this is a deliberate reversal, register it as a new decision " +
            "in docs/adr/README.md rather than re-minting the code.");
    }
}
