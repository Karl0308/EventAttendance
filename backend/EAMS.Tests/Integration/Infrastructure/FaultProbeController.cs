using Microsoft.AspNetCore.Mvc;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// An endpoint that throws, existing only inside <see cref="EamsApiFactory"/>'s host.
///
/// <para>
/// What has to be proven is what the <em>pipeline</em> does with an exception nothing caught — that
/// it becomes an RFC 7807 body rather than a stack trace or an empty 500. Proving it needs an
/// unhandled exception, and manufacturing one from a real endpoint would mean corrupting the
/// database to make a service throw: slower, fragile, and it would test the corruption as much as
/// the pipeline. A controller whose entire job is to throw is the honest subject, and it is
/// unreachable from the shipped host because it lives in the test assembly.
/// </para>
/// </summary>
[ApiController]
[Route("test-only/fault-probe")]
public sealed class FaultProbeController : ControllerBase
{
    /// <summary>
    /// The message must not appear in any response body. It is deliberately shaped like something
    /// that would be damaging to leak, so the assertion is about disclosure rather than formatting.
    /// </summary>
    public const string Message = "Sensitive internal detail: connection to server 10.0.0.4 failed.";

    [HttpGet]
    public IActionResult Throw() => throw new InvalidOperationException(Message);
}
