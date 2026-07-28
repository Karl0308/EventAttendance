using EAMS.Application.Abstractions;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// A settable <see cref="ICurrentUser"/>, mirroring <see cref="TestSchoolContext"/>.
///
/// <para>
/// Defaults to <c>null</c> — the same answer the production <c>UnauthenticatedCurrentUser</c> gives
/// for the whole of the pre-auth build — so every test that does not mention attribution exercises
/// exactly what ships today. A test that wants to prove the seam is actually wired sets it, which is
/// the only way to distinguish "the field is populated from ICurrentUser" from "the field happens to
/// be null because nothing writes it".
/// </para>
/// </summary>
public sealed class TestCurrentUser : ICurrentUser
{
    public Guid? UserId { get; set; }
}
