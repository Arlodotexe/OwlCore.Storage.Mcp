using ModelContextProtocol.Server;
using System.ComponentModel;
using OwlCore.Storage;
using OwlCore.Storage.System.IO;
using OwlCore.Kubo;
using System.Collections.Concurrent;
using System.Text;
using Ipfs.Http;

[McpServerToolType]
public static class StorageTools
{
    internal static readonly ConcurrentDictionary<string, IStorable> _storableRegistry = new();

    internal static async Task EnsureStorableRegistered(string id)
    {
        if (_storableRegistry.ContainsKey(id)) return;

        // Check if this is a custom protocol
        var protocolHandler = ProtocolRegistry.GetProtocolHandler(id);
        if (protocolHandler != null)
        {
            // Try to create a direct resource first (for protocols like HTTP)
            var resource = await protocolHandler.CreateResourceAsync(id);
            if (resource != null)
            {
                _storableRegistry[id] = resource;
                return;
            }

            // Let the protocol handler decide if registration is needed for filesystem-style protocols
            if (!protocolHandler.NeedsRegistration(id)) return;
        }

        // Handle regular filesystem paths
        if (Directory.Exists(id))
            _storableRegistry[id] = new SystemFolder(new DirectoryInfo(id));
        else if (File.Exists(id))
            _storableRegistry[id] = new SystemFile(new FileInfo(id));
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
}
