using System.Text.Json.Serialization;

namespace WinGlance.Statistics;

[JsonSerializable(typeof(BucketData))]
[JsonSerializable(typeof(ApplicationEntry))]
internal partial class StatisticsJsonContext : JsonSerializerContext;
