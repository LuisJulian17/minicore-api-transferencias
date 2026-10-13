using MiniCore.Infrastructure;

namespace MiniCore.Api.Auth;

public sealed class DevFaultInjector : ITransferFaultInjector
{
    private readonly IHttpContextAccessor _http;

    public DevFaultInjector(IHttpContextAccessor http) => _http = http;

    public void AfterDebit()
    {
        var header = _http.HttpContext?.Request.Headers["X-Simulate-Failure"].FirstOrDefault();
        if (string.Equals(header, "after-debit", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Falla simulada despues del debito (solo desarrollo).");
    }
}
