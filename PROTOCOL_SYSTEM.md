# Protocol Registry System

The MCP Server now supports a generalized protocol system that allows you to easily add custom storage protocols beyond the built-in file system and IPFS MFS support.

## Overview

The protocol system consists of:

1. **ProtocolRegistry** - Central registry for managing protocol handlers
2. **IProtocolHandler** - Interface that protocol implementations must implement
3. **Built-in handlers** - Currently includes IPFS MFS handler

## How to Add a New Protocol

### 1. Create a Protocol Handler

Implement the `IProtocolHandler` interface:

```csharp
public class MyCustomProtocolHandler : IProtocolHandler
{
    public Task<IStorable> CreateRootAsync(string rootUri)
    {
        // Create and return the root storage item for your protocol
        var customRoot = new MyCustomStorageRoot(rootUri);
        return Task.FromResult<IStorable>(customRoot);
    }

    public string CreateItemId(string parentId, string itemName)
    {
        // Define how item IDs are constructed in your protocol
        return parentId == "mycustom://" ? $"mycustom://{itemName}" : $"{parentId}/{itemName}";
    }

    public async Task<object> GetDriveInfoAsync(string rootUri)
    {
        // Return drive information for your protocol
        return new
        {
            id = rootUri,
            name = "My Custom Storage",
            type = "custom",
            driveType = "NetworkDrive",
            isReady = true,
            totalSize = 1000000L,
            availableFreeSpace = 500000L
        };
    }

    public bool NeedsRegistration(string id)
    {
        // Return true if items need to be explicitly registered
        // Return false if items are registered when first accessed
        return true;
    }
}
```

### 2. Register the Protocol

Add the protocol to the registry in `ProtocolRegistry.cs`:

```csharp
static ProtocolRegistry()
{
    // Register built-in protocol handlers
    RegisterProtocol("mfs", new IpfsMfsProtocolHandler());
    
    // Add your custom protocol
    RegisterProtocol("mycustom", new MyCustomProtocolHandler());
}
```

### 3. Implement Storage Interfaces

Your custom storage classes need to implement the appropriate OwlCore.Storage interfaces:

- `IStorable` - Base interface for all storage items
- `IFolder` - For directory-like containers
- `IFile` - For file items
- `IModifiableFolder` - For folders that can be modified
- `IStorableChild` - For items that have a parent

## Example: Azure Blob Storage Protocol

Here's a conceptual example of how you might implement Azure Blob Storage:

```csharp
public class AzureBlobProtocolHandler : IProtocolHandler
{
    public Task<IStorable> CreateRootAsync(string rootUri)
    {
        // Parse connection string from URI
        var connectionString = ExtractConnectionString(rootUri);
        var containerName = ExtractContainerName(rootUri);
        
        var blobContainer = new AzureBlobContainer(connectionString, containerName);
        return Task.FromResult<IStorable>(blobContainer);
    }

    public string CreateItemId(string parentId, string itemName)
    {
        var containerPath = ExtractContainerPath(parentId);
        return $"azure-blob://{containerPath}/{itemName}";
    }

    public async Task<object> GetDriveInfoAsync(string rootUri)
    {
        // Get container properties from Azure
        var properties = await GetContainerPropertiesAsync(rootUri);
        
        return new
        {
            id = rootUri,
            name = $"Azure Blob Container: {properties.Name}",
            type = "azure-blob",
            driveType = "NetworkDrive",
            isReady = true,
            totalSize = properties.Quota ?? -1,
            availableFreeSpace = -1 // Azure doesn't provide this directly
        };
    }

    public bool NeedsRegistration(string id) => false;
}
```

## Benefits of the Generalized System

1. **Extensibility** - Easy to add new storage protocols
2. **Consistency** - All protocols follow the same patterns
3. **Maintainability** - Protocol-specific logic is isolated
4. **Testability** - Each protocol can be tested independently

## Migration from Hardcoded IPFS

The system automatically handles the migration from the old hardcoded `mfs://` checks to the new generalized approach:

- `StartsWith("mfs://")` → `ProtocolRegistry.IsCustomProtocol(id)`
- `CreateMfsItemId()` → `CreateCustomItemId()`
- Protocol-specific drive info → `protocolHandler.GetDriveInfoAsync()`

All existing IPFS MFS functionality remains unchanged from the user perspective.
