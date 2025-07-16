using OwlCore.Storage.System.IO;
using OwlCore.Storage;
using System.Collections.Concurrent;

namespace OwlCore.Storage.Mcp;

/// <summary>
/// Registry for custom storage protocols that maps protocol schemes to storage implementations
/// </summary>
public static class ProtocolRegistry
{
    private static readonly ConcurrentDictionary<string, IProtocolHandler> _protocolHandlers = new();
    private static readonly ConcurrentDictionary<string, MountedFolderProtocolHandler> _mountedFolders = new();
    private static MountSettings? _mountSettings;
    
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
    /// Ensures mount settings are initialized and mounts are restored
    /// Call this once during application startup
    /// </summary>
    public static async Task EnsureInitializedAsync()
    {
        if (_mountSettings != null) return; // Already initialized
        
        await InitializeSettingsAndRestoreMountsAsync();
    }

    /// <summary>
    /// Initializes the settings system and restores persisted mounts
    /// </summary>
    private static async Task InitializeSettingsAndRestoreMountsAsync()
    {
        try
        {
            // Use a shared location for mount settings that syncs across MCP instances
            // Use the user's AppData\Roaming folder for cross-instance persistence
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var mcpSettingsPath = Path.Combine(appDataPath, "OwlCore", "Storage", "Mcp");
            Directory.CreateDirectory(mcpSettingsPath);
            
            var settingsFolder = new SystemFolder(new DirectoryInfo(mcpSettingsPath));
            
            // Initialize mount settings
            _mountSettings = new MountSettings(settingsFolder);
            await _mountSettings.LoadAsync();

            // Restore mounts in dependency order
            var mountsToRestore = _mountSettings.GetMountsInDependencyOrder();
            var restoredCount = 0;
            var failedMounts = new List<string>();

            Console.WriteLine($"Found {mountsToRestore.Count} persisted mounts to restore from {mcpSettingsPath}");

            foreach (var mountConfig in mountsToRestore)
            {
                try
                {
                    // Try to get the original folder
                    if (await TryRegisterStorableAsync(mountConfig.OriginalFolderId))
                    {
                        if (StorageTools._storableRegistry.TryGetValue(mountConfig.OriginalFolderId, out var registeredItem) && 
                            registeredItem is IFolder folder)
                        {
                            // Restore the mount without persisting again
                            var handler = new MountedFolderProtocolHandler(folder, mountConfig.MountName, mountConfig.ProtocolScheme);
                            
                            _protocolHandlers[mountConfig.ProtocolScheme] = handler;
                            _mountedFolders[mountConfig.ProtocolScheme] = handler;
                            
                            // CRITICAL: Register the mounted root in the storage registry BEFORE continuing
                            // This ensures dependent mounts can find the protocol when they try to register
                            var rootUri = $"{mountConfig.ProtocolScheme}://";
                            StorageTools._storableRegistry[rootUri] = folder;
                            
                            restoredCount++;
                            Console.WriteLine($"Restored mount: {mountConfig.ProtocolScheme}:// -> {mountConfig.OriginalFolderId}");
                        }
                        else
                        {
                            failedMounts.Add($"{mountConfig.ProtocolScheme} (not a folder)");
                        }
                    }
                    else
                    {
                        failedMounts.Add($"{mountConfig.ProtocolScheme} (folder not accessible)");
                    }
                }
                catch (Exception ex)
                {
                    failedMounts.Add($"{mountConfig.ProtocolScheme} ({ex.Message})");
                }
            }

            Console.WriteLine($"Mount restoration complete: {restoredCount} restored, {failedMounts.Count} failed");
            if (failedMounts.Any())
            {
                Console.WriteLine($"Failed mounts: {string.Join(", ", failedMounts)}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error initializing mount settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Tries to register a storable item if it's not already registered
    /// </summary>
    private static async Task<bool> TryRegisterStorableAsync(string folderId)
    {
        try
        {
            await StorageTools.EnsureStorableRegistered(folderId);
            return StorageTools._storableRegistry.ContainsKey(folderId);
        }
        catch
        {
            return false;
        }
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
    /// <param name="originalFolderId">The original folder ID to preserve in settings (optional)</param>
    /// <returns>The root URI for the mounted folder</returns>
    /// <exception cref="ArgumentException">Thrown if the protocol scheme is already registered</exception>
    public static string MountFolder(IFolder folder, string protocolScheme, string mountName, string? originalFolderId = null)
    {
        if (string.IsNullOrWhiteSpace(protocolScheme))
            throw new ArgumentException("Protocol scheme cannot be null or empty", nameof(protocolScheme));
        
        if (string.IsNullOrWhiteSpace(mountName))
            throw new ArgumentException("Mount name cannot be null or empty", nameof(mountName));

        // Ensure protocol scheme doesn't conflict with existing protocols
        if (_protocolHandlers.ContainsKey(protocolScheme))
            throw new ArgumentException($"Protocol scheme '{protocolScheme}' is already registered", nameof(protocolScheme));

        // Check for potential cycles before mounting
        if (folder is IStorableChild childFolder && WouldCreateCycle(childFolder.Id, protocolScheme))
            throw new ArgumentException($"Mounting '{protocolScheme}' would create a cycle in the mount graph", nameof(protocolScheme));

        var handler = new MountedFolderProtocolHandler(folder, mountName, protocolScheme);
        var rootUri = $"{protocolScheme}://";
        
        _protocolHandlers[protocolScheme] = handler;
        _mountedFolders[protocolScheme] = handler;
        
        // Persist the mount configuration
        if (_mountSettings != null)
        {
            // Use the original folder ID if provided, otherwise fall back to the folder's ID
            var folderIdToStore = originalFolderId ?? (folder is IStorableChild storableChild ? storableChild.Id : folder.Id);
            _mountSettings.AddOrUpdateMount(protocolScheme, folderIdToStore, mountName);
        }
        
        return rootUri;
    }

    /// <summary>
    /// Checks if mounting a folder would create a cycle in the mount dependency graph
    /// </summary>
    /// <param name="sourceFolderId">The ID of the folder being mounted</param>
    /// <param name="targetProtocolScheme">The protocol scheme it would be mounted as</param>
    /// <returns>True if a cycle would be created, false otherwise</returns>
    private static bool WouldCreateCycle(string sourceFolderId, string targetProtocolScheme)
    {
        var visited = new HashSet<string>();
        var recursionStack = new HashSet<string>();
        
        return HasCycleDfs(sourceFolderId, targetProtocolScheme, visited, recursionStack);
    }

    /// <summary>
    /// Depth-first search to detect cycles in the mount dependency graph
    /// </summary>
    private static bool HasCycleDfs(string currentId, string targetProtocolScheme, HashSet<string> visited, HashSet<string> recursionStack)
    {
        // If we're trying to mount something that would point back to the target protocol, that's a cycle
        var targetUri = $"{targetProtocolScheme}://";
        if (currentId == targetUri)
            return true;

        if (recursionStack.Contains(currentId))
            return true;

        if (visited.Contains(currentId))
            return false;

        visited.Add(currentId);
        recursionStack.Add(currentId);

        // Check if current ID is from a mounted protocol
        var scheme = ExtractScheme(currentId);
        if (scheme != null && _mountedFolders.TryGetValue(scheme, out var handler))
        {
            // Get the original folder this mount points to
            if (handler.MountedFolder is IStorableChild child)
            {
                if (HasCycleDfs(child.Id, targetProtocolScheme, visited, recursionStack))
                    return true;
            }
        }

        recursionStack.Remove(currentId);
        return false;
    }

    /// <summary>
    /// Resolves a mounted path to its final underlying storage location
    /// </summary>
    /// <param name="mountedPath">The mounted path to resolve</param>
    /// <param name="maxDepth">Maximum resolution depth to prevent infinite loops</param>
    /// <returns>The final resolved path, or the original path if not a mount</returns>
    public static string ResolveMountPath(string mountedPath, int maxDepth = 10)
    {
        var currentPath = mountedPath;
        var depth = 0;
        
        while (depth < maxDepth)
        {
            var scheme = ExtractScheme(currentPath);
            if (scheme == null || !_mountedFolders.TryGetValue(scheme, out var handler))
                break;

            // Get the underlying folder's ID
            if (handler.MountedFolder is IStorableChild child)
            {
                // Replace the mount scheme with the underlying path
                var remainingPath = currentPath.Substring($"{scheme}://".Length);
                currentPath = string.IsNullOrEmpty(remainingPath) ? child.Id : $"{child.Id}/{remainingPath}";
            }
            else
            {
                break;
            }

            depth++;
        }

        if (depth >= maxDepth)
            throw new InvalidOperationException($"Mount resolution exceeded maximum depth of {maxDepth} for path: {mountedPath}");

        return currentPath;
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
        
        // Remove from persistent settings
        _mountSettings?.RemoveMount(protocolScheme);
        
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

    /// <summary>
    /// Gets the mount settings instance for external saving operations
    /// </summary>
    internal static MountSettings? GetMountSettings()
    {
        return _mountSettings;
    }

    /// <summary>
    /// Renames a mounted folder's protocol scheme and/or display name atomically
    /// </summary>
    /// <param name="currentProtocolScheme">The current protocol scheme to rename</param>
    /// <param name="newProtocolScheme">The new protocol scheme (optional, keeps current if null)</param>
    /// <param name="newMountName">The new display name (optional, keeps current if null)</param>
    /// <returns>The new root URI after renaming</returns>
    /// <exception cref="ArgumentException">Thrown if the mount doesn't exist or new scheme conflicts</exception>
    public static string RenameMountedFolder(string currentProtocolScheme, string? newProtocolScheme = null, string? newMountName = null)
    {
        if (string.IsNullOrWhiteSpace(currentProtocolScheme))
            throw new ArgumentException("Current protocol scheme cannot be null or empty", nameof(currentProtocolScheme));

        // Must be a mounted folder, not a built-in protocol
        if (!_mountedFolders.TryGetValue(currentProtocolScheme, out var currentHandler))
            throw new ArgumentException($"Protocol scheme '{currentProtocolScheme}' is not a mounted folder", nameof(currentProtocolScheme));

        // Use current values if new ones not provided
        var finalProtocolScheme = newProtocolScheme ?? currentProtocolScheme;
        var finalMountName = newMountName ?? currentHandler.MountName;

        // If neither changed, nothing to do
        if (finalProtocolScheme == currentProtocolScheme && finalMountName == currentHandler.MountName)
            return $"{currentProtocolScheme}://";

        // Validate new protocol scheme if it's changing
        if (finalProtocolScheme != currentProtocolScheme)
        {
            if (string.IsNullOrWhiteSpace(finalProtocolScheme))
                throw new ArgumentException("New protocol scheme cannot be null or empty", nameof(newProtocolScheme));

            if (finalProtocolScheme.Contains("://") || finalProtocolScheme.Contains("/") || finalProtocolScheme.Contains("\\"))
                throw new ArgumentException("Protocol scheme must be a simple identifier without special characters", nameof(newProtocolScheme));

            // Ensure new scheme doesn't conflict with existing protocols
            if (_protocolHandlers.ContainsKey(finalProtocolScheme))
                throw new ArgumentException($"Protocol scheme '{finalProtocolScheme}' is already registered", nameof(newProtocolScheme));
        }

        // Create new handler with updated information
        var newHandler = new MountedFolderProtocolHandler(currentHandler.MountedFolder, finalMountName, finalProtocolScheme);
        var newRootUri = $"{finalProtocolScheme}://";

        // Perform atomic update
        if (finalProtocolScheme != currentProtocolScheme)
        {
            // Protocol scheme is changing - need to update both registries
            _protocolHandlers.TryRemove(currentProtocolScheme, out _);
            _mountedFolders.TryRemove(currentProtocolScheme, out _);
            
            _protocolHandlers[finalProtocolScheme] = newHandler;
            _mountedFolders[finalProtocolScheme] = newHandler;
        }
        else
        {
            // Only display name is changing - update in place
            _protocolHandlers[currentProtocolScheme] = newHandler;
            _mountedFolders[currentProtocolScheme] = newHandler;
        }

        // Update persistent settings
        _mountSettings?.RenameMount(currentProtocolScheme, newProtocolScheme, newMountName);

        return newRootUri;
    }

    /// <summary>
    /// Substitutes long IDs with shorter mount aliases where possible, to make them more manageable for smaller models
    /// </summary>
    /// <param name="fullId">The full ID to potentially substitute</param>
    /// <returns>The shortest possible alias ID, or the original ID if no suitable mount exists</returns>
    public static string SubstituteWithMountAlias(string fullId)
    {
        if (string.IsNullOrWhiteSpace(fullId))
            return fullId;

        // Find the best (longest matching) mount that can substitute part of this ID
        string bestAlias = fullId;
        int longestMatchLength = 0;

        foreach (var mount in _mountedFolders.Values)
        {
            if (mount.MountedFolder is not IStorableChild mountedChild)
                continue;

            var mountedId = mountedChild.Id;
            
            // Check if the full ID starts with the mounted folder's ID
            if (fullId.StartsWith(mountedId, StringComparison.OrdinalIgnoreCase))
            {
                var matchLength = mountedId.Length;
                
                // Only substitute if this mount provides a longer match than what we already found
                if (matchLength > longestMatchLength)
                {
                    var remainingPart = fullId.Substring(matchLength);
                    // Ensure proper path separator handling
                    var aliasId = string.IsNullOrEmpty(remainingPart) ? 
                        $"{mount.ProtocolScheme}://" : 
                        $"{mount.ProtocolScheme}://{remainingPart.TrimStart('/', '\\')}";
                    
                    bestAlias = aliasId;
                    longestMatchLength = matchLength;
                }
            }
        }

        // If we found a substitution, recursively check if it can be further shortened
        if (bestAlias != fullId)
        {
            var furtherSubstituted = SubstituteWithMountAlias(bestAlias);
            if (furtherSubstituted != bestAlias)
                return furtherSubstituted;
        }

        return bestAlias;
    }

    /// <summary>
    /// Resolves a potentially aliased ID back to its full underlying ID
    /// </summary>
    /// <param name="aliasId">The potentially aliased ID to resolve</param>
    /// <param name="maxDepth">Maximum resolution depth to prevent infinite loops</param>
    /// <returns>The fully resolved underlying ID</returns>
    public static string ResolveAliasToFullId(string aliasId, int maxDepth = 10)
    {
        if (string.IsNullOrWhiteSpace(aliasId))
            return aliasId;

        var currentId = aliasId;
        var depth = 0;
        
        while (depth < maxDepth)
        {
            var scheme = ExtractScheme(currentId);
            if (scheme == null || !_mountedFolders.TryGetValue(scheme, out var handler))
                break;

            // Get the underlying folder's ID
            if (handler.MountedFolder is not IStorableChild child)
                break;

            // Replace the mount scheme with the underlying ID
            var remainingPath = currentId.Substring($"{scheme}://".Length);
            
            // Normalize path separators and combine properly
            if (string.IsNullOrEmpty(remainingPath))
            {
                currentId = child.Id;
            }
            else
            {
                // Normalize path separators to match the underlying system
                var normalizedPath = remainingPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                currentId = Path.Combine(child.Id, normalizedPath);
            }

            depth++;
        }

        if (depth >= maxDepth)
            throw new InvalidOperationException($"Alias resolution exceeded maximum depth of {maxDepth} for ID: {aliasId}");

        return currentId;
    }
}
