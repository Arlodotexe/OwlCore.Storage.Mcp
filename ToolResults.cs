/// <summary>
/// Result types for MCP tool methods. Using concrete records instead of anonymous types for trim/AOT compatibility.
/// </summary>
namespace OwlCore.Storage.Mcp;

public record StorableItemResult(string Id, string Name, string Type);

public record StorableItemWithArchiveTypeResult(string Id, string Name, string Type, string? ArchiveType);

public record PaginatedItemsResult(StorableItemResult[] Items, int TotalCount, bool HasMore);

public record DriveInfoResult(string Id, string Name, string Type, string DriveType, bool IsReady, long TotalSize, long AvailableFreeSpace);

public record StorableInfoResult(string Id, string Name, string Type, long? SizeBytes, int? LineCount);

public record ProtocolInfoResult(string Scheme, string Name, string Type, bool HasBrowsableRoot, bool SupportsDirectResources, string Description);

public record MountResult(bool Success, string RootUri, string ProtocolScheme, string MountName, string OriginalId, string Message);

public record UnmountResult(bool Success, string ProtocolScheme, string Message);

public record RenameMountResult(bool Success, string OldProtocolScheme, string NewProtocolScheme, string? NewMountName, string NewRootUri, string Message);

public record ContentMatchLine(int Line, string Text);

public record FindResultWithMatches(string Id, string Name, string Type, ContentMatchLine[]? Matches = null);

public record LaunchResult(bool Started, string Mode, string Message);

public record ExecuteResult(int ExitCode, string Stdout, string? Stderr, bool TimedOut, string? Error = null);

public record StartResult(string Mode, bool? Started = null, string? Message = null, int? ExitCode = null, string? Stdout = null, string? Stderr = null, bool? TimedOut = null, string? Error = null);

public record MountedFolderInfo(string ProtocolScheme, string MountName, string RootUri, string FolderType, string MountType, string OriginalId);
