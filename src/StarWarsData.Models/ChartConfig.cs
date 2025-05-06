namespace StarWarsData.Models;

public record ChartConfig(string Type, IList<string> Labels, IList<double> Data);