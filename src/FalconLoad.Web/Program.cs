using System.Text.Json.Serialization;
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
builder.Services.AddSingleton<RunManager>();

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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
