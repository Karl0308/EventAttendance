/*
    ADR-015 D-98, the pre-go-live audit: card UIDs held by both a student and a personnel member.

    READ-ONLY. It writes nothing, takes no lock beyond a read, and runs on SQL Server 2012.

    Run it against the EAMS database, per school, and hand the list to QA:

        sqlcmd -S <server> -d EAMS -i scripts\audit-card-uid-collisions.sql

    Every school is listed when @SchoolId is NULL. To run it for one school, set @SchoolId below.

    Each row is one collision, and is live: an active card and a not-deleted personnel record, the same
    predicates the two unique indexes use. A soft-deleted student's active card still holds its slot, so
    it is listed, with StudentDeleted = 1.

    No rows means clean. Until it is clean, a collided UID resolves to the STUDENT, because the tap reads
    student cards first, and the personnel member's taps are recorded against that student. The one
    exception is StudentDeleted = 1: the tap skips a deleted student, so that UID reaches the personnel
    member, but the card still blocks the UID for everyone else.
*/
SET NOCOUNT ON;

DECLARE @SchoolId uniqueidentifier = NULL;

SELECT
    c.SchoolId,
    c.CardUid,
    c.Id              AS RfidCardId,
    s.Id              AS StudentId,
    s.StudentNumber,
    s.IsDeleted       AS StudentDeleted,
    p.Id              AS PersonnelId,
    p.PersonnelNumber,
    p.Status          AS PersonnelStatus
FROM dbo.RfidCards AS c
JOIN dbo.Students  AS s ON s.Id = c.StudentId
JOIN dbo.Personnel AS p ON p.SchoolId = c.SchoolId AND p.RfidUid = c.CardUid
WHERE c.IsActive = 1
  AND p.IsDeleted = 0
  AND p.RfidUid IS NOT NULL
  AND (@SchoolId IS NULL OR c.SchoolId = @SchoolId)
ORDER BY c.SchoolId, c.CardUid;
