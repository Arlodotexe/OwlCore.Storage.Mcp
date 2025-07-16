using OwlCore.Storage.System.IO;
using System.Collections.Concurrent;

/// <summary>
/// Registry for custom storage protocols that maps protocol schemes to storage implementations
/// </summary>
public static class ProtocolRegistry
{
    private static readonly ConcurrentDictionary<string, IProtocolHandler> _protocolHandlers = new();
    
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
}
