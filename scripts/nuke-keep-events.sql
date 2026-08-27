/* =====================================================================================
   EAMS - delete everything except two named events, the people in them, and their taps.

   WHAT SURVIVES
       The two events listed in @KeepEvents, their schedules, their INDIVIDUAL audience
       rows, their attendance records, the students those rows name, and the RFID cards
       those students hold (plus any card a kept tap was made with, even if it has since
       been reassigned).
       Untouched entirely: Schools, Users, Roles, Permissions, UserRoles, RolePermissions,
       RefreshTokens, Devices, SystemSettings, __EFMigrationsHistory.

   WHAT GOES
       Every other event. Every other student. The whole academic layer - enrolments,
       term records, course offerings, courses, programs, colleges, instructors, terms.
       All student groups and their memberships. All SIS import batches, rows and
       profiles. The audit log.

   THE ONE CONSEQUENCE TO READ TWICE
       A kept event's GROUP audience rows are deleted, because every StudentGroup is.
       For a Closed or Cancelled event that costs nothing: the close already wrote the
       whole audience down as individual student rows (ADR-003 D-13), and those are kept,
       so the denominator and the absentee list survive intact.
       For a Draft or Open event whose audience was attached as sections, the audience is
       GONE and `expected` becomes 0. The event itself, and any taps already recorded on
       it, remain. Re-attach an audience after the next roster import.
       The report at the end tells you which case each kept event is in.

   SAFE BY DEFAULT
       @DryRun is 1. The deletes run, the counts are real, and then everything ROLLS BACK.
       Read the report, then set @DryRun = 0 and run it again to commit.

   HOW IT DELETES
       Foreign keys are disabled across the database, the deletes run in any order, and
       the constraints are re-enabled WITH CHECK inside the same transaction. That last
       step is the correctness proof: if the keep-set left a row pointing at something
       deleted, SQL Server names the constraint and the whole thing rolls back rather
       than leaving the database quietly inconsistent.

   Written for SQL Server 2012 (the deployment VM's version) - nothing newer is used.
   Run it against the EAMS database, not master.
   ===================================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

-- ------------------------------------------------------------------ what to keep, and whether to commit

DECLARE @DryRun BIT = 1;        -- 1 = roll back and just report.  0 = COMMIT. THIS IS THE SWITCH.

DECLARE @KeepEvents TABLE (EventId UNIQUEIDENTIFIER PRIMARY KEY);

INSERT INTO @KeepEvents (EventId) VALUES
    ('9D2DA7BE-1D90-4523-B5BC-DFB1959622C6'),   -- AYF 2026
    ('8B8C6A52-EF19-483E-8975-3C3374920B64');   -- PSD Mass 2026 - retain users

-- ------------------------------------------------------------------ refuse to run on a typo
--
-- A GUID that matches no event is the difference between "keep two events" and "keep none",
-- and nothing later in this script would notice: every DELETE would simply match more rows.
-- One wrong character is a total wipe that reports success, so the ids are checked first.

IF EXISTS (SELECT 1 FROM @KeepEvents k
           WHERE NOT EXISTS (SELECT 1 FROM dbo.Events e WHERE e.Id = k.EventId))
BEGIN
    PRINT 'STOPPED. These ids in @KeepEvents match no row in dbo.Events:';
    SELECT k.EventId AS [Unmatched EventId] FROM @KeepEvents k
    WHERE NOT EXISTS (SELECT 1 FROM dbo.Events e WHERE e.Id = k.EventId);
    PRINT 'Nothing was changed. Fix the ids and run again.';
    RETURN;
END;

IF EXISTS (SELECT 1 FROM dbo.Events e
           JOIN @KeepEvents k ON k.EventId = e.Id
           WHERE e.IsDeleted = 1)
BEGIN
    PRINT 'NOTE: at least one kept event is soft-deleted (IsDeleted = 1). It is kept anyway.';
END;

-- ------------------------------------------------------------------ resolve the keep-set
--
-- Students first, from both places a kept event can name one: its individual audience rows
-- and its attendance rows. A frozen (closed) event carries its whole roster in the first;
-- a walk-in appears only in the second. Both are people this event is a record of.

DECLARE @KeepStudents TABLE (StudentId UNIQUEIDENTIFIER PRIMARY KEY);

INSERT INTO @KeepStudents (StudentId)
SELECT s.Id
FROM dbo.Students s
WHERE EXISTS (SELECT 1 FROM dbo.EventGroups g
              JOIN @KeepEvents k ON k.EventId = g.EventId
              WHERE g.StudentId = s.Id)
   OR EXISTS (SELECT 1 FROM dbo.AttendanceRecords a
              JOIN @KeepEvents k ON k.EventId = a.EventId
              WHERE a.StudentId = s.Id);

-- Cards second, and the second half of this predicate is not redundant.
-- AttendanceRecords.RfidCardId is a foreign key to the exact card the tap was made with.
-- A reissue leaves the old row in place and moves nothing, so a card that recorded a tap
-- last term can belong to a student who is not in the keep-set today. Deleting it would
-- break a kept attendance row - which the re-enable at the end would catch, but as a
-- rollback rather than as the right answer.

DECLARE @KeepCards TABLE (CardId UNIQUEIDENTIFIER PRIMARY KEY);

INSERT INTO @KeepCards (CardId)
SELECT c.Id
FROM dbo.RfidCards c
WHERE c.StudentId IN (SELECT StudentId FROM @KeepStudents)
   OR EXISTS (SELECT 1 FROM dbo.AttendanceRecords a
              JOIN @KeepEvents k ON k.EventId = a.EventId
              WHERE a.RfidCardId = c.Id);

-- Counted into variables first: PRINT takes a scalar expression and a subquery inside one
-- is a parse error, not a runtime one - the whole batch refuses to compile.
DECLARE @nEvents INT, @nStudents INT, @nCards INT;
SELECT @nEvents   = COUNT(*) FROM @KeepEvents;
SELECT @nStudents = COUNT(*) FROM @KeepStudents;
SELECT @nCards    = COUNT(*) FROM @KeepCards;

PRINT '--------------------------------------------------------------------';
PRINT 'Keep-set resolved:';
PRINT '  events   : ' + CAST(@nEvents   AS VARCHAR(20));
PRINT '  students : ' + CAST(@nStudents AS VARCHAR(20));
PRINT '  cards    : ' + CAST(@nCards    AS VARCHAR(20));
PRINT '--------------------------------------------------------------------';

-- ------------------------------------------------------------------ before/after counting
--
-- The snapshot query is generated from sys.tables rather than written out, so it cannot
-- drift from the schema and the "before" and "after" passes are provably the same query.

IF OBJECT_ID('tempdb..#Snap') IS NOT NULL DROP TABLE #Snap;
CREATE TABLE #Snap (Phase VARCHAR(6), TableName SYSNAME, Rows BIGINT);

DECLARE @CountSql NVARCHAR(MAX) = N'';

SELECT @CountSql = @CountSql
    + N'SELECT @phase, ''' + t.name + N''', COUNT_BIG(*) FROM '
    + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N' UNION ALL '
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE t.is_ms_shipped = 0;

SET @CountSql = N'INSERT INTO #Snap (Phase, TableName, Rows) '
              + LEFT(@CountSql, LEN(@CountSql) - LEN(N'UNION ALL '));

EXEC sp_executesql @CountSql, N'@phase VARCHAR(6)', @phase = 'before';

-- ------------------------------------------------------------------ do it

BEGIN TRANSACTION;

EXEC sp_MSforeachtable @command1 = 'ALTER TABLE ? NOCHECK CONSTRAINT ALL';

-- Events and everything hanging off them.
DELETE FROM dbo.AttendanceRecords WHERE EventId NOT IN (SELECT EventId FROM @KeepEvents);
DELETE FROM dbo.EventGroups       WHERE EventId NOT IN (SELECT EventId FROM @KeepEvents)
                                     OR StudentGroupId IS NOT NULL;
DELETE FROM dbo.EventSchedules    WHERE EventId NOT IN (SELECT EventId FROM @KeepEvents);
DELETE FROM dbo.Events            WHERE Id      NOT IN (SELECT EventId FROM @KeepEvents);

-- Groups. All of them, derived and manual alike - the projection rebuilds the derived ones
-- on the next roster import, and a manual group whose members are gone is not a group.
DELETE FROM dbo.StudentGroupMembers;
DELETE FROM dbo.StudentGroups;

-- The academic layer.
DELETE FROM dbo.Enrollments;
DELETE FROM dbo.StudentTermRecords;
DELETE FROM dbo.CourseOfferingInstructors;
DELETE FROM dbo.CourseOfferings;
DELETE FROM dbo.Courses;
DELETE FROM dbo.Instructors;

-- Import history and the audit log.
DELETE FROM dbo.SisImportRowEntities;
DELETE FROM dbo.SisImportRows;
DELETE FROM dbo.SisImportBatches;
DELETE FROM dbo.SisImportProfileColumns;
DELETE FROM dbo.SisImportProfiles;
DELETE FROM dbo.AuditLogs;

-- People. Cards before students only for readability; constraints are off either way.
DELETE FROM dbo.RfidCards WHERE Id NOT IN (SELECT CardId    FROM @KeepCards);
DELETE FROM dbo.Students  WHERE Id NOT IN (SELECT StudentId FROM @KeepStudents);

-- The reference rows the academic layer hung from. Terms last: offerings, groups, term
-- records and import batches all point at them.
DELETE FROM dbo.Programs;
DELETE FROM dbo.Colleges;
DELETE FROM dbo.Terms;

-- The proof. WITH CHECK re-validates every row that is left; a kept row pointing at a
-- deleted one fails here, names its constraint, and XACT_ABORT rolls the whole thing back.
EXEC sp_MSforeachtable @command1 = 'ALTER TABLE ? WITH CHECK CHECK CONSTRAINT ALL';

-- ------------------------------------------------------------------ report

EXEC sp_executesql @CountSql, N'@phase VARCHAR(6)', @phase = 'after';

SELECT  b.TableName                          AS [Table],
        b.Rows                               AS [Before],
        a.Rows                               AS [After],
        b.Rows - a.Rows                      AS [Deleted],
        CASE WHEN b.Rows = a.Rows THEN 'untouched'
             WHEN a.Rows = 0      THEN 'emptied'
             ELSE 'partial' END              AS [Result]
FROM #Snap b
JOIN #Snap a ON a.TableName = b.TableName AND a.Phase = 'after'
WHERE b.Phase = 'before'
ORDER BY (b.Rows - a.Rows) DESC, b.TableName;

SELECT  e.Id                                 AS [Kept event],
        e.Name,
        e.Status,
        (SELECT COUNT(*) FROM dbo.EventGroups g
         WHERE g.EventId = e.Id AND g.StudentId IS NOT NULL)      AS [Audience students],
        (SELECT COUNT(*) FROM dbo.AttendanceRecords a
         WHERE a.EventId = e.Id)                                  AS [Attendance rows],
        CASE WHEN e.Status IN ('Closed', 'Cancelled')
                  THEN 'frozen - audience and absentee list intact'
             WHEN EXISTS (SELECT 1 FROM dbo.EventGroups g
                          WHERE g.EventId = e.Id AND g.StudentId IS NOT NULL)
                  THEN 'live - individual audience survived'
             ELSE 'live - AUDIENCE IS NOW EMPTY, expected = 0, re-attach after the next import'
        END                                                       AS [State]
FROM dbo.Events e
JOIN @KeepEvents k ON k.EventId = e.Id;

-- ------------------------------------------------------------------ commit, or not

IF @DryRun = 1
BEGIN
    ROLLBACK TRANSACTION;
    PRINT '';
    PRINT '====================================================================';
    PRINT 'DRY RUN - everything above was rolled back. Nothing changed.';
    PRINT 'Set @DryRun = 0 at the top and run again to commit.';
    PRINT '====================================================================';
END
ELSE
BEGIN
    COMMIT TRANSACTION;
    PRINT '';
    PRINT '====================================================================';
    PRINT 'COMMITTED. The deletes above are permanent.';
    PRINT 'Restart the API (or touch web.config) so it reloads.';
    PRINT 'A term must exist before the next roster import - create one on the';
    PRINT '/terms page. Do not INSERT INTO dbo.Terms by hand.';
    PRINT '====================================================================';
END;

DROP TABLE #Snap;
