using OwlCore.Storage;

namespace OwlCore.Storage.Mcp;

/// <summary>
/// Interface for protocol handlers that manage custom storage protocols
/// </summary>
public interface IProtocolHandler
{
    /// <summary>
    /// Creates a root storage item for this protocol if it supports browsable roots
    /// </summary>
    /// <param name="rootUri">The root URI for the protocol</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The root storage item, or null if this protocol doesn't have browsable roots</returns>
    Task<IStorable?> CreateRootAsync(string rootUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a storage item directly from a resource URI (for protocols that support individual resources)
    /// </summary>
    /// <param name="resourceUri">The resource URI</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The storage item, or null if this protocol doesn't support direct resource creation</returns>
    Task<IStorable?> CreateResourceAsync(string resourceUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an item ID within this protocol
    /// </summary>
    /// <param name="parentId">The parent item ID</param>
    /// <param name="itemName">The item name</param>
    /// <returns>The constructed item ID</returns>
    string CreateItemId(string parentId, string itemName);

    /// <summary>
    /// Gets drive information for this protocol (only for protocols that have browsable roots)
    /// </summary>
    /// <param name="rootUri">The root URI</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Drive information object, or null if this protocol doesn't have browsable roots</returns>
    Task<object?> GetDriveInfoAsync(string rootUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if this protocol supports browsable roots (shows up in GetAvailableDrives)
    /// </summary>
    /// <returns>True if it has browsable roots, false for resource-only protocols</returns>
    bool HasBrowsableRoot { get; }

    /// <summary>
    /// Checks if an item needs to be registered in the storable registry
    /// </summary>
    /// <param name="id">The item ID</param>
    /// <returns>True if registration is needed, false otherwise</returns>
    bool NeedsRegistration(string id);
}
