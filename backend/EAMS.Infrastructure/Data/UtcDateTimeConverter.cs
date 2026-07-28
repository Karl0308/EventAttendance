using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EAMS.Infrastructure.Data;

/// <summary>
/// Restores <see cref="DateTimeKind.Utc"/> on every <c>DateTime</c> materialized from SQL Server.
///
/// <para>
/// <b>The bug this closes.</b> §4 stores timestamps as <c>datetime2</c>, which has no offset, so EF
/// materializes them as <see cref="DateTimeKind.Unspecified"/>. <c>System.Text.Json</c> serializes
/// an <c>Unspecified</c> value with no <c>Z</c> and no offset — <c>"2026-07-28T02:17:11.98"</c> —
/// and JavaScript's <c>new Date()</c> reads an offset-less date-time as <em>local</em>. In Manila
/// that renders every persisted timestamp eight hours off, with nothing in the payload to reveal it.
/// The write path made it worse rather than obvious: a freshly created record was returned straight
/// from the tracked entity, where <c>DateTime.UtcNow</c> still carried <c>Kind = Utc</c>, so the
/// same record serialized <em>with</em> a <c>Z</c> on create and <em>without</em> one on read-back.
/// </para>
///
/// <para>
/// <b>Why a converter and not <c>datetimeoffset</c>.</b> Both fix the wire format.
/// <c>datetimeoffset</c> also makes the invariant self-describing in the database — but it costs an
/// <c>ALTER COLUMN</c> across every timestamp in nineteen tables, drift from §4's literal
/// <c>datetime2</c> column list (which the migration was deliberately authored to stay verifiable
/// against), and either a CLR-type change to <c>DateTimeOffset</c> through Domain, Application and
/// the published DTOs, or a converter anyway. The converter is additive, provider-neutral, applies
/// to entities that do not exist yet, and generates <b>no migration at all</b> — the column type is
/// unchanged, so there is no schema change to preserve data across.
/// </para>
///
/// <para>
/// <b>What it costs.</b> The database no longer knows the values are UTC — only this convention
/// does. A row inserted by a tool that bypasses EF (SSMS, a bulk load, a raw <c>INSERT</c>) in local
/// time will be read back labelled UTC and be wrong by the offset, and nothing will flag it. That is
/// the trade accepted here: correctness enforced by convention rather than by column type. If a
/// second writer ever touches these tables, revisit and move to <c>datetimeoffset</c>.
/// </para>
///
/// <para>
/// The write side converts <see cref="DateTimeKind.Local"/> to UTC and passes <c>Utc</c> and
/// <c>Unspecified</c> through untouched — see <see cref="EAMS.Domain.UtcTime"/> for why
/// <c>Unspecified</c> must not be shifted. The expressions are kept inline rather than delegating to
/// that helper so EF composes a trivial expression tree per property.
/// </para>
/// </summary>
internal sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter() : base(
        toProvider => toProvider.Kind == DateTimeKind.Local ? toProvider.ToUniversalTime() : toProvider,
        fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc))
    {
    }
}

/// <summary>
/// The nullable counterpart of <see cref="UtcDateTimeConverter"/>. Declared explicitly rather than
/// relying on EF to lift the non-nullable one, so the null path is visible and cannot regress.
/// </summary>
internal sealed class NullableUtcDateTimeConverter : ValueConverter<DateTime?, DateTime?>
{
    public NullableUtcDateTimeConverter() : base(
        toProvider => toProvider.HasValue && toProvider.Value.Kind == DateTimeKind.Local
            ? toProvider.Value.ToUniversalTime()
            : toProvider,
        fromProvider => fromProvider.HasValue
            ? DateTime.SpecifyKind(fromProvider.Value, DateTimeKind.Utc)
            : fromProvider)
    {
    }
}
