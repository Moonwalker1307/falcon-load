namespace FalconLoad.Web.Components.Shared;

public sealed record ChartSeries(string Name, string Color, IReadOnlyList<double> Values, bool Fill = false);
