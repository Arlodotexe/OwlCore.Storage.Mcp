using ModelContextProtocol.Server;
using ModelContextProtocol;
using System.ComponentModel;
using OwlCore.Storage;
using OwlCore.Storage.System.IO;
using OwlCore.Kubo;
using System.Collections.Concurrent;
using System.Text;
using Ipfs.Http;
using CommunityToolkit.Diagnostics;

namespace OwlCore.Storage.Mcp;

[McpServerToolType]
public static class StorageTools
{
    internal static readonly ConcurrentDictionary<string, IStorable> _storableRegistry = new();
    private static volatile bool _isInitialized = false;
    private static readonly SemaphoreSlim _initializationSemaphore = new(1, 1);
    
    // Issue003: inbound canonicalization for browsable protocols only (filesystem-like); resource protocols keep original form.
    private static string NormalizeInboundExternalId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return id;
        var sep = id.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0) return id; // not a scheme-form ID
        var scheme = id.Substring(0, sep);
        var handler = ProtocolRegistry.GetProtocolHandler($"{scheme}://");
        if (handler?.HasBrowsableRoot == true)
            return StoragePathNormalizer.NormalizeExternalId(id);
        return id; // leave non-browsable protocols unchanged (trailing slash may be semantic)
    }
    
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
                        var root = await protocolHandler.CreateRootAsync(rootUri, CancellationToken.None);
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

    // Issue003: early canonicalization (memory://foo/ -> memory://foo, preserving roots) for identity + symmetric errors.
        var originalId = id;
        var normalizedId = StoragePathNormalizer.NormalizeExternalId(originalId);
        if (normalizedId != originalId)
        {
            // Fast path: if normalized already registered, just map alias and return.
            if (_storableRegistry.TryGetValue(normalizedId, out var existing))
            {
                _storableRegistry[originalId] = existing; // alias key
                return;
            }
            id = normalizedId; // proceed using normalized id
        }
        
        // Check if already registered - if so, we're done
        if (_storableRegistry.ContainsKey(id)) 
        {
            Console.WriteLine($"[STORAGE] Item already registered: {id}");
            return;
        }

        // Basic validation
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("Storage ID cannot be null, empty, or whitespace");
        }

        // Try resolving as an alias first
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
        Console.WriteLine($"[STORAGE] Attempting to register: {registrationId}");

        // Handle regular filesystem paths first (fast path)
        if (Directory.Exists(registrationId))
        {
            var folder = new SystemFolder(new DirectoryInfo(registrationId));
            _storableRegistry[registrationId] = folder;
            if (id != registrationId)
                _storableRegistry[id] = folder;
            Console.WriteLine($"[STORAGE] Registered system folder: {registrationId}");
            return;
        }
        else if (File.Exists(registrationId))
        {
            var file = new SystemFile(new FileInfo(registrationId));
            _storableRegistry[registrationId] = file;
            if (id != registrationId)
                _storableRegistry[id] = file;
            Console.WriteLine($"[STORAGE] Registered system file: {registrationId}");
            return;
        }

        // Handle protocol-based IDs
        if (registrationId.Contains("://"))
        {
            var scheme = registrationId.Split("://")[0];
            var pathPart = registrationId.Split("://", 2)[1];
            
            var knownProtocols = ProtocolRegistry.GetRegisteredProtocols();
            if (!knownProtocols.Contains(scheme))
            {
                throw new InvalidOperationException($"Unknown protocol scheme '{scheme}' in ID '{registrationId}'. Known protocols: {string.Join(", ", knownProtocols)}. Use GetAvailableDrives and GetSupportedProtocols to see available starting points.");
            }

            // CRITICAL: For protocols without browseable roots (like HTTP, IPFS, IPNS), 
            // direct resource access should be allowed. Only filesystem-like protocols 
            // should require navigation from roots.
            if (!string.IsNullOrEmpty(pathPart))
            {
                var handler = ProtocolRegistry.GetProtocolHandler(registrationId);
                if (handler == null)
                {
                    throw new InvalidOperationException($"No protocol handler found for '{scheme}'. Use GetAvailableDrives() to see available protocols.");
                }

                // If the protocol has browseable roots, it's a filesystem-like protocol that requires navigation
                if (handler.HasBrowsableRoot)
                {
                    var rootUri = $"{scheme}://";
                    var availableRoots = _storableRegistry.Keys.Where(k => k.EndsWith("://")).ToList();
                    
                    throw new InvalidOperationException(
                        $"Cannot directly access '{registrationId}' - this ID exists but hasn't been seen at runtime yet. " +
                        $"Navigation can only start from already-loaded items. " +
                        $"Start from root '{rootUri}' and navigate to this path using GetItemByRelativePath('{rootUri}', '{pathPart}'). " +
                        $"Available roots: {string.Join(", ", availableRoots)}");
                }
                
                // For protocols without browseable roots (direct resource protocols), 
                // continue to the CreateResourceAsync logic below
            }

            var protocolHandler = ProtocolRegistry.GetProtocolHandler(registrationId);
            if (protocolHandler == null)
            {
                throw new InvalidOperationException($"No protocol handler found for '{scheme}'. Use GetAvailableDrives() to see available protocols.");
            }

            Console.WriteLine($"[STORAGE] Found protocol handler for {registrationId}: {protocolHandler.GetType().Name}");
            
            try
            {
                // Only allow registration of root URIs for protocols
                if (registrationId.EndsWith("://") && protocolHandler.HasBrowsableRoot)
                {
                    var root = await protocolHandler.CreateRootAsync(registrationId, CancellationToken.None);
                    if (root != null)
                    {
                        _storableRegistry[registrationId] = root;
                        if (id != registrationId)
                            _storableRegistry[id] = root;
                        Console.WriteLine($"[STORAGE] Successfully registered root: {registrationId}");
                        return;
                    }
                }

                // Try to create a direct resource (for non-filesystem protocols like HTTP)
                var resource = await protocolHandler.CreateResourceAsync(registrationId, CancellationToken.None);
                if (resource != null)
                {
                    _storableRegistry[registrationId] = resource;
                    if (id != registrationId)
                        _storableRegistry[id] = resource;
                    Console.WriteLine($"[STORAGE] Successfully registered resource: {registrationId}");
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[STORAGE] Failed to register {registrationId}: {ex.Message}");
                throw new InvalidOperationException($"Failed to access '{registrationId}': {ex.Message}. Use GetAvailableDrives() to see valid starting points.", ex);
            }
        }

        // If we get here, the item couldn't be registered
        throw new InvalidOperationException($"Cannot access item '{registrationId}': not found or not accessible. Use GetAvailableDrives() to see valid starting points for navigation.");
    }

    internal static string CreateCustomItemId(string parentId, string itemName)
    {
        return ProtocolRegistry.CreateCustomItemId(parentId, itemName);
    }

    [McpServerTool, Description("Gets the available browseable drives. Use these drive IDs as starting points for GetItemByRelativePath navigation.")]
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

        // Add mounted folders
        await ProtocolRegistry.EnsureInitializedAsync();
        var mountedFolders = ProtocolRegistry.GetMountedFolders();
        foreach (var mount in mountedFolders)
        {
            driveInfos.Add(mount);
        }

        // Add custom protocol roots (only for protocols that have browsable roots and aren't mounted folders)
        foreach (var protocolScheme in ProtocolRegistry.GetRegisteredProtocols())
        {
            var rootUri = $"{protocolScheme}://";
            try
            {
                var protocolHandler = ProtocolRegistry.GetProtocolHandler(rootUri);
                if (protocolHandler == null || !protocolHandler.HasBrowsableRoot) continue;
                
                // Skip mounted folder protocols since they're already included above
                if (protocolHandler is MountedFolderProtocolHandler) continue;

                // Only register root if not already registered
                if (!_storableRegistry.ContainsKey(rootUri))
                {
                    var protocolRoot = await protocolHandler.CreateRootAsync(rootUri, CancellationToken.None);
                    if (protocolRoot != null)
                    {
                        _storableRegistry[rootUri] = protocolRoot;
                    }
                }
                
                // Get drive information from the protocol handler
                var driveInfo = await protocolHandler.GetDriveInfoAsync(rootUri, CancellationToken.None);
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

    [McpServerTool, Description("Lists all items in a folder by ID or path. Returns array of items with their IDs, names, and types.")]
    public static async Task<object[]> GetFolderItems(string folderId)
    {
        try
        {
            // Inbound normalization for browsable protocols only (Issue 003 symmetry)
            folderId = NormalizeInboundExternalId(folderId);
            if (string.IsNullOrWhiteSpace(folderId))
                throw new McpException("Folder ID cannot be empty", McpErrorCode.InvalidParams);

            await EnsureStorableRegistered(folderId);

            // Archive guidance (mirrors GetFolderFiles/GetFolderSubfolders)
            if (_storableRegistry.TryGetValue(folderId, out var origItem) && origItem is IFile && ProtocolRegistry.TryGetArchiveMountScheme(folderId, out var archiveScheme) && !folderId.EndsWith("://"))
                throw new McpException($"Archive file '{folderId}' is mounted as '{archiveScheme}://'. Enumerate via '{archiveScheme}://' instead of the physical file path.", McpErrorCode.InvalidParams);

            if (!_storableRegistry.TryGetValue(folderId, out var registeredItem) || registeredItem is not IFolder folder)
                throw new McpException($"Folder with ID '{folderId}' not found", McpErrorCode.InvalidParams);

            var items = new List<object>();
            await foreach (var item in folder.GetItemsAsync())
            {
                string itemId = ProtocolRegistry.IsCustomProtocol(folderId) ? CreateCustomItemId(folderId, item.Name) : item.Id;
                _storableRegistry[itemId] = item;
                string externalId = ProtocolRegistry.SubstituteWithMountAlias(itemId);
                if (externalId != itemId)
                    _storableRegistry[externalId] = item;
                items.Add(new { id = externalId, name = item.Name, type = item switch { IFile => "file", IFolder => "folder", _ => "unknown" } });
            }
            return items.ToArray();
        }
        catch (McpException) { throw; }
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
            // Quick validation for obviously invalid IDs
            if (string.IsNullOrWhiteSpace(folderId))
                throw new McpException("Folder ID cannot be empty", McpErrorCode.InvalidParams);

            await EnsureStorableRegistered(folderId);

            // Archive guidance
            if (_storableRegistry.TryGetValue(folderId, out var origItem2) && origItem2 is IFile && ProtocolRegistry.TryGetArchiveMountScheme(folderId, out var archiveScheme2) && !folderId.EndsWith("://"))
                throw new McpException($"Archive file '{folderId}' is mounted as '{archiveScheme2}://'. Enumerate via '{archiveScheme2}://' instead of the physical file path.", McpErrorCode.InvalidParams);

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
            // Quick validation for obviously invalid IDs
            if (string.IsNullOrWhiteSpace(folderId))
                throw new McpException("Folder ID cannot be empty", McpErrorCode.InvalidParams);

            await EnsureStorableRegistered(folderId);

            // Archive guidance
            if (_storableRegistry.TryGetValue(folderId, out var origItem3) && origItem3 is IFile && ProtocolRegistry.TryGetArchiveMountScheme(folderId, out var archiveScheme3) && !folderId.EndsWith("://"))
                throw new McpException($"Archive file '{folderId}' is mounted as '{archiveScheme3}://'. Enumerate via '{archiveScheme3}://' instead of the physical file path.", McpErrorCode.InvalidParams);

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

    [McpServerTool, Description("Gets an item with a known ID by recursively searching through a folder hierarchy. The targetItemId must be a known storable ID, not a filename or search term.")]
    public static async Task<object?> GetItemRecursively(string folderId, string targetItemId)
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
            if (string.IsNullOrWhiteSpace(startingItemId))
                throw new McpException("Starting item ID cannot be empty", McpErrorCode.InvalidParams);

            await EnsureStorableRegistered(startingItemId);

            if (!_storableRegistry.TryGetValue(startingItemId, out var startingItem))
            {
                var availableDrives = await GetAvailableDrives();
                var driveList = string.Join(", ", availableDrives.Cast<dynamic>().Select(d => $"'{d.id}'"));
                throw new McpException($"Starting item with ID '{startingItemId}' not found. For new navigation, use drive roots from GetAvailableDrives(): {driveList}", McpErrorCode.InvalidParams);
            }

            IStorable? lastItem = null;

            // Register each item along the navigation chain during iteration (non-creating traversal)
            await foreach (var node in startingItem.GetItemsAlongRelativePathAsync(relativePath, CancellationToken.None))
            {
                _storableRegistry[node.Id] = node;
                var aliasId = ProtocolRegistry.SubstituteWithMountAlias(node.Id);
                if (aliasId != node.Id)
                    _storableRegistry[aliasId] = node;

                lastItem = node;
            }

            Guard.IsNotNull(lastItem);
            var targetItem = lastItem;
            _storableRegistry[targetItem.Id] = targetItem;

            var externalId = ProtocolRegistry.SubstituteWithMountAlias(targetItem.Id);
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
            throw;
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to navigate to '{relativePath}' from '{startingItemId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Gets a relative path from a folder to a child item and registers the chain along that path.")]
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

            var relative = await fromFolder.GetRelativePathToAsync(toChild, CancellationToken.None);

            IStorable? lastItem = null;

            // Register each visited node along the computed relative path (non-creating traversal)
            await foreach (var node in fromFolder.GetItemsAlongRelativePathAsync(relative, CancellationToken.None))
            {
                _storableRegistry[node.Id] = node;
                var aliasId = ProtocolRegistry.SubstituteWithMountAlias(node.Id);
                if (aliasId != node.Id)
                    _storableRegistry[aliasId] = node;
            }

            return relative;
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

    [McpServerTool, Description("Reads the content of a file as bytes by file ID, path, or URL.")]
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

    [McpServerTool, Description("Reads the content of a file as text with specified encoding. Works with both browseable (local storage, memory, ipfs, ipns, mfs) and non-browseable (http, https) supported protocols.")]
    public static async Task<string> ReadFileAsText([Description("The ID of the file to read.")] string fileId, string encoding = "UTF-8")
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

    [McpServerTool, Description("Reads file text from http, https, local storage, memory, ipfs, ipns, mfs, and all other supported protocols.")]
    public static async Task<string> ReadFileTextRange([Description("The ID of the file to read.")] string fileId, [Description("1-based indexing.")] int startLine, [Description("Omit this to read to end of file.")] int? endLine = null)
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

    [McpServerTool, Description("Lists all supported storage protocols and their capabilities (mfs, memory, http, ipfs, ipns).")]
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

    [McpServerTool, Description("Mounts an existing folder OR supported archive file as a browsable drive with a custom protocol scheme. The mounted item will appear in GetAvailableDrives() and can be browsed like any other drive.")]
    public static async Task<object> MountFolder(
        [Description("The ID or path of the folder or archive file to mount")] string folderId,
        [Description("The custom protocol scheme to use (e.g., 'myproject', 'backup', 'archive')")] string protocolScheme,
        [Description("Display name for the mounted item")] string mountName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folderId))
                throw new McpException("ID cannot be null or empty", McpErrorCode.InvalidParams);
            if (string.IsNullOrWhiteSpace(protocolScheme))
                throw new McpException("Protocol scheme cannot be null or empty", McpErrorCode.InvalidParams);
            if (string.IsNullOrWhiteSpace(mountName))
                throw new McpException("Mount name cannot be null or empty", McpErrorCode.InvalidParams);
            if (protocolScheme.Contains("://") || protocolScheme.Contains("/") || protocolScheme.Contains("\\"))
                throw new McpException("Protocol scheme must be a simple identifier without special characters", McpErrorCode.InvalidParams);

            await EnsureStorableRegistered(folderId);
            if (!_storableRegistry.TryGetValue(folderId, out var registeredItem))
                throw new McpException($"Item with ID '{folderId}' not found", McpErrorCode.InvalidParams);

            if (registeredItem is not IFolder && registeredItem is not IFile)
                throw new McpException("Item must be either a folder or file", McpErrorCode.InvalidParams);

            var rootUri = ProtocolRegistry.MountStorable(registeredItem, protocolScheme, mountName, folderId);

            // Removed explicit registration of rootUri to avoid forcing a specific representation.
            // Root folder (including archive wrapper) will be created lazily on first access via EnsureStorableRegistered.

            var mountSettings = ProtocolRegistry.GetMountSettings();
            if (mountSettings != null)
            {
                await mountSettings.SaveAsync();
            }

            return new
            {
                success = true,
                rootUri,
                protocolScheme,
                mountName,
                originalId = folderId,
                message = $"Successfully mounted '{mountName}' as {protocolScheme}://"
            };
        }
        catch (McpException) { throw; }
        catch (ArgumentException ex)
        {
            throw new McpException($"Failed to mount: {ex.Message}", ex, McpErrorCode.InvalidParams);
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to mount '{folderId}': {ex.Message}", ex, McpErrorCode.InternalError);
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

            var wasUnmounted = await ProtocolRegistry.UnmountFolder(protocolScheme);
            
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

// Issue003 helper: trim trailing slashes from scheme-form IDs (except roots) without touching internal (/...) IDs.
internal static class StoragePathNormalizer
{
    internal static string NormalizeExternalId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        if (raw.StartsWith('/')) return raw; // internal canonical IDs untouched
        var sep = raw.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0) return raw; // not a scheme-form ID
        if (raw.EndsWith("://", StringComparison.Ordinal)) return raw; // root URI
        int end = raw.Length - 1;
        while (end >= 0 && raw[end] == '/') end--; // trim all trailing slashes
        if (end == raw.Length - 1) return raw; // no trailing slash
        return raw.Substring(0, end + 1);
    }
}
