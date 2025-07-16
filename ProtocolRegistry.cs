using OwlCore.Storage.System.IO;
using OwlCore.Storage;
using System.Collections.Concurrent;

/// <summary>
/// Registry for custom storage protocols that maps protocol schemes to storage implementations
/// </summary>
public static class ProtocolRegistry
{
    private static readonly ConcurrentDictionary<string, IProtocolHandler> _protocolHandlers = new();
    private static readonly ConcurrentDictionary<string, MountedFolderProtocolHandler> _mountedFolders = new();
    
    static ProtocolRegistry()
    {
        // Register built-in protocol handlers
        RegisterProtocol("mfs", new IpfsMfsProtocolHandler());
        RegisterProtocol("http", new HttpProtocolHandler());
        RegisterProtocol("https", new HttpProtocolHandler());
        RegisterProtocol("ipfs", new IpfsProtocolHandler());
        RegisterProtocol("ipns", new IpnsProtocolHandler());
        RegisterProtocol("memory", new MemoryProtocolHandler());
        // Add more protocols here as needed
        // RegisterProtocol("azure-blob", new AzureBlobProtocolHandler());
        // RegisterProtocol("s3", new S3ProtocolHandler());
    }

    /// <summary>
    /// Registers a protocol handler for a specific scheme
    /// </summary>
    /// <param name="scheme">The protocol scheme (e.g., "mfs", "s3", etc.)</param>
    /// <param name="handler">The handler implementation</param>
    public static void RegisterProtocol(string scheme, IProtocolHandler handler)
    {
        _protocolHandlers[scheme] = handler;
    }

    /// <summary>
    /// Gets the protocol handler for a given URI or ID
    /// </summary>
    /// <param name="uri">The URI or ID to parse</param>
    /// <returns>The protocol handler if found, null otherwise</returns>
    public static IProtocolHandler? GetProtocolHandler(string uri)
    {
        var scheme = ExtractScheme(uri);
        return scheme != null && _protocolHandlers.TryGetValue(scheme, out var handler) ? handler : null;
    }

    /// <summary>
    /// Checks if a URI uses a custom protocol
    /// </summary>
    /// <param name="uri">The URI to check</param>
    /// <returns>True if it's a custom protocol, false otherwise</returns>
    public static bool IsCustomProtocol(string uri)
    {
        return GetProtocolHandler(uri) != null;
    }

    /// <summary>
    /// Extracts the protocol scheme from a URI
    /// </summary>
    /// <param name="uri">The URI to parse</param>
    /// <returns>The scheme if found, null otherwise</returns>
    public static string? ExtractScheme(string uri)
    {
        var colonIndex = uri.IndexOf("://");
        return colonIndex > 0 ? uri.Substring(0, colonIndex) : null;
    }

    /// <summary>
    /// Creates an item ID for a custom protocol
    /// </summary>
    /// <param name="parentId">The parent item ID</param>
    /// <param name="itemName">The item name</param>
    /// <returns>The constructed item ID</returns>
    public static string CreateCustomItemId(string parentId, string itemName)
    {
        var handler = GetProtocolHandler(parentId);
        return handler?.CreateItemId(parentId, itemName) ?? $"{parentId}/{itemName}";
    }

    /// <summary>
    /// Gets all registered protocol schemes
    /// </summary>
    /// <returns>Collection of registered protocol schemes</returns>
    public static IEnumerable<string> GetRegisteredProtocols()
    {
        return _protocolHandlers.Keys;
    }

    /// <summary>
    /// Mounts an IFolder with a custom protocol scheme, making it available as a browsable drive
    /// </summary>
    /// <param name="folder">The folder to mount</param>
    /// <param name="protocolScheme">The custom protocol scheme (e.g., "mydata", "project1")</param>
    /// <param name="mountName">Display name for the mounted folder</param>
    /// <returns>The root URI for the mounted folder</returns>
    /// <exception cref="ArgumentException">Thrown if the protocol scheme is already registered</exception>
    public static string MountFolder(IFolder folder, string protocolScheme, string mountName)
    {
        if (string.IsNullOrWhiteSpace(protocolScheme))
            throw new ArgumentException("Protocol scheme cannot be null or empty", nameof(protocolScheme));
        
        if (string.IsNullOrWhiteSpace(mountName))
            throw new ArgumentException("Mount name cannot be null or empty", nameof(mountName));

        // Ensure protocol scheme doesn't conflict with existing protocols
        if (_protocolHandlers.ContainsKey(protocolScheme))
            throw new ArgumentException($"Protocol scheme '{protocolScheme}' is already registered", nameof(protocolScheme));

        var handler = new MountedFolderProtocolHandler(folder, mountName, protocolScheme);
        var rootUri = $"{protocolScheme}://";
        
        _protocolHandlers[protocolScheme] = handler;
        _mountedFolders[protocolScheme] = handler;
        
        return rootUri;
    }

    /// <summary>
    /// Unmounts a previously mounted folder by protocol scheme
    /// </summary>
    /// <param name="protocolScheme">The protocol scheme to unmount</param>
    /// <returns>True if the folder was unmounted, false if it wasn't found or wasn't a mounted folder</returns>
    public static bool UnmountFolder(string protocolScheme)
    {
        if (string.IsNullOrWhiteSpace(protocolScheme))
            return false;

        // Only allow unmounting of mounted folders, not built-in protocols
        if (!_mountedFolders.ContainsKey(protocolScheme))
            return false;

        _protocolHandlers.TryRemove(protocolScheme, out _);
        _mountedFolders.TryRemove(protocolScheme, out _);
        
        return true;
    }

    /// <summary>
    /// Gets information about all mounted folders
    /// </summary>
    /// <returns>Array of mounted folder information</returns>
    public static object[] GetMountedFolders()
    {
        return _mountedFolders.Values.Select(handler => new
        {
            protocolScheme = handler.ProtocolScheme,
            mountName = handler.MountName,
            rootUri = $"{handler.ProtocolScheme}://",
            folderType = handler.MountedFolder.GetType().Name
        }).ToArray();
    }

    /// <summary>
    /// Checks if a protocol scheme represents a mounted folder
    /// </summary>
    /// <param name="protocolScheme">The protocol scheme to check</param>
    /// <returns>True if it's a mounted folder, false otherwise</returns>
    public static bool IsMountedFolder(string protocolScheme)
    {
        return _mountedFolders.ContainsKey(protocolScheme);
    }
}
