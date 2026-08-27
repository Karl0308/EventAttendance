using EAMS.Application.Dtos;
using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// <c>SisImportBatchDto.IsTerminal</c> — the predicate a progress poller stops on.
///
/// <para>
/// <b>It has a twin on the other side of the wire</b>, <c>isTerminalStatus</c> in
/// <c>web-admin/src/sisImport.ts</c>, written the same way and for the same reason. They are one
/// predicate expressed twice, and if they disagree the SPA either polls a batch that is finished or
/// renders a finished-import screen over one that has not run. So the expectations below are
/// <em>hand-written</em>, one line per status, rather than computed from <c>SisImportStatus.All</c> by
/// the same rule the property uses — a test that re-derives the rule under test agrees with any rule,
/// including a wrong one.
/// </para>
///
/// <para>
/// <b>The unknown-status case is the point of the whole file.</b> Both sides classify a status this
/// build has never heard of as <em>terminal</em>. That is deliberate and it is the safe direction: a
/// positive list of the four finished states would file an unrecognised status as unfinished and leave
/// a poller waiting on a run that will never come back.
/// </para>
/// </summary>
public class SisImportBatchDtoTests
{
    /// <summary>
    /// The six documented statuses and their terminality, worked out by hand: a batch is live while it
    /// is waiting to run or running, and over in every other case.
    /// </summary>
    [Theory]
    [InlineData("Pending", false)]
    [InlineData("Running", false)]
    [InlineData("Completed", true)]
    [InlineData("CompletedWithWarnings", true)]
    [InlineData("CompletedWithErrors", true)]
    [InlineData("Failed", true)]
    public void Every_documented_status_is_classified_the_way_the_SPA_classifies_it(
        string status, bool expected)
    {
        Assert.Equal(expected, Batch(status).IsTerminal);

        // The SPA's rule, transcribed literally from web-admin/src/sisImport.ts rather than referred
        // to: `status !== Pending && status !== Running`. This is the agreement, stated as an
        // assertion instead of as a comment somebody has to trust.
        var spa = status != "Pending" && status != "Running";
        Assert.Equal(spa, Batch(status).IsTerminal);
    }

    /// <summary>
    /// A status neither side has heard of — a newer server, a corrupt row, a value written by hand.
    /// Terminal, on both sides, so a poller can stop and the operator sees the word itself rather than
    /// a spinner forever.
    /// </summary>
    [Theory]
    [InlineData("Quarantined")]
    [InlineData("Cancelled")]
    [InlineData("")]
    [InlineData("pending")]      // wrong casing is not the Pending value; the columns store canonical
    [InlineData("Running ")]     // nor is a trailing space
    public void An_unknown_status_is_classified_terminal_rather_than_left_polling(string status)
    {
        Assert.True(Batch(status).IsTerminal);

        var spa = status != "Pending" && status != "Running";
        Assert.True(spa);
    }

    /// <summary>
    /// The coverage guard: the theory above names every value in <see cref="SisImportStatus.All"/>, so
    /// a seventh status added to the domain without a decision recorded here fails rather than
    /// silently inheriting "terminal" from the unknown-value branch.
    /// </summary>
    [Fact]
    public void The_hand_written_table_covers_every_status_the_domain_defines()
    {
        string[] tabled =
        [
            "Pending", "Running", "Completed",
            "CompletedWithWarnings", "CompletedWithErrors", "Failed",
        ];

        Assert.Equal(tabled.Order(), SisImportStatus.All.Order());
    }

    /// <summary>
    /// <c>CountersReconcile</c> is arithmetic and stays arithmetic: on a running batch it is
    /// <em>false</em>, because <c>TotalRows</c> is the staged count from upload while the four outcome
    /// counters are only written by the tally at the end of the run.
    ///
    /// <para>
    /// This is pinned rather than fixed. Making it return <c>true</c> mid-run would produce a
    /// reconciliation check that cannot fail during the one window in which the pipeline is actually
    /// writing the numbers being reconciled — a check true by construction proves nothing. The caller's
    /// job is to ask it only of a terminal batch, which is what <see cref="SisImportBatchDto.IsTerminal"/>
    /// is for, and this test states both halves so a later "fix" has to argue with it.
    /// </para>
    /// </summary>
    [Fact]
    public void A_running_batch_does_not_reconcile_and_that_is_expected()
    {
        var running = Batch("Running") with { TotalRows = 536 };

        Assert.False(running.IsTerminal);
        Assert.False(running.CountersReconcile);

        // The same counters on a finished batch: 470 + 0 + 0 + 66 = 536, by hand.
        var finished = running with
        {
            Status = "CompletedWithWarnings",
            InsertedRows = 470,
            SkippedRows = 66,
        };

        Assert.True(finished.IsTerminal);
        Assert.True(finished.CountersReconcile);
    }

    /// <summary>A batch that has reported nothing: seven nulls, which is what the column defaults are absent for.</summary>
    private static SisImportBatchDto Batch(string status) => new(
        Id: Guid.NewGuid(),
        TermId: Guid.NewGuid(),
        TermCode: "2025-2026-1",
        Source: SisImportSource.Excel,
        FileName: "CCJ-roster.xlsx",
        SourceSheetName: "Faculty Evaluation Report",
        FileHash: null,
        Status: status,
        TotalRows: 0,
        InsertedRows: 0,
        UpdatedRows: 0,
        FailedRows: 0,
        SkippedRows: 0,
        WarningRows: 0,
        StartedAt: null,
        FinishedAt: null,
        ProgressPhase: null,
        ProgressPhaseNumber: null,
        ProgressPhaseCount: null,
        ProgressUnitsDone: null,
        ProgressUnitsTotal: null,
        ProgressUpdatedAt: null,
        FailureReason: null);
}

/// <summary>
/// <see cref="SisImportPhase"/>'s ordering, which is a contract rather than a presentation detail: the
/// phase a run reports is numbered by its position in <see cref="SisImportPhase.Working"/>, and those
/// numbers are written to the batch row. Re-ordering the list would re-interpret every number already
/// stored, so the order is pinned here, by hand, against the steps of
/// <c>SisImportService.ExecuteAsync</c>.
/// </summary>
public class SisImportPhaseTests
{
    [Fact]
    public void The_working_phases_are_listed_in_the_order_a_run_executes_them()
    {
        string[] inRunOrder =
        [
            "ClearingPreviousRun",
            "ParsingRows",
            "ResolvingDimensions",
            "ResolvingFacts",
            "WritingFacts",
            "RecordingRowResults",
            "RefreshingStudentCache",
            "SyncingStudentGroups",
        ];

        Assert.Equal(inRunOrder, SisImportPhase.Working);
    }

    /// <summary>
    /// <c>Done</c> is in <see cref="SisImportPhase.All"/> and out of <see cref="SisImportPhase.Working"/>,
    /// and the split is what keeps "phase 8 of 8" from reading as "one step left". Counting the
    /// terminal value as a step would make a finished run report 9 of 9 and its last real step report
    /// 8 of 9.
    /// </summary>
    [Fact]
    public void Done_is_a_phase_but_not_a_step()
    {
        Assert.Contains(SisImportPhase.Done, SisImportPhase.All);
        Assert.DoesNotContain(SisImportPhase.Done, SisImportPhase.Working);

        Assert.Equal(8, SisImportPhase.Working.Count);
        Assert.Equal(9, SisImportPhase.All.Count);
        Assert.Equal(SisImportPhase.Done, SisImportPhase.All[^1]);
    }

    [Theory]
    [InlineData("ParsingRows", "ParsingRows")]
    [InlineData("parsingrows", "ParsingRows")]
    [InlineData("  WritingFacts  ", "WritingFacts")]
    [InlineData("DONE", "Done")]
    public void A_documented_phase_in_any_casing_normalizes_to_its_canonical_spelling(
        string sent, string expected)
    {
        Assert.True(SisImportPhase.TryNormalize(sent, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData("Parsing")]
    [InlineData("Finished")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_outside_the_set_is_rejected(string? phase)
    {
        Assert.False(SisImportPhase.TryNormalize(phase, out var canonical));
        Assert.Equal("", canonical);
    }
}
