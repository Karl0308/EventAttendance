using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// ADR-015 D-99's fail-closed v1 mapper, on its own. The service applies the no-header rules first, so
/// through the API this mapper never sees a personnel answer, which is exactly why it needs a test that
/// does not go through the service: a backstop nothing exercises is a backstop nobody knows still works.
/// </summary>
public class CaptureV1MapperTests
{
    private static readonly DateTime ServerTime = new(2026, 10, 31, 1, 0, 0, DateTimeKind.Utc);

    private static AttendanceDtoV2 PersonnelRecord() => new(
        Guid.NewGuid(), Guid.NewGuid(), null, null, null,
        ServerTime, null, "Present", "Rfid",
        AttendeePersonType.Personnel, Guid.NewGuid(), "EMP-0001", "Jose P Rizal");

    private static AttendanceDtoV2 StudentRecord(Guid studentId) => new(
        Guid.NewGuid(), Guid.NewGuid(), studentId, "Maria Reyes Santos", "2023-0001",
        ServerTime, null, "Present", "Rfid",
        AttendeePersonType.Student, null, null, null);

    [Theory]
    [InlineData(TapOutcome.Recorded)]
    [InlineData(TapOutcome.DuplicateIgnored)]
    [InlineData(TapOutcome.CheckedOut)]
    [InlineData(TapOutcome.AlreadyRecorded)]
    [InlineData(TapOutcome.TooSoonIgnored)]
    public void A_personnel_record_becomes_CardNotFound_and_no_record_is_built(TapOutcome outcome)
    {
        var (mapped, result) = AttendanceController.ToV1(
            TapResponse.For(outcome, success: true, "Recorded.", PersonnelRecord(), ServerTime));

        Assert.Equal(TapOutcome.CardNotFound, mapped);
        Assert.Equal(nameof(TapOutcome.CardNotFound), result.Code);
        Assert.False(result.Success);
        Assert.Null(result.Record);
        Assert.Equal(ServerTime, result.ServerTime);
        Assert.DoesNotContain("personnel", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(TapOutcome.PersonnelInactive)]
    [InlineData(TapOutcome.PersonnelNotInAudience)]
    public void A_personnel_refusal_becomes_CardNotFound(TapOutcome outcome)
    {
        var (mapped, result) = AttendanceController.ToV1(
            TapResponse.For(outcome, success: false, "Refused.", record: null, ServerTime));

        Assert.Equal(TapOutcome.CardNotFound, mapped);
        Assert.Equal(nameof(TapOutcome.CardNotFound), result.Code);
        Assert.Null(result.Record);
    }

    /// <summary>A record claiming to be a student and carrying a personnel id is not a student record.</summary>
    [Fact]
    public void A_record_that_does_not_cleanly_say_student_fails_closed()
    {
        var ambiguous = StudentRecord(Guid.NewGuid()) with { PersonnelId = Guid.NewGuid() };

        var (mapped, result) = AttendanceController.ToV1(
            TapResponse.For(TapOutcome.Recorded, success: true, "Recorded.", ambiguous, ServerTime));

        Assert.Equal(TapOutcome.CardNotFound, mapped);
        Assert.Null(result.Record);
    }

    [Fact]
    public void A_student_record_passes_through_field_for_field()
    {
        var studentId = Guid.NewGuid();
        var record = StudentRecord(studentId);

        var (mapped, result) = AttendanceController.ToV1(
            TapResponse.For(TapOutcome.Recorded, success: true, "Checked in (Present).", record, ServerTime));

        Assert.Equal(TapOutcome.Recorded, mapped);
        Assert.Equal(nameof(TapOutcome.Recorded), result.Code);
        Assert.Equal("Checked in (Present).", result.Message);
        Assert.Equal(
            new AttendanceDto(
                record.Id, record.EventId, studentId, "Maria Reyes Santos", "2023-0001",
                ServerTime, null, "Present", "Rfid"),
            result.Record);
    }

    [Fact]
    public void A_refusal_without_a_record_keeps_its_own_code()
    {
        var (mapped, result) = AttendanceController.ToV1(
            TapResponse.For(TapOutcome.EventNotOpen, success: false, "Event is Closed, not Open.", null, ServerTime));

        Assert.Equal(TapOutcome.EventNotOpen, mapped);
        Assert.Equal("Event is Closed, not Open.", result.Message);
    }
}
