using OwlCore.Storage;
using OwlCore.Kubo;
using Ipfs.Http;
/// <summary>
/// Protocol handler for IPFS MFS (Mutable File System)
/// </summary>
public class IpfsMfsProtocolHandler : IProtocolHandler
{
    public Task<IStorable> CreateRootAsync(string rootUri)
    {
        var client = new IpfsClient();
        return Task.FromResult<IStorable>(new MfsFolder("/", client));
    }

    public string CreateItemId(string parentId, string itemName)
    {
        return parentId == "ipfs-mfs://" ? $"ipfs-mfs://{itemName}" : $"{parentId}/{itemName}";
    }

    public async Task<object> GetDriveInfoAsync(string rootUri)
    {
        try
        {
            var client = new IpfsClient();
            var repoStats = await client.Stats.RepositoryAsync();
            
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
