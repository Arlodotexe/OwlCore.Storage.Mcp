using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using OwlCore.Storage;
using OwlCore.Storage.System.IO;
using OwlCore.Kubo;
using System.Text;
using System.Collections.Concurrent;
using ModelContextProtocol.Protocol;
using System.Diagnostics;
using Ipfs.Http;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(consoleLogOptions =>
{
    // Configure all logs to go to stderr
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

// Simple storage registry 
var storableRegistry = new ConcurrentDictionary<string, IStorable>();

await builder.Build().RunAsync();

[McpServerToolType]
public static class StorageReadTools
{
    private static readonly ConcurrentDictionary<string, IStorable> _storableRegistry = new();

    private static void EnsureStorableRegistered(string id)
    {
        if (_storableRegistry.ContainsKey(id)) return;

        if (id.StartsWith("ipfs-mfs://")) return; // MFS items are registered when first accessed

        // Handle regular filesystem paths
        if (Directory.Exists(id))
            _storableRegistry[id] = new SystemFolder(new DirectoryInfo(id));
        else if (File.Exists(id))
            _storableRegistry[id] = new SystemFile(new FileInfo(id));
    }

    private static string CreateMfsItemId(string parentId, string itemName)
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

    [McpServerTool, Description("Gets a specific item by name from a folder by ID or path. Returns the item's ID, name, and type.")]
    public static async Task<object?> GetFolderItemByName(string folderId, string itemName)
    {
        EnsureStorableRegistered(folderId);

        if (!_storableRegistry.TryGetValue(folderId, out var storable) || storable is not IFolder folder)
            throw new ArgumentException($"Folder with ID '{folderId}' not found");

        var namedItem = await folder.GetFirstByNameAsync(itemName);
        string itemId = folderId.StartsWith("ipfs-mfs://") ? CreateMfsItemId(folderId, itemName) : namedItem.Id;
        _storableRegistry[itemId] = namedItem;

        return new
        {
            id = itemId,
            name = namedItem.Name,
            type = namedItem switch
            {
                IFile => "file",
                IFolder => "folder",
                _ => "unknown"
            }
        };
    }

    [McpServerTool, Description("Reads the content of a file as text by file ID or path.")]
    public static async Task<string> ReadFileAsText(string fileId)
    {
        EnsureStorableRegistered(fileId);

        if (!_storableRegistry.TryGetValue(fileId, out var item) || item is not IFile file)
            throw new ArgumentException($"File with ID '{fileId}' not found");

        return await file.ReadTextAsync();
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
}

// [McpServerToolType]
public static class EchoTool
{
    [McpServerTool, Description("Echoes the message back to the client.")]
    public static string Echo(string message) => $"hello {message}";
}

[McpServerToolType]
public static class TimeTool
{
    [McpServerTool, Description("Gets the current date and time.")]
    public static string GetCurrentTime() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
}

// [McpServerToolType]
public static class UuidTool
{
    [McpServerTool, Description("Generates a new UUID.")]
    public static string GenerateUuid() => Guid.NewGuid().ToString();
}

// [McpServerToolType]
public static class CalculatorTool
{
    [McpServerTool, Description("Adds two numbers together.")]
    public static double Add(double a, double b) => a + b;

    [McpServerTool, Description("Subtracts the second number from the first.")]
    public static double Subtract(double a, double b) => a - b;

    [McpServerTool, Description("Multiplies two numbers.")]
    public static double Multiply(double a, double b) => a * b;

    [McpServerTool, Description("Divides the first number by the second.")]
    public static double Divide(double a, double b) => b != 0 ? a / b : throw new ArgumentException("Division by zero is not allowed");
}