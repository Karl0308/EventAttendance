namespace EAMS.Application.Abstractions;

/// <summary>One outbound email.</summary>
/// <param name="ToEmail">The recipient address — already validated as present by the caller.</param>
/// <param name="ToName">The recipient's display name, for the greeting.</param>
/// <param name="Subject">The subject line.</param>
/// <param name="Body">The plain-text body.</param>
public record EmailMessage(string ToEmail, string ToName, string Subject, string Body);

/// <summary>
/// <b>The seam through which the system sends email.</b> Introduced by the Live Attendance module
/// (LiveAttendance.docx §4, "Email Attendance Code"), which is the first feature that emails anyone.
///
/// <para>
/// <b>No SMTP is wired in this build.</b> The registered implementation is a logging no-op, so the email
/// flow — recipient selection, validation, the sent/skipped summary — is exercisable end to end without a
/// mail server, and a real transport (SMTP, a provider API) drops in behind this interface without
/// touching the callers. That deferral is deliberate, not an oversight, and is the reason the send is
/// modelled as a fire-and-forget <c>SendAsync</c> rather than something that returns a provider receipt.
/// </para>
/// </summary>
public interface IEmailSender
{
    /// <summary>Sends one message. Implementations that cannot deliver throw; the caller counts the send
    /// as attempted only when this completes without throwing.</summary>
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}
