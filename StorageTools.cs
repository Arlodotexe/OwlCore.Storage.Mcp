using ModelContextProtocol.Server;
using System.ComponentModel;
using OwlCore.Storage;
using OwlCore.Storage.System.IO;
using OwlCore.Kubo;
using System.Collections.Concurrent;
using System.Text;
using Ipfs.Http;

namespace OwlCore.Storage.Mcp;

[McpServerToolType]
public static class StorageTools
{
    internal static readonly ConcurrentDictionary<string, IStorable> _storableRegistry = new();
    private static volatile bool _isInitialized = false;
    private static readonly SemaphoreSlim _initializationSemaphore = new(1, 1);
    
    static StorageTools()
    {
        // Don't do async work in static constructor - just ensure basic setup
    }

    /// <summary>
    /// Ensures that the storage system is fully initialized before proceeding
    /// </summary>
    private static async Task EnsureInitializedAsync()
    {
        if (_isInitialized) return;

        await _initializationSemaphore.WaitAsync();
        try
        {
            if (_isInitialized) return; // Double-check after acquiring lock

            // Initialize ProtocolRegistry and restore mounts
            await ProtocolRegistry.EnsureInitializedAsync();
            
            // Pre-register common protocol roots after mount restoration
            foreach (var protocolScheme in ProtocolRegistry.GetRegisteredProtocols())
            {
                var rootUri = $"{protocolScheme}://";
                var protocolHandler = ProtocolRegistry.GetProtocolHandler(rootUri);
                if (protocolHandler?.HasBrowsableRoot == true)
                {
                    try
                    {
                        var root = await protocolHandler.CreateRootAsync(rootUri);
                        if (root != null)
                        {
                            _storableRegistry[rootUri] = root;
                            Console.WriteLine($"Pre-registered protocol root: {rootUri}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to pre-register {rootUri}: {ex.Message}");
                    }
                }
            }

            _isInitialized = true;
        }
        finally
        {
            _initializationSemaphore.Release();
        }
    }

    internal static async Task EnsureStorableRegistered(string id)
    {
        // Ensure the storage system is fully initialized first
        await EnsureInitializedAsync();
        
        if (_storableRegistry.ContainsKey(id)) 
        {
            Console.WriteLine($"[STORAGE] Item already registered: {id}");
            return;
        }

        Console.WriteLine($"[STORAGE] Registering item: {id}");

        try
        {
            // Check if this is a custom protocol
            var protocolHandler = ProtocolRegistry.GetProtocolHandler(id);
            if (protocolHandler != null)
            {
                Console.WriteLine($"[STORAGE] Found protocol handler for {id}: {protocolHandler.GetType().Name}");
                
                // For filesystem protocols, try to create a root if this looks like a root URI
                if (protocolHandler.HasBrowsableRoot && id.EndsWith("://"))
                {
                    Console.WriteLine($"[STORAGE] Creating root for filesystem protocol: {id}");
                    var root = await protocolHandler.CreateRootAsync(id);
                    if (root != null)
                    {
                        _storableRegistry[id] = root;
                        Console.WriteLine($"[STORAGE] Successfully registered root: {id} as {root.GetType().Name}");
                        return;
                    }
                }

                // Try to create a direct resource first (for protocols like HTTP or specific resource URIs)
                Console.WriteLine($"[STORAGE] Creating resource for: {id}");
                var resource = await protocolHandler.CreateResourceAsync(id);
                if (resource != null)
                {
                    _storableRegistry[id] = resource;
                    Console.WriteLine($"[STORAGE] Successfully registered resource: {id} as {resource.GetType().Name}");
                    return;
                }

                // Let the protocol handler decide if registration is needed for filesystem-style protocols
                if (!protocolHandler.NeedsRegistration(id)) 
                {
                    // Special case: For mounted folder protocols, we still need to register specific items
                    // even if the protocol handler says registration isn't needed
                    if (protocolHandler is MountedFolderProtocolHandler mountHandler && !id.EndsWith("://"))
                    {
                        // This is a specific path within a mounted folder - we need to navigate to it
                        try
                        {
                            var relativePath = id.Substring($"{mountHandler.ProtocolScheme}://".Length);
                            if (!string.IsNullOrEmpty(relativePath))
                            {
                                var targetItem = await mountHandler.MountedFolder.GetItemByRelativePathAsync(relativePath);
                                _storableRegistry[id] = targetItem;
                                Console.WriteLine($"[STORAGE] Successfully registered mounted folder item: {id} as {targetItem.GetType().Name}");
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[STORAGE] Failed to navigate to mounted folder item {id}: {ex.Message}");
                            // Continue to regular "registration not needed" logic
                        }
                    }
                    
                    Console.WriteLine($"[STORAGE] Protocol handler says registration not needed for: {id}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[STORAGE] Error during protocol registration for {id}: {ex.Message}");
            Console.WriteLine($"[STORAGE] Full exception: {ex}");
            throw new InvalidOperationException($"Failed to register protocol resource '{id}': {ex.Message}", ex);
        }

        // Handle regular filesystem paths
        if (Directory.Exists(id))
        {
            _storableRegistry[id] = new SystemFolder(new DirectoryInfo(id));
            Console.WriteLine($"Registered system folder: {id}");
        }
        else if (File.Exists(id))
        {
            _storableRegistry[id] = new SystemFile(new FileInfo(id));
            Console.WriteLine($"Registered system file: {id}");
        }
        else
        {
            Console.WriteLine($"Could not register item: {id} (not found in filesystem and no protocol handler)");
        }
    }

    internal static string CreateCustomItemId(string parentId, string itemName)
    {
        return ProtocolRegistry.CreateCustomItemId(parentId, itemName);
    }

    [McpServerTool, Description("Gets the paths of the available drives including IPFS MFS, Memory storage, and other custom protocol roots")]
    public static async Task<object[]> GetAvailableDrives()
    {
        var driveInfos = new List<object>();

        // Get all available drives
        var drives = DriveInfo.GetDrives();
        foreach (var drive in drives)
        {
            try
            {
                // Create a SystemFolder for each drive
                var driveFolder = new SystemFolder(new DirectoryInfo(drive.RootDirectory.FullName));
                _storableRegistry[drive.RootDirectory.FullName] = driveFolder;
                
                // Add drive info to result
                driveInfos.Add(new
                {
                    id = drive.RootDirectory.FullName,
                    name = !string.IsNullOrEmpty(drive.VolumeLabel) ? $"{drive.Name} ({drive.VolumeLabel})" : drive.Name,
                    type = "drive",
                    driveType = drive.DriveType.ToString(),
                    isReady = drive.IsReady,
                    totalSize = drive.IsReady ? drive.TotalSize : 0,
                    availableFreeSpace = drive.IsReady ? drive.AvailableFreeSpace : 0
                });
            }
            catch
            {
                // Skip drives that aren't ready or throw errors
                continue;
            }
        }

        // Add custom protocol roots (only for protocols that have browsable roots)
        foreach (var protocolScheme in ProtocolRegistry.GetRegisteredProtocols())
        {
            var rootUri = $"{protocolScheme}://";
            try
            {
                var protocolHandler = ProtocolRegistry.GetProtocolHandler(rootUri);
                if (protocolHandler == null || !protocolHandler.HasBrowsableRoot) continue;

                // Only register root if not already registered
                if (!_storableRegistry.ContainsKey(rootUri))
                {
                    var protocolRoot = await protocolHandler.CreateRootAsync(rootUri);
                    if (protocolRoot != null)
                    {
                        _storableRegistry[rootUri] = protocolRoot;
                    }
                }
                
                // Get drive information from the protocol handler
                var driveInfo = await protocolHandler.GetDriveInfoAsync(rootUri);
                if (driveInfo != null)
                {
                    driveInfos.Add(driveInfo);
                }
            }
            catch (Exception ex)
            {
                // If protocol is not available, log but don't fail
                Console.Error.WriteLine($"{protocolScheme} protocol not available: {ex.Message}");
            }
        }

        return driveInfos.ToArray();
    }

    [McpServerTool, Description("Lists all items in a folder by ID or path. Works with local folders, IPFS MFS, and IPFS/IPNS folder hashes. Returns array of items with their IDs, names, and types.")]
    public static async Task<object[]> GetFolderItems(string folderId)
    {
        await EnsureStorableRegistered(folderId);

        if (!_storableRegistry.TryGetValue(folderId, out var registeredItem) || registeredItem is not IFolder folder)
            throw new ArgumentException($"Folder with ID '{folderId}' not found");

        var items = new List<object>();
        await foreach (var item in folder.GetItemsAsync())
        {
            string itemId = ProtocolRegistry.IsCustomProtocol(folderId) ? CreateCustomItemId(folderId, item.Name) : item.Id;
            _storableRegistry[itemId] = item;
            
            items.Add(new
            {
                id = itemId,
                name = item.Name,
                type = item switch
                {
                    IFile => "file",
                    IFolder => "folder",
                    _ => "unknown"
                }
            });
        }

        return items.ToArray();
    }

    [McpServerTool, Description("Lists only files in a folder by ID or path. Returns array of file items.")]
    public static async Task<object[]> GetFolderFiles(string folderId)
    {
        await EnsureStorableRegistered(folderId);

        if (!_storableRegistry.TryGetValue(folderId, out var registeredItem) || registeredItem is not IFolder folder)
            throw new ArgumentException($"Folder with ID '{folderId}' not found");

        var files = new List<object>();
        await foreach (var file in folder.GetFilesAsync())
        {
            string fileId = ProtocolRegistry.IsCustomProtocol(folderId) ? CreateCustomItemId(folderId, file.Name) : file.Id;
            _storableRegistry[fileId] = file;
            
            files.Add(new
            {
                id = fileId,
                name = file.Name,
                type = "file"
            });
        }

        return files.ToArray();
    }

    [McpServerTool, Description("Lists only folders in a folder by ID or path. Returns array of folder items.")]
    public static async Task<object[]> GetFolderSubfolders(string folderId)
    {
        await EnsureStorableRegistered(folderId);

        if (!_storableRegistry.TryGetValue(folderId, out var registeredItem) || registeredItem is not IFolder folder)
            throw new ArgumentException($"Folder with ID '{folderId}' not found");

        var folders = new List<object>();
        await foreach (var subfolder in folder.GetFoldersAsync())
        {
            string subfolderId = ProtocolRegistry.IsCustomProtocol(folderId) ? CreateCustomItemId(folderId, subfolder.Name) : subfolder.Id;
            _storableRegistry[subfolderId] = subfolder;
            
            folders.Add(new
            {
                id = subfolderId,
                name = subfolder.Name,
                type = "folder"
            });
        }

        return folders.ToArray();
    }

    [McpServerTool, Description("Recursively searches for an item by ID in a folder and all its subfolders.")]
    public static async Task<object?> FindItemRecursively(string folderId, string targetItemId)
    {
        await EnsureStorableRegistered(folderId);

        if (!_storableRegistry.TryGetValue(folderId, out var registeredItem) || registeredItem is not IFolder folder)
            throw new ArgumentException($"Folder with ID '{folderId}' not found");

        try
        {
            var foundItem = await folder.GetItemRecursiveAsync(targetItemId);
            _storableRegistry[foundItem.Id] = foundItem;

            return new
            {
                id = foundItem.Id,
                name = foundItem.Name,
                type = foundItem switch
                {
                    IFile => "file",
                    IFolder => "folder",
                    _ => "unknown"
                }
            };
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    [McpServerTool, Description("Navigates to an item using a relative path from a starting item.")]
    public static async Task<object> GetItemByRelativePath(string startingItemId, string relativePath)
    {
        await EnsureStorableRegistered(startingItemId);

        if (!_storableRegistry.TryGetValue(startingItemId, out var startingItem))
            throw new ArgumentException($"Starting item with ID '{startingItemId}' not found");

        var targetItem = await startingItem.GetItemByRelativePathAsync(relativePath);
        _storableRegistry[targetItem.Id] = targetItem;

        return new
        {
            id = targetItem.Id,
            name = targetItem.Name,
            type = targetItem switch
            {
                IFile => "file",
                IFolder => "folder",
                _ => "unknown"
            }
        };
    }

    [McpServerTool, Description("Gets the relative path from one folder to another item.")]
    public static async Task<string> GetRelativePath(string fromFolderId, string toItemId)
    {
        await EnsureStorableRegistered(fromFolderId);
        await EnsureStorableRegistered(toItemId);

        if (!_storableRegistry.TryGetValue(fromFolderId, out var fromItem) || fromItem is not IFolder fromFolder)
            throw new ArgumentException($"From folder with ID '{fromFolderId}' not found or not a folder");

        if (!_storableRegistry.TryGetValue(toItemId, out var toItem) || toItem is not IStorableChild toChild)
            throw new ArgumentException($"To item with ID '{toItemId}' not found or not a child item");

        return await fromFolder.GetRelativePathToAsync(toChild);
    }

    [McpServerTool, Description("Reads the content of a file as bytes by file ID, path, or URL (supports HTTP/HTTPS URLs, IPFS hashes, and IPNS names).")]
    public static async Task<byte[]> ReadFileAsBytes(string fileId)
    {
        await EnsureStorableRegistered(fileId);

        if (!_storableRegistry.TryGetValue(fileId, out var item) || item is not IFile file)
            throw new ArgumentException($"File with ID '{fileId}' not found");

        return await file.ReadBytesAsync(CancellationToken.None);
    }

    [McpServerTool, Description("Reads the content of a file as text with specified encoding by file ID, path, or URL (supports HTTP/HTTPS URLs, IPFS hashes, and IPNS names).")]
    public static async Task<string> ReadFileAsTextWithEncoding(string fileId, string encoding = "UTF-8")
    {
        await EnsureStorableRegistered(fileId);

        if (!_storableRegistry.TryGetValue(fileId, out var item) || item is not IFile file)
            throw new ArgumentException($"File with ID '{fileId}' not found");

        var textEncoding = encoding.ToUpperInvariant() switch
        {
            "UTF-8" or "UTF8" => Encoding.UTF8,
            "UTF-16" or "UTF16" => Encoding.Unicode,
            "ASCII" => Encoding.ASCII,
            "UNICODE" => Encoding.Unicode,
            _ => Encoding.UTF8
        };

        return await file.ReadTextAsync(textEncoding, CancellationToken.None);
    }

    [McpServerTool, Description("Gets information about a seen storable item by ID, path, or URL")]
    public static async Task<object?> GetStorableInfo(string id)
    {
        await EnsureStorableRegistered(id);

        if (!_storableRegistry.TryGetValue(id, out var storable))
            throw new ArgumentException($"Item with ID '{id}' not found");

        return new
        {
            id = storable.Id,
            name = storable.Name,
            type = storable switch
            {
                IFile => "file",
                IFolder => "folder",
                _ => "unknown"
            }
        };
    }

    [McpServerTool, Description("Gets the root folder of a storage item by tracing up the parent hierarchy.")]
    public static async Task<object?> GetRootFolder(string itemId)
    {
        await EnsureStorableRegistered(itemId);

        if (!_storableRegistry.TryGetValue(itemId, out var item) || item is not IStorableChild storableChild)
            throw new ArgumentException($"Item with ID '{itemId}' not found or not a child item");

        var rootFolder = await storableChild.GetRootAsync();
        if (rootFolder == null)
            return null;

        _storableRegistry[rootFolder.Id] = rootFolder;

        return new
        {
            id = rootFolder.Id,
            name = rootFolder.Name,
            type = "folder"
        };
    }

    [McpServerTool, Description("Gets a specific item by ID from a folder.")]
    public static async Task<object> GetItemById(string folderId, string itemId)
    {
        await EnsureStorableRegistered(folderId);

        if (!_storableRegistry.TryGetValue(folderId, out var registeredItem) || registeredItem is not IFolder folder)
            throw new ArgumentException($"Folder with ID '{folderId}' not found");

        try
        {
            var foundItem = await folder.GetItemAsync(itemId);
            _storableRegistry[foundItem.Id] = foundItem;

            return new
            {
                id = foundItem.Id,
                name = foundItem.Name,
                type = foundItem switch
                {
                    IFile => "file",
                    IFolder => "folder",
                    _ => "unknown"
                }
            };
        }
        catch (FileNotFoundException)
        {
            throw new ArgumentException($"Item with ID '{itemId}' not found in folder '{folder.Name}'");
        }
    }

    [McpServerTool, Description("Gets the parent folder of a storage item.")]
    public static async Task<object?> GetParentFolder(string itemId)
    {
        await EnsureStorableRegistered(itemId);

        if (!_storableRegistry.TryGetValue(itemId, out var item) || item is not IStorableChild storableChild)
            throw new ArgumentException($"Item with ID '{itemId}' not found or not a child item");

        var parentFolder = await storableChild.GetParentAsync();
        if (parentFolder == null)
            return null;

        _storableRegistry[parentFolder.Id] = parentFolder;

        return new
        {
            id = parentFolder.Id,
            name = parentFolder.Name,
            type = "folder"
        };
    }

    [McpServerTool, Description("Lists all supported storage protocols and their capabilities")]
    public static object[] GetSupportedProtocols()
    {
        var protocols = new List<object>();

        // Add built-in filesystem support
        protocols.Add(new
        {
            scheme = "file",
            name = "Local File System",
            type = "filesystem",
            hasBrowsableRoot = true,
            supportsDirectResources = false,
            description = "Local disk drives and folders"
        });

        // Add custom protocols
        foreach (var protocolScheme in ProtocolRegistry.GetRegisteredProtocols())
        {
            var rootUri = $"{protocolScheme}://";
            var protocolHandler = ProtocolRegistry.GetProtocolHandler(rootUri);
            
            if (protocolHandler != null)
            {
                protocols.Add(new
                {
                    scheme = protocolScheme,
                    name = protocolScheme.ToUpper() + " Protocol",
                    type = protocolHandler.HasBrowsableRoot ? "filesystem" : "resource",
                    hasBrowsableRoot = protocolHandler.HasBrowsableRoot,
                    supportsDirectResources = !protocolHandler.HasBrowsableRoot,
                    description = protocolScheme switch
                    {
                        "mfs" => "IPFS Mutable File System - browsable IPFS storage",
                        "memory" => "In-memory temporary storage for testing",
                        "http" or "https" => "HTTP/HTTPS web resources and files",
                        "ipfs" => "IPFS content addressed by hash - files or folders accessible by hash",
                        "ipns" => "IPNS names that resolve to IPFS content - files or folders accessible by name",
                        _ => $"Custom {protocolScheme} protocol"
                    }
                });
            }
        }

        return protocols.ToArray();
    }

    [McpServerTool, Description("Mounts an existing folder as a browsable drive with a custom protocol scheme. The folder will appear in GetAvailableDrives() and can be browsed like any other drive.")]
    public static async Task<object> MountFolder(
        [Description("The ID or path of the folder to mount")] string folderId,
        [Description("The custom protocol scheme to use (e.g., 'myproject', 'backup', 'archive')")] string protocolScheme,
        [Description("Display name for the mounted folder")] string mountName)
    {
        if (string.IsNullOrWhiteSpace(folderId))
            throw new ArgumentException("Folder ID cannot be null or empty", nameof(folderId));
        
        if (string.IsNullOrWhiteSpace(protocolScheme))
            throw new ArgumentException("Protocol scheme cannot be null or empty", nameof(protocolScheme));
        
        if (string.IsNullOrWhiteSpace(mountName))
            throw new ArgumentException("Mount name cannot be null or empty", nameof(mountName));

        // Validate protocol scheme format
        if (protocolScheme.Contains("://") || protocolScheme.Contains("/") || protocolScheme.Contains("\\"))
            throw new ArgumentException("Protocol scheme must be a simple identifier without special characters", nameof(protocolScheme));

        // Ensure the folder exists and is accessible
        await EnsureStorableRegistered(folderId);
        
        if (!_storableRegistry.TryGetValue(folderId, out var registeredItem) || registeredItem is not IFolder folder)
            throw new ArgumentException($"Folder with ID '{folderId}' not found", nameof(folderId));

        try
        {
            var rootUri = ProtocolRegistry.MountFolder(folder, protocolScheme, mountName, folderId);
            
            // Register the mounted root in our storable registry
            _storableRegistry[rootUri] = folder;
            
            // Save the mount settings to persist the configuration
            var mountSettings = ProtocolRegistry.GetMountSettings();
            if (mountSettings != null)
            {
                await mountSettings.SaveAsync();
            }
            
            return new
            {
                success = true,
                rootUri = rootUri,
                protocolScheme = protocolScheme,
                mountName = mountName,
                originalFolderId = folderId,
                message = $"Successfully mounted '{mountName}' as {protocolScheme}://"
            };
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Failed to mount folder: {ex.Message}", ex);
        }
    }

    [McpServerTool, Description("Unmounts a previously mounted folder, removing it from available drives.")]
    public static async Task<object> UnmountFolder(
        [Description("The protocol scheme of the mounted folder to unmount")] string protocolScheme)
    {
        if (string.IsNullOrWhiteSpace(protocolScheme))
            throw new ArgumentException("Protocol scheme cannot be null or empty", nameof(protocolScheme));

        var wasUnmounted = ProtocolRegistry.UnmountFolder(protocolScheme);
        
        if (wasUnmounted)
        {
            // Remove from storable registry as well
            var rootUri = $"{protocolScheme}://";
            _storableRegistry.TryRemove(rootUri, out _);
            
            // Save the mount settings to persist the change
            var mountSettings = ProtocolRegistry.GetMountSettings();
            if (mountSettings != null)
            {
                await mountSettings.SaveAsync();
            }
            
            return new
            {
                success = true,
                protocolScheme = protocolScheme,
                message = $"Successfully unmounted {protocolScheme}://"
            };
        }
        else
        {
            return new
            {
                success = false,
                protocolScheme = protocolScheme,
                message = $"Protocol scheme '{protocolScheme}' not found or is not a mounted folder"
            };
        }
    }

    [McpServerTool, Description("Lists all currently mounted folders and their information.")]
    public static async Task<object[]> GetMountedFolders()
    {
        await ProtocolRegistry.EnsureInitializedAsync();
        return ProtocolRegistry.GetMountedFolders();
    }

    [McpServerTool, Description("Renames a mounted folder's protocol scheme and/or display name. Preserves all existing references and dependencies.")]
    public static async Task<object> RenameMountedFolder(
        [Description("The current protocol scheme to rename")] string currentProtocolScheme,
        [Description("The new protocol scheme (optional, leave empty to keep current)")] string? newProtocolScheme = null,
        [Description("The new display name (optional, leave empty to keep current)")] string? newMountName = null)
    {
        if (string.IsNullOrWhiteSpace(currentProtocolScheme))
            throw new ArgumentException("Current protocol scheme cannot be null or empty", nameof(currentProtocolScheme));

        try
        {
            var newRootUri = ProtocolRegistry.RenameMountedFolder(currentProtocolScheme, newProtocolScheme, newMountName);
            
            // Update storable registry if protocol scheme changed
            if (!string.IsNullOrEmpty(newProtocolScheme) && newProtocolScheme != currentProtocolScheme)
            {
                var oldRootUri = $"{currentProtocolScheme}://";
                if (_storableRegistry.TryRemove(oldRootUri, out var folder))
                {
                    _storableRegistry[newRootUri] = folder;
                }
            }
            
            // Save the mount settings to persist the change
            var mountSettings = ProtocolRegistry.GetMountSettings();
            if (mountSettings != null)
            {
                await mountSettings.SaveAsync();
            }
            
            return new
            {
                success = true,
                oldProtocolScheme = currentProtocolScheme,
                newProtocolScheme = newProtocolScheme ?? currentProtocolScheme,
                newMountName = newMountName,
                newRootUri = newRootUri,
                message = $"Successfully renamed mount to {newRootUri}"
            };
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Failed to rename mounted folder: {ex.Message}", ex);
        }
    }
}
