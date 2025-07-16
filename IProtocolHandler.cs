using OwlCore.Storage;
/// <summary>
/// Interface for protocol handlers that manage custom storage protocols
/// </summary>
public interface IProtocolHandler
{
    /// <summary>
    /// Creates a root storage item for this protocol
    /// </summary>
    /// <param name="rootUri">The root URI for the protocol</param>
    /// <returns>The root storage item</returns>
    Task<IStorable> CreateRootAsync(string rootUri);

    /// <summary>
    /// Creates an item ID within this protocol
    /// </summary>
    /// <param name="parentId">The parent item ID</param>
    /// <param name="itemName">The item name</param>
    /// <returns>The constructed item ID</returns>
    string CreateItemId(string parentId, string itemName);

    /// <summary>
    /// Gets drive information for this protocol
    /// </summary>
    /// <param name="rootUri">The root URI</param>
    /// <returns>Drive information object</returns>
    Task<object> GetDriveInfoAsync(string rootUri);

    /// <summary>
    /// Checks if an item needs to be registered in the storable registry
    /// </summary>
    /// <param name="id">The item ID</param>
    /// <returns>True if registration is needed, false otherwise</returns>
    bool NeedsRegistration(string id);
}
