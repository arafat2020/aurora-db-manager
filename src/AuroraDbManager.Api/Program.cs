using AuroraDbManager.Api;

// The REST API alone, without the UI: for a headless installation. What it is made of is in
// AuroraHost, which the UI host (AuroraDbManager.Web) is built from as well.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuroraCore(builder.Configuration);
builder.Services.AddAuroraApi();

var app = builder.Build();

app.UseAuroraEdge();
app.UseAuroraApiErrors();
app.UseAuroraRequestPipeline();
app.MapAuroraApi();

app.Run();
