using FalconLoad.Core.Engine;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Web.Services;

/// <summary>A small JSON API so load tests can also be scripted, e.g. from CI.</summary>
public static class ApiEndpoints
{
    public static void MapFalconApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").DisableAntiforgery();

        api.MapGet("/plans", (PlanStore store) => store.GetAll());

        api.MapGet("/plans/{id:guid}", (Guid id, PlanStore store) =>
            store.Get(id) is { } plan ? Results.Ok(plan) : Results.NotFound());

        api.MapPost("/plans", (LoadTestPlan plan, PlanStore store) =>
        {
            var problems = plan.Validate();
            if (problems.Count > 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["plan"] = [.. problems] });
            }

            var saved = store.Save(plan);
            return Results.Created($"/api/plans/{saved.Id}", saved);
        });

        api.MapDelete("/plans/{id:guid}", (Guid id, PlanStore store) =>
            store.Delete(id) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/plans/{id:guid}/run", (Guid id, PlanStore store, RunManager runs) =>
            store.Get(id) is { } plan ? StartRun(plan, runs) : Results.NotFound());

        // Run an ad-hoc plan without saving it.
        api.MapPost("/runs", (LoadTestPlan plan, RunManager runs) => StartRun(plan, runs));

        api.MapGet("/runs", (RunManager runs) => runs.GetAll().Select(Summarize));

        api.MapGet("/runs/{id:guid}", (Guid id, RunManager runs) =>
            runs.Get(id) is { } run ? Results.Ok(Detail(run)) : Results.NotFound());

        api.MapGet("/runs/{id:guid}/export", (Guid id, RunManager runs) =>
            runs.Get(id) is { } run
                ? Results.Json(run.ToExport(), PlanStore.JsonOptions, statusCode: 200)
                    .WithDownloadName($"falcon-load-{Slug(run.Plan.Name)}-{run.StartedAt:yyyyMMdd-HHmmss}.json")
                : Results.NotFound());

        api.MapPost("/runs/{id:guid}/stop", (Guid id, RunManager runs) =>
        {
            if (runs.Get(id) is not { } run)
            {
                return Results.NotFound();
            }

            run.Stop();
            return Results.Accepted($"/api/runs/{id}");
        });

        // Combine results exported from several machines into one view.
        api.MapPost("/results/combine", (RunExport[] results) =>
        {
            var problems = ResultCombiner.Validate(results);
            return problems.Count > 0
                ? Results.ValidationProblem(new Dictionary<string, string[]> { ["results"] = [.. problems] })
                : Results.Ok(ResultCombiner.Combine(results));
        });

        api.MapPost("/probe", async (RequestDefinition request, bool? ignoreTlsErrors, CancellationToken ct) =>
            await RequestProbe.SendAsync(request, new LoadProfile { IgnoreTlsErrors = ignoreTlsErrors ?? false }, cancellationToken: ct));
    }

    private static IResult StartRun(LoadTestPlan plan, RunManager runs)
    {
        var problems = plan.Validate();
        if (problems.Count > 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["plan"] = [.. problems] });
        }

        var run = runs.Start(plan);
        return Results.Accepted($"/api/runs/{run.Id}", Summarize(run));
    }

    private static object Summarize(LoadTestRun run) => new
    {
        run.Id,
        PlanId = run.Plan.Id,
        PlanName = run.Plan.Name,
        run.StartedAt,
        run.Snapshot.State,
        run.Snapshot.Elapsed,
        run.Snapshot.TotalRequests,
        run.Snapshot.FailedRequests,
        run.Snapshot.RequestsPerSecond,
        P95LatencyMs = run.Snapshot.Latency.P95Ms,
    };

    private static object Detail(LoadTestRun run) => new
    {
        run.Id,
        run.StartedAt,
        run.Plan,
        Results = run.Snapshot,
    };

    private static string Slug(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "run" : slug;
    }
}

file static class ResultExtensions
{
    public static IResult WithDownloadName(this IResult result, string fileName) => new DownloadResult(result, fileName);

    private sealed class DownloadResult(IResult inner, string fileName) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
            return inner.ExecuteAsync(httpContext);
        }
    }
}
