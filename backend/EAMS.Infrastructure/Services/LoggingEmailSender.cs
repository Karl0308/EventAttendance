using EAMS.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// The no-op <see cref="IEmailSender"/> this build ships with — it logs the message instead of delivering
/// it. No SMTP or provider is wired yet (see <see cref="IEmailSender"/>), so this stands in so the Live
/// Attendance email flow is exercisable end to end. It never throws, so every attempted send is counted as
/// sent; a real transport that can fail replaces it behind the same interface.
///
/// <para>
/// It logs the recipient and subject, <b>not the body</b> — a code in a log is a code leaked to anyone who
/// can read logs, and the body carries the attendee's code.
/// </para>
/// </summary>
internal sealed class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;

    public LoggingEmailSender(ILogger<LoggingEmailSender> logger) => _logger = logger;

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Email (no transport configured; not delivered) to {ToName} <{ToEmail}>: {Subject}",
            message.ToName, message.ToEmail, message.Subject);
        return Task.CompletedTask;
    }
}
