using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// Technical Plan §6.6 — device registration and the key lifecycle (Phase 4a design, D-25).
///
/// <para>
/// <b>The invariant this whole class exists to keep: a plaintext key leaves the process exactly
/// twice</b> — in the response to <c>POST /devices</c> and in the response to
/// <c>POST /devices/{id}/regenerate-key</c>. <see cref="ToDto"/> is the only mapper, it has no key
/// field to fill, and every read goes through it, so "add the key to the list response" is not a thing
/// that can be done by accident.
/// <c>DeviceLifecycleTests.No_read_response_ever_carries_the_key</c> asserts it on the serialized
/// bytes rather than on the record shape, because a future <c>[JsonExtensionData</c>] or an anonymous
/// projection would slip past a type-level check.
/// </para>
/// </summary>
internal sealed class DeviceService : IDeviceService
{
    private readonly EamsDbContext _db;
    private readonly ISchoolContext _school;

    public DeviceService(EamsDbContext db, ISchoolContext school)
    {
        _db = db;
        _school = school;
    }

    private static DeviceDto ToDto(Device d) => new(
        d.Id, d.Name, d.DeviceType, d.ReaderModel, d.IsActive,
        d.ApiKeyId,
        HasActiveKey: d.HasKey && d.ApiKeyRevokedAt is null && d.IsActive,
        d.ApiKeyIssuedAt, d.ApiKeyLastUsedAt, d.ApiKeyRevokedAt, d.LastSeenAt);

    public async Task<IReadOnlyList<DeviceDto>> ListAsync(CancellationToken ct = default)
    {
        var devices = await _db.Devices.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
        return devices.Select(ToDto).ToList();
    }

    public async Task<DeviceDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        return device is null ? null : ToDto(device);
    }

    public async Task<DeviceWriteResponse> RegisterAsync(
        DeviceWriteRequest request, CancellationToken ct = default)
    {
        if (!Validate(request, out var name, out var deviceType, out var readerModel, out var message))
            return new DeviceWriteResponse(DeviceWriteOutcome.ValidationFailed, message, null, null);

        // The tenant comes from the context, never from the request — the same refusal
        // StudentService and EventService make, through the same helper.
        var schoolId = await _db.ResolveSchoolIdAsync(_school, ct);
        if (schoolId is null)
        {
            return new DeviceWriteResponse(
                DeviceWriteOutcome.NoSchoolResolved,
                "No school could be resolved for this device. Exactly one school must exist, or a " +
                "tenant must be pinned.",
                null, null);
        }

        var device = new Device
        {
            SchoolId = schoolId.Value,
            Name = name,
            DeviceType = deviceType,
            ReaderModel = readerModel,
            IsActive = request.IsActive,
        };

        var issued = ApplyNewKey(device);
        _db.Devices.Add(device);

        return await SaveIssuingAsync(device, issued, "Device registered.", ct);
    }

    public async Task<DeviceWriteResponse> UpdateAsync(
        Guid id, DeviceWriteRequest request, CancellationToken ct = default)
    {
        if (!Validate(request, out var name, out var deviceType, out var readerModel, out var message))
            return new DeviceWriteResponse(DeviceWriteOutcome.ValidationFailed, message, null, null);

        var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null) return NotFound();

        device.Name = name;
        device.DeviceType = deviceType;
        device.ReaderModel = readerModel;

        // Deliberately does not touch any ApiKey* column. Retiring a device and burning its
        // credential are two different statements — see Device.ApiKeyRevokedAt.
        device.IsActive = request.IsActive;
        device.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return new DeviceWriteResponse(DeviceWriteOutcome.Saved, "Device updated.", ToDto(device), null);
    }

    /// <summary>
    /// Hard cut: the new key overwrites the old one in the same row, so the previous token stops
    /// working the instant this commits. There is no overlap window and no second key column — see
    /// <see cref="IDeviceService"/> for why that operational cost is chosen rather than tolerated.
    ///
    /// <para>
    /// <c>ApiKeyRevokedAt</c> is cleared, because the row now holds a key that has <em>not</em> been
    /// revoked. Rotating is how an operator recovers a device whose key they burned.
    /// </para>
    /// </summary>
    public async Task<DeviceWriteResponse> RegenerateKeyAsync(Guid id, CancellationToken ct = default)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null) return NotFound();

        var issued = ApplyNewKey(device);
        device.UpdatedAt = DateTime.UtcNow;

        return await SaveIssuingAsync(
            device, issued,
            "A new key was issued. The previous key stopped working immediately, and this key is " +
            "shown once and cannot be retrieved again.",
            ct);
    }

    public async Task<DeviceWriteResponse> RevokeKeyAsync(Guid id, CancellationToken ct = default)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null) return NotFound();

        // Idempotent: already revoked, or never issued, both leave the postcondition true and report
        // Saved. A retry after a client timeout must not become a 409 on an operation whose whole
        // purpose is to make a credential stop working.
        if (device.ApiKeyRevokedAt is null && device.HasKey)
        {
            device.ApiKeyRevokedAt = DateTime.UtcNow;
            device.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return new DeviceWriteResponse(
            DeviceWriteOutcome.Saved,
            "The device's key is revoked. It can no longer authenticate; issue a new key to restore it.",
            ToDto(device), null);
    }

    /// <summary>
    /// §6.6 liveness. Deliberately writes both <c>LastSeenAt</c> and <c>ApiKeyLastUsedAt</c>: the call
    /// is itself an authenticated use of the key, and leaving the second column behind would make an
    /// idle-but-heartbeating device look like one whose key had gone unused.
    /// </summary>
    public async Task<DeviceWriteResponse> HeartbeatAsync(Guid id, CancellationToken ct = default)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null) return NotFound();

        var now = DateTime.UtcNow;
        device.LastSeenAt = now;
        device.ApiKeyLastUsedAt = now;
        device.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        return new DeviceWriteResponse(DeviceWriteOutcome.Saved, "Heartbeat recorded.", ToDto(device), null);
    }

    // ---------------------------------------------------------------------------------- internals

    /// <summary>
    /// Mints a key onto the entity and hands back the plaintext. The secret exists only in the
    /// returned value and in the caller's response body; the entity gets the id and the hash.
    /// <c>ApiKey</c> — §4.10's original single-column key — is left untouched and therefore null,
    /// which <c>DeviceLifecycleTests.Registering_and_rotating_never_write_the_plan_s_original_ApiKey_column</c>
    /// pins.
    /// </summary>
    private static DeviceKey.IssuedKey ApplyNewKey(Device device)
    {
        var issued = DeviceKey.Issue();

        device.ApiKeyId = issued.KeyId;
        device.ApiKeyHash = issued.Hash;
        device.ApiKeyIssuedAt = DateTime.UtcNow;
        device.ApiKeyRevokedAt = null;
        device.ApiKeyLastUsedAt = null;

        return issued;
    }

    /// <summary>
    /// Saves a key-issuing write, translating the one unique index that can reject it
    /// (<c>UX_Devices_ApiKeyId</c>) into an outcome rather than a 500.
    ///
    /// <para>
    /// A 48-bit collision is not a real event — it is reported rather than retried precisely so that
    /// if it ever happens, someone sees it. A silent retry loop would hide a broken RNG, which is the
    /// only realistic way this branch is ever reached.
    /// </para>
    /// </summary>
    private async Task<DeviceWriteResponse> SaveIssuingAsync(
        Device device, DeviceKey.IssuedKey issued, string message, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            // EF leaves the failed entity Added/Modified after a failed SaveChanges, and any later
            // save on this context would retry it. A registration is detached outright; a rotation is
            // reloaded from the database, so the device keeps the key it already had rather than
            // carrying a half-applied new one in memory.
            var entry = _db.Entry(device);
            if (entry.State == EntityState.Added) entry.State = EntityState.Detached;
            else await entry.ReloadAsync(ct);

            return new DeviceWriteResponse(
                DeviceWriteOutcome.KeyCollision,
                "The generated key id collided with an existing one. This should be impossible; " +
                "retry, and if it recurs the random number generator is the thing to look at.",
                null, null);
        }

        return new DeviceWriteResponse(
            DeviceWriteOutcome.Saved, message, ToDto(device),
            new DeviceKeyIssuedDto(ToDto(device), issued.Token));
    }

    private static DeviceWriteResponse NotFound() =>
        new(DeviceWriteOutcome.NotFound, "Device not found.", null, null);

    /// <summary>
    /// §4.10's column rules, checked before anything is read or written — in the service rather than
    /// the controller, so the next caller that is not HTTP inherits them. Same placement, and the same
    /// reason, as <c>AttendanceService.ManualAsync</c>'s status and notes guards.
    /// </summary>
    private static bool Validate(
        DeviceWriteRequest request,
        out string name, out string deviceType, out string? readerModel, out string message)
    {
        name = request.Name?.Trim() ?? "";
        deviceType = "";
        readerModel = string.IsNullOrWhiteSpace(request.ReaderModel) ? null : request.ReaderModel.Trim();
        message = "";

        if (!DeviceText.IsValidName(request.Name))
        {
            message = $"Name is required and must be {DeviceText.NameMaxLength} characters or fewer.";
            return false;
        }

        // Null or blank takes §4.10's most common reader: a fixed kiosk. Stated rather than left to a
        // column default, because the column has none and NOT NULL would otherwise reject the row.
        var requestedType = string.IsNullOrWhiteSpace(request.DeviceType)
            ? DeviceTypes.Kiosk
            : request.DeviceType;

        if (!DeviceTypes.TryNormalize(requestedType, out deviceType))
        {
            message = $"DeviceType must be one of {string.Join(", ", DeviceTypes.All)}.";
            return false;
        }

        if (!DeviceText.IsValidReaderModel(readerModel))
        {
            message = $"ReaderModel must be {DeviceText.ReaderModelMaxLength} characters or fewer.";
            return false;
        }

        return true;
    }
}
