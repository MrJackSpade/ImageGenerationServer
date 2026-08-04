//TODO: CHECK FOR FALLBACKS
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImageGen.Web.Controllers;

[Authorize]
public sealed class ComposeController : Controller
{
    [HttpGet("/")]
    public IActionResult Index() => View();
}
