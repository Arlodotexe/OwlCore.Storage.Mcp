using ModelContextProtocol.Server;
using ModelContextProtocol;
using System.ComponentModel;
using OwlCore.Storage;
using OwlCore.Storage.System.IO;
using OwlCore.Kubo;
using System.Collections.Concurrent;
using System.Text;
using Ipfs.Http;

namespace OwlCore.Storage.Mcp;

[McpServerToolType]
public static class StorageTools
{
    internal static readonly ConcurrentDictionary<string, IStorable> _storableRegistry = new();
    private static volatile bool _isInitialized = false;
    private static readonly SemaphoreSlim _initializationSemaphore = new(1, 1);
    
    static StorageTools()
    {
        // Don't do async work in static constructor - just ensure basic setup
    }

    /// <summary>
    /// Ensures that the storage system is fully initialized before proceeding
    /// </summary>
    private static async Task EnsureInitializedAsync()
    {
        if (_isInitialized) return;

        await _initializationSemaphore.WaitAsync();
        try
        {
            if (_isInitialized) return; // Double-check after acquiring lock

            // Initialize ProtocolRegistry and restore mounts
            await ProtocolRegistry.EnsureInitializedAsync();
            
            // Pre-register common protocol roots after mount restoration
            foreach (var protocolScheme in ProtocolRegistry.GetRegisteredProtocols())
            {
                var rootUri = $"{protocolScheme}://";
                var protocolHandler = ProtocolRegistry.GetProtocolHandler(rootUri);
                if (protocolHandler?.HasBrowsableRoot == true)
                {
                    try
                    {
                        var root = await protocolHandler.CreateRootAsync(rootUri);
                        if (root != null)
                        {
                            _storableRegistry[rootUri] = root;
                            Console.WriteLine($"Pre-registered protocol root: {rootUri}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to pre-register {rootUri}: {ex.Message}");
                    }
                }
            }

            _isInitialized = true;
        }
        finally
        {
            _initializationSemaphore.Release();
        }
    }

    internal static async Task EnsureStorableRegistered(string id)
    {
        // Ensure the storage system is fully initialized first
        await EnsureInitializedAsync();
        
        // Try the original ID first
        if (_storableRegistry.ContainsKey(id)) 
        {
            Console.WriteLine($"[STORAGE] Item already registered: {id}");
            return;
        }

        // If not found, try resolving it as a potential alias
        var resolvedId = ProtocolRegistry.ResolveAliasToFullId(id);
        if (resolvedId != id && _storableRegistry.TryGetValue(resolvedId, out var resolvedItem))
        {
            // Register the alias to point to the same resolved item
            _storableRegistry[id] = resolvedItem;
            Console.WriteLine($"[STORAGE] Alias {id} resolved to existing item: {resolvedId}");
            return;
        }

        // Use the resolved ID for registration attempts
        var registrationId = resolvedId;
        Console.WriteLine($"[STORAGE] Registering item: {registrationId}");

        try
        {
            // Check if this is a custom protocol
            var protocolHandler = ProtocolRegistry.GetProtocolHandler(registrationId);
            if (protocolHandler != null)
            {
                Console.WriteLine($"[STORAGE] Found protocol handler for {registrationId}: {protocolHandler.GetType().Name}");
                
                // For filesystem protocols, try to create a root if this looks like a root URI
                if (protocolHandler.HasBrowsableRoot && registrationId.EndsWith("://"))
                {
                    Console.WriteLine($"[STORAGE] Creating root for filesystem protocol: {registrationId}");
                    var root = await protocolHandler.CreateRootAsync(registrationId);
                    if (root != null)
                    {
                        _storableRegistry[registrationId] = root;
                        // Also register the original alias if different
                        if (id != registrationId)
                            _storableRegistry[id] = root;
                        Console.WriteLine($"[STORAGE] Successfully registered root: {registrationId} as {root.GetType().Name}");
                        return;
                    }
                }

                // Try to create a direct resource first (for protocols like HTTP or specific resource URIs)
                Console.WriteLine($"[STORAGE] Creating resource for: {registrationId}");
                var resource = await protocolHandler.CreateResourceAsync(registrationId);
                if (resource != null)
                {
                    _storableRegistry[registrationId] = resource;
                    // Also register the original alias if different
                    if (id != registrationId)
                        _storableRegistry[id] = resource;
                    Console.WriteLine($"[STORAGE] Successfully registered resource: {registrationId} as {resource.GetType().Name}");
                    return;
                }

                // Let the protocol handler decide if registration is needed for filesystem-style protocols
                if (!protocolHandler.NeedsRegistration(registrationId)) 
                {
                    // Special case: For mounted folder protocols, we still need to register specific items
                    // even if the protocol handler says registration isn't needed
                    if (protocolHandler is MountedFolderProtocolHandler mountHandler && !registrationId.EndsWith("://"))
                    {
                        // This is a specific path within a mounted folder - we need to navigate to it
                        try
                        {
                            var relativePath = registrationId.Substring($"{mountHandler.ProtocolScheme}://".Length);
                            if (!string.IsNullOrEmpty(relativePath))
                            {
                                var targetItem = await mountHandler.MountedFolder.GetItemByRelativePathAsync(relativePath);
                                _storableRegistry[registrationId] = targetItem;
                                // Also register the original alias if different
                                if (id != registrationId)
                                    _storableRegistry[id] = targetItem;
                                Console.WriteLine($"[STORAGE] Successfully registered mounted folder item: {registrationId} as {targetItem.GetType().Name}");
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[STORAGE] Failed to navigate to mounted folder item {registrationId}: {ex.Message}");
                            // Continue to regular "registration not needed" logic
                        }
                    }
                    
                    Console.WriteLine($"[STORAGE] Protocol handler says registration not needed for: {registrationId}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[STORAGE] Error during protocol registration for {registrationId}: {ex.Message}");
            Console.WriteLine($"[STORAGE] Full exception: {ex}");
            throw new InvalidOperationException($"Failed to register protocol resource '{registrationId}': {ex.Message}", ex);
        }

        // Handle regular filesystem paths
        if (Directory.Exists(registrationId))
        {
            var folder = new SystemFolder(new DirectoryInfo(registrationId));
            _storableRegistry[registrationId] = folder;
            // Also register the original alias if different
            if (id != registrationId)
                _storableRegistry[id] = folder;
            Console.WriteLine($"Registered system folder: {registrationId}");
        }
        else if (File.Exists(registrationId))
        {
            var file = new SystemFile(new FileInfo(registrationId));
            _storableRegistry[registrationId] = file;
            // Also register the original alias if different
            if (id != registrationId)
                _storableRegistry[id] = file;
            Console.WriteLine($"Registered system file: {registrationId}");
        }
        else
        {
            Console.WriteLine($"Could not register item: {registrationId} (not found in filesystem and no protocol handler)");
        }
    }

    internal static string CreateCustomItemId(string parentId, string itemName)
    {
        return ProtocolRegistry.CreateCustomItemId(parentId, itemName);
    }

    [McpServerTool, Description("Gets the paths of the available drives including IPFS MFS, Memory storage, and other custom protocol roots")]
    public static async Task<object[]> GetAvailableDrives()
    {
        var driveInfos = new List<object>();

        // Get all available drives
        var drives = DriveInfo.GetDrives();
        foreach (var drive in drives)
        {
            try
            {
                // Create a SystemFolder for each drive
                var driveFolder = new SystemFolder(new DirectoryInfo(drive.RootDirectory.FullName));
                _storableRegistry[drive.RootDirectory.FullName] = driveFolder;
                
                // Add drive info to result
                driveInfos.Add(new
                {
                    id = drive.RootDirectory.FullName,
                    name = !string.IsNullOrEmpty(drive.VolumeLabel) ? $"{drive.Name} ({drive.VolumeLabel})" : drive.Name,
                    type = "drive",
                    driveType = drive.DriveType.ToString(),
                    isReady = drive.IsReady,
                    totalSize = drive.IsReady ? drive.TotalSize : 0,
                    availableFreeSpace = drive.IsReady ? drive.AvailableFreeSpace : 0
                });
            }
            catch
            {
                // Skip drives that aren't ready or throw errors
                continue;
            }
        }

        // Add custom protocol roots (only for protocols that have browsable roots)
        foreach (var protocolScheme in ProtocolRegistry.GetRegisteredProtocols())
        {
            var rootUri = $"{protocolScheme}://";
            try
            {
                var protocolHandler = ProtocolRegistry.GetProtocolHandler(rootUri);
                if (protocolHandler == null || !protocolHandler.HasBrowsableRoot) continue;

                // Only register root if not already registered
                if (!_storableRegistry.ContainsKey(rootUri))
                {
                    var protocolRoot = await protocolHandler.CreateRootAsync(rootUri);
                    if (protocolRoot != null)
                    {
                        _storableRegistry[rootUri] = protocolRoot;
                    }
                }
                
                // Get drive information from the protocol handler
                var driveInfo = await protocolHandler.GetDriveInfoAsync(rootUri);
                if (driveInfo != null)
                {
                    driveInfos.Add(driveInfo);
                }
            }
            catch (Exception ex)
            {
                // If protocol is not available, log but don't fail
                Console.Error.WriteLine($"{protocolScheme} protocol not available: {ex.Message}");
            }
        }

        return driveInfos.ToArray();
    }

    [McpServerTool, Description("Lists all items in a folder by ID or path. Works with local folders, IPFS MFS, and IPFS/IPNS folder hashes. Returns array of items with their IDs, names, and types.")]
    public static async Task<object[]> GetFolderItems(string folderId)
    {
        try
        {
            await EnsureStorableRegistered(folderId);

            if (!_storableRegistry.TryGetValue(folderId, out var registeredItem) || registeredItem is not IFolder folder)
                throw new McpException($"Folder with ID '{folderId}' not found", McpErrorCode.InvalidParams);

            var items = new List<object>();
            await foreach (var item in folder.GetItemsAsync())
            {
                string itemId = ProtocolRegistry.IsCustomProtocol(folderId) ? CreateCustomItemId(folderId, item.Name) : item.Id;
                _storableRegistry[itemId] = item;
                
                // Use mount alias substitution to present shorter IDs externally
                string externalId = ProtocolRegistry.SubstituteWithMountAlias(itemId);
                // Ensure the alias also maps to the same item for external access
                if (externalId != itemId)
                    _storableRegistry[externalId] = item;
                
                items.Add(new
                {
                    id = externalId,
                    name = item.Name,
                    type = item switch
                    {
                        IFile => "file",
                        IFolder => "folder",
                        _ => "unknown"
                    }
                });
            }

            return items.ToArray();
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to get folder items for '{folderId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Lists only files in a folder by ID or path. Returns array of file items.")]
    public static async Task<object[]> GetFolderFiles(string folderId)
    {
        try
        {
            await EnsureStorableRegistered(folderId);

            if (!_storableRegistry.TryGetValue(folderId, out var registeredItem) || registeredItem is not IFolder folder)
                throw new McpException($"Folder with ID '{folderId}' not found", McpErrorCode.InvalidParams);

            var files = new List<object>();
            await foreach (var file in folder.GetFilesAsync())
            {
                string fileId = ProtocolRegistry.IsCustomProtocol(folderId) ? CreateCustomItemId(folderId, file.Name) : file.Id;
                _storableRegistry[fileId] = file;
                
                // Use mount alias substitution to present shorter IDs externally
                string externalId = ProtocolRegistry.SubstituteWithMountAlias(fileId);
                // Ensure the alias also maps to the same item for external access
                if (externalId != fileId)
                    _storableRegistry[externalId] = file;
                
                files.Add(new
                {
                    id = externalId,
                    name = file.Name,
                    type = "file"
                });
            }

            return files.ToArray();
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to get folder files for '{folderId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Lists only folders in a folder by ID or path. Returns array of folder items.")]
    public static async Task<object[]> GetFolderSubfolders(string folderId)
    {
        try
        {
            await EnsureStorableRegistered(folderId);

            if (!_storableRegistry.TryGetValue(folderId, out var registeredItem) || registeredItem is not IFolder folder)
                throw new McpException($"Folder with ID '{folderId}' not found", McpErrorCode.InvalidParams);

            var folders = new List<object>();
            await foreach (var subfolder in folder.GetFoldersAsync())
            {
                string subfolderId = ProtocolRegistry.IsCustomProtocol(folderId) ? CreateCustomItemId(folderId, subfolder.Name) : subfolder.Id;
                _storableRegistry[subfolderId] = subfolder;
                
                // Use mount alias substitution to present shorter IDs externally
                string externalId = ProtocolRegistry.SubstituteWithMountAlias(subfolderId);
                // Ensure the alias also maps to the same item for external access
                if (externalId != subfolderId)
                    _storableRegistry[externalId] = subfolder;
                
                folders.Add(new
                {
                    id = externalId,
                    name = subfolder.Name,
                    type = "folder"
                });
            }

            return folders.ToArray();
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to get folder subfolders for '{folderId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Recursively searches for an item by ID in a folder and all its subfolders.")]
    public static async Task<object?> FindItemRecursively(string folderId, string targetItemId)
    {
        try
        {
            await EnsureStorableRegistered(folderId);

            if (!_storableRegistry.TryGetValue(folderId, out var registeredItem) || registeredItem is not IFolder folder)
                throw new McpException($"Folder with ID '{folderId}' not found", McpErrorCode.InvalidParams);

            try
            {
                var foundItem = await folder.GetItemRecursiveAsync(targetItemId);
                _storableRegistry[foundItem.Id] = foundItem;

                // Use mount alias substitution to present shorter IDs externally
                string externalId = ProtocolRegistry.SubstituteWithMountAlias(foundItem.Id);
                // Ensure the alias also maps to the same item for external access
                if (externalId != foundItem.Id)
                    _storableRegistry[externalId] = foundItem;

                return new
                {
                    id = externalId,
                    name = foundItem.Name,
                    type = foundItem switch
                    {
                        IFile => "file",
                        IFolder => "folder",
                        _ => "unknown"
                    }
                };
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to find item '{targetItemId}' recursively in '{folderId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Navigates to an item using a relative path from a starting item.")]
    public static async Task<object> GetItemByRelativePath(string startingItemId, string relativePath)
    {
        try
        {
            await EnsureStorableRegistered(startingItemId);

            if (!_storableRegistry.TryGetValue(startingItemId, out var startingItem))
                throw new McpException($"Starting item with ID '{startingItemId}' not found", McpErrorCode.InvalidParams);

            var targetItem = await startingItem.GetItemByRelativePathAsync(relativePath);
            _storableRegistry[targetItem.Id] = targetItem;

            // Use mount alias substitution to present shorter IDs externally
            string externalId = ProtocolRegistry.SubstituteWithMountAlias(targetItem.Id);
            // Ensure the alias also maps to the same item for external access
            if (externalId != targetItem.Id)
                _storableRegistry[externalId] = targetItem;

            return new
            {
                id = externalId,
                name = targetItem.Name,
                type = targetItem switch
                {
                    IFile => "file",
                    IFolder => "folder",
                    _ => "unknown"
                }
            };
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to navigate to '{relativePath}' from '{startingItemId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Gets the relative path from one folder to another item.")]
    public static async Task<string> GetRelativePath(string fromFolderId, string toItemId)
    {
        try
        {
            await EnsureStorableRegistered(fromFolderId);
            await EnsureStorableRegistered(toItemId);

            if (!_storableRegistry.TryGetValue(fromFolderId, out var fromItem) || fromItem is not IFolder fromFolder)
                throw new McpException($"From folder with ID '{fromFolderId}' not found or not a folder", McpErrorCode.InvalidParams);

            if (!_storableRegistry.TryGetValue(toItemId, out var toItem) || toItem is not IStorableChild toChild)
                throw new McpException($"To item with ID '{toItemId}' not found or not a child item", McpErrorCode.InvalidParams);

            return await fromFolder.GetRelativePathToAsync(toChild);
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to get relative path from '{fromFolderId}' to '{toItemId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Reads the content of a file as bytes by file ID, path, or URL (supports HTTP/HTTPS URLs, IPFS hashes, and IPNS names).")]
    public static async Task<byte[]> ReadFileAsBytes(string fileId)
    {
        try
        {
            await EnsureStorableRegistered(fileId);

            if (!_storableRegistry.TryGetValue(fileId, out var item) || item is not IFile file)
                throw new McpException($"File with ID '{fileId}' not found", McpErrorCode.InvalidParams);

            return await file.ReadBytesAsync(CancellationToken.None);
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to read bytes from file '{fileId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Reads the content of a file as text with specified encoding by file ID, path, or URL (supports HTTP/HTTPS URLs, IPFS hashes, and IPNS names).")]
    public static async Task<string> ReadFileAsText(string fileId, string encoding = "UTF-8")
    {
        try
        {
            await EnsureStorableRegistered(fileId);

            if (!_storableRegistry.TryGetValue(fileId, out var item) || item is not IFile file)
                throw new McpException($"File with ID '{fileId}' not found", McpErrorCode.InvalidParams);

            var textEncoding = encoding.ToUpperInvariant() switch
            {
                "UTF-8" or "UTF8" => Encoding.UTF8,
                "UTF-16" or "UTF16" => Encoding.Unicode,
                "ASCII" => Encoding.ASCII,
                "UNICODE" => Encoding.Unicode,
                _ => Encoding.UTF8
            };

            return await file.ReadTextAsync(textEncoding, CancellationToken.None);
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to read text with encoding '{encoding}' from file '{fileId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Reads a specific range of text from a file by line numbers (1-based indexing). To read from startLine to end of file, omit endLine parameter. To read specific range, provide both startLine and endLine. endLine must be >= startLine and <= total lines. Do NOT use endLine=0, use null or omit it.")]
    public static async Task<string> ReadFileTextRange(string fileId, int startLine, int? endLine = null)
    {
        try
        {
            await EnsureStorableRegistered(fileId);

            if (!_storableRegistry.TryGetValue(fileId, out var item) || item is not IFile file)
                throw new McpException($"File with ID '{fileId}' not found", McpErrorCode.InvalidParams);

            // Explicitly reject endLine of 0 since it's invalid (1-based indexing)
            if (endLine.HasValue && endLine.Value <= 0)
                throw new McpException($"Invalid endLine value: {endLine.Value}. endLine must be >= 1 (1-based indexing) or null to read to end. To read to end, omit endLine entirely.", McpErrorCode.InvalidParams);

            var content = await file.ReadTextAsync(CancellationToken.None);
            var lines = content.Split('\n');
            
            // Validate line numbers (1-based)
            if (startLine < 1 || startLine > lines.Length)
                throw new McpException($"Invalid startLine: {startLine}. Must be between 1 and {lines.Length} (file has {lines.Length} lines)", McpErrorCode.InvalidParams);
            
            int actualEndLine = endLine ?? lines.Length;
            if (actualEndLine < startLine || actualEndLine > lines.Length)
                throw new McpException($"Invalid endLine: {actualEndLine}. Must be between {startLine} and {lines.Length}", McpErrorCode.InvalidParams);

            // Extract the requested range (convert to 0-based indexing)
            var selectedLines = lines[(startLine - 1)..actualEndLine];
            return string.Join('\n', selectedLines);
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to read text range (lines {startLine}-{endLine}) from file '{fileId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Gets information about a seen storable item by ID, path, or URL")]
    public static async Task<object?> GetStorableInfo(string id)
    {
        try
        {
            await EnsureStorableRegistered(id);

            if (!_storableRegistry.TryGetValue(id, out var storable))
                throw new McpException($"Item with ID '{id}' not found", McpErrorCode.InvalidParams);

            return new
            {
                id = storable.Id,
                name = storable.Name,
                type = storable switch
                {
                    IFile => "file",
                    IFolder => "folder",
                    _ => "unknown"
                }
            };
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to get storable info for '{id}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Gets the root folder of a storage item by tracing up the parent hierarchy.")]
    public static async Task<object?> GetRootFolder(string itemId)
    {
        try
        {
            await EnsureStorableRegistered(itemId);

            if (!_storableRegistry.TryGetValue(itemId, out var item) || item is not IStorableChild storableChild)
                throw new McpException($"Item with ID '{itemId}' not found or not a child item", McpErrorCode.InvalidParams);

            var rootFolder = await storableChild.GetRootAsync();
            if (rootFolder == null)
                return null;

            _storableRegistry[rootFolder.Id] = rootFolder;

            // Use mount alias substitution to present shorter IDs externally
            string externalId = ProtocolRegistry.SubstituteWithMountAlias(rootFolder.Id);
            // Ensure the alias also maps to the same item for external access
            if (externalId != rootFolder.Id)
                _storableRegistry[externalId] = rootFolder;

            return new
            {
                id = externalId,
                name = rootFolder.Name,
                type = "folder"
            };
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to get root folder for '{itemId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Gets a specific item by ID from a folder.")]
    public static async Task<object> GetItemById(string folderId, string itemId)
    {
        try
        {
            await EnsureStorableRegistered(folderId);

            if (!_storableRegistry.TryGetValue(folderId, out var registeredItem) || registeredItem is not IFolder folder)
                throw new McpException($"Folder with ID '{folderId}' not found", McpErrorCode.InvalidParams);

            try
            {
                var foundItem = await folder.GetItemAsync(itemId);
                _storableRegistry[foundItem.Id] = foundItem;

                // Use mount alias substitution to present shorter IDs externally
                string externalId = ProtocolRegistry.SubstituteWithMountAlias(foundItem.Id);
                // Ensure the alias also maps to the same item for external access
                if (externalId != foundItem.Id)
                    _storableRegistry[externalId] = foundItem;

                return new
                {
                    id = externalId,
                    name = foundItem.Name,
                    type = foundItem switch
                    {
                        IFile => "file",
                        IFolder => "folder",
                        _ => "unknown"
                    }
                };
            }
            catch (FileNotFoundException)
            {
                throw new McpException($"Item with ID '{itemId}' not found in folder '{folder.Name}'", McpErrorCode.InvalidParams);
            }
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to get item '{itemId}' from folder '{folderId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Gets the parent folder of a storage item.")]
    public static async Task<object?> GetParentFolder(string itemId)
    {
        try
        {
            await EnsureStorableRegistered(itemId);

            if (!_storableRegistry.TryGetValue(itemId, out var item) || item is not IStorableChild storableChild)
                throw new McpException($"Item with ID '{itemId}' not found or not a child item", McpErrorCode.InvalidParams);

            var parentFolder = await storableChild.GetParentAsync();
            if (parentFolder == null)
                return null;

            _storableRegistry[parentFolder.Id] = parentFolder;

            // Use mount alias substitution to present shorter IDs externally
            string externalId = ProtocolRegistry.SubstituteWithMountAlias(parentFolder.Id);
            // Ensure the alias also maps to the same item for external access
            if (externalId != parentFolder.Id)
                _storableRegistry[externalId] = parentFolder;

            return new
            {
                id = externalId,
                name = parentFolder.Name,
                type = "folder"
            };
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to get parent folder of item '{itemId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Lists all supported storage protocols and their capabilities")]
    public static object[] GetSupportedProtocols()
    {
        var protocols = new List<object>();

        // Add built-in filesystem support
        protocols.Add(new
        {
            scheme = "file",
            name = "Local File System",
            type = "filesystem",
            hasBrowsableRoot = true,
            supportsDirectResources = false,
            description = "Local disk drives and folders"
        });

        // Add custom protocols
        foreach (var protocolScheme in ProtocolRegistry.GetRegisteredProtocols())
        {
            var rootUri = $"{protocolScheme}://";
            var protocolHandler = ProtocolRegistry.GetProtocolHandler(rootUri);
            
            if (protocolHandler != null)
            {
                protocols.Add(new
                {
                    scheme = protocolScheme,
                    name = protocolScheme.ToUpper() + " Protocol",
                    type = protocolHandler.HasBrowsableRoot ? "filesystem" : "resource",
                    hasBrowsableRoot = protocolHandler.HasBrowsableRoot,
                    supportsDirectResources = !protocolHandler.HasBrowsableRoot,
                    description = protocolScheme switch
                    {
                        "mfs" => "IPFS Mutable File System - browsable IPFS storage",
                        "memory" => "In-memory temporary storage for testing",
                        "http" or "https" => "HTTP/HTTPS web resources and files",
                        "ipfs" => "IPFS content addressed by hash - files or folders accessible by hash",
                        "ipns" => "IPNS names that resolve to IPFS content - files or folders accessible by name",
                        _ => $"Custom {protocolScheme} protocol"
                    }
                });
            }
        }

        return protocols.ToArray();
    }

    [McpServerTool, Description("Mounts an existing folder as a browsable drive with a custom protocol scheme. The folder will appear in GetAvailableDrives() and can be browsed like any other drive.")]
    public static async Task<object> MountFolder(
        [Description("The ID or path of the folder to mount")] string folderId,
        [Description("The custom protocol scheme to use (e.g., 'myproject', 'backup', 'archive')")] string protocolScheme,
        [Description("Display name for the mounted folder")] string mountName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folderId))
                throw new McpException("Folder ID cannot be null or empty", McpErrorCode.InvalidParams);
            
            if (string.IsNullOrWhiteSpace(protocolScheme))
                throw new McpException("Protocol scheme cannot be null or empty", McpErrorCode.InvalidParams);
            
            if (string.IsNullOrWhiteSpace(mountName))
                throw new McpException("Mount name cannot be null or empty", McpErrorCode.InvalidParams);

            // Validate protocol scheme format
            if (protocolScheme.Contains("://") || protocolScheme.Contains("/") || protocolScheme.Contains("\\"))
                throw new McpException("Protocol scheme must be a simple identifier without special characters", McpErrorCode.InvalidParams);

            // Ensure the folder exists and is accessible
            await EnsureStorableRegistered(folderId);
            
            if (!_storableRegistry.TryGetValue(folderId, out var registeredItem) || registeredItem is not IFolder folder)
                throw new McpException($"Folder with ID '{folderId}' not found", McpErrorCode.InvalidParams);

            var rootUri = ProtocolRegistry.MountFolder(folder, protocolScheme, mountName, folderId);
            
            // Register the mounted root in our storable registry
            _storableRegistry[rootUri] = folder;
            
            // Save the mount settings to persist the configuration
            var mountSettings = ProtocolRegistry.GetMountSettings();
            if (mountSettings != null)
            {
                await mountSettings.SaveAsync();
            }
            
            return new
            {
                success = true,
                rootUri = rootUri,
                protocolScheme = protocolScheme,
                mountName = mountName,
                originalFolderId = folderId,
                message = $"Successfully mounted '{mountName}' as {protocolScheme}://"
            };
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (ArgumentException ex)
        {
            throw new McpException($"Failed to mount folder: {ex.Message}", ex, McpErrorCode.InvalidParams);
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to mount folder '{folderId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Unmounts a previously mounted folder, removing it from available drives.")]
    public static async Task<object> UnmountFolder(
        [Description("The protocol scheme of the mounted folder to unmount")] string protocolScheme)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(protocolScheme))
                throw new McpException("Protocol scheme cannot be null or empty", McpErrorCode.InvalidParams);

            var wasUnmounted = ProtocolRegistry.UnmountFolder(protocolScheme);
            
            if (wasUnmounted)
            {
                // Remove from storable registry as well
                var rootUri = $"{protocolScheme}://";
                _storableRegistry.TryRemove(rootUri, out _);
                
                // Save the mount settings to persist the change
                var mountSettings = ProtocolRegistry.GetMountSettings();
                if (mountSettings != null)
                {
                    await mountSettings.SaveAsync();
                }
                
                return new
                {
                    success = true,
                    protocolScheme = protocolScheme,
                    message = $"Successfully unmounted {protocolScheme}://"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    protocolScheme = protocolScheme,
                    message = $"Protocol scheme '{protocolScheme}' not found or is not a mounted folder"
                };
            }
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to unmount folder '{protocolScheme}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Lists all currently mounted folders and their information.")]
    public static async Task<object[]> GetMountedFolders()
    {
        await ProtocolRegistry.EnsureInitializedAsync();
        return ProtocolRegistry.GetMountedFolders();
    }

    [McpServerTool, Description("Renames a mounted folder's protocol scheme and/or display name. Preserves all existing references and dependencies.")]
    public static async Task<object> RenameMountedFolder(
        [Description("The current protocol scheme to rename")] string currentProtocolScheme,
        [Description("The new protocol scheme (optional, leave empty to keep current)")] string? newProtocolScheme = null,
        [Description("The new display name (optional, leave empty to keep current)")] string? newMountName = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(currentProtocolScheme))
                throw new McpException("Current protocol scheme cannot be null or empty", McpErrorCode.InvalidParams);

            var newRootUri = ProtocolRegistry.RenameMountedFolder(currentProtocolScheme, newProtocolScheme, newMountName);
            
            // Update storable registry if protocol scheme changed
            if (!string.IsNullOrEmpty(newProtocolScheme) && newProtocolScheme != currentProtocolScheme)
            {
                var oldRootUri = $"{currentProtocolScheme}://";
                if (_storableRegistry.TryRemove(oldRootUri, out var folder))
                {
                    _storableRegistry[newRootUri] = folder;
                }
            }
            
            // Save the mount settings to persist the change
            var mountSettings = ProtocolRegistry.GetMountSettings();
            if (mountSettings != null)
            {
                await mountSettings.SaveAsync();
            }
            
            return new
            {
                success = true,
                oldProtocolScheme = currentProtocolScheme,
                newProtocolScheme = newProtocolScheme ?? currentProtocolScheme,
                newMountName = newMountName,
                newRootUri = newRootUri,
                message = $"Successfully renamed mount to {newRootUri}"
            };
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (ArgumentException ex)
        {
            throw new McpException($"Failed to rename mounted folder: {ex.Message}", ex, McpErrorCode.InvalidParams);
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to rename mounted folder '{currentProtocolScheme}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

}
