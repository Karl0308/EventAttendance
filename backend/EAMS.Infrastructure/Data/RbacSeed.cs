using EAMS.Application.Abstractions;
using EAMS.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Data;

/// <summary>
/// Writes the §4.11 rows the authorization model is <em>made of</em> — the <c>Permissions</c> the
/// product declares and the four <c>Roles</c> with their grants.
///
/// <para>
/// <b>This runs in every environment, Production included, and that is the decision this file
/// exists for.</b> It is the twin of <see cref="SeedData"/> and its opposite: that one writes eight
/// fictional students and a kiosk whose key is printed in source, and must never run anywhere real;
/// this one writes rows without which permission enforcement authorizes nobody at all. Both used to
/// ride the same boolean — <c>seed: app.Environment.IsDevelopment()</c> — which meant the first
/// production deployment with §11 turned on would have locked every operator out of their own
/// installation, and the cause would have looked like a broken authorization layer rather than a
/// missing row.
/// </para>
///
/// <para>
/// <b>Both halves are independently idempotent, and each is guarded on its own rows.</b> That is
/// the rule <see cref="SeedData.InitializeAsync"/> learned the hard way: a step guarded on some other
/// table's contents only ever runs on a database nobody has. A permission is inserted if its code is
/// absent; a role is created if its name is absent. Neither reads the other's guard.
/// </para>
/// </summary>
internal static class RbacSeed
{
    /// <summary>
    /// Applies the reference data. Safe to call on every start and on a database that already holds
    /// a school, users, and rows an operator has edited.
    /// </summary>
    public static async Task ApplyAsync(
        EamsDbContext db, RbacReferenceData reference, ILogger logger, CancellationToken ct = default)
    {
        if (reference.Permissions.Count == 0 && reference.Roles.Count == 0) return;

        var permissionIds = await SeedPermissionsAsync(db, reference, logger, ct);
        await SeedRolesAsync(db, reference, permissionIds, logger, ct);
    }

    /// <summary>
    /// Inserts any declared code that has no row, and returns the id of every code — new or
    /// pre-existing — so the role step can grant without a second round trip per grant.
    ///
    /// <para>
    /// <b>It never deletes a code it does not recognise.</b> A row this build has no constant for is
    /// either a code from a newer deployment sharing the database or one an operator added; deleting
    /// it would take its <c>RolePermissions</c> grants with it, which is data loss decided by a
    /// startup path. <c>RbacSeedTests</c> asserts the table holds no unknown code, which is the right
    /// place for that statement — a test can fail loudly where a seeder can only destroy quietly.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<string, Guid>> SeedPermissionsAsync(
        EamsDbContext db, RbacReferenceData reference, ILogger logger, CancellationToken ct)
    {
        // Ordinal throughout. RequireClaim compares permission codes ordinally, so 'Students.Read'
        // and 'students.read' are two permissions to the authorization layer and must be two rows
        // here rather than one that matched case-insensitively.
        var existing = await db.Permissions
            .ToDictionaryAsync(p => p.Code, p => p.Id, StringComparer.Ordinal, ct);

        var added = 0;
        foreach (var declared in reference.Permissions)
        {
            if (existing.ContainsKey(declared.Code)) continue;

            var permission = new Permission { Code = declared.Code, Description = declared.Description };
            db.Permissions.Add(permission);
            existing[declared.Code] = permission.Id;
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Seeded {Count} §4.11 permission code(s). Reference data — this runs in every " +
                "environment, because enforcement over an empty Permissions table authorizes nobody.",
                added);
        }

        return existing;
    }

    /// <summary>
    /// Creates any declared role that does not exist, <b>with its grants, at creation, once</b>.
    ///
    /// <para>
    /// <b>An existing role is left completely alone — its grants are never reconciled.</b> An
    /// administrator who narrows <c>Organizer</c> has made a decision about their institution, and a
    /// startup that re-applied the matrix would silently restore permissions somebody deliberately
    /// removed, on the next deploy, with no log line and no audit row. Adding a role that is absent is
    /// additive and safe; rewriting one that is present is not. A genuinely new grant for an existing
    /// role is a migration or an operator action, not a startup side effect.
    /// </para>
    ///
    /// <para>
    /// <b>A code granted here that has no <c>Permissions</c> row is skipped and logged</b> rather than
    /// throwing. The two lists come from one registry so it should be impossible; if it happens, a
    /// host that refuses to start over a grant is worse than one that starts with a role missing a
    /// permission and says so.
    /// </para>
    /// </summary>
    private static async Task SeedRolesAsync(
        EamsDbContext db,
        RbacReferenceData reference,
        Dictionary<string, Guid> permissionIds,
        ILogger logger,
        CancellationToken ct)
    {
        var existing = await db.Roles.Select(r => r.Name).ToListAsync(ct);
        var known = existing.ToHashSet(StringComparer.Ordinal);

        var created = 0;
        foreach (var declared in reference.Roles)
        {
            if (known.Contains(declared.Name)) continue;

            var role = new Role
            {
                Name = declared.Name,
                Description = declared.Description,
                // All four are built-in. The flag is what a future role editor reads to refuse
                // deletion — a product whose four built-in roles can be deleted is one that can be
                // locked out of itself — and it is set at creation because retrofitting it means
                // deciding after the fact which of an operator's rows were ours.
                IsSystem = true,
            };
            db.Roles.Add(role);

            // The grant matrix, written exactly once per role. NOTE: no role grants
            // attendance.capture, and that empty column is deliberate — it is the device's
            // permission, and a human holding it could post taps indistinguishable from a real card
            // presentation at a reader. See EamsRoles.HumanAssignable for the full reasoning, and
            // RbacSeedTests, which asserts the absence as a positive statement so it cannot be
            // quietly filled in.
            foreach (var code in declared.PermissionCodes)
            {
                if (!permissionIds.TryGetValue(code, out var permissionId))
                {
                    logger.LogWarning(
                        "Role '{Role}' declares permission '{Code}', which has no Permissions row. " +
                        "The grant was skipped. Both lists come from one registry, so this means the " +
                        "registry and the seed have drifted apart.",
                        declared.Name, code);
                    continue;
                }

                db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permissionId });
            }

            created++;
        }

        if (created == 0) return;

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Created {Count} §4.11 role(s) with their grants. Grants are applied at creation only — " +
            "an existing role's permissions are never reconciled, so a narrowing an administrator " +
            "made is not reverted on the next start.",
            created);
    }
}
