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

    internal static void EnsureStorableRegistered(string id)
    {
        if (_storableRegistry.ContainsKey(id)) return;

        if (id.StartsWith("ipfs-mfs://")) return; // MFS items are registered when first accessed

        // Handle regular filesystem paths
        if (Directory.Exists(id))
            _storableRegistry[id] = new SystemFolder(new DirectoryInfo(id));
        else if (File.Exists(id))
            _storableRegistry[id] = new SystemFile(new FileInfo(id));
    }

    internal static string CreateMfsItemId(string parentId, string itemName)
    {
        return parentId == "ipfs-mfs://" ? $"ipfs-mfs://{itemName}" : $"{parentId}/{itemName}";
    }

    [McpServerTool, Description("Gets the paths of the available drives including IPFS MFS")]
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

        // Add IPFS MFS root
        const string mfsId = "ipfs-mfs://";
        try
        {
            var client = new IpfsClient();
            
            // Only register MFS root if not already registered
            if (!_storableRegistry.ContainsKey(mfsId))
            {
                var mfsRoot = new MfsFolder("/", client);
                _storableRegistry[mfsId] = mfsRoot;
            }
            
            // Get repository statistics from IPFS
            var repoStats = await client.Stats.RepositoryAsync();
            
            driveInfos.Add(new
            {
                id = mfsId,
                name = "IPFS MFS Root",
                type = "mfs",
                driveType = "NetworkDrive",
                isReady = true,
                totalSize = (long)repoStats.StorageMax,
                availableFreeSpace = (long)(repoStats.StorageMax - repoStats.RepoSize)
            });
        }
        catch (Exception ex)
        {
            // If IPFS is not available, log but don't fail
            Console.Error.WriteLine($"IPFS MFS not available: {ex.Message}");
        }

        return driveInfos.ToArray();
    }

    [McpServerTool, Description("Lists all items in a folder by ID or path. Returns array of items with their IDs, names, and types.")]
    public static async Task<object[]> GetFolderItems(string folderId)
    {
        EnsureStorableRegistered(folderId);

        if (!_storableRegistry.TryGetValue(folderId, out var registeredItem) || registeredItem is not IFolder folder)
            throw new ArgumentException($"Folder with ID '{folderId}' not found");

        var items = new List<object>();
        await foreach (var item in folder.GetItemsAsync())
        {
            string itemId = folderId == "ipfs-mfs://" ? CreateMfsItemId(folderId, item.Name) : item.Id;
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
        EnsureStorableRegistered(folderId);

        if (!_storableRegistry.TryGetValue(folderId, out var registeredItem) || registeredItem is not IFolder folder)
            throw new ArgumentException($"Folder with ID '{folderId}' not found");

        var files = new List<object>();
        await foreach (var file in folder.GetFilesAsync())
        {
            string fileId = folderId == "ipfs-mfs://" ? CreateMfsItemId(folderId, file.Name) : file.Id;
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
        EnsureStorableRegistered(folderId);

        if (!_storableRegistry.TryGetValue(folderId, out var registeredItem) || registeredItem is not IFolder folder)
            throw new ArgumentException($"Folder with ID '{folderId}' not found");

        var folders = new List<object>();
        await foreach (var subfolder in folder.GetFoldersAsync())
        {
            string subfolderId = folderId == "ipfs-mfs://" ? CreateMfsItemId(folderId, subfolder.Name) : subfolder.Id;
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
        EnsureStorableRegistered(folderId);

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
        EnsureStorableRegistered(startingItemId);

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
        EnsureStorableRegistered(fromFolderId);
        EnsureStorableRegistered(toItemId);

        if (!_storableRegistry.TryGetValue(fromFolderId, out var fromItem) || fromItem is not IFolder fromFolder)
            throw new ArgumentException($"From folder with ID '{fromFolderId}' not found or not a folder");

        if (!_storableRegistry.TryGetValue(toItemId, out var toItem) || toItem is not IStorableChild toChild)
            throw new ArgumentException($"To item with ID '{toItemId}' not found or not a child item");

        return await fromFolder.GetRelativePathToAsync(toChild);
    }

    [McpServerTool, Description("Reads the content of a file as bytes by file ID or path.")]
    public static async Task<byte[]> ReadFileAsBytes(string fileId)
    {
        EnsureStorableRegistered(fileId);

        if (!_storableRegistry.TryGetValue(fileId, out var item) || item is not IFile file)
            throw new ArgumentException($"File with ID '{fileId}' not found");

        return await file.ReadBytesAsync(CancellationToken.None);
    }

    [McpServerTool, Description("Reads the content of a file as text with specified encoding by file ID or path.")]
    public static async Task<string> ReadFileAsTextWithEncoding(string fileId, string encoding = "UTF-8")
    {
        EnsureStorableRegistered(fileId);

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

    [McpServerTool, Description("Opens a file stream for reading by file ID or path. Returns stream information.")]
    public static async Task<object> OpenFileForReading(string fileId)
    {
        EnsureStorableRegistered(fileId);

        if (!_storableRegistry.TryGetValue(fileId, out var item) || item is not IFile file)
            throw new ArgumentException($"File with ID '{fileId}' not found");

        using var stream = await file.OpenReadAsync();
        
        return new
        {
            id = fileId,
            name = file.Name,
            canRead = stream.CanRead,
            canWrite = stream.CanWrite,
            canSeek = stream.CanSeek,
            length = stream.CanSeek ? stream.Length : -1,
            position = stream.CanSeek ? stream.Position : -1
        };
    }

    [McpServerTool, Description("Gets information about a seen storable item by ID")]
    public static object? GetStorableInfo(string id)
    {
        if (!_storableRegistry.TryGetValue(id, out var storable))
            throw new ArgumentException($"Folder with ID '{id}' not found");

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
        EnsureStorableRegistered(itemId);

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
        EnsureStorableRegistered(folderId);

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
        EnsureStorableRegistered(itemId);

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
}
