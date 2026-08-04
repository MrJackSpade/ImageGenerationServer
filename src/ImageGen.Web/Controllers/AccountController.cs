//TODO: CHECK FOR FALLBACKS
using System.Security.Claims;
using ImageGen.Application.Services;
using ImageGen.Domain.Entities;
using ImageGen.Web.Auth;
using ImageGen.Web.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImageGen.Web.Controllers;

[Route("account")]
public sealed class AccountController(UserService users, AuthOptions auth) : Controller
{
    private readonly UserService _users = users;
    private readonly AuthOptions _auth = auth;

    [HttpGet("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(string? returnUrl, CancellationToken ct)
    {
        // No accounts yet: a sign-in form has nothing to sign in to, and the only answer it can give is "wrong
        // username or password", which is a lie about what is wrong. Send them to make the first one.
        if (!await _users.AnyExistAsync(ct))
            return RedirectToAction(nameof(Register), new { returnUrl, first = true });

        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    /// <summary>No automated model binding: read the form explicitly, validate, then map.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(CancellationToken ct)
    {
        var form = Request.Form;
        var username = form["username"].ToString().Trim();
        var password = form["password"].ToString();
        var returnUrl = form["returnUrl"].ToString();
        var vm = new LoginViewModel { Username = username, ReturnUrl = returnUrl };

        if (username.Length == 0 || password.Length == 0)
        {
            vm.Error = "Username and password are required.";
            return View(vm);
        }
        var user = await _users.AuthenticateAsync(username, password, ct);
        if (user is null)
        {
            vm.Error = "Wrong username or password.";
            return View(vm);
        }
        await SignInAsync(user);
        return RedirectToLocalOrHome(returnUrl);
    }

    [HttpGet("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register(string? returnUrl, bool first, CancellationToken ct) =>
        View(new RegisterViewModel
        {
            ReturnUrl = returnUrl,
            RequiresCode = _auth.RegistrationRequiresCode,
            // Only when they were BOUNCED here and it is still true — a stale ?first=true after someone else has
            // registered would tell the second person the box is empty.
            IsFirstAccount = first && !await _users.AnyExistAsync(ct),
        });

    [HttpPost("register")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(CancellationToken ct)
    {
        var form = Request.Form;
        var username = form["username"].ToString().Trim();
        var password = form["password"].ToString();
        var displayName = form["displayName"].ToString().Trim();
        var code = form["code"].ToString();
        var returnUrl = form["returnUrl"].ToString();
        var vm = new RegisterViewModel
        {
            Username = username,
            DisplayName = displayName,
            ReturnUrl = returnUrl,
            RequiresCode = _auth.RegistrationRequiresCode,
        };

        if (username.Length == 0 || password.Length == 0)
            return Fail(vm, "Username and password are required.");
        if (password.Length < 8)
            return Fail(vm, "Password must be at least 8 characters.");
        if (_auth.RegistrationRequiresCode && code != _auth.RegistrationCode)
            return Fail(vm, "Invalid registration code.");

        var user = await _users.RegisterAsync(username, password, displayName, ct);
        if (user is null)
            return Fail(vm, "That username is taken.");

        await SignInAsync(user);
        return RedirectToLocalOrHome(returnUrl);
    }

    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    private IActionResult Fail(RegisterViewModel vm, string error) { vm.Error = error; return View(vm); }

    private Task SignInAsync(User user)
    {
        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.DisplayName));
        return HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    private IActionResult RedirectToLocalOrHome(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : Redirect("/");
}
