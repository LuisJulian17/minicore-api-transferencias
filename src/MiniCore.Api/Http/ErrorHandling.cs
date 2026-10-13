using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using MiniCore.Domain;

namespace MiniCore.Api.Http;

public sealed class DomainExceptionFilter : IExceptionFilter
{
    private readonly ILogger<DomainExceptionFilter> _logger;

    public DomainExceptionFilter(ILogger<DomainExceptionFilter> logger) => _logger = logger;

    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not DomainException ex) return;

        var problem = new ProblemDetails
        {
            Status = ex.StatusCode,
            Title = TitleFor(ex.StatusCode),
            Detail = ex.Message,
        };
        if (ex.Code is not null) problem.Extensions["code"] = ex.Code;
        problem.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;

        _logger.LogInformation("Operacion rechazada: {Code} ({Status})", ex.Code ?? "-", ex.StatusCode);

        context.Result = new ObjectResult(problem)
        {
            StatusCode = ex.StatusCode,
            ContentTypes = { "application/problem+json" },
        };
        context.ExceptionHandled = true;
    }

    private static string TitleFor(int status) => status switch
    {
        400 => "Solicitud inválida",
        401 => "No autenticado",
        403 => "Acceso denegado",
        404 => "No encontrado",
        409 => "Conflicto",
        422 => "No se pudo procesar la solicitud",
        _ => "Error",
    };
}

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IProblemDetailsService _problems;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IProblemDetailsService problems)
    {
        _logger = logger;
        _problems = problems;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        _logger.LogError(exception, "Error no controlado");

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return await _problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Error interno",
                Detail = "Ocurrió un error inesperado. Informa el correlationId al equipo de soporte.",
            },
        });
    }
}
