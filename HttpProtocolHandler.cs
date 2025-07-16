using OwlCore.Storage;
using OwlCore.Storage.System.Net.Http;

namespace OwlCore.Storage.Mcp;

/// <summary>
/// Protocol handler for HTTP file resources
/// This handler supports individual HTTP file URLs but doesn't provide a browsable root
/// </summary>
public class HttpProtocolHandler : IProtocolHandler
{
    private static readonly HttpClient _httpClient = new();

    public bool HasBrowsableRoot => false; // HTTP doesn't have a browsable root

    public Task<IStorable?> CreateRootAsync(string rootUri)
    {
        // HTTP protocol doesn't have a browsable root
        return Task.FromResult<IStorable?>(null);
    }

    public Task<IStorable?> CreateResourceAsync(string resourceUri)
    {
        try
        {
            // Create an HttpFile for the given URL
            var httpFile = new HttpFile(resourceUri, _httpClient);
            return Task.FromResult<IStorable?>(httpFile);
        }
        catch
        {
            // Return null if we can't create the HTTP file
            return Task.FromResult<IStorable?>(null);
        }
    }

    public string CreateItemId(string parentId, string itemName)
    {
        // For HTTP, we don't really have parent/child relationships in the traditional sense
        // This would be used if we were constructing URLs, but typically HTTP files are accessed directly
        return $"{parentId.TrimEnd('/')}/{itemName}";
    }

    public Task<object?> GetDriveInfoAsync(string rootUri)
    {
        // HTTP protocol doesn't have drive info since it doesn't have a browsable root
        return Task.FromResult<object?>(null);
    }

    public bool NeedsRegistration(string id)
    {
        // HTTP files should be registered when accessed
        return true;
    }
}
