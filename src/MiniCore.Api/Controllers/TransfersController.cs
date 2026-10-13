using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniCore.Api.Auth;
using MiniCore.Domain;
using MiniCore.Infrastructure;

namespace MiniCore.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/transfers")]
public sealed class TransfersController : ControllerBase
{
    private readonly ITransferService _transfers;

    public TransfersController(ITransferService transfers) => _transfers = transfers;

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] TransferRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var response = await _transfers.CreateAsync(User.ToCaller(), idempotencyKey, request, HttpContext.TraceIdentifier, ct);
        return this.ToResult(response);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TransferDetail>> Get(Guid id, CancellationToken ct) =>
        Ok(await _transfers.GetAsync(User.ToCaller(), id, ct));

    [HttpPost("{id:guid}/reverse")]
    [Authorize(Roles = AppRoles.Operator)]
    public async Task<IActionResult> Reverse(
        Guid id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var response = await _transfers.ReverseAsync(User.ToCaller(), id, idempotencyKey, HttpContext.TraceIdentifier, ct);
        return this.ToResult(response);
    }
}

internal static class ApiResponseExtensions
{

    public static IActionResult ToResult(this ControllerBase controller, ApiResponse response)
    {
        if (response.Replayed)
            controller.Response.Headers["Idempotent-Replayed"] = "true";

        return new ContentResult
        {
            StatusCode = response.StatusCode,
            Content = response.JsonBody,
            ContentType = "application/json",
        };
    }
}
