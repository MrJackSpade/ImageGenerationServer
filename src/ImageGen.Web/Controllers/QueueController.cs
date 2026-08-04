using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImageGen.Web.Controllers;

/// <summary>
/// The active-gen queue page — a JS-driven shell. Data comes from /forge/queue (every active gen on this box,
/// with the prompt only for the requester's own jobs) and /forge/workflows (config id -> friendly name + size).
/// </summary>
[Authorize]
public sealed class QueueController : Controller
{
    [HttpGet("/queue")]
    public IActionResult Index() => View();
}
