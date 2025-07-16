using OwlCore.Storage;
using OwlCore.Kubo;
using Ipfs.Http;

/// <summary>
/// Protocol handler for IPFS content addressed by hash
/// This handler supports individual IPFS content by hash - content can be files or browsable folders
/// </summary>
public class IpfsProtocolHandler : IProtocolHandler
{
    public bool HasBrowsableRoot => false; // IPFS doesn't have a single global root, but individual hashes can point to browsable folders

    public Task<IStorable?> CreateRootAsync(string rootUri)
    {
        // IPFS protocol doesn't have a single global browsable root
        // Individual IPFS hashes are accessed via CreateResourceAsync
        return Task.FromResult<IStorable?>(null);
    }

    public async Task<IStorable?> CreateResourceAsync(string resourceUri)
    {
        try
        {
            // Extract the IPFS hash from the URI (e.g., "ipfs://QmHash" -> "QmHash")
            var hash = ExtractIpfsHash(resourceUri);
            if (string.IsNullOrEmpty(hash))
            {
                Console.WriteLine($"Could not extract IPFS hash from URI: {resourceUri}");
                return null;
            }

            var client = new IpfsClient();
            
            // Test IPFS client connectivity
            try
            {
                await client.Generic.IdAsync();
                Console.WriteLine("IPFS client connectivity test passed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"IPFS client connectivity test failed: {ex.Message}");
                throw new InvalidOperationException($"Cannot connect to IPFS node: {ex.Message}", ex);
            }

            // IpfsFolder can represent either a folder or a file - it will determine the type based on the hash
            var ipfsFolder = new IpfsFolder(hash, client);
            Console.WriteLine($"Successfully created IpfsFolder for hash: {hash}");
            return ipfsFolder;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating IPFS resource for {resourceUri}: {ex.Message}");
            throw;
        }
    }

    public string CreateItemId(string parentId, string itemName)
    {
        // For IPFS paths within a folder, construct the path
        // e.g., "ipfs://QmHash" + "file.txt" -> "ipfs://QmHash/file.txt"
        return $"{parentId.TrimEnd('/')}/{itemName}";
    }

    public Task<object?> GetDriveInfoAsync(string rootUri)
    {
        // IPFS protocol doesn't have drive info since it doesn't have a global root
        // Individual IPFS resources don't have drive-like properties
        return Task.FromResult<object?>(null);
    }

    public bool NeedsRegistration(string id)
    {
        // IPFS resources should be registered when accessed
        return true;
    }

    private string ExtractIpfsHash(string ipfsUri)
    {
        // Extract hash from "ipfs://QmHash" or "ipfs://QmHash/path"
        if (!ipfsUri.StartsWith("ipfs://"))
            return string.Empty;

        var pathPart = ipfsUri.Substring(7); // Remove "ipfs://"
        var segments = pathPart.Split('/', StringSplitOptions.RemoveEmptyEntries);
        
        return segments.Length > 0 ? segments[0] : string.Empty;
    }
}
