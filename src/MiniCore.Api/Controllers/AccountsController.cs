using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniCore.Api.Auth;
using MiniCore.Domain;
using MiniCore.Infrastructure;

namespace MiniCore.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/accounts")]
public sealed class AccountsController : ControllerBase
{
    private readonly IAccountQueries _queries;

    public AccountsController(IAccountQueries queries) => _queries = queries;

    [HttpGet("{id}/movements")]
    public async Task<ActionResult<MovementsPage>> Movements(
        string id,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct,
        [FromQuery] int page = 1,
        [FromQuery] int size = 20) =>
        Ok(await _queries.GetMovementsAsync(User.ToCaller(), id, from, to, page, size, ct));

    [HttpGet("{id}/balance")]
    public async Task<ActionResult<BalanceResult>> Balance(string id, CancellationToken ct) =>
        Ok(await _queries.GetBalanceAsync(User.ToCaller(), id, ct));
}
