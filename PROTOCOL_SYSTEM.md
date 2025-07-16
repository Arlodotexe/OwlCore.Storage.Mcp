# Protocol Registry System

The MCP Server now supports a generalized protocol system that allows you to easily add custom storage protocols beyond the built-in file system and IPFS MFS support.

## Overview

The protocol system consists of:

1. **ProtocolRegistry** - Central registry for managing protocol handlers
2. **IProtocolHandler** - Interface that protocol implementations must implement
3. **Built-in handlers** - Currently includes IPFS MFS, HTTP/HTTPS, and Memory storage handlers

## Protocol Types

The system now supports two types of protocols:

### Filesystem Protocols
These protocols have browsable roots that appear alongside mounted storage in `GetAvailableDrives()`:
- **IPFS MFS** (`mfs://`) - Mutable File System for IPFS
- **Memory Storage** (`memory://`) - In-memory temporary storage
- **Azure Blob** (`azure-blob://`) - Container-based blob storage (example)
- **S3** (`s3://`) - Amazon S3 bucket storage (example)

### Resource Protocols  
These protocols address individual resources directly without browsable roots:
- **HTTP/HTTPS** (`http://`, `https://`) - Individual web resources and files
- **IPFS** (`ipfs://`) - IPFS content addressed by hash (files or folders)
- **IPNS** (`ipns://`) - IPNS names that resolve to IPFS content (files or folders)

## How to Add a New Protocol

### 1. Create a Protocol Handler

Implement the `IProtocolHandler` interface:

```csharp
public class MyCustomProtocolHandler : IProtocolHandler
{
    // Indicates if this protocol has browsable roots (shows up in GetAvailableDrives)
    public bool HasBrowsableRoot => true; // or false for resource-only protocols

    public Task<IStorable?> CreateRootAsync(string rootUri)
    {
        // For filesystem protocols: Create and return the root storage item
        // For resource protocols: Return null
        if (!HasBrowsableRoot) return Task.FromResult<IStorable?>(null);
        
        var customRoot = new MyCustomStorageRoot(rootUri);
        return Task.FromResult<IStorable?>(customRoot);
    }

    public Task<IStorable?> CreateResourceAsync(string resourceUri)
    {
        // For resource protocols: Create storage item directly from URI
        // For filesystem protocols: Return null (items accessed via filesystem navigation)
        if (HasBrowsableRoot) return Task.FromResult<IStorable?>(null);
        
        var resource = new MyCustomResource(resourceUri);
        return Task.FromResult<IStorable?>(resource);
    }

    public string CreateItemId(string parentId, string itemName)
    {
        // Define how item IDs are constructed in your protocol
        return parentId == "mycustom://" ? $"mycustom://{itemName}" : $"{parentId}/{itemName}";
    }

    public async Task<object?> GetDriveInfoAsync(string rootUri)
    {
        // Return drive information for filesystem protocols, null for resource protocols
        if (!HasBrowsableRoot) return null;
        
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
    RegisterProtocol("http", new HttpProtocolHandler());
    RegisterProtocol("https", new HttpProtocolHandler());
    
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

## Built-in Examples

### HTTP File Protocol

The HTTP protocol handler demonstrates resource-based protocols:

```csharp
public class HttpProtocolHandler : IProtocolHandler
{
    public bool HasBrowsableRoot => false; // No browsable filesystem

    public Task<IStorable?> CreateRootAsync(string rootUri) => 
        Task.FromResult<IStorable?>(null); // No root to browse

    public Task<IStorable?> CreateResourceAsync(string resourceUri)
    {
        // Create HttpFile directly from URL
        var httpFile = new HttpFile(resourceUri, _httpClient);
        return Task.FromResult<IStorable?>(httpFile);
    }

    // ... other methods
}
```

Usage: When the agent calls `OpenFileForReading("https://example.com/file.pdf")`, the system automatically creates an `HttpFile` instance for that URL.

### Memory Storage Protocol

The Memory protocol handler demonstrates filesystem-based protocols:

```csharp
public class MemoryProtocolHandler : IProtocolHandler
{
    public bool HasBrowsableRoot => true; // Has browsable filesystem

    public Task<IStorable?> CreateRootAsync(string rootUri)
    {
        // Create in-memory folder hierarchy
        var spaceName = ExtractSpaceName(rootUri);
        var memoryRoot = new MemoryFolder(spaceName, spaceName);
        return Task.FromResult<IStorable?>(memoryRoot);
    }

    public Task<IStorable?> CreateResourceAsync(string resourceUri) => 
        Task.FromResult<IStorable?>(null); // Use filesystem navigation

    // ... other methods
}
```

Usage: Shows up in `GetAvailableDrives()` as "Memory Storage: spacename" and can be browsed like a regular filesystem.

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

## Usage Examples

### Filesystem Protocols
These protocols appear in `GetAvailableDrives()` and can be browsed like traditional file systems:

```
// Browse IPFS MFS root
GetAvailableDrives() → includes "mfs://" 
GetFolderItems("mfs://") → lists MFS root contents

// Browse memory storage
GetAvailableDrives() → includes "memory://" 
GetFolderItems("memory://") → lists memory storage contents
```

### Resource Protocols  
These protocols support direct resource access without browsable roots:

```
// Read HTTP file directly
ReadFileAsBytes("https://example.com/file.txt")
ReadFileAsTextWithEncoding("https://example.com/data.json")

// Access IPFS content by hash
ReadFileAsBytes("ipfs://QmHash...")
GetFolderItems("ipfs://QmFolderHash...")  // If the hash points to a folder

// Access IPNS content by name
ReadFileAsTextWithEncoding("ipns://example.com")
GetFolderItems("ipns://example.com")  // If the IPNS name resolves to a folder
```

### Protocol Discovery
Use the built-in tools to discover what's available:

```
// See all supported protocols
GetSupportedProtocols() → lists all registered protocols and their capabilities

// Get available browsable drives/roots
GetAvailableDrives() → includes filesystem protocols only
```

## Benefits of the Generalized System

1. **Extensibility** - Easy to add new storage protocols
2. **Consistency** - All protocols follow the same patterns
3. **Maintainability** - Protocol-specific logic is isolated
4. **Testability** - Each protocol can be tested independently
5. **Dynamic Mounting** - Any `IFolder` can be mounted as a browsable drive

## Dynamic Folder Mounting

The protocol system now supports mounting any `IFolder` instance as a browsable drive with a custom protocol scheme. This allows you to:

- Mount subfolders as separate drives for easier navigation
- Create temporary mounts for specific projects or workflows  
- Mount folders from other protocols as top-level drives
- Organize complex folder hierarchies with meaningful names

### Mounting a Folder

Use the `MountFolder` tool to mount any accessible folder:

```
// Mount a subfolder as a separate drive
MountFolder(
    folderId: "C:/Projects/MyProject/src", 
    protocolScheme: "myproject-src", 
    mountName: "My Project Source"
)

// Mount an IPFS folder as a custom drive
MountFolder(
    folderId: "ipfs://QmProjectHash",
    protocolScheme: "ipfs-project", 
    mountName: "IPFS Project Archive"
)

// Mount a memory storage folder
MountFolder(
    folderId: "memory://temp/workspace",
    protocolScheme: "workspace",
    mountName: "Current Workspace"
)
```

After mounting, the folder appears in `GetAvailableDrives()` and can be browsed like any other drive:

```
GetAvailableDrives() → includes "myproject-src://" 
GetFolderItems("myproject-src://") → lists mounted folder contents
```

### Unmounting a Folder

Use the `UnmountFolder` tool to remove a mounted folder:

```
UnmountFolder("myproject-src")
```

### Listing Mounted Folders

Use the `GetMountedFolders` tool to see all currently mounted folders:

```
GetMountedFolders() → returns array of mounted folder information
```

### Example Workflow: Project Organization

```
// Start with a project folder
GetFolderItems("C:/Projects/LargeProject/")
→ [src/, docs/, tests/, assets/, build/]

// Mount key subfolders for easier access
MountFolder("C:/Projects/LargeProject/src", "proj-src", "Project Source")
MountFolder("C:/Projects/LargeProject/docs", "proj-docs", "Project Documentation") 
MountFolder("C:/Projects/LargeProject/tests", "proj-tests", "Project Tests")

// Now they appear as top-level drives
GetAvailableDrives()
→ includes "proj-src://", "proj-docs://", "proj-tests://"

// Direct access to project areas
GetFolderItems("proj-src://") → lists source files
GetFolderItems("proj-docs://") → lists documentation files

// Unmount when done
UnmountFolder("proj-src")
UnmountFolder("proj-docs") 
UnmountFolder("proj-tests")
```

### Security and Limitations

- Protocol schemes must be simple identifiers (no special characters)
- Cannot override built-in protocol schemes (mfs, http, https, ipfs, ipns, memory)
- Mounted folders inherit the permissions and capabilities of the underlying `IFolder`
- Unmounting removes the drive but doesn't affect the original folder
