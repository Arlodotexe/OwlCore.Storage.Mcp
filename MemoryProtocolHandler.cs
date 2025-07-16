using OwlCore.Storage;
using OwlCore.Storage.Memory;

/// <summary>
/// Protocol handler for in-memory storage using OwlCore.Storage.Memory
/// Useful for testing and temporary storage that doesn't persist
/// </summary>
public class MemoryProtocolHandler : IProtocolHandler
{
    private static readonly Dictionary<string, MemoryFolder> _memoryRoots = new();

    public bool HasBrowsableRoot => true; // Memory storage has browsable roots

    public Task<IStorable?> CreateRootAsync(string rootUri)
    {
        // Extract the memory space name from the URI (e.g., "memory://testspace" -> "testspace")
        var spaceName = ExtractSpaceName(rootUri);
        
        // Get or create a memory folder for this space
        if (!_memoryRoots.TryGetValue(spaceName, out var memoryRoot))
        {
            memoryRoot = new MemoryFolder(spaceName, spaceName);
            _memoryRoots[spaceName] = memoryRoot;
        }

        return Task.FromResult<IStorable?>(memoryRoot);
    }

    public Task<IStorable?> CreateResourceAsync(string resourceUri)
    {
        // Memory storage doesn't support direct resource creation - items are accessed through the filesystem
        return Task.FromResult<IStorable?>(null);
    }

    public string CreateItemId(string parentId, string itemName)
    {
        // Parse the parent URI to get the space name and path
        var (spaceName, parentPath) = ParseMemoryUri(parentId);
        
        // Construct the new path
        var newPath = string.IsNullOrEmpty(parentPath) ? itemName : $"{parentPath}/{itemName}";
        
        return $"memory://{spaceName}/{newPath}";
    }

    public async Task<object?> GetDriveInfoAsync(string rootUri)
    {
        var spaceName = ExtractSpaceName(rootUri);
        
        // Calculate approximate memory usage for this space
        var memoryUsage = await CalculateMemoryUsage(spaceName);
        
        return new
        {
            id = rootUri,
            name = $"Memory Storage: {spaceName}",
            type = "memory",
            driveType = "Ram",
            isReady = true,
            totalSize = -1L, // Memory doesn't have a fixed total size
            availableFreeSpace = -1L, // Memory availability varies
            estimatedUsage = memoryUsage
        };
    }

    public bool NeedsRegistration(string id)
    {
        // Memory items don't need explicit registration
        // They're created on-demand
        return false;
    }

    private string ExtractSpaceName(string rootUri)
    {
        // Extract space name from "memory://spacename" or "memory://spacename/path"
        var uri = new Uri(rootUri);
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        return segments.Length > 0 ? segments[0] : "default";
    }

    private (string spaceName, string path) ParseMemoryUri(string memoryUri)
    {
        var uri = new Uri(memoryUri);
        var pathParts = uri.AbsolutePath.Trim('/').Split('/');
        
        if (pathParts.Length == 0 || string.IsNullOrEmpty(pathParts[0]))
        {
            return ("default", "");
        }
        
        var spaceName = pathParts[0];
        var path = pathParts.Length > 1 ? string.Join("/", pathParts.Skip(1)) : "";
        
        return (spaceName, path);
    }

    private async Task<long> CalculateMemoryUsage(string spaceName)
    {
        if (!_memoryRoots.TryGetValue(spaceName, out var root))
        {
            return 0;
        }

        try
        {
            return await CalculateFolderSize(root);
        }
        catch
        {
            return 0;
        }
    }

    private async Task<long> CalculateFolderSize(IFolder folder)
    {
        long totalSize = 0;

        await foreach (var item in folder.GetItemsAsync())
        {
            if (item is IFile file)
            {
                try
                {
                    using var stream = await file.OpenReadAsync();
                    totalSize += stream.Length;
                }
                catch
                {
                    // Skip files that can't be read
                }
            }
            else if (item is IFolder subfolder)
            {
                totalSize += await CalculateFolderSize(subfolder);
            }
        }

        return totalSize;
    }
}
