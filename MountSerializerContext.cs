using System.Text.Json.Serialization;

namespace OwlCore.Storage.Mcp;

/// <summary>
/// Supplies type information for mount settings serialization.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, MountConfiguration>))]
[JsonSerializable(typeof(MountConfiguration))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(DateTime))]
public partial class MountSerializerContext : JsonSerializerContext
{
}
