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
        if (parentId == "mfs://") return $"mfs://{itemName}";
        if (parentId.EndsWith("/")) return $"{parentId}{itemName}";
        return $"{parentId}/{itemName}";
    }

    public async Task<DriveInfoResult?> GetDriveInfoAsync(string rootUri, CancellationToken cancellationToken = default)
    {
        try
        {
            var repoStats = await _client.Stats.RepositoryAsync(cancellationToken);
            
            return new DriveInfoResult(
                Id: rootUri,
                Name: "IPFS MFS Root",
                Type: "mfs",
                DriveType: "NetworkDrive",
                IsReady: true,
                TotalSize: (long)repoStats.StorageMax,
                AvailableFreeSpace: (long)(repoStats.StorageMax - repoStats.RepoSize)
            );
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
