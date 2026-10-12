var app = WebApplication.CreateBuilder(args).Build();

app.MapGet("/healt", () => "ok");

app.Run();

public partial class Program { }