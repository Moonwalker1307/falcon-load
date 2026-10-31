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

        // Export and import use the PlanFile format, so a test can be shared between machines.
        api.MapGet("/plans/export", (PlanStore store) =>
            PlanDownload(store.GetAll(), $"falcon-load-tests-{DateTime.Now:yyyyMMdd-HHmmss}.json"));

        api.MapGet("/plans/{id:guid}/export", (Guid id, PlanStore store) =>
            store.Get(id) is { } plan ? PlanDownload([plan], $"falcon-load-{Slug(plan.Name)}.json") : Results.NotFound());

        api.MapPost("/plans/import", async (HttpRequest request, PlanStore store) =>
        {
            using var reader = new StreamReader(request.Body);
            var import = PlanFile.Read(await reader.ReadToEndAsync(request.HttpContext.RequestAborted));
            if (!import.Succeeded)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["plan"] = [.. import.Problems] });
            }

            return Results.Ok(store.SaveAll(import.Plans));
        });

        api.MapDelete("/plans/{id:guid}", (Guid id, PlanStore store) =>
            store.Delete(id) ? Results.NoContent() : Results.NotFound());

        // Environments hold the variables, such as {{baseUrl}} or a token, that a run substitutes into a test. Secret
        // values are write-only here: responses leave them out (null means "set, but hidden"), and saving a secret with
        // a null value keeps the stored one.
        api.MapGet("/environments", (EnvironmentStore environments) => environments.GetAll().Select(e => e.Redacted()));

        api.MapGet("/environments/{id:guid}", (Guid id, EnvironmentStore environments) =>
            environments.Get(id) is { } environment ? Results.Ok(environment.Redacted()) : Results.NotFound());

        api.MapPost("/environments", (TestEnvironment environment, EnvironmentStore environments) =>
        {
            var problems = environments.Validate(environment);
            if (problems.Count > 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["environment"] = [.. problems] });
            }

            var saved = environments.Save(environment);
            return Results.Created($"/api/environments/{saved.Id}", saved.Redacted());
        });

        api.MapGet("/environments/export", (EnvironmentStore environments) =>
            EnvironmentDownload(environments.GetAll(), $"falcon-load-environments-{DateTime.Now:yyyyMMdd-HHmmss}.json"));

        api.MapGet("/environments/{id:guid}/export", (Guid id, EnvironmentStore environments) =>
            environments.Get(id) is { } environment ? EnvironmentDownload([environment], $"falcon-load-environment-{Slug(environment.Name)}.json") : Results.NotFound());

        api.MapPost("/environments/import", async (HttpRequest request, EnvironmentStore environments) =>
        {
            using var reader = new StreamReader(request.Body);
            var import = EnvironmentFile.Read(await reader.ReadToEndAsync(request.HttpContext.RequestAborted));
            if (!import.Succeeded)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["environment"] = [.. import.Problems] });
            }

            return Results.Ok(environments.SaveAll(import.Environments).Select(e => e.Redacted()));
        });

        api.MapDelete("/environments/{id:guid}", (Guid id, EnvironmentStore environments) =>
            environments.Delete(id) ? Results.NoContent() : Results.NotFound());

        // The environment comes from ?environment=Name, or else from the test's own "environment" setting.
        api.MapPost("/plans/{id:guid}/run", (Guid id, string? environment, PlanStore store, EnvironmentStore environments, RunManager runs) =>
            store.Get(id) is { } plan ? StartRun(plan, environment, environments, runs) : Results.NotFound());

        // Run an ad-hoc plan without saving it.
        api.MapPost("/runs", (LoadTestPlan plan, string? environment, EnvironmentStore environments, RunManager runs) =>
            StartRun(plan, environment, environments, runs));

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

        api.MapPost("/probe", async (RequestDefinition request, bool? ignoreTlsErrors, string? environment, EnvironmentStore environments, CancellationToken ct) =>
        {
            if (!environments.TryResolve(environment, out var resolved, out var problem))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["environment"] = [problem!] });
            }

            var profile = new LoadProfile { IgnoreTlsErrors = ignoreTlsErrors ?? false };
            return Results.Ok(await RequestProbe.SendAsync(request, profile, cancellationToken: ct, environment: resolved));
        });
    }

    private static IResult StartRun(LoadTestPlan plan, string? environmentName, EnvironmentStore environments, RunManager runs)
    {
        if (!environments.TryResolve(string.IsNullOrWhiteSpace(environmentName) ? plan.Environment : environmentName, out var environment, out var missing))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["environment"] = [missing!] });
        }

        var problems = plan.ValidateForRun(environment);
        if (problems.Count > 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["plan"] = [.. problems] });
        }

        var run = runs.Start(plan, environment);
        return Results.Accepted($"/api/runs/{run.Id}", Summarize(run));
    }

    private static IResult EnvironmentDownload(IEnumerable<TestEnvironment> environments, string fileName) =>
        Results.Text(EnvironmentFile.Write(environments), "application/json").WithDownloadName(fileName);

    private static IResult PlanDownload(IEnumerable<LoadTestPlan> plans, string fileName) =>
        Results.Text(PlanFile.Write(plans), "application/json").WithDownloadName(fileName);

    private static object Summarize(LoadTestRun run) => new
    {
        run.Id,
        PlanId = run.Plan.Id,
        PlanName = run.Plan.Name,
        run.StartedAt,
        run.EnvironmentName,
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
        // ASCII only: Kestrel refuses non-ASCII header values, and the slug ends up in Content-Disposition.
        var chars = name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
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
