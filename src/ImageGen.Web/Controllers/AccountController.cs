using ImageGen.Application.Services;
using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Entities;
using ImageGen.Web.Auth;
using ImageGen.Web.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

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
        {
            return RedirectToAction(nameof(Register), new { returnUrl, first = true });
        }

        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    /// <summary>No automated model binding: read the form explicitly, validate, then map.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(CancellationToken ct)
    {
        IFormCollection form = Request.Form;
        string username = form[FormFields.Username].ToString().Trim();
        string password = form[FormFields.Password].ToString();
        string returnUrl = form[FormFields.ReturnUrl].ToString();
        LoginViewModel vm = new() { Username = username, ReturnUrl = returnUrl };

        if (username.Length == 0 || password.Length == 0)
        {
            vm.Error = "Username and password are required.";
            return View(vm);
        }

        User? user = await _users.AuthenticateAsync(username, password, ct);
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
        IFormCollection form = Request.Form;
        string username = form[FormFields.Username].ToString().Trim();
        string password = form[FormFields.Password].ToString();
        string displayName = form[FormFields.DisplayName].ToString().Trim();
        string code = form[FormFields.Code].ToString();
        string returnUrl = form[FormFields.ReturnUrl].ToString();
        RegisterViewModel vm = new()
        {
            Username = username,
            DisplayName = displayName,
            ReturnUrl = returnUrl,
            RequiresCode = _auth.RegistrationRequiresCode,
        };

        if (username.Length == 0 || password.Length == 0)
        {
            return Fail(vm, "Username and password are required.");
        }

        if (password.Length < 8)
        {
            return Fail(vm, "Password must be at least 8 characters.");
        }

        if (_auth.RegistrationRequiresCode && code != _auth.RegistrationCode)
        {
            return Fail(vm, "Invalid registration code.");
        }

        User? user = await _users.RegisterAsync(username, password, displayName, ct);
        if (user is null)
        {
            return Fail(vm, "That username is taken.");
        }

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

    private ViewResult Fail(
        RegisterViewModel vm,
        [AllowMagicStrings("User-facing validation messages are prose shown in the UI, not identifiers to name as constants.")] string error)
    {
        vm.Error = error;
        return View(vm);
    }

    private Task SignInAsync(User user)
    {
        ClaimsIdentity identity = new(CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.DisplayName));
        return HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    private RedirectResult RedirectToLocalOrHome(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : Redirect(Routes.Home);

    /// <summary>Names of the fields read from the login and registration forms.</summary>
    private static class FormFields
    {
        /// <summary>The account's login name.</summary>
        public const string Username = "username";

        /// <summary>The account's password.</summary>
        public const string Password = "password";

        /// <summary>The display name chosen at registration.</summary>
        public const string DisplayName = "displayName";

        /// <summary>The registration code, when registration is gated by one.</summary>
        public const string Code = "code";

        /// <summary>The local URL to return to after a successful sign-in.</summary>
        public const string ReturnUrl = "returnUrl";
    }

    /// <summary>Local routes this controller redirects to.</summary>
    private static class Routes
    {
        /// <summary>The application home page.</summary>
        public const string Home = "/";
    }
}