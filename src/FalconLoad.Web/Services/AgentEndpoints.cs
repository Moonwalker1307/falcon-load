using System.Reflection;
using FalconLoad.Core.Distributed;
using FalconLoad.Core.Model;

namespace FalconLoad.Web.Services;

/// <summary>
/// The endpoints a controller uses to run a share of a test on this machine. They only work when agent mode is on,
/// and every call needs the agent token. The runs they start are ordinary runs, so they show up on this machine's
/// Runs page too.
/// </summary>
public static class AgentEndpoints
{
    // The informational version carries the commit hash after a '+', which is noise next to an agent's name.
    private static readonly string Version =
        (typeof(AgentEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AgentEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown").Split('+')[0];

    public static void MapAgentApi(this IEndpointRouteBuilder app)
    {
        var agent = app.MapGroup("/api/agent").DisableAntiforgery().AddEndpointFilter(RequireToken);

        agent.MapGet("/", (RunManager runs) =>
            new AgentInfo(Environment.MachineName, Version, DateTimeOffset.UtcNow, runs.GetAll().Count(r => !r.Snapshot.IsFinished)));

        agent.MapPost("/runs", (AgentStartRequest request, RunManager runs) =>
        {
            var problems = request.Plan.ValidateForRun(request.Environment).ToList();
            if (request.StartAt is { } startAt && startAt - DateTimeOffset.UtcNow > RunManager.MaxStartDelay)
            {
                problems.Add($"The start time is more than {RunManager.MaxStartDelay.TotalSeconds:0} seconds ahead. Are the clocks in sync?");
            }

            if (problems.Count > 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["plan"] = [.. problems] });
            }

            var run = runs.Start(request.Plan, request.Environment, request.StartAt, request.Controller ?? "a controller");
            return Results.Accepted($"/api/agent/runs/{run.Id}", new AgentRunStarted(run.Id));
        });

        // The export carries the histograms, so the controller can merge agents' results exactly while they run.
        agent.MapGet("/runs/{id:guid}", (Guid id, RunManager runs) =>
            runs.Get(id) is { } run ? Results.Json(run.ToExport(), PlanStore.JsonOptions) : Results.NotFound());

        agent.MapPost("/runs/{id:guid}/stop", (Guid id, RunManager runs) =>
        {
            if (runs.Get(id) is not { } run)
            {
                return Results.NotFound();
            }

            run.Stop();
            return Results.Accepted($"/api/agent/runs/{id}");
        });
    }

    private static async ValueTask<object?> RequireToken(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var mode = context.HttpContext.RequestServices.GetRequiredService<AgentMode>();
        if (!mode.IsEnabled)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Agent mode is off",
                detail: $"Agent mode is off on this falcon-load. Start it with the {AgentMode.SettingName} setting (or the FalconLoad__AgentToken environment variable) set to a token, and give a controller that token.");
        }

        if (!mode.Accepts(context.HttpContext.Request.Headers.Authorization))
        {
            context.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "The agent token is missing or wrong");
        }

        return await next(context);
    }
}
