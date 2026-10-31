using System.Text.Json.Serialization;
using FalconLoad.Core.Distributed;
using FalconLoad.Web.Components;
using FalconLoad.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    // The import page lets people paste a whole Postman collection or OpenAPI spec, which can be larger than SignalR's 32 KB default.
    .AddHubOptions(options => options.MaximumReceiveMessageSize = 10 * 1024 * 1024);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<PlanStore>();
builder.Services.AddSingleton<EnvironmentStore>();
builder.Services.AddSingleton<RunHistoryStore>();
builder.Services.AddSingleton(RegressionSettings.Read(builder.Configuration));
builder.Services.AddSingleton<RunManager>();

// Agents: this machine can be one (AgentMode, with the token from configuration) and can drive others (the registry,
// the client that talks to them and the manager that runs tests across them).
builder.Services.AddSingleton<AgentMode>();
builder.Services.AddHttpClient<AgentClient>(http => http.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<DistributedRunManager>();

// Live metrics for Prometheus (scraped from /metrics), InfluxDB and OpenTelemetry (pushed in the background).
builder.Services.AddSingleton<TelemetryStore>();
builder.Services.AddHttpClient(nameof(TelemetryService));
builder.Services.AddSingleton<TelemetryService>();
builder.Services.AddHostedService(services => services.GetRequiredService<TelemetryService>());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapFalconApi();
app.MapAgentApi();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
