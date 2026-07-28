using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace EAMS.Api.Controllers;

// Permission codes are Technical Plan §6.2's, action for action. ADR-001 D-6's bargain was that
// endpoints get decorated as they are written so Phase 6 wires enforcement rather than re-deriving
// what each endpoint should have demanded — and the attribute enforces nothing, so this is a
// declaration of intent and not a change in behaviour. See HasPermissionNotEnforcedAttribute.
[ApiController]
[Route("api/v1/students")]
public class StudentsController : ControllerBase
{
    private readonly IStudentService _students;
    public StudentsController(IStudentService students) => _students = students;

    [HttpGet]
    [HasPermissionNotEnforced("students.read")]
    public async Task<ActionResult<IEnumerable<StudentDto>>> List(
        [FromQuery] string? search, [FromQuery] string? course, [FromQuery] string? status,
        CancellationToken ct)
        => Ok(await _students.ListAsync(search, course, status, ct));

    [HttpGet("{id:guid}")]
    [HasPermissionNotEnforced("students.read")]
    public async Task<ActionResult<StudentDto>> Get(Guid id, CancellationToken ct)
    {
        var s = await _students.GetAsync(id, ct);
        return s is null ? NotFound() : Ok(s);
    }

    // GET /students/by-card/{cardUid} — UID→student resolution for the mobile scan screen.
    // The service normalizes the UID; callers may pass any reader format.
    //
    // `attendance.capture`, not `students.read` — §6.2 assigns this one endpoint the capture
    // permission because its caller is the reader/mobile scan screen, which must resolve a UID
    // without being trusted to browse the roster. A device API key is scoped to `attendance.capture`
    // alone (§11), so reading it as a student permission would lock the kiosks out of the one lookup
    // they exist to perform.
    [HttpGet("by-card/{cardUid}")]
    [HasPermissionNotEnforced("attendance.capture")]
    public async Task<ActionResult<StudentDto>> ByCard(string cardUid, CancellationToken ct)
    {
        var s = await _students.GetByCardUidAsync(cardUid, ct);
        return s is null ? NotFound("No active card matches that UID.") : Ok(s);
    }
}
