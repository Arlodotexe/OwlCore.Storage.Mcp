using ModelContextProtocol.Server;
using ModelContextProtocol;
using System.ComponentModel;
using OwlCore.Storage;
using OwlCore.Storage.System.IO;
using OwlCore.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Ipfs.Http;
using CommunityToolkit.Diagnostics;

namespace OwlCore.Storage.Mcp;

public static class StorageTools
{
    internal static readonly ConcurrentDictionary<string, IStorable> _storableRegistry = new();
    private static volatile bool _isInitialized = false;
    private static readonly SemaphoreSlim _initializationSemaphore = new(1, 1);
    // Semaphores are never pruned; entries accumulate for every distinct file.Id ever accessed.
    internal static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileAccessSemaphores = new();

    internal static string NormalizeOutboundAliasId(string id, IStorable item)
    {
        id = EnsureFolderTrailingSlash(id, item);
        id = NormalizeForwardSlashes(id, item);
        return id;
    }

    /// <summary>
    /// Ensures folder IDs consistently end with a trailing slash.
    /// File IDs are returned unchanged.
    /// </summary>
    internal static string EnsureFolderTrailingSlash(string id, IStorable item)
    {
        if (item is IFolder && !id.EndsWith("/"))
            return id + "/";

        return id;
    }

    internal static string NormalizeForwardSlashes(string id, IStorable item)
    {
        while (item is IFolder && id.Contains("\\"))
        {
            id = id.Replace('\\', '/');
        }
        
        return id;
    }

    // Issue003: inbound canonicalization for browsable protocols only (filesystem-like); resource protocols keep original form.
    private static string NormalizeInboundExternalId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return id;

        var sep = id.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0)
            return id; // not a scheme-form ID

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
    private static async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_isInitialized) return;

        await _initializationSemaphore.WaitAsync();
        try
        {
            if (_isInitialized) return; // Double-check after acquiring lock

            // Initialize ProtocolRegistry and restore mounts
            await ProtocolRegistry.EnsureInitializedAsync(cancellationToken);

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
                            Logger.LogInformation($"Pre-registered protocol root: {rootUri}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogInformation($"Failed to pre-register {rootUri}: {ex.Message}");
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

    internal static async Task EnsureStorableRegistered(string id, CancellationToken cancellationToken)
    {
        // Ensure the storage system is fully initialized first
        await EnsureInitializedAsync(cancellationToken);

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
            Logger.LogInformation($"[STORAGE] Item already registered: {id}");
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
            Logger.LogInformation($"[STORAGE] Alias {id} resolved to existing item: {resolvedId}");
            return;
        }

        // If the ID is a mounted-folder alias with a path component, auto-navigate from the mount root.
        // This lets callers access items by alias ID (e.g., skills://terminal/pwsh/file.md) without
        // having to manually navigate from root first.
        var idScheme = ProtocolRegistry.ExtractScheme(id);
        if (idScheme != null && ProtocolRegistry.IsMountedFolder(idScheme))
        {
            var mountRootUri = $"{idScheme}://";
            var pathPortion = id.Substring(mountRootUri.Length);
            if (!string.IsNullOrEmpty(pathPortion) && _storableRegistry.TryGetValue(mountRootUri, out var mountRoot) && mountRoot is IFolder mountRootFolder)
            {
                try
                {
                    // Navigate from mount root along the relative path
                    IStorable? lastNode = null;
                    await foreach (var node in mountRootFolder.GetItemsAlongRelativePathAsync(pathPortion, cancellationToken))
                    {
                        _storableRegistry[node.Id] = node;
                        var aliasId = ProtocolRegistry.SubstituteWithMountAlias(node.Id);
                        if (aliasId != node.Id)
                            _storableRegistry[aliasId] = node;
                        var normalizedAliasId = NormalizeOutboundAliasId(aliasId, node);
                        _storableRegistry[normalizedAliasId] = node;
                        lastNode = node;
                    }

                    if (lastNode != null)
                    {
                        // Also register under the original requested ID
                        _storableRegistry[id] = lastNode;
                        Logger.LogInformation($"[STORAGE] Auto-navigated mount alias {id} to item: {lastNode.Id}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogInformation($"[STORAGE] Auto-navigation for mount alias {id} failed: {ex.Message}");
                    // Fall through to standard resolution
                }
            }
        }

        // Use the resolved ID for registration attempts
        var registrationId = resolvedId;
        Logger.LogInformation($"[STORAGE] Attempting to register: {registrationId}");

        // Handle regular filesystem paths first (fast path)
        if (Directory.Exists(registrationId))
        {
            // Check for ambiguity: this native ID may also be the root of a protocol or mount.
            // If so, the model should use the protocol alias instead of the raw path.
            var aliases = await ProtocolRegistry.GetAllAliasesForNativeIdAsync(registrationId);
            if (aliases.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Ambiguous ID '{registrationId}': this path is also the underlying root of {aliases.Count} protocol(s). " +
                    $"Use the protocol alias instead: {string.Join(", ", aliases.Select(a => $"'{a}'"))}. " +
                    $"These aliases disambiguate between storage implementations that share the same native ID.");
            }

            var folder = new SystemFolder(new DirectoryInfo(registrationId));
            _storableRegistry[registrationId] = folder;
            if (id != registrationId)
                _storableRegistry[id] = folder;
            Logger.LogInformation($"[STORAGE] Registered system folder: {registrationId}");
            return;
        }
        else if (File.Exists(registrationId))
        {
            var file = new SystemFile(new FileInfo(registrationId));
            _storableRegistry[registrationId] = file;
            if (id != registrationId)
                _storableRegistry[id] = file;
            Logger.LogInformation($"[STORAGE] Registered system file: {registrationId}");
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

            Logger.LogInformation($"[STORAGE] Found protocol handler for {registrationId}: {protocolHandler.GetType().Name}");

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
                        Logger.LogInformation($"[STORAGE] Successfully registered root: {registrationId}");
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
                    Logger.LogInformation($"[STORAGE] Successfully registered resource: {registrationId}");
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.LogInformation($"[STORAGE] Failed to register {registrationId}: {ex.Message}");
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

    [Description("Gets the available browseable drives, both system and mounted. Use these drive IDs as starting points for GetItemByRelativePath navigation.")]
    public static async Task<DriveInfoResult[]> GetAvailableDrives()
    {
        var driveInfos = new List<DriveInfoResult>();
        var cancellationToken = CancellationToken.None;

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
                driveInfos.Add(new DriveInfoResult(
                    Id: drive.RootDirectory.FullName,
                    Name: !string.IsNullOrEmpty(drive.VolumeLabel) ? $"{drive.Name} ({drive.VolumeLabel})" : drive.Name,
                    Type: "drive",
                    DriveType: drive.DriveType.ToString(),
                    IsReady: drive.IsReady,
                    TotalSize: drive.IsReady ? drive.TotalSize : 0,
                    AvailableFreeSpace: drive.IsReady ? drive.AvailableFreeSpace : 0
                ));
            }
            catch
            {
                // Skip drives that aren't ready or throw errors
                continue;
            }
        }

        // Add mounted folders
        await ProtocolRegistry.EnsureInitializedAsync(cancellationToken);
        try
        {
            var mountedFolders = ProtocolRegistry.GetMountedFolders();
            foreach (var mount in mountedFolders)
            {
                driveInfos.Add(new DriveInfoResult(
                    Id: mount.RootUri,
                    Name: $"Mounted: {mount.MountName}",
                    Type: "mounted-folder",
                    DriveType: "NetworkDrive",
                    IsReady: true,
                    TotalSize: -1L,
                    AvailableFreeSpace: -1L
                ));
            }
        }
        catch (Exception ex)
        {
            Logger.LogInformation($"Failed to get mounted folders: {ex.Message}");
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

    [Description($"Lists all items in a folder by ID or path. Returns array of items with their IDs, names, and types. Prefer {nameof(GetItemByRelativePath)} for hierarchical or path-based navigation.")]
    public static async Task<PaginatedItemsResult> GetFolderItems(string folderId, [Description("Maximum number of results to return. Default 50.")] int maxResults = 50, [Description("Number of items to skip for pagination. Default 0.")] int skip = 0)
    {
        var cancellationToken = CancellationToken.None;

        try
        {
            // Inbound normalization for browsable protocols only (Issue 003 symmetry)
            folderId = NormalizeInboundExternalId(folderId);
            if (string.IsNullOrWhiteSpace(folderId))
                throw new McpException("Folder ID cannot be empty", McpErrorCode.InvalidParams);

            await EnsureStorableRegistered(folderId, cancellationToken);

            // Archive guidance
            if (_storableRegistry.TryGetValue(folderId, out var origItem) && origItem is IFile && ProtocolRegistry.TryGetArchiveMountScheme(folderId, out var archiveScheme) && !folderId.EndsWith("://"))
                throw new McpException($"Archive file '{folderId}' is mounted as '{archiveScheme}://'. Enumerate via '{archiveScheme}://' instead of the physical file path.", McpErrorCode.InvalidParams);

            if (!_storableRegistry.TryGetValue(folderId, out var registeredItem) || registeredItem is not IFolder folder)
                throw new McpException($"Folder with ID '{folderId}' not found", McpErrorCode.InvalidParams);

            var items = new List<StorableItemResult>();
            int index = 0;
            int collected = 0;
            await foreach (var item in folder.GetItemsAsync())
            {
                string itemId = ProtocolRegistry.IsCustomProtocol(folderId) ? CreateCustomItemId(folderId, item.Name) : item.Id;
                _storableRegistry[itemId] = item;
                string externalId = ProtocolRegistry.SubstituteWithMountAlias(itemId);
                if (externalId != itemId)
                    _storableRegistry[externalId] = item;
                externalId = NormalizeOutboundAliasId(externalId, item);
                _storableRegistry[externalId] = item;

                if (index >= skip && collected < maxResults)
                {
                    items.Add(new StorableItemResult(Id: externalId, Name: item.Name, Type: item switch { IFile => "file", IFolder => "folder", _ => "unknown" }));
                    collected++;
                }
                index++;
            }
            return new PaginatedItemsResult(Items: items.ToArray(), TotalCount: index, HasMore: index > skip + collected);
        }
        catch (McpException) { throw; }
        catch (Exception ex)
        {
            throw new McpException($"Failed to get folder items for '{folderId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [Description("Gets an item with a known ID by recursively searching through a folder hierarchy. The targetItemId must be a known storable ID, not a filename or search term.")]
    public static async Task<StorableItemResult?> GetItemRecursivelyById(string folderId, string targetItemId)
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            await EnsureStorableRegistered(folderId, cancellationToken);

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
                externalId = NormalizeOutboundAliasId(externalId, foundItem);
                _storableRegistry[externalId] = foundItem;

                return new StorableItemResult(
                    Id: externalId,
                    Name: foundItem.Name,
                    Type: foundItem switch
                    {
                        IFile => "file",
                        IFolder => "folder",
                        _ => "unknown"
                    }
                );
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


    [Description("Searches for files and folders by name pattern within a folder hierarchy. Uses depth-first recursive traversal. Supports glob patterns (e.g., '*.cs', 'src/**/*.json') and regex patterns.")]
    public static async Task<FindResultWithMatches[]> FindAll(
        [Description("The ID of the folder to search within.")] string folderId,
        [Description($"Glob pattern to match against storable item names. Within a single storable file/folder name, '*' for any or no chars, '?' for single char, '**' for recursive directory match. Examples: '*.cs', 'test*', '**/*.json', '*foldername*'. Optional param, searches all storables recursively if excluded. Either this, {nameof(fileContentRegex)}, or both must be included and non-empty.")] string? nameOrPathGlob = null,
        [Description($"Regex pattern to search within file contents. Only files are content-searched. Matched lines are returned with line numbers. Optional param, surfaces storables but not content if excluded. Either this, {nameof(nameOrPathGlob)} or both must be included and non-empty.")] string? fileContentRegex = null,
        [Description("What to filter for glob and regex matches: 'all' (default), 'file', or 'folder'. ")] string storableTypeToMatch = "all",
        [Description("Maximum number of results to return. Default 100.")] int maxResults = 100)
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            if (string.IsNullOrWhiteSpace(folderId))
                throw new McpException("Folder ID cannot be empty", McpErrorCode.InvalidParams);
            if (string.IsNullOrWhiteSpace(nameOrPathGlob) && string.IsNullOrWhiteSpace(fileContentRegex))
                throw new McpException("At least one of 'nameOrPathGlob' or 'fileContentRegex' must be provided.", McpErrorCode.InvalidParams);
            if (maxResults <= 0)
                throw new McpException("maxResults must be a positive integer", McpErrorCode.InvalidParams);

            if (fileContentRegex is not null && string.IsNullOrWhiteSpace(fileContentRegex) && !string.IsNullOrWhiteSpace(nameOrPathGlob))
            {
                throw new McpException("Empty regex cannot be used to find text or glob for files. Either include regex or exclude the parameter altogether.");
            }

            folderId = NormalizeInboundExternalId(folderId);
            await EnsureStorableRegistered(folderId, cancellationToken);

            if (!_storableRegistry.TryGetValue(folderId, out var registeredItem) || registeredItem is not IFolder folder)
                throw new McpException($"Folder with ID '{folderId}' not found", McpErrorCode.InvalidParams);

            // Build name glob regex
            Regex? nameRegex = null;
            if (!string.IsNullOrWhiteSpace(nameOrPathGlob))
            {
                try
                {
                    nameRegex = new Regex(GlobToRegex(nameOrPathGlob), RegexOptions.IgnoreCase | RegexOptions.Compiled);
                }
                catch (ArgumentException ex)
                {
                    throw new McpException($"Invalid glob pattern '{nameOrPathGlob}': {ex.Message}", McpErrorCode.InvalidParams);
                }
            }

            // Build content regex
            Regex? fileContentRegexCompiled = null;
            if (!string.IsNullOrWhiteSpace(fileContentRegex))
            {
                try
                {
                    fileContentRegexCompiled = new Regex(fileContentRegex, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                }
                catch (ArgumentException ex)
                {
                    throw new McpException($"Invalid content regex '{fileContentRegex}': {ex.Message}", McpErrorCode.InvalidParams);
                }
            }

            // Determine StorableType filter
            var storableType = storableTypeToMatch.ToLowerInvariant() switch
            {
                "all" => StorableType.All,
                "file" => StorableType.File,
                "folder" => StorableType.Folder,
                _ => throw new McpException($"Invalid itemType '{storableTypeToMatch}'. Use 'all', 'file', or 'folder'.", McpErrorCode.InvalidParams)
            };

            var recursive = new DepthFirstRecursiveFolder(folder);
            var results = new List<FindResultWithMatches>();

            await foreach (var item in recursive.GetItemsAsync(storableType, cancellationToken))
            {
                // Name filter
                if (nameRegex != null && !nameRegex.IsMatch(item.Name))
                    continue;

                // Register the item using its real ID — unlike flat folder listings,
                // recursive results may be many levels deep, so we can't assume parentId == folderId.
                _storableRegistry[item.Id] = item;
                await EnsureStorableRegistered(item.Id, cancellationToken);

                string externalId = ProtocolRegistry.SubstituteWithMountAlias(item.Id);
                await EnsureStorableRegistered(externalId, cancellationToken);

                var typeStr = item switch
                {
                    IFile => "file",
                    IFolder => "folder",
                    _ => "unknown"
                };

                // Content filter (files only)
                if (fileContentRegexCompiled != null && item is IFile file)
                {
                    try
                    {
                        var fileSem = _fileAccessSemaphores.GetOrAdd(file.Id, _ => new SemaphoreSlim(1, 1));
                        await fileSem.WaitAsync(CancellationToken.None);
                        string content;
                        try { content = await file.ReadTextAsync(CancellationToken.None); }
                        finally { fileSem.Release(); }
                        var lines2 = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                        var matches = new List<ContentMatchLine>();

                        for (int i = 0; i < lines2.Length; i++)
                        {
                            if (fileContentRegexCompiled.IsMatch(lines2[i]))
                                matches.Add(new ContentMatchLine(Line: i + 1, Text: lines2[i].TrimEnd()));
                        }

                        if (matches.Count == 0)
                            continue;

                        results.Add(new FindResultWithMatches(Id: externalId, Name: item.Name, Type: typeStr, Matches: matches.ToArray()));
                    }
                    catch
                    {
                        continue; // Skip files that can't be read as text
                    }
                }
                else if (fileContentRegexCompiled != null && item is not IFile)
                {
                    continue; // Content search requested but item is a folder
                }
                else
                {
                    results.Add(new FindResultWithMatches(Id: externalId, Name: item.Name, Type: typeStr));
                }

                if (results.Count >= maxResults)
                    break;
            }

            return results.ToArray();
        }
        catch (McpException) { throw; }
        catch (Exception ex)
        {
            throw new McpException($"Failed to search in '{folderId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    /// <summary>
    /// Converts a glob pattern to a regex pattern.
    /// Supports: * (any chars except separator), ? (single char), **/ (recursive directory match).
    /// </summary>
    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        int i = 0;
        while (i < glob.Length)
        {
            char c = glob[i];
            if (c == '*')
            {
                // Check for **/ or ** (recursive match — matches everything including separators)
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    i += 2;
                    // skip optional following / or backslash
                    if (i < glob.Length && (glob[i] == '/' || glob[i] == '\\'))
                        i++;
                    sb.Append(".*");
                }
                else
                {
                    // Single * — match any characters (name matching, no separator restriction needed)
                    sb.Append(".*");
                    i++;
                }
            }
            else if (c == '?')
            {
                sb.Append('.');
                i++;
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
        }
        sb.Append('$');
        return sb.ToString();
    }

    [Description("Navigates to an item using a relative path from a starting item. Accepts both forward and backslashes.")]
    public static async Task<StorableItemResult> GetItemByRelativePath(string startingItemId, string relativePath)
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            if (string.IsNullOrWhiteSpace(startingItemId))
                throw new McpException("Starting item ID cannot be empty", McpErrorCode.InvalidParams);

            await EnsureStorableRegistered(startingItemId, cancellationToken);

            // Normalize relative path — ensure it starts with "./" as required by the storage API
            if (!string.IsNullOrEmpty(relativePath) && !relativePath.StartsWith("./") && !relativePath.StartsWith("../"))
                relativePath = "./" + relativePath;

            if (!_storableRegistry.TryGetValue(startingItemId, out var startingItem))
            {
                var availableDrives = await GetAvailableDrives();
                var driveList = string.Join(", ", availableDrives.Select(d => $"'{d.Id}'"));
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
                var normalizedAliasId = NormalizeOutboundAliasId(aliasId, node);
                _storableRegistry[normalizedAliasId] = node;

                lastItem = node;
            }

            Guard.IsNotNull(lastItem);
            var targetItem = lastItem;
            _storableRegistry[targetItem.Id] = targetItem;

            var externalId = ProtocolRegistry.SubstituteWithMountAlias(targetItem.Id);
            if (externalId != targetItem.Id)
                _storableRegistry[externalId] = targetItem;
            externalId = NormalizeOutboundAliasId(externalId, targetItem);
            _storableRegistry[externalId] = targetItem;

            return new StorableItemResult(
                Id: externalId,
                Name: targetItem.Name,
                Type: targetItem switch
                {
                    IFile => "file",
                    IFolder => "folder",
                    _ => "unknown"
                }
            );
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

    [Description("Gets a relative path from a folder to a child item and registers the chain along that path.")]
    public static async Task<string> GetRelativePath(string fromFolderId, string toItemId)
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            await EnsureStorableRegistered(fromFolderId, cancellationToken);
            await EnsureStorableRegistered(toItemId, cancellationToken);

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
                var normalizedAliasId = NormalizeOutboundAliasId(aliasId, node);
                _storableRegistry[normalizedAliasId] = node;
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

    private const int ReadFileAsTextDefaultMaxLines = 100;
    private const int ReadFileAsTextDefaultMaxColumns = 256;

    private static string ApplyDefaultReadFileAsTextTruncation(string content)
    {
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var selectedLineCount = Math.Min(lines.Length, ReadFileAsTextDefaultMaxLines);
        var selectedLines = new string[selectedLineCount];
        var lineCountTruncated = lines.Length > ReadFileAsTextDefaultMaxLines;
        var columnTruncationDetails = new List<string>();

        for (int i = 0; i < selectedLineCount; i++)
        {
            var line = lines[i];

            if (line.Length > ReadFileAsTextDefaultMaxColumns)
            {
                selectedLines[i] = line[..ReadFileAsTextDefaultMaxColumns];
                var excludedColumns = line.Length - ReadFileAsTextDefaultMaxColumns;
                var columnLabel = excludedColumns == 1 ? "column" : "columns";
                columnTruncationDetails.Add($"{excludedColumns} {columnLabel} on line {i + 1}");
            }
            else
            {
                selectedLines[i] = line;
            }
        }

        if (!lineCountTruncated && columnTruncationDetails.Count == 0)
            return content;

        var excludedLineCount = Math.Max(0, lines.Length - selectedLineCount);
        var truncationParts = new List<string>();
        if (excludedLineCount > 0)
        {
            var lineLabel = excludedLineCount == 1 ? "line" : "lines";
            truncationParts.Add($"{excludedLineCount} {lineLabel}");
        }
        if (columnTruncationDetails.Count > 0)
            truncationParts.Add(string.Join(", ", columnTruncationDetails));

        return string.Join('\n', selectedLines)
            + $"\n\n[Output truncated, excluded {string.Join(", ", truncationParts)}. Use read_file_text_range for larger or more precise reads.]";
    }

    [Description("Reads a preview of file text, limited to 100 lines and 256 columns per line. Use for small files or quick previews. For larger or precise reads, use get_storable_info first, then use read_file_text_range.")]
    public static async Task<string> ReadFileAsText([Description("The ID of the file to read.")] string fileId, string encoding = "UTF-8")
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            await EnsureStorableRegistered(fileId, cancellationToken);

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

            var fileSem = _fileAccessSemaphores.GetOrAdd(file.Id, _ => new SemaphoreSlim(1, 1));
            await fileSem.WaitAsync(cancellationToken);
            string content;
            try { content = await file.ReadTextAsync(textEncoding, CancellationToken.None); }
            finally { fileSem.Release(); }
            return ApplyDefaultReadFileAsTextTruncation(content);
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

    private const int ReadFileTextRangeMaxBytes = 8 * 1024 - 192; // 8 KB minus overhead for truncation message

    [Description("Reads file text from http, https, local storage, memory, ipfs, ipns, mfs, and all other supported protocols. Max 8KB reads per call, tool result tells you where to resume if truncated.")]
    public static async Task<string> ReadFileTextRange([Description("The ID of the file to read.")] string fileId, [Description("1-based indexing.")] int startLine, [Description("Omit this to read to end of file. Prefer including when known.")] int? endLine = null, int? columnLimit = ReadFileTextRangeMaxBytes, [Description("Set true to prefix each line content with its exact line number as [LX]. Disable when only gist is being read rather than verbatim details being read/written.")] bool prefixLineNumbers = true)
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            await EnsureStorableRegistered(fileId, cancellationToken);

            if (!_storableRegistry.TryGetValue(fileId, out var item) || item is not IFile file)
                throw new McpException($"File with ID '{fileId}' not found", McpErrorCode.InvalidParams);

            // Explicitly reject endLine of 0 since it's invalid (1-based indexing)
            if (endLine.HasValue && endLine.Value <= 0)
                throw new McpException($"Invalid endLine value: {endLine.Value}. endLine must be >= 1 (1-based indexing) or null to read to end. To read to end, omit endLine entirely.", McpErrorCode.InvalidParams);

            // Validate columnLimit when provided
            if (columnLimit.HasValue && columnLimit.Value <= 0)
                throw new McpException($"Invalid columnLimit: {columnLimit.Value}. Must be a positive integer, or null to disable the limit.", McpErrorCode.InvalidParams);

            var fileSem = _fileAccessSemaphores.GetOrAdd(file.Id, _ => new SemaphoreSlim(1, 1));
            await fileSem.WaitAsync(cancellationToken);
            string content;
            try { content = await file.ReadTextAsync(CancellationToken.None); }
            finally { fileSem.Release(); }
            var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

            // Validate line numbers (1-based)
            if (startLine < 1 || startLine > lines.Length)
                throw new McpException($"Invalid startLine: {startLine}. Stop blindly reading and use get_storable_info upfront for line count. Must be between 1 and {lines.Length} (file has {lines.Length} lines)", McpErrorCode.InvalidParams);

            int actualEndLine = endLine ?? lines.Length;
            if (actualEndLine < startLine || actualEndLine > lines.Length)
                throw new McpException($"Invalid endLine: {actualEndLine}. Must be between {startLine} and {lines.Length}. Stop blindly reading and use get_storable_info upfront for line count.", McpErrorCode.InvalidParams);

            // Extract the requested range (convert to 0-based indexing)
            var selectedLines = lines[(startLine - 1)..actualEndLine];

            // Prefix line numbers if requested
            if (prefixLineNumbers)
            {
                for (int i = 0; i < selectedLines.Length; i++)
                {
                    int lineNumber = startLine + i;
                    selectedLines[i] = $"[L{lineNumber}]{selectedLines[i]}";
                }
            }

            // Apply per-line column limit if specified
            if (columnLimit is int maxCols)
            {
                for (int i = 0; i < selectedLines.Length; i++)
                {
                    var line = selectedLines[i];
                    if (line.Length > maxCols)
                    {
                        selectedLines[i] = line.Substring(0, maxCols);
                    }
                }
            }

            var result = string.Join('\n', selectedLines);

            if (Encoding.UTF8.GetByteCount(result) > ReadFileTextRangeMaxBytes)
            {
                // Trim lines until we're under the limit
                int keep = selectedLines.Length;
                while (keep > 0)
                {
                    var trimmed = string.Join('\n', selectedLines[..keep]);
                    if (Encoding.UTF8.GetByteCount(trimmed) <= ReadFileTextRangeMaxBytes)
                    {
                        var excludedLines = actualEndLine - (startLine - 1 + keep);
                        return trimmed
                            + $"\n\n[Output truncated to {ReadFileTextRangeMaxBytes} bytes. "
                            + $"{excludedLines} lines excluded from requested range. Read from startLine {startLine + keep} to continue.]";
                    }
                    keep--;
                }
            }

            return result;
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

    [Description("Gets information about seen storable items by its ID, including custom mount aliases. Recommended for precise ranged read/write windows or for incremental/re-reads to discern updates or appends. Yields ID, name, storage type, size in bytes, line count, and datetime property metadata.")]
    public static async Task<StorableInfoResult[]> GetStorableInfos(string[] ids)
    {
        var results = new List<StorableInfoResult>();
        foreach (var id in ids)
        {
            var cancellationToken = CancellationToken.None;
            try
            {
                await EnsureStorableRegistered(id, cancellationToken);

                if (!_storableRegistry.TryGetValue(id, out var storable))
                    throw new McpException($"Item with ID '{id}' not found", McpErrorCode.InvalidParams);

                var typeStr = storable switch
                {
                    IFile => "file",
                    IFolder => "folder",
                    _ => "unknown"
                };

                // For files, get size (always) and line count (text only)
                long? sizeBytes = null;
                int? lineCount = null;
                if (storable is IFile file)
                {
                    try
                    {
                        var fileSem1 = _fileAccessSemaphores.GetOrAdd(file.Id, _ => new SemaphoreSlim(1, 1));
                        await fileSem1.WaitAsync(CancellationToken.None);
                        try
                        {
                            using var stream = await file.OpenStreamAsync(FileAccess.Read, CancellationToken.None);
                            sizeBytes = stream.Length;
                        }
                        finally { fileSem1.Release(); }
                    }
                    catch
                    {
                        // Stream not available or not seekable
                    }

                    try
                    {
                        var fileSem2 = _fileAccessSemaphores.GetOrAdd(file.Id, _ => new SemaphoreSlim(1, 1));
                        await fileSem2.WaitAsync(CancellationToken.None);
                        string content;
                        try { content = await file.ReadTextAsync(CancellationToken.None); }
                        finally { fileSem2.Release(); }
                        if (sizeBytes == null)
                            sizeBytes = Encoding.UTF8.GetByteCount(content);
                        lineCount = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).Length;
                    }
                    catch
                    {
                        // Not a text file or unreadable — lineCount stays null
                    }
                }


                results.Add(new StorableInfoResult(
                    Id: storable.Id,
                    Name: storable.Name,
                    Type: typeStr,
                    SizeBytes: sizeBytes,
                    LineCount: lineCount,
                    LastModifiedAt: storable is ILastModifiedAt lastModifiedAt ? await lastModifiedAt.LastModifiedAt.GetValueAsync(cancellationToken) : null,
                    LastAccessedAt: storable is ILastAccessedAt lastAccessedAt ? await lastAccessedAt.LastAccessedAt.GetValueAsync(cancellationToken) : null,
                    CreatedAt: storable is ICreatedAt createdAt ? await createdAt.CreatedAt.GetValueAsync(cancellationToken) : null
                ));
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

        return results.ToArray();
    }

    [Description("Gets the root folder of a given storage item id.")]
    public static async Task<StorableItemResult?> GetRootFolder(string itemId)
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            await EnsureStorableRegistered(itemId, cancellationToken);

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
            externalId = NormalizeOutboundAliasId(externalId, rootFolder);
            _storableRegistry[externalId] = rootFolder;

            return new StorableItemResult(
                Id: externalId,
                Name: rootFolder.Name,
                Type: "folder"
            );
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

    [Description("Gets a specific item by the item's ID from a folder.")]
    public static async Task<StorableItemResult> GetItemById(string folderId, string itemId)
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            await EnsureStorableRegistered(folderId, cancellationToken);

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
                externalId = NormalizeOutboundAliasId(externalId, foundItem);
                _storableRegistry[externalId] = foundItem;

                return new StorableItemResult(
                    Id: externalId,
                    Name: foundItem.Name,
                    Type: foundItem switch
                    {
                        IFile => "file",
                        IFolder => "folder",
                        _ => "unknown"
                    }
                );
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

    [Description("Gets the parent folder of a storage item.")]
    public static async Task<StorableItemResult?> GetParentFolder(string itemId)
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            await EnsureStorableRegistered(itemId, cancellationToken);

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
            externalId = NormalizeOutboundAliasId(externalId, parentFolder);
            _storableRegistry[externalId] = parentFolder;

            return new StorableItemResult(
                Id: externalId,
                Name: parentFolder.Name,
                Type: "folder"
            );
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

    [Description("Lists all supported storage protocols and their capabilities (mfs, memory, http, ipfs, ipns, custom mounted ID aliases, etc.). Use these `scheme://` IDs as starting points for GetItemByRelativePath navigation.")]
    public static ProtocolInfoResult[] GetSupportedProtocols()
    {
        try
        {
            var protocols = new List<ProtocolInfoResult>();

            // Add built-in filesystem support
            protocols.Add(new ProtocolInfoResult(
                Scheme: "file",
                Name: "Local File System",
                Type: "filesystem",
                HasBrowsableRoot: true,
                SupportsDirectResources: false,
                Description: "Local disk drives and folders"
            ));

            // Add custom protocols
            foreach (var protocolScheme in ProtocolRegistry.GetRegisteredProtocols())
            {
                var rootUri = $"{protocolScheme}://";
                var protocolHandler = ProtocolRegistry.GetProtocolHandler(rootUri);

                if (protocolHandler != null)
                {
                    protocols.Add(new ProtocolInfoResult(
                        Scheme: protocolScheme,
                        Name: protocolScheme.ToUpper() + " Protocol",
                        Type: protocolHandler.HasBrowsableRoot ? "filesystem" : "resource",
                        HasBrowsableRoot: protocolHandler.HasBrowsableRoot,
                        SupportsDirectResources: !protocolHandler.HasBrowsableRoot,
                        Description: protocolScheme switch
                        {
                            "mfs" => "IPFS Mutable File System - browsable IPFS storage",
                            "memory" => "In-memory temporary storage for testing",
                            "http" or "https" => "HTTP/HTTPS web resources and files",
                            "ipfs" => "IPFS content addressed by hash - files or folders accessible by hash",
                            "ipns" => "IPNS names that resolve to IPFS content - files or folders accessible by name",
                            _ => $"Custom {protocolScheme} protocol"
                        }
                    ));
                }
            }

            return protocols.ToArray();
        }
        catch (McpException) { throw; }
        catch (Exception ex)
        {
            Logger.LogError($"{nameof(GetSupportedProtocols)} failed: {ex}", ex);
            throw new McpException($"Failed to get supported protocols: {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [Description("Mounts an existing folder OR supported archive file as a browsable drive with a custom protocol scheme. The mounted item will appear in available drives and can be browsed like any other drive.")]
    public static async Task<MountResult> MountFolder(
        [Description("The ID or path of the folder or archive file to mount")] string folderId,
        [Description("The custom protocol scheme to use (e.g., 'myproject', 'backup', 'archive')")] string protocolScheme,
        [Description("Display name for the mounted item")] string mountName)
    {
        var cancellationToken = CancellationToken.None;
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

            await EnsureStorableRegistered(folderId, cancellationToken);
            if (!_storableRegistry.TryGetValue(folderId, out var registeredItem))
                throw new McpException($"Item with ID '{folderId}' not found", McpErrorCode.InvalidParams);

            if (registeredItem is not IFolder && registeredItem is not IFile)
                throw new McpException("Item must be either a folder or file", McpErrorCode.InvalidParams);

            var rootUri = await ProtocolRegistry.MountStorable(registeredItem, protocolScheme, mountName, folderId);

            // Removed explicit registration of rootUri to avoid forcing a specific representation.
            // Root folder (including archive wrapper) will be created lazily on first access via EnsureStorableRegistered.

            var mountSettings = ProtocolRegistry.GetMountSettings();
            if (mountSettings != null)
            {
                await mountSettings.SaveAsync();
            }

            return new MountResult(
                Success: true,
                RootUri: rootUri,
                ProtocolScheme: protocolScheme,
                MountName: mountName,
                OriginalId: folderId,
                Message: $"Successfully mounted '{mountName}' as {protocolScheme}://"
            );
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

    [Description("Unmounts a previously mounted folder, removing it from available drives.")]
    public static async Task<UnmountResult> UnmountFolder(
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

                return new UnmountResult(
                    Success: true,
                    ProtocolScheme: protocolScheme,
                    Message: $"Successfully unmounted {protocolScheme}://"
                );
            }
            else
            {
                return new UnmountResult(
                    Success: false,
                    ProtocolScheme: protocolScheme,
                    Message: $"Protocol scheme '{protocolScheme}' not found or is not a mounted folder"
                );
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

    [Description("Lists all currently mounted folders and their information. Mounted folder `protocolScheme://`s are standalone IDs and are direct alias substitutes for other folder IDs (or file IDs in the case of archive folders). Use these mount `protocolScheme://` IDs as starting points for GetItemByRelativePath navigation.")]
    public static async Task<MountedFolderInfo[]> GetMountedFolders()
    {
        try
        {
            var cancellationToken = CancellationToken.None;
            await ProtocolRegistry.EnsureInitializedAsync(cancellationToken);
            return ProtocolRegistry.GetMountedFolders();
        }
        catch (McpException) { throw; }
        catch (Exception ex)
        {
            Logger.LogError($"{nameof(GetMountedFolders)} failed: {ex}", ex);
            throw new McpException($"Failed to get mounted folders: {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [Description("Renames a mounted folder's protocol scheme and/or display name. Preserves all existing references and dependencies.")]
    public static async Task<RenameMountResult> RenameMountedFolder(
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

            return new RenameMountResult(
                Success: true,
                OldProtocolScheme: currentProtocolScheme,
                NewProtocolScheme: newProtocolScheme ?? currentProtocolScheme,
                NewMountName: newMountName,
                NewRootUri: newRootUri,
                Message: $"Successfully renamed mount to {newRootUri}"
            );
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
    /// <summary>
    /// Normalizes external IDs for consistent registry lookup.
    /// </summary>
    /// <remarks>
    /// Does NOT strip trailing slashes — IDs are opaque and trailing characters may be
    /// semantically significant in the underlying storage implementation (e.g., MFS folder
    /// IDs end with '/'). Stripping them would break alias round-tripping.
    /// </remarks>
    internal static string NormalizeExternalId(string raw)
    {
        return raw;
    }
}