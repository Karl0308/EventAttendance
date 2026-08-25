namespace EAMS.Application.Dtos;

/// <summary>
/// The body of <c>POST /auth/login</c>.
/// </summary>
/// <param name="Email">
/// The login identifier. Trimmed and lower-cased server-side before the lookup, because
/// <c>UX_Users_Email</c> stores the normalized form — a user who types <c>Admin@…</c> on a phone
/// keyboard must not be told their password is wrong.
/// </param>
/// <param name="Password">
/// The presented password. <b>Never logged, never echoed, never audited</b> — the failure row records
/// the address that was tried and nothing else.
/// </param>
public record LoginRequest(string Email, string Password);

/// <summary>
/// The body of <c>POST /auth/change-password</c>.
/// </summary>
/// <param name="CurrentPassword">
/// Re-verified even though the caller already holds a valid access token. <b>The token is not the
/// proof this operation needs.</b> A token is what an attacker who walked past an unlocked laptop
/// has; the current password is what only the account holder has, and this is the operation that
/// would lock the real owner out.
/// </param>
/// <param name="NewPassword">
/// Held to the same rules <c>IUserProvisioningService</c> applies at creation, so an account cannot
/// be walked below the bar it was created at.
/// </param>
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// The 200 body of <c>POST /auth/login</c> and <c>POST /auth/refresh</c>.
///
/// <para>
/// <b>There is deliberately no <c>refreshToken</c> field here, and there was one in the §6.1 sketch.</b>
/// The refresh token now travels as an <c>httpOnly</c> cookie, so no script in the SPA can read it and
/// no XSS can exfiltrate it; the access token stays in the body precisely so the SPA holds it in
/// memory and sends it as <c>Authorization: Bearer</c>. That split is what confines CSRF exposure to
/// the two routes the cookie is scoped to — every other endpoint is unreachable by a cross-site page
/// because a cross-site page cannot set an <c>Authorization</c> header. Recorded as drift from §6.1.
/// </para>
/// </summary>
/// <param name="AccessToken">
/// The signed JWT. Fifteen-minute lifetime (<c>JwtOptions.AccessTokenLifetime</c>) — its lifetime
/// <em>is</em> its revocation window, which is why the long-lived half lives in a table instead.
/// </param>
/// <param name="TokenType">
/// Always <c>Bearer</c>. Present so a generated client composes the header from the response rather
/// than from a hard-coded string it will get wrong once.
/// </param>
/// <param name="ExpiresAt">
/// When <paramref name="AccessToken"/> stops being accepted, UTC. The SPA refreshes ahead of this
/// rather than waiting for a 401 — a 401 mid-navigation is a lost page, not a renewal.
/// </param>
/// <param name="User">Who the token speaks for, and what it may do.</param>
public record AuthTokenResponse(
    string AccessToken,
    string TokenType,
    DateTime ExpiresAt,
    AuthUserDto User);

/// <summary>
/// The authenticated user, as the SPA gates its navigation on. Also the whole body of
/// <c>GET /auth/me</c>.
/// </summary>
/// <param name="Permissions">
/// <b>The permissions carried by the presented token, not a fresh read of the database.</b>
///
/// <para>
/// The difference matters on exactly the day it looks wrong. Permissions are baked into the access
/// token at issue, so a grant revoked one minute ago is still honoured by a token minted two minutes
/// ago until it expires. If this field answered from the database instead, the UI would hide a button
/// that the server would still accept, or — far worse — show one that the server would refuse, and
/// the user would meet a 403 the interface promised them they would not. Answering from the token
/// makes the UI's model of what it may do exactly the server's model, for exactly as long as the
/// token lives. A change takes effect at the next refresh, which is at most fifteen minutes away.
/// </para>
/// </param>
public record AuthUserDto(
    Guid Id,
    Guid SchoolId,
    string Email,
    string FullName,
    IReadOnlyList<string> Permissions);
