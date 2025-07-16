using OwlCore.Storage;
using System.Collections.Concurrent;

/// <summary>
/// Protocol handler for mounting arbitrary IFolder instances with custom protocol schemes
/// </summary>
public class MountedFolderProtocolHandler : IProtocolHandler
{
    private readonly IFolder _mountedFolder;
    private readonly string _mountName;
    private readonly string _protocolScheme;

    public MountedFolderProtocolHandler(IFolder folder, string mountName, string protocolScheme)
    {
        _mountedFolder = folder ?? throw new ArgumentNullException(nameof(folder));
        _mountName = mountName ?? throw new ArgumentNullException(nameof(mountName));
        _protocolScheme = protocolScheme ?? throw new ArgumentNullException(nameof(protocolScheme));
    }

    public bool HasBrowsableRoot => true;

    public Task<IStorable?> CreateRootAsync(string rootUri)
    {
        // Return the mounted folder as the root
        return Task.FromResult<IStorable?>(_mountedFolder);
    }

    public Task<IStorable?> CreateResourceAsync(string resourceUri)
    {
        // Mounted folders use filesystem navigation, not direct resource access
        return Task.FromResult<IStorable?>(null);
    }

    public string CreateItemId(string parentId, string itemName)
    {
        // Use the protocol scheme to construct child IDs
        if (parentId == $"{_protocolScheme}://")
        {
            return $"{_protocolScheme}://{itemName}";
        }
        
        return $"{parentId}/{itemName}";
    }

    public async Task<object?> GetDriveInfoAsync(string rootUri)
    {
        // Return drive information for the mounted folder
        return new
        {
            id = rootUri,
            name = $"Mounted: {_mountName}",
            type = "mounted-folder",
            driveType = "NetworkDrive",
            isReady = true,
            totalSize = -1L, // Unknown for arbitrary folders
            availableFreeSpace = -1L // Unknown for arbitrary folders
        };
    }

    public bool NeedsRegistration(string id)
    {
        // Items should be registered when accessed
        return false;
    }

    /// <summary>
    /// Gets the underlying mounted folder
    /// </summary>
    public IFolder MountedFolder => _mountedFolder;

    /// <summary>
    /// Gets the mount name
    /// </summary>
    public string MountName => _mountName;

    /// <summary>
    /// Gets the protocol scheme
    /// </summary>
    public string ProtocolScheme => _protocolScheme;
}
