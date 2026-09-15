namespace EAMS.Application.Dtos;

/// <summary>
/// One entry in the institution's classification vocabulary.
/// </summary>
/// <param name="Id">
/// The stable identity. <b>This is what a student row holds</b>, so it survives every rename — which is
/// the whole reason the vocabulary is a table rather than a string column.
/// </param>
/// <param name="Name">
/// The display form, exactly as authored — <c>SUPERVISORY/MANAGERIAL</c>, slash and all.
/// </param>
/// <param name="NameKey">
/// The normalized key the uniqueness index is built on (letters and digits, upper-cased). Published
/// for the reason <c>CourseOfferingDto.sectionKey</c> is: it is what a duplicate is decided on, so an
/// administrator told "that name is taken" by a row that does not look taken can see why, and Phase
/// 1b's importer matches the roster's own category strings against this value rather than the display
/// name.
/// </param>
/// <param name="IsActive">
/// <c>false</c> is retired: it is not offered for new assignments and <b>every student already
/// carrying it still carries it</b>. Pickers filter on this; reports must not.
/// </param>
/// <param name="RetiredAt">When it was last retired, or null while active.</param>
/// <param name="MergedIntoClassificationId">
/// The classification that absorbed this one, if any. Always accompanied by
/// <paramref name="IsActive"/> <c>false</c> — a merged row is a tombstone explaining a retirement, not
/// a redirect anything resolves through.
///
/// </param>
/// <param name="Axis">
/// Which of the source's four category columns this value belongs to — <c>Student</c>,
/// <c>Personnel</c>, <c>Friars</c> or <c>Special</c>. Set at creation and never afterwards.
///
/// <para>
/// <b>A picker needs it, not merely an admin screen.</b> A person holds at most one classification per
/// axis and may hold several axes, so "which classifications may I give this person" is a question
/// about axes — a flat list with no axis on it cannot be grouped, and a client would have to guess
/// which selections are mutually exclusive.
/// </para>
/// </param>
/// <param name="StudentCount">
/// How many people currently hold this classification. <b>It is what makes a refusal actionable</b>:
/// "this cannot be deleted" is an instruction to argue with, and "271 people are filed under this —
/// retire it or merge it into another" is one to act on. Counted against the same tenant filter as the
/// row itself.
/// </param>
public record ClassificationDto(
    Guid Id,
    string Name,
    string NameKey,
    string Axis,
    bool IsActive,
    DateTime? RetiredAt,
    Guid? MergedIntoClassificationId,
    int StudentCount);

/// <summary>
/// The body of <c>POST /classifications</c>.
/// </summary>
/// <param name="Name">
/// The display name. Required, 100 characters or fewer, no leading or trailing whitespace (refused
/// rather than trimmed), and it must contain at least one letter or digit so it has a normalized key.
/// </param>
/// <param name="Axis">
/// <c>Student</c>, <c>Personnel</c>, <c>Friars</c> or <c>Special</c>. Required, matched
/// case-insensitively and stored canonically.
///
/// <para>
/// <b>There is no default, and that is the decision.</b> <c>Student</c> would be the obvious one —
/// 20,861 of the 21,497 sampled rows carry it — and it would be wrong silently: a personnel category
/// created without an axis would land on the student axis, where it competes for the one slot a person
/// has there, and nothing would say so until somebody noticed a member of staff had stopped being a
/// student. Being asked is cheap; the wrong axis is not editable afterwards.
/// </para>
/// </param>
public record ClassificationCreateRequest(string Name, string? Axis);

/// <summary>
/// The body of <c>PUT /classifications/{id}</c>.
/// </summary>
/// <remarks>
/// <b>It carries no axis, and the omission is load-bearing rather than tidy.</b> A classification's
/// axis is immutable: <c>UX_StudentClassifications_Student_Axis</c> caps a person at one classification
/// per axis and every assignment row denormalizes the axis, so moving a classification between axes
/// would have to rewrite all of those in the same breath and could collide with something the person
/// already holds on the destination axis — a rename that half-succeeds, per person. A shared
/// create/rename request type would have made that expressible, and the PUT handler would have been
/// one forgotten line away from allowing it.
/// </remarks>
/// <param name="Name">The new display name. Same rules as on creation.</param>
public record ClassificationRenameRequest(string Name);

/// <summary>
/// The body of <c>PATCH /classifications/{id}/active</c>.
/// </summary>
/// <param name="IsActive">
/// <b>Nullable, and an omitted member is a 400 rather than a default.</b> A missing JSON member binds
/// to <c>false</c>, and on this route <c>false</c> retires a category — the same reasoning
/// <c>PATCH /academic/terms/{id}/current</c> records, and the same hazard.
/// </param>
public record ClassificationActiveRequest(bool? IsActive);

/// <summary>
/// The body of <c>POST /classifications/{id}/merge</c> — <c>{id}</c> is the classification being
/// collapsed, this names the one that absorbs it.
/// </summary>
/// <param name="IntoClassificationId">
/// The survivor. Nullable for the reason <see cref="ClassificationActiveRequest.IsActive"/> is: an
/// omitted member would bind to <c>Guid.Empty</c>, which is indistinguishable from a caller who meant
/// it, and the refusal for "you did not say what to merge into" should not read as "no such
/// classification".
/// </param>
public record ClassificationMergeRequest(Guid? IntoClassificationId);
