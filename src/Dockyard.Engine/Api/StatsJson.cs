using System.Text.Json.Serialization;

namespace Dockyard.Engine;

[JsonSerializable(typeof(StatsSample))]
partial class StatsJson : JsonSerializerContext;
