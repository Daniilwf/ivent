var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

/// <summary>Entry point, exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
