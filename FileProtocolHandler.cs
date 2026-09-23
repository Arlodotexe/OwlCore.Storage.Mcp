using System.IO;
using OwlCore.Storage;
using OwlCore.Storage.System.IO;

namespace OwlCore.Storage.Mcp;

/// <summary>
/// Built-in protocol handler for the native system store (local disk), exposed as the `file://` scheme.
/// The root is the platform's system root directory (e.g. "/" on Linux, "C:\" on Windows), derived at
/// runtime from the temp path - one code path, no platform conditionals, no hardcoded root ID.
/// Registering this handler makes the system store a first-class storage instance with its own distinct
/// ID (`file://`), so root IDs shared with another protocol (e.g. "/" with the MFS root on Linux) are
/// disambiguated by the standard single-vs-multiple mechanism instead of an implicit fast path.
/// </summary>
public class FileProtocolHandler : IProtocolHandler
{
    private const string RootUri = "file://";

    // The native system store's root directory (e.g. "/" on Linux, "C:\" on Windows).
    private static readonly string _nativeRoot = Path.GetPathRoot(Path.GetTempPath())!;

    public bool HasBrowsableRoot => true; // The system store has a browsable root

    public Task<IStorable?> CreateRootAsync(string rootUri, CancellationToken cancellationToken = default)
    {
        // Return the system store's root as a first-class storable so it can be registered under "file://".
        return Task.FromResult<IStorable?>(Directory.Exists(_nativeRoot)
            ? new SystemFolder(new DirectoryInfo(_nativeRoot))
            : null);
    }

    public Task<IStorable?> CreateResourceAsync(string resourceUri, CancellationToken cancellationToken = default)
    {
        // file:// items are accessed through filesystem navigation from the browsable root;
        // direct resource creation is not supported (same pattern as the memory/mounted folder handlers).
        return Task.FromResult<IStorable?>(null);
    }

    public string CreateItemId(string parentId, string itemName)
    {
        // Simple path construction for the file protocol
        if (parentId == RootUri)
        {
            return $"{RootUri}{itemName}";
        }
        if (parentId.EndsWith("/"))
            return $"{parentId}{itemName}";
        return $"{parentId.TrimEnd('/')}/{itemName}";
    }

    public Task<DriveInfoResult?> GetDriveInfoAsync(string rootUri, CancellationToken cancellationToken = default)
    {
        // Simple drive info for the file protocol
        return Task.FromResult<DriveInfoResult?>(new DriveInfoResult(
            Id: rootUri,
            Name: "Local File System",
            Type: "filesystem",
            DriveType: "Fixed",
            IsReady: true,
            TotalSize: -1L,
            AvailableFreeSpace: -1L
        ));
    }

    public bool NeedsRegistration(string id)
    {
        // File items resolve through the system store fast path; no explicit registration needed.
        return false;
    }
}
