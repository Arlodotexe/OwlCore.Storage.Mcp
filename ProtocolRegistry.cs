using OwlCore.Storage.System.IO;
using OwlCore.Storage;
using System.Collections.Concurrent;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OwlCore.ComponentModel;
using OwlCore.Storage.SharpCompress;
using SharpCompress.Archives;
using OwlCore.Diagnostics;

namespace OwlCore.Storage.Mcp;

/// <summary>
/// Registry for custom storage protocols that maps protocol schemes to storage implementations
/// </summary>
public static class ProtocolRegistry
{
    private static readonly ConcurrentDictionary<string, IProtocolHandler> _protocolHandlers = new();
    private static readonly ConcurrentDictionary<string, MountedFolderProtocolHandler> _mountedFolders = new();
    private static readonly ConcurrentDictionary<string, string> _mountedOriginalIds = new(); // originalId -> protocolScheme
    private static MountSettings _mountSettings = null!; // Initialized in EnsureInitializedAsync
    
    static ProtocolRegistry()
    {
        // Register built-in protocol handlers
        RegisterProtocol("mfs", new IpfsMfsProtocolHandler());
        RegisterProtocol("http", new HttpProtocolHandler());
        RegisterProtocol("https", new HttpProtocolHandler());
        RegisterProtocol("ipfs", new IpfsProtocolHandler());
        RegisterProtocol("ipns", new IpnsProtocolHandler());
        RegisterProtocol("memory", new MemoryProtocolHandler());
        // Add more protocols here as needed
        // RegisterProtocol("azure-blob", new AzureBlobProtocolHandler());
        // RegisterProtocol("s3", new S3ProtocolHandler());
    }

    /// <summary>
    /// Ensures mount settings are initialized and mounts are restored
    /// Call this once during application startup
    /// </summary>
    public static async Task EnsureInitializedAsync()
    {
        if (_mountSettings != null!) return; // Already initialized
        
        await InitializeSettingsAndRestoreMountsAsync();
    }

    /// <summary>
    /// Initializes the settings system and restores persisted mounts
    /// </summary>
    private static async Task InitializeSettingsAndRestoreMountsAsync()
    {
        try
        {
            // Use a shared location for mount settings that syncs across MCP instances
            // Use the user's AppData\Roaming folder for cross-instance persistence
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var mcpSettingsPath = Path.Combine(appDataPath, "OwlCore", "Storage", "Mcp");
            Directory.CreateDirectory(mcpSettingsPath);
            
            var settingsFolder = new SystemFolder(new DirectoryInfo(mcpSettingsPath));
            
            // Initialize mount settings
            _mountSettings = new MountSettings(settingsFolder);
            await _mountSettings.LoadAsync();
            _mountSettings.MigrateLegacyOriginalId();

            // Restore mounts in dependency order
            var mountsToRestore = _mountSettings.GetMountsInDependencyOrder();
            var restoredCount = 0;
            var failed = new List<string>();

            Console.WriteLine($"Found {mountsToRestore.Count} persisted mounts to restore from {mcpSettingsPath}");

            foreach (var cfg in mountsToRestore)
            {
                try
                {
                    var originalId = MountSettings.ResolveOriginalId(cfg);
                    if (!await TryRegisterStorableAsync(originalId))
                    {
                        failed.Add($"{cfg.ProtocolScheme} (not accessible)");
                        continue;
                    }

                    if (!StorageTools._storableRegistry.TryGetValue(originalId, out var storable))
                    {
                        failed.Add($"{cfg.ProtocolScheme} (not registered)");
                        continue;
                    }

                    IFolder? folder = null;
                    // Archive mounts are stored as File. Detect by flags.
                    bool isArchiveMount = (cfg.MountType == StorableType.File && storable is IFile);
                    if (isArchiveMount && storable is IFile archiveFile)
                        folder = await WrapArchiveFileAsync(archiveFile, CancellationToken.None);
                    else if (storable is IFolder f)
                        folder = f;

                    if (folder is null)
                    {
                        failed.Add($"{cfg.ProtocolScheme} (type mismatch for mountType {cfg.MountType})");
                        continue;
                    }

                    var handler = new MountedFolderProtocolHandler(folder, cfg.MountName, cfg.ProtocolScheme);
                    _protocolHandlers[cfg.ProtocolScheme] = handler;
                    _mountedFolders[cfg.ProtocolScheme] = handler;
                    StorageTools._storableRegistry[$"{cfg.ProtocolScheme}://"] = folder;
                    restoredCount++;
                    Console.WriteLine($"Restored mount: {cfg.ProtocolScheme}:// -> {originalId} (MountType: {cfg.MountType})");
                }
                catch (Exception ex)
                {
                    failed.Add($"{cfg.ProtocolScheme} ({ex.Message})");
                }
            }

            Console.WriteLine($"Mount restoration complete: {restoredCount} restored, {failed.Count} failed");
            if (failed.Count > 0)
                Console.WriteLine("Failed mounts: " + string.Join(", ", failed));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error initializing mount settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Tries to register a storable item if it's not already registered
    /// </summary>
    private static async Task<bool> TryRegisterStorableAsync(string id)
    {
        try
        {
            // First check if it's already registered
            if (StorageTools._storableRegistry.ContainsKey(id)) return true;

            // Avoid circular dependency during initialization - only try filesystem paths directly
            if (Directory.Exists(id)) { StorageTools._storableRegistry[id] = new SystemFolder(new DirectoryInfo(id)); return true; }
            if (File.Exists(id)) { StorageTools._storableRegistry[id] = new SystemFile(new FileInfo(id)); return true; }
            
            // For protocol-based IDs during initialization, try to resolve them carefully
            if (id.Contains("://"))
            {
                var scheme = ExtractScheme(id);
                if (scheme != null && _protocolHandlers.TryGetValue(scheme, out var handler))
                {
                    // If this is a root URI and the protocol exists, it might be restorable
                    if (id.EndsWith("://") && handler.HasBrowsableRoot)
                    {
                        try
                        {
                            var root = await handler.CreateRootAsync(id, CancellationToken.None);
                            if (root != null) { StorageTools._storableRegistry[id] = root; return true; }
                        }
                        catch { return false; }
                    }
                    // For mounted folder protocols with paths, try to navigate to them
                    else if (handler is MountedFolderProtocolHandler mountHandler)
                    {
                        try
                        {
                            var rel = id.Substring(($"{scheme}://").Length);
                            if (!string.IsNullOrEmpty(rel))
                            {
                                var target = await mountHandler.MountedFolder.GetItemByRelativePathAsync(rel, CancellationToken.None);
                                StorageTools._storableRegistry[id] = target;
                                return true;
                            }
                        }
                        catch { return false; }
                    }
                }
            }
            
            // For all other cases during initialization, we can't resolve them yet
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Registers a protocol handler for a specific scheme
    /// </summary>
    /// <param name="scheme">The protocol scheme (e.g., "mfs", "s3", etc.)</param>
    /// <param name="handler">The handler implementation</param>
    public static void RegisterProtocol(string scheme, IProtocolHandler handler) => _protocolHandlers[scheme] = handler;

    /// <summary>
    /// Gets the protocol handler for a given URI or ID
    /// </summary>
    /// <param name="uri">The URI or ID to parse</param>
    /// <returns>The protocol handler if found, null otherwise</returns>
    public static IProtocolHandler? GetProtocolHandler(string uri) { var s = ExtractScheme(uri); return s != null && _protocolHandlers.TryGetValue(s, out var h) ? h : null; }

    /// <summary>
    /// Checks if a URI uses a custom protocol
    /// </summary>
    /// <param name="uri">The URI to check</param>
    /// <returns>True if it's a custom protocol, false otherwise</returns>
    public static bool IsCustomProtocol(string uri) => GetProtocolHandler(uri) != null;

    /// <summary>
    /// Extracts the protocol scheme from a URI
    /// </summary>
    /// <param name="uri">The URI to parse</param>
    /// <returns>The scheme if found, null otherwise</returns>
    public static string? ExtractScheme(string uri) { var i = uri.IndexOf("://"); return i > 0 ? uri[..i] : null; }

    /// <summary>
    /// Creates an item ID for a custom protocol
    /// </summary>
    /// <param name="parentId">The parent item ID</param>
    /// <param name="itemName">The item name</param>
    /// <returns>The constructed item ID</returns>
    public static string CreateCustomItemId(string parentId, string itemName) { var h = GetProtocolHandler(parentId); return h?.CreateItemId(parentId, itemName) ?? $"{parentId}/{itemName}"; }

    /// <summary>
    /// Gets all registered protocol schemes
    /// </summary>
    /// <returns>Collection of registered protocol schemes</returns>
    public static IEnumerable<string> GetRegisteredProtocols() => _protocolHandlers.Keys;

    /// <summary>
    /// Generalized mounting: accepts either an IFolder or supported archive IFile. Replaces MountFolder internally.
    /// </summary>
    public static string MountStorable(IStorable storable, string protocolScheme, string mountName, string? originalId = null)
    {
        if (string.IsNullOrWhiteSpace(protocolScheme)) throw new ArgumentException("Protocol scheme cannot be null or empty", nameof(protocolScheme));
        if (string.IsNullOrWhiteSpace(mountName)) throw new ArgumentException("Mount name cannot be null or empty", nameof(mountName));
        if (_protocolHandlers.ContainsKey(protocolScheme)) throw new ArgumentException($"Protocol scheme '{protocolScheme}' is already registered", nameof(protocolScheme));

        StorableType mountType;
        IFolder folderToMount;
        if (storable is IFolder folder)
        {
            mountType = StorableType.Folder;
            folderToMount = folder;
            if (folder is IStorableChild child && WouldCreateCycle(child.Id, protocolScheme))
                throw new ArgumentException($"Mounting '{protocolScheme}' would create a cycle", nameof(protocolScheme));
        }
        else if (storable is IFile file && ArchiveSupport.IsSupportedArchiveExtension(file.Name))
        {
            // Archive origin only; presented as folder implicitly (read/write if possible)
            mountType = StorableType.File;
            var resolvedOriginal = originalId ?? (storable is IStorableChild scFile ? scFile.Id : storable.Id);

            // Enforce single mount per underlying archive
            if (_mountedOriginalIds.TryGetValue(resolvedOriginal, out var existingScheme))
                throw new ArgumentException($"Archive already mounted as '{existingScheme}://'. Multi-mount disabled.");

            folderToMount = WrapArchiveFileAsync(file, CancellationToken.None).GetAwaiter().GetResult();
            // Track original for later, provisional until success
        }
        else
            throw new ArgumentException("Storable must be a folder or supported archive file.", nameof(storable));

        var handler = new MountedFolderProtocolHandler(folderToMount, mountName, protocolScheme);
        var rootUri = $"{protocolScheme}://";
        _protocolHandlers[protocolScheme] = handler;
        _mountedFolders[protocolScheme] = handler;

        // Ensure the root alias is registered so callers can start navigation at protocol root.
        // This preserves internal IDs (e.g., archive root name/hash) while keeping alias substitution at API interface/implementation boundaries.
        StorageTools._storableRegistry[rootUri] = folderToMount;

        var idToStore = originalId ?? (storable is IStorableChild sc ? sc.Id : storable.Id);
        _mountSettings.AddOrUpdateMount(protocolScheme, idToStore, mountName, mountType);
        
        if (mountType == StorableType.File)
            _mountedOriginalIds[idToStore] = protocolScheme;
        return rootUri;
    }

    /// <summary>
    /// Mounts an IFolder with a custom protocol scheme, making it available as a browsable drive
    /// </summary>
    /// <param name="folder">The folder to mount</param>
    /// <param name="protocolScheme">The custom protocol scheme (e.g., "mydata", "project1")</param>
    /// <param name="mountName">Display name for the mounted folder</param>
    /// <param name="originalFolderId">The original folder ID to preserve in settings (optional)</param>
    /// <returns>The root URI for the mounted folder</returns>
    /// <exception cref="ArgumentException">Thrown if the protocol scheme is already registered</exception>
    public static string MountFolder(IFolder folder, string protocolScheme, string mountName, string? originalFolderId = null)
        => MountStorable(folder, protocolScheme, mountName, originalFolderId);

    /// <summary>
    /// Checks if mounting a folder would create a cycle in the mount dependency graph
    /// </summary>
    /// <param name="sourceFolderId">The ID of the folder being mounted</param>
    /// <param name="targetProtocolScheme">The protocol scheme it would be mounted as</param>
    /// <returns>True if a cycle would be created, false otherwise</returns>
    private static bool WouldCreateCycle(string sourceFolderId, string targetProtocolScheme)
    {
        var visited = new HashSet<string>();
        var stack = new HashSet<string>();
        
        return HasCycleDfs(sourceFolderId, targetProtocolScheme, visited, stack);
    }

    /// <summary>
    /// Depth-first search to detect cycles in the mount dependency graph
    /// </summary>
    private static bool HasCycleDfs(string currentId, string targetProtocolScheme, HashSet<string> visited, HashSet<string> stack)
    {
        // If we're trying to mount something that would point back to the target protocol, that's a cycle
        var targetUri = $"{targetProtocolScheme}://";
        if (currentId == targetUri) return true;
        if (stack.Contains(currentId)) return true;
        if (visited.Contains(currentId)) return false;
        visited.Add(currentId); stack.Add(currentId);

        // Check if current ID is from a mounted protocol
        var scheme = ExtractScheme(currentId);
        if (scheme != null && _mountedFolders.TryGetValue(scheme, out var handler) && handler.MountedFolder is IStorableChild child)
            if (HasCycleDfs(child.Id, targetProtocolScheme, visited, stack)) return true;

        stack.Remove(currentId);
        return false;
    }

    /// <summary>
    /// Resolves a mounted path to its final underlying storage location
    /// </summary>
    /// <param name="mountedPath">The mounted path to resolve</param>
    /// <param name="maxDepth">Maximum resolution depth to prevent infinite loops</param>
    /// <returns>The final resolved path, or the original path if not a mount</returns>
    public static string ResolveMountPath(string mountedPath, int maxDepth = 10)
    {
        var currentPath = mountedPath;
        var depth = 0;
        
        while (depth < maxDepth)
        {
            var scheme = ExtractScheme(currentPath);
            if (scheme == null || !_mountedFolders.TryGetValue(scheme, out var handler))
                break;

            // Get the underlying folder's ID
            if (handler.MountedFolder is IStorableChild child)
            {
                // Replace the mount scheme with the underlying path
                var remainingPath = currentPath.Substring($"{scheme}://".Length);
                currentPath = string.IsNullOrEmpty(remainingPath) ? child.Id : $"{child.Id}/{remainingPath}";
            }
            else
            {
                break;
            }

            depth++;
        }

        if (depth >= maxDepth)
            throw new InvalidOperationException($"Mount resolution exceeded maximum depth of {maxDepth} for path: {mountedPath}");

        return currentPath;
    }

    /// <summary>
    /// Unmounts a previously mounted folder by protocol scheme
    /// </summary>
    /// <param name="protocolScheme">The protocol scheme to unmount</param>
    /// <returns>True if the folder was unmounted, false if it wasn't found or wasn't a mounted folder</returns>
    public static async Task<bool> UnmountFolder(string protocolScheme)
    {
        if (string.IsNullOrWhiteSpace(protocolScheme))
            return false;
        if (!_mountedFolders.ContainsKey(protocolScheme))
            return false;

        // Flush and dispose mounted folder to ensure changes are saved before disposal
        if (_mountedFolders.TryGetValue(protocolScheme, out var mountedHandler))
        {
            try
            {
                // First, flush any pending changes if the folder supports it
                if (mountedHandler.MountedFolder is IFlushable flushable)
                {
                    Console.WriteLine($"Flushing changes for mounted folder {protocolScheme}");
                    await flushable.FlushAsync(CancellationToken.None);
                    Console.WriteLine($"Successfully flushed changes for {protocolScheme}");
                }
                
                // Then dispose to release handles
                if (mountedHandler.MountedFolder is IDisposable d)
                {
                    Console.WriteLine($"Disposing mounted folder for {protocolScheme}");
                    d.Dispose();
                    Console.WriteLine($"Successfully disposed mounted folder for {protocolScheme}");
                }
            }
            catch (Exception ex) 
            { 
                Console.WriteLine($"Error flushing/disposing mounted folder for {protocolScheme}: {ex.Message}");
            }
        }

        // Remove originalId tracking
        var toRemove = _mountedOriginalIds.Where(kvp => kvp.Value == protocolScheme).Select(kvp => kvp.Key).ToList();
        foreach (var key in toRemove)
            _mountedOriginalIds.TryRemove(key, out _);

        _protocolHandlers.TryRemove(protocolScheme, out _);
        _mountedFolders.TryRemove(protocolScheme, out _);
        
        // Remove from persistent settings
        _mountSettings.RemoveMount(protocolScheme);
        
        return true;
    }

    /// <summary>
    /// Gets information about all mounted folders
    /// </summary>
    /// <returns>Array of mounted folder information</returns>
    public static object[] GetMountedFolders()
    {
        return _mountedFolders.Values.Select(h =>
        {
            StorableType mountType = StorableType.Folder;
            string originalId = string.Empty;
            if (_mountSettings != null && _mountSettings.Mounts.TryGetValue(h.ProtocolScheme, out var cfg))
            {
                mountType = cfg.MountType;
                originalId = MountSettings.ResolveOriginalId(cfg);
            }
            return new
            {
                protocolScheme = h.ProtocolScheme,
                mountName = h.MountName,
                rootUri = $"{h.ProtocolScheme}://",
                folderType = h.MountedFolder.GetType().Name,
                mountType,
                originalId
            };
        }).ToArray();
    }

    /// <summary>
    /// Checks if a protocol scheme represents a mounted folder
    /// </summary>
    /// <param name="protocolScheme">The protocol scheme to check</param>
    /// <returns>True if it's a mounted folder, false otherwise</returns>
    public static bool IsMountedFolder(string protocolScheme)
    {
        return _mountedFolders.ContainsKey(protocolScheme);
    }

    /// <summary>
    /// Returns true if the provided original ID is the source of a mounted archive (file), with its protocol scheme.
    /// </summary>
    internal static bool TryGetArchiveMountScheme(string originalId, out string? protocolScheme)
    {
        protocolScheme = null;
        if (string.IsNullOrWhiteSpace(originalId))
            return false;
        foreach (var cfg in _mountSettings.Mounts.Values)
        {
            if (cfg.MountType != StorableType.File)
                continue;
            var storedOriginal = MountSettings.ResolveOriginalId(cfg);
            if (string.Equals(storedOriginal, originalId, StringComparison.OrdinalIgnoreCase))
            {
                protocolScheme = cfg.ProtocolScheme;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Gets the mount settings instance for external saving operations
    /// </summary>
    internal static MountSettings GetMountSettings()
    {
        return _mountSettings; // Non-null after initialization
    }

    /// <summary>
    /// Renames a mounted folder's protocol scheme and/or display name atomically
    /// </summary>
    /// <param name="currentProtocolScheme">The current protocol scheme to rename</param>
    /// <param name="newProtocolScheme">The new protocol scheme (optional, keeps current if null)</param>
    /// <param name="newMountName">The new display name (optional, keeps current if null)</param>
    /// <returns>The new root URI after renaming</returns>
    /// <exception cref="ArgumentException">Thrown if the mount doesn't exist or new scheme conflicts</exception>
    public static string RenameMountedFolder(string currentProtocolScheme, string? newProtocolScheme = null, string? newMountName = null)
    {
        if (string.IsNullOrWhiteSpace(currentProtocolScheme))
            throw new ArgumentException("Current protocol scheme cannot be null or empty", nameof(currentProtocolScheme));

        // Must be a mounted folder, not a built-in protocol
        if (!_mountedFolders.TryGetValue(currentProtocolScheme, out var currentHandler))
            throw new ArgumentException($"Protocol scheme '{currentProtocolScheme}' is not a mounted folder", nameof(currentProtocolScheme));

        // Use current values if new ones not provided
        var finalProtocolScheme = newProtocolScheme ?? currentProtocolScheme;
        var finalMountName = newMountName ?? currentHandler.MountName;

        // If neither changed, nothing to do
        if (finalProtocolScheme == currentProtocolScheme && finalMountName == currentHandler.MountName)
            return $"{currentProtocolScheme}://";

        // Validate new protocol scheme if it's changing
        if (finalProtocolScheme != currentProtocolScheme)
        {
            if (string.IsNullOrWhiteSpace(finalProtocolScheme))
                throw new ArgumentException("New protocol scheme cannot be null or empty", nameof(newProtocolScheme));

            if (finalProtocolScheme.Contains("://") || finalProtocolScheme.Contains("/") || finalProtocolScheme.Contains("\\"))
                throw new ArgumentException("Protocol scheme must be a simple identifier without special characters", nameof(newProtocolScheme));

            // Ensure new scheme doesn't conflict with existing protocols
            if (_protocolHandlers.ContainsKey(finalProtocolScheme))
                throw new ArgumentException($"Protocol scheme '{finalProtocolScheme}' is already registered", nameof(newProtocolScheme));
        }

        // Create new handler with updated information
        var newHandler = new MountedFolderProtocolHandler(currentHandler.MountedFolder, finalMountName, finalProtocolScheme);
        var newRootUri = $"{finalProtocolScheme}://";

        // Perform atomic update
        if (finalProtocolScheme != currentProtocolScheme)
        {
            // Protocol scheme is changing - need to update both registries
            _protocolHandlers.TryRemove(currentProtocolScheme, out _);
            _mountedFolders.TryRemove(currentProtocolScheme, out _);
            
            _protocolHandlers[finalProtocolScheme] = newHandler;
            _mountedFolders[finalProtocolScheme] = newHandler;
        }
        else
        {
            // Only display name is changing - update in place
            _protocolHandlers[currentProtocolScheme] = newHandler;
            _mountedFolders[currentProtocolScheme] = newHandler;
        }

        // Update persistent settings
        _mountSettings.RenameMount(currentProtocolScheme, newProtocolScheme, newMountName);

        return newRootUri;
    }

    /// <summary>
    /// Substitutes long IDs with shorter mount aliases where possible, to make them more manageable for smaller models
    /// </summary>
    /// <param name="fullId">The full ID to potentially substitute</param>
    /// <returns>The shortest possible alias ID, or the original ID if no suitable mount exists</returns>
    public static string SubstituteWithMountAlias(string fullId)
    {
        if (string.IsNullOrWhiteSpace(fullId))
            return fullId;

        // Find the best (longest matching) mount that can substitute part of this ID
        string bestAlias = fullId;
        int longestMatchLength = 0;

        foreach (var mount in _mountedFolders.Values)
        {
            if (mount.MountedFolder is not IStorableChild mountedChild)
                continue;

            var mountedId = mountedChild.Id;
            
            // Check if the full ID starts with the mounted folder's ID
            if (fullId.StartsWith(mountedId, StringComparison.OrdinalIgnoreCase))
            {
                var matchLength = mountedId.Length;
                
                // Only substitute if this mount provides a longer match than what we already found
                if (matchLength > longestMatchLength)
                {
                    var remainingPart = fullId.Substring(matchLength);
                    // Ensure proper path separator handling
                    var aliasId = string.IsNullOrEmpty(remainingPart) ? 
                        $"{mount.ProtocolScheme}://" : 
                        $"{mount.ProtocolScheme}://{remainingPart.TrimStart('/', '\\')}";
                    
                    bestAlias = aliasId;
                    longestMatchLength = matchLength;
                }
            }
        }

        // If we found a substitution, recursively check if it can be further shortened
        if (bestAlias != fullId)
        {
            var furtherSubstituted = SubstituteWithMountAlias(bestAlias);
            if (furtherSubstituted != bestAlias)
                return furtherSubstituted;
        }

        return bestAlias;
    }

    /// <summary>
    /// Resolves a potentially aliased ID back to its full underlying ID
    /// </summary>
    /// <param name="aliasId">The potentially aliased ID to resolve</param>
    /// <param name="maxDepth">Maximum resolution depth to prevent infinite loops</param>
    /// <returns>The fully resolved underlying ID</returns>
    public static string ResolveAliasToFullId(string aliasId, int maxDepth = 10)
    {
        if (string.IsNullOrWhiteSpace(aliasId))
            return aliasId;

        var currentId = aliasId;
        var depth = 0;
        
        while (depth < maxDepth)
        {
            var scheme = ExtractScheme(currentId);
            if (scheme == null || !_mountedFolders.TryGetValue(scheme, out var handler))
                break;

            // Get the underlying folder's ID
            if (handler.MountedFolder is not IStorableChild child)
                break;

            // Replace the mount scheme with the underlying ID
            var remainingPath = currentId.Substring($"{scheme}://".Length);
            
            // Normalize path separators and combine properly
            if (string.IsNullOrEmpty(remainingPath))
            {
                currentId = child.Id;
            }
            else
            {
                // Normalize path separators to match the underlying system
                var normalizedPath = remainingPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                currentId = Path.Combine(child.Id, normalizedPath);
            }

            depth++;
        }

        if (depth >= maxDepth)
            throw new InvalidOperationException($"Alias resolution exceeded maximum depth of {maxDepth} for ID: {aliasId}");

        return currentId;
    }

    private static async Task<IFolder> WrapArchiveFileAsync(IFile archiveFile, CancellationToken cancellationToken)
    {
        // Default to read-only unless we can verify parent is modifiable
        bool parentModifiable = false;
        if (archiveFile is IStorableChild child)
        {
            try
            {
                var parent = await child.GetParentAsync(cancellationToken);
                parentModifiable = parent is IModifiableFolder;
            }
            catch
            {
                // ignore, fallback to read-only
            }
        }

        // For writable archives, use file-based constructor with backing stream for flush support
        if (parentModifiable && ArchiveSupport.IsWritableArchiveExtension(archiveFile.Name))
        {
            try
            {
                // Create a memory stream for fast archive operations
                var backingMemoryStream = new MemoryStream();
                
                // Create a disposal delegate that will flush to file when disposed
                var flushToFileDelegate = new DisposableDelegate()
                {
                    Inner = () =>
                    {
                        try
                        {
                            Logger.LogInformation($"DisposalDelegate triggered for archive: {archiveFile.Name}");
                            Logger.LogInformation($"Backing memory stream position: {backingMemoryStream.Position}, length: {backingMemoryStream.Length}");
                            
                            // Open the file and flush the memory stream to it (synchronous)
                            // TODO: Needs DisposeAsync versions of DisposableDelegate and DelegatedDisposalStream.
                            using var destinationStream = archiveFile.OpenReadWriteAsync(CancellationToken.None).GetAwaiter().GetResult();
                            Logger.LogInformation($"Opened destination stream for {archiveFile.Name}, stream length: {destinationStream.Length}");
                            
                            destinationStream.Position = 0;
                            destinationStream.SetLength(0);
                            Logger.LogInformation("Cleared destination stream");
                            
                            backingMemoryStream.Position = 0;
                            backingMemoryStream.CopyTo(destinationStream);
                            Logger.LogInformation($"Copied {backingMemoryStream.Length} bytes from backing stream to file");
                            
                            destinationStream.Flush();
                            Logger.LogInformation($"Flushed destination stream for {archiveFile.Name}");
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"Error in disposal delegate for {archiveFile.Name}: {ex.Message}");
                            throw;
                        }
                    }
                };
                
                // Wrap the memory stream with delegated disposal to trigger file flush
                var backingStreamWithFlush = new DelegatedDisposalStream(backingMemoryStream)
                {
                    Inner = flushToFileDelegate
                };
                
                return new ArchiveFolder(archiveFile, backingStreamWithFlush);
            }
            catch
            {
                // If ArchiveFolder construction fails, fall through to read-only
            }
        }

        // For read-only archives, use ReadOnlyArchiveFolder(IFile) constructor
        // This will be disposed properly on unmount
        return new ReadOnlyArchiveFolder(archiveFile);
    }
}
