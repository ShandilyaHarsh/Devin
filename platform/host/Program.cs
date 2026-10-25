using Barebone.Platform;

var builder = WebApplication.CreateBuilder(args);
builder.AddBarebonePlatform();

var app = builder.Build();
app.UseBarebonePlatform();
app.MapFallbackToFile("index.html");
app.Run();

/// <summary>Entry point; partial so tests can use WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
