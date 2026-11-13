using System.Text.Json.Serialization;

namespace Dockyard.Dashboard.Features.Resources;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DashboardSettings))]
partial class DashboardJson : JsonSerializerContext;
