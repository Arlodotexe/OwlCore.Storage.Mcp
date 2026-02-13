using OwlCore.Storage;
using OwlCore.Kubo;
using Ipfs.Http;
using OwlCore.Diagnostics;

namespace OwlCore.Storage.Mcp;

/// <summary>
/// Protocol handler for IPNS (InterPlanetary Name System) content
/// This handler supports IPNS names that resolve to IPFS content - content can be files or browsable folders
/// </summary>
public class IpnsProtocolHandler : IProtocolHandler
{
    private readonly IpfsClient _client;

    public IpnsProtocolHandler(IpfsClient client)
    {
        _client = client;
    }

    public bool HasBrowsableRoot => false; // IPNS doesn't have a single global root, but individual names can point to browsable folders

    public Task<IStorable?> CreateRootAsync(string rootUri, CancellationToken cancellationToken = default)
    {
        // IPNS protocol doesn't have a single global browsable root
        // Individual IPNS names are accessed via CreateResourceAsync
        return Task.FromResult<IStorable?>(null);
    }

    public async Task<IStorable?> CreateResourceAsync(string resourceUri, CancellationToken cancellationToken = default)
    {
        Logger.LogInformation($"[IPNS] CreateResourceAsync called with URI: {resourceUri}");
        
        // Extract the IPNS name and path from the URI (e.g., "ipns://example.com/path" -> "example.com" and "/path")
        var (ipnsName, ipnsPath) = ExtractIpnsNameAndPath(resourceUri);
        Logger.LogInformation($"[IPNS] Extracted name: '{ipnsName}', path: '{ipnsPath}'");
        
        if (string.IsNullOrEmpty(ipnsName))
            throw new ArgumentException($"Could not extract IPNS name from URI: {resourceUri}");

        try
        {
            Logger.LogInformation("[IPNS] Testing IPFS client accessibility...");
            // Test if IPFS client is accessible first
            await _client.Generic.IdAsync();
            Logger.LogInformation("[IPNS] IPFS client is accessible");
        }
        catch (Exception ex)
        {
            Logger.LogInformation($"[IPNS] IPFS client not accessible: {ex.Message}");
            throw new InvalidOperationException($"IPFS client not accessible: {ex.Message}", ex);
        }

        try
        {
            // Format the IPNS address as required by OwlCore.Kubo (must start with /ipns/)
            // Include the full path: /ipns/domain.com/path
            var ipnsAddress = $"/ipns/{ipnsName}{ipnsPath}";
            Logger.LogInformation($"[IPNS] Creating IPNS resource with address: {ipnsAddress}");
            
            // Determine if this should be a file or folder based on the path
            // If the path has a file extension or points to a specific file, create IpnsFile
            // Otherwise, create IpnsFolder
            if (!string.IsNullOrEmpty(ipnsPath) && HasFileExtension(ipnsPath))
            {
                Logger.LogInformation($"[IPNS] Path appears to be a file, creating IpnsFile");
                var ipnsFile = new IpnsFile(ipnsAddress, _client);
                Logger.LogInformation($"[IPNS] Successfully created IpnsFile of type: {ipnsFile.GetType().Name}");
                return ipnsFile;
            }
            else
            {
                Logger.LogInformation($"[IPNS] Path appears to be a folder, creating IpnsFolder");
                var ipnsFolder = new IpnsFolder(ipnsAddress, _client);
                Logger.LogInformation($"[IPNS] Successfully created IpnsFolder of type: {ipnsFolder.GetType().Name}");
                return ipnsFolder;
            }
        }
        catch (Exception ex)
        {
            Logger.LogInformation($"[IPNS] Failed to create IPNS resource: {ex.Message}");
            Logger.LogInformation($"[IPNS] Full exception: {ex}");
            throw new InvalidOperationException($"Failed to create IPNS resource for '{ipnsName}{ipnsPath}': {ex.Message}", ex);
        }
    }

    public string CreateItemId(string parentId, string itemName)
    {
        // For IPNS paths within a folder, construct the path
        // e.g., "ipns://example.com" + "file.txt" -> "ipns://example.com/file.txt"
        if (parentId.EndsWith("/"))
            return $"{parentId}{itemName}";
        return $"{parentId.TrimEnd('/')}/{itemName}";
    }

    public Task<DriveInfoResult?> GetDriveInfoAsync(string rootUri, CancellationToken cancellationToken = default)
    {
        // IPNS protocol doesn't have drive info since it doesn't have a global root
        // Individual IPNS resources don't have drive-like properties
        return Task.FromResult<DriveInfoResult?>(null);
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

    private bool HasFileExtension(string path)
    {
        // Check if the path has a file extension
        // Common file extensions that indicate this is a file, not a folder
        var fileName = Path.GetFileName(path);
        return !string.IsNullOrEmpty(fileName) && fileName.Contains('.') && !fileName.EndsWith('/');
    }
}
