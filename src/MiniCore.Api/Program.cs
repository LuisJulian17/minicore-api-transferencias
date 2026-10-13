using Microsoft.AspNetCore.Mvc;
using MiniCore.Api.Auth;
using MiniCore.Api.Http;
using MiniCore.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z' ";
});

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 16 * 1024);

DapperSetup.Configure();

var connectionString = builder.Configuration.GetConnectionString("MiniCore")
    ?? throw new InvalidOperationException("Falta ConnectionStrings:MiniCore.");

builder.Services.AddSingleton(new SqlConnectionFactory(connectionString));
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<ITransferService, TransferService>();
builder.Services.AddScoped<IAccountQueries, AccountQueries>();

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ITransferFaultInjector, DevFaultInjector>(); // solo desarrollo (escenario E12)
}
else
{
    builder.Services.AddSingleton<ITransferFaultInjector, NoFaultInjector>();
}

builder.Services.AddMiniCoreAuth(builder.Configuration, builder.Environment);

builder.Services.AddControllers(o => o.Filters.Add<DomainExceptionFilter>())
    .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = ctx =>
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Solicitud inválida",
            Detail = "El cuerpo o los parámetros de la solicitud no tienen un formato válido.",
        };
        problem.Extensions["code"] = "INVALID_REQUEST";
        problem.Extensions["correlationId"] = ctx.HttpContext.TraceIdentifier;
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" },
        };
    });
builder.Services.AddProblemDetails(o =>
    o.CustomizeProblemDetails = ctx => ctx.ProblemDetails.Extensions["correlationId"] = ctx.HttpContext.TraceIdentifier);
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages(); 

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

if (app.Environment.IsDevelopment())
{
    app.MapGet("/dev/token/{user}", (string user, DevTokenIssuer issuer) =>
            issuer.Issue(user) is { } token ? Results.Ok(new { token }) : Results.NotFound())
        .AllowAnonymous();
}

app.Run();

public partial class Program { }
