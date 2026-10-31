using FalconLoad.Core.Metrics;

namespace FalconLoad.Web.Services;

public static class RegressionSettings
{
    /// <summary>
    /// The thresholds the UI judges runs by, from the <c>FalconLoad:Regression</c> section, with the defaults of
    /// <see cref="RegressionThresholds"/> for anything not set. Invalid values stop the app at startup, where the
    /// message is easy to see, rather than quietly judging every run by them.
    /// </summary>
    public static RegressionThresholds Read(IConfiguration configuration)
    {
        var thresholds = configuration.GetSection("FalconLoad:Regression").Get<RegressionThresholds>() ?? new RegressionThresholds();
        var problems = thresholds.Validate();
        return problems.Count == 0
            ? thresholds
            : throw new InvalidOperationException("FalconLoad:Regression is invalid: " + string.Join(" ", problems));
    }
}
