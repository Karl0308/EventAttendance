using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// One school, one event with an audience definition, one individually attached student with a card, and
/// one personnel member with a card: the world the ADR-015 B1c-2 tests read. A school of its own every
/// time, so two worlds side by side are two tenants holding the same card UIDs.
/// </summary>
internal sealed record PersonnelEventWorld(
    Guid SchoolId, Guid EventId, Guid StudentId, Guid PersonnelId, Guid DefinitionId)
{
    public const string StudentUid = "04A7B8C9";
    public const string PersonnelUid = "05D4E5F6";
    public const string PersonnelNumber = "EMP-0001";
    public const string PersonnelName = "Jose P Rizal";
    public const string PersonnelEmail = "EMP-0001@usa.edu.ph";

    /// <param name="db">Saved to before this returns.</param>
    /// <param name="eventStatus">The event's status.</param>
    /// <param name="personnelStatus">The holder's status.</param>
    /// <param name="inAudience">
    /// False attaches a definition that resolves other people, so the event has an audience and the holder
    /// is outside it.
    /// </param>
    public static async Task<PersonnelEventWorld> ArrangeAsync(
        EamsDbContext db, string eventStatus = EventStatus.Open,
        string personnelStatus = EAMS.Domain.PersonnelStatus.Active, bool inAudience = true)
    {
        var school = TestData.NewSchool($"USA-{Guid.NewGuid():N}"[..12]);
        db.Schools.Add(school);

        var student = TestData.NewStudent(school.Id);
        db.Students.Add(student);
        db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, StudentUid));

        var person = TestData.NewPersonnel(school.Id, PersonnelNumber, rfidUid: PersonnelUid, status: personnelStatus);
        db.Personnel.Add(person);

        var ev = TestData.NewLiveEvent(school.Id, eventStatus);
        db.Events.Add(ev);
        db.EventGroups.Add(new EventGroup { EventId = ev.Id, StudentId = student.Id });

        var classification = TestData.NewEventClassification(school.Id);
        db.EventClassifications.Add(classification);

        var definition = new AudienceDefinition
        {
            SchoolId = school.Id,
            EventClassificationId = classification.Id,
            Name = "The audience",
            NameKey = AudienceText.KeyFor("The audience"),
            AudienceType = inAudience ? AudienceType.UniversityWide : AudienceType.Department,
            CriteriaJson = inAudience
                ? """{"scope":"Employees"}"""
                : """{"departments":["NOBODY-HERE"],"scope":"Employees"}""",
        };
        db.AudienceDefinitions.Add(definition);
        db.EventAudienceDefinitions.Add(new EventAudienceDefinition
        {
            EventId = ev.Id,
            AudienceDefinitionId = definition.Id,
        });

        await db.SaveChangesAsync();

        return new PersonnelEventWorld(school.Id, ev.Id, student.Id, person.Id, definition.Id);
    }

    /// <summary>What <c>attendance.scan.unresolved</c> has recorded against this event.</summary>
    public Task<int> ScanRowCountAsync(EamsDbContext db) =>
        db.AuditLogs.AsNoTracking().CountAsync(
            a => a.EntityId == EventId && a.Action == EAMS.Application.Abstractions.ScanLog.UnresolvedAction);
}
