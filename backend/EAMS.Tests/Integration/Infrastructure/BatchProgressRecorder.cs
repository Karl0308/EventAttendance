using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// Records the batch row's progress columns immediately after every statement that stamps
/// <c>ProgressUpdatedAt</c> on one batch. That covers the claim (D-54.4) and each heartbeat (D-54.5).
///
/// <para>
/// <b>A command interceptor, not a save interceptor.</b> Progress is written through
/// <c>ExecuteUpdate</c>, which never reaches <see cref="CapturingSaveInterceptor"/> by design, and a read
/// after the run sees only the last stamp. The only way to see the sequence is to look after each
/// statement.
/// </para>
///
/// <para>
/// <b>The read runs on the intercepted command's own connection and transaction.</b> The claim
/// executes inside a transaction holding an exclusive lock on the row. A read from a second connection
/// would wait for that lock while this callback holds up the commit, and the two would block each
/// other until the command timeout.
/// </para>
/// </summary>
internal sealed class BatchProgressRecorder : DbCommandInterceptor
{
    private readonly Guid _batchId;
    private readonly List<Snapshot> _snapshots = [];

    public BatchProgressRecorder(Guid batchId) => _batchId = batchId;

    /// <summary>The row as each stamp left it, in execution order.</summary>
    public IReadOnlyList<Snapshot> Snapshots => _snapshots;

    public sealed record Snapshot(
        string Status, string? Phase, int? PhaseNumber, int? PhaseCount, int? UnitsDone, int? UnitsTotal,
        DateTime? UpdatedAt, string? FailureReason);

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        if (result > 0 && IsProgressStamp(command))
            _snapshots.Add(await ReadAsync(command, cancellationToken));

        return result;
    }

    private static bool IsProgressStamp(DbCommand command) =>
        command.CommandText.Contains("UPDATE", StringComparison.Ordinal)
        && command.CommandText.Contains("[SisImportBatches]", StringComparison.Ordinal)
        && command.CommandText.Contains("[ProgressUpdatedAt] =", StringComparison.Ordinal);

    private async Task<Snapshot> ReadAsync(DbCommand stamp, CancellationToken ct)
    {
        await using var read = stamp.Connection!.CreateCommand();
        read.Transaction = stamp.Transaction;
        read.CommandText =
            "SELECT Status, ProgressPhase, ProgressPhaseNumber, ProgressPhaseCount, ProgressUnitsDone, " +
            "ProgressUnitsTotal, ProgressUpdatedAt, FailureReason FROM dbo.SisImportBatches WHERE Id = @id";

        var id = read.CreateParameter();
        id.ParameterName = "@id";
        id.Value = _batchId;
        read.Parameters.Add(id);

        await using var reader = await read.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException($"Batch {_batchId} is not in SisImportBatches.");

        return new Snapshot(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }
}
