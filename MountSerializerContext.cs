using System.Text.Json.Serialization;

namespace OwlCore.Storage.Mcp;

/// <summary>
/// Supplies type information for mount settings serialization.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, MountConfiguration>))]
[JsonSerializable(typeof(List<MountConfiguration>))]
[JsonSerializable(typeof(MountConfiguration))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(StartMode))]
[JsonSerializable(typeof(StorableItemResult))]
[JsonSerializable(typeof(StorableItemResult[]))]
[JsonSerializable(typeof(StorableItemWithArchiveTypeResult))]
[JsonSerializable(typeof(PaginatedItemsResult))]
[JsonSerializable(typeof(DriveInfoResult))]
[JsonSerializable(typeof(DriveInfoResult[]))]
[JsonSerializable(typeof(StorableInfoResult))]
[JsonSerializable(typeof(ProtocolInfoResult))]
[JsonSerializable(typeof(ProtocolInfoResult[]))]
[JsonSerializable(typeof(MountResult))]
[JsonSerializable(typeof(UnmountResult))]
[JsonSerializable(typeof(RenameMountResult))]
[JsonSerializable(typeof(ContentMatchLine))]
[JsonSerializable(typeof(ContentMatchLine[]))]
[JsonSerializable(typeof(FindResultWithMatches))]
[JsonSerializable(typeof(FindResultWithMatches[]))]
[JsonSerializable(typeof(LaunchResult))]
[JsonSerializable(typeof(ExecuteResult))]
[JsonSerializable(typeof(StartResult))]
[JsonSerializable(typeof(MountedFolderInfo))]
[JsonSerializable(typeof(MountedFolderInfo[]))]
public partial class MountSerializerContext : JsonSerializerContext
{
}
