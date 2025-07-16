using OwlCore.Storage;
using OwlCore.Storage.Memory;

namespace OwlCore.Storage.Mcp;

/// <summary>
/// Protocol handler for in-memory storage using OwlCore.Storage.Memory
/// Useful for testing and temporary storage that doesn't persist
/// </summary>
public class MemoryProtocolHandler : IProtocolHandler
{
    private static readonly MemoryFolder _memoryRoot = new MemoryFolder("memory", "memory");

    public bool HasBrowsableRoot => true; // Memory storage has a browsable root

    public Task<IStorable?> CreateRootAsync(string rootUri, CancellationToken cancellationToken = default)
    {
        // Always return the same memory root folder
        return Task.FromResult<IStorable?>(_memoryRoot);
    }

    public Task<IStorable?> CreateResourceAsync(string resourceUri, CancellationToken cancellationToken = default)
    {
        // Memory storage doesn't support direct resource creation - items are accessed through the filesystem
        return Task.FromResult<IStorable?>(null);
    }

    public string CreateItemId(string parentId, string itemName)
    {
        // Simple path construction for memory storage
        // Ensure proper URI format with double slashes
        if (parentId == "memory://")
        {
            return $"memory://{itemName}";
        }
        return $"{parentId.TrimEnd('/')}/{itemName}";
    }

    public Task<object?> GetDriveInfoAsync(string rootUri, CancellationToken cancellationToken = default)
    {
        // Simple drive info for memory storage
        return Task.FromResult<object?>(new
        {
            id = rootUri,
            name = "Memory Storage",
            type = "memory",
            driveType = "Ram",
            isReady = true,
            totalSize = -1L, // Memory doesn't have a fixed total size
            availableFreeSpace = -1L, // Memory availability varies
            estimatedUsage = 0L // Could calculate this if needed
        });
    }

    public bool NeedsRegistration(string id)
    {
        // Memory items don't need explicit registration
        return false;
    }
}
