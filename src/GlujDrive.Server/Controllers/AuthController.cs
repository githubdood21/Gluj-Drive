using System.Security.Claims;
using GlujDrive.Server.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GlujDrive.Server.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    RootAccountStore accounts,
    ServerSettingsStore settings) : ControllerBase
{
    [HttpGet("status")]
    public ActionResult<AuthStatusResponse> Status() => Ok(new AuthStatusResponse(
        HostConnection.IsLocal(HttpContext),
        !accounts.IsConfigured,
        User.Identity?.IsAuthenticated == true,
        Request.IsHttps,
        User.Identity?.IsAuthenticated == true || HostConnection.IsLocal(HttpContext)
            ? accounts.Username
            : null));

    [HttpPost("setup")]
    [HostOnly]
    public async Task<ActionResult<AuthStatusResponse>> SetupAsync(
        CredentialsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await accounts.CreateAsync(request.Username, request.Password, cancellationToken))
            {
                return Conflict(new ProblemDetails { Title = "The root account is already configured." });
            }
            await SignInAsync(accounts.Username!, accounts.GetSecurityStamp());
            return Ok(new AuthStatusResponse(true, false, true, Request.IsHttps, accounts.Username));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails { Title = "The account details are invalid.", Detail = exception.Message });
        }
    }

    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<AuthStatusResponse>> LoginAsync(
        CredentialsRequest request,
        CancellationToken cancellationToken)
    {
        if (!accounts.IsConfigured)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Owner setup is required.",
                Detail = "Open Gluj Drive on the host PC and create the root account first."
            });
        }
        if (!await accounts.VerifyAsync(request.Username, request.Password, cancellationToken))
        {
            return Unauthorized(new ProblemDetails { Title = "The account name or password is incorrect." });
        }

        await SignInAsync(accounts.Username!, accounts.GetSecurityStamp());
        return Ok(new AuthStatusResponse(
            HostConnection.IsLocal(HttpContext),
            false,
            true,
            Request.IsHttps,
            accounts.Username));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> LogoutAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [HttpPut("account")]
    [HostOnly]
    public async Task<ActionResult<AuthStatusResponse>> UpdateAccountAsync(
        CredentialsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await accounts.UpdateAsync(request.Username, request.Password, cancellationToken);
            await SignInAsync(accounts.Username!, accounts.GetSecurityStamp());
            return Ok(new AuthStatusResponse(true, false, true, Request.IsHttps, accounts.Username));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new ProblemDetails { Title = "The account could not be updated.", Detail = exception.Message });
        }
    }

    private Task SignInAsync(string username, string securityStamp)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, username),
            new Claim("security_stamp", securityStamp)
        ],
        CookieAuthenticationDefaults.AuthenticationScheme);
        return HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                AllowRefresh = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(settings.Current.SessionLifetimeDays)
            });
    }
}

public sealed record CredentialsRequest(string Username, string Password);
public sealed record AuthStatusResponse(
    bool IsHostConnection,
    bool SetupRequired,
    bool IsAuthenticated,
    bool IsSecureConnection,
    string? Username);
