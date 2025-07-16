using OwlCore.Storage;
using OwlCore.Kubo;
using Ipfs.Http;

/// <summary>
/// Protocol handler for IPNS (InterPlanetary Name System) content
/// This handler supports IPNS names that resolve to IPFS content - content can be files or browsable folders
/// </summary>
public class IpnsProtocolHandler : IProtocolHandler
{
    public bool HasBrowsableRoot => false; // IPNS doesn't have a single global root, but individual names can point to browsable folders

    public Task<IStorable?> CreateRootAsync(string rootUri)
    {
        // IPNS protocol doesn't have a single global browsable root
        // Individual IPNS names are accessed via CreateResourceAsync
        return Task.FromResult<IStorable?>(null);
    }

    public async Task<IStorable?> CreateResourceAsync(string resourceUri)
    {
        // Extract the IPNS name and path from the URI (e.g., "ipns://example.com/path" -> "example.com" and "/path")
        var (ipnsName, ipnsPath) = ExtractIpnsNameAndPath(resourceUri);
        if (string.IsNullOrEmpty(ipnsName))
            throw new ArgumentException($"Could not extract IPNS name from URI: {resourceUri}");

        var client = new IpfsClient();
        
        try
        {
            // Test if IPFS client is accessible first
            await client.Generic.IdAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"IPFS client not accessible: {ex.Message}", ex);
        }

        try
        {
            // Format the IPNS address as required by OwlCore.Kubo (must start with /ipns/)
            // Include the full path: /ipns/domain.com/path
            var ipnsAddress = $"/ipns/{ipnsName}{ipnsPath}";
            Console.WriteLine($"Creating IPNS folder with address: {ipnsAddress}");
            
            // IpnsFolder can represent either a folder or a file - it will determine the type based on the IPNS name
            var ipnsFolder = new IpnsFolder(ipnsAddress, client);
            return ipnsFolder;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create IPNS resource for '{ipnsName}{ipnsPath}': {ex.Message}", ex);
        }
    }

    public string CreateItemId(string parentId, string itemName)
    {
        // For IPNS paths within a folder, construct the path
        // e.g., "ipns://example.com" + "file.txt" -> "ipns://example.com/file.txt"
        return $"{parentId.TrimEnd('/')}/{itemName}";
    }

    public Task<object?> GetDriveInfoAsync(string rootUri)
    {
        // IPNS protocol doesn't have drive info since it doesn't have a global root
        // Individual IPNS resources don't have drive-like properties
        return Task.FromResult<object?>(null);
    }

    public bool NeedsRegistration(string id)
    {
        // IPNS resources should be registered when accessed
        return true;
    }

    private string ExtractIpnsName(string ipnsUri)
    {
        // Extract name from "ipns://example.com" or "ipns://example.com/path"
        if (!ipnsUri.StartsWith("ipns://"))
            return string.Empty;

        var pathPart = ipnsUri.Substring(7); // Remove "ipns://"
        var segments = pathPart.Split('/', StringSplitOptions.RemoveEmptyEntries);
        
        return segments.Length > 0 ? segments[0] : string.Empty;
    }

    private (string name, string path) ExtractIpnsNameAndPath(string ipnsUri)
    {
        // Extract name and path from "ipns://example.com/path" -> ("example.com", "/path")
        if (!ipnsUri.StartsWith("ipns://"))
            return (string.Empty, string.Empty);

        var pathPart = ipnsUri.Substring(7); // Remove "ipns://"
        var slashIndex = pathPart.IndexOf('/');
        
        if (slashIndex == -1)
        {
            // No path, just domain: "ipns://example.com"
            return (pathPart, string.Empty);
        }
        
        var name = pathPart.Substring(0, slashIndex);
        var path = pathPart.Substring(slashIndex); // Include the leading slash
        
        return (name, path);
    }
}
