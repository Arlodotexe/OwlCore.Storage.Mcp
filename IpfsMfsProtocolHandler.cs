using OwlCore.Storage;
using OwlCore.Kubo;
using Ipfs.Http;

namespace OwlCore.Storage.Mcp;

/// <summary>
/// Protocol handler for IPFS MFS (Mutable File System)
/// </summary>
public class IpfsMfsProtocolHandler : IProtocolHandler
{
    private readonly IpfsClient _client;

    public IpfsMfsProtocolHandler(IpfsClient client)
    {
        _client = client;
    }

    public bool HasBrowsableRoot => true; // MFS has a browsable root filesystem

    public Task<IStorable?> CreateRootAsync(string rootUri, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IStorable?>(new MfsFolder("/", _client));
    }

    public Task<IStorable?> CreateResourceAsync(string resourceUri, CancellationToken cancellationToken = default)
    {
        // MFS doesn't support direct resource creation - items are accessed through the filesystem
        return Task.FromResult<IStorable?>(null);
    }

    public string CreateItemId(string parentId, string itemName)
    {
        return parentId == "mfs://" ? $"mfs://{itemName}" : $"{parentId}/{itemName}";
    }

    public async Task<object?> GetDriveInfoAsync(string rootUri, CancellationToken cancellationToken = default)
    {
        try
        {
            var repoStats = await _client.Stats.RepositoryAsync(cancellationToken);
            
            return new
            {
                id = rootUri,
                name = "IPFS MFS Root",
                type = "mfs",
                driveType = "NetworkDrive",
                isReady = true,
                totalSize = (long)repoStats.StorageMax,
                availableFreeSpace = (long)(repoStats.StorageMax - repoStats.RepoSize)
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"IPFS MFS not available: {ex.Message}", ex);
        }
    }

    public bool NeedsRegistration(string id)
    {
        // MFS items are registered when first accessed
        return false;
    }
}
