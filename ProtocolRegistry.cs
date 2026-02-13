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
using Ipfs.Http;
using Ipfs.CoreApi;

namespace OwlCore.Storage.Mcp;

/// <summary>
/// Registry for custom storage protocols that maps protocol schemes to storage implementations.
/// </summary>
/// <remarks>
/// <para><strong>Mount Alias System — Design Intent</strong></para>
/// <para>
/// The alias system provides a bidirectional mapping between human-readable protocol schemes
/// (e.g., <c>mfs://</c>, <c>onedrive://</c>, <c>home://</c>, <c>skills://</c>) and the native
/// storage IDs of the folders they represent. Native IDs may be filesystem paths, random hashes
/// (e.g., OneDrive/MS Graph), IPFS CIDs, or any other opaque identifier.
/// </para>
/// <para>
/// <strong>Alias creation</strong> (<see cref="SubstituteWithMountAlias"/>): Given a native storage ID,
/// finds the mounted folder whose native ID is a leading substring of the target ID, and replaces
/// that prefix with the mount's protocol scheme. For example, if <c>home://</c> is mounted to
/// <c>/home/username/</c>, then native ID <c>/home/username/Documents/file.txt</c> becomes
/// <c>home://Documents/file.txt</c>. This is a function between two IDs — the alias scheme
/// representing a known folder instance, and the literal child ID within that folder.
/// </para>
/// <para>
/// <strong>Text collision handling</strong>: When the alias's literal ID is the leading text of
/// a target child item's ID, the parent folder's ID is substituted with the alias. This primarily
/// accommodates common path-based ID systems (where child IDs literally contain the parent's ID
/// as a prefix), but has broader implications for alias chaining and virtual subfolder mounts.
/// </para>
/// <para>
/// <strong>Alias resolution</strong> (<see cref="ResolveAliasToFullId"/>): The inverse operation.
/// Given an aliased ID, replaces the protocol scheme prefix with the mounted folder's native ID
/// to recover the original storage ID. This recurses to handle chained mounts.
/// </para>
/// <para>
/// <strong>Why recursion</strong>: Most storage IDs aren't human- or agent-readable. Recursion
/// enables protocol-like shortcuts into arbitrary depth — e.g., <c>skills://</c> mounted from
/// <c>mfs://owlcore.storage.mcp.skills/</c> which itself resolves through the MFS root.
/// </para>
/// <para>
/// <strong>Key use cases</strong>:
/// <list type="bullet">
///   <item>Disambiguate conflicting IDs (e.g., MFS root <c>/</c> vs System.IO root <c>/</c>)</item>
///   <item>Shortcut into an impossible-to-remember ID (e.g., OneDrive's random hash root)</item>
///   <item>Mount commonly accessed folders (<c>home://</c>, <c>dev://</c>, <c>downloads://</c>)</item>
///   <item>Mount subfolders of existing protocols (<c>skills://</c> into <c>mfs://</c>)</item>
///   <item>Chain mounts for virtual hierarchy organization</item>
/// </list>
/// </para>
/// <para>
/// <strong>Important</strong>: The alias functions operate purely on ID string substitution —
/// replacing one ID prefix with another. They do NOT assume IDs are filesystem paths. Operations
/// like <c>Path.Combine</c> must not be used, as IDs may be hashes, URIs, or other non-path formats.
/// </para>
/// <para>
/// <strong>Protocols vs Mounts</strong>: Built-in protocols (mfs, ipfs, http, etc.) and mounted folder
/// aliases are distinct concepts. <see cref="ResolveAliasToFullId"/> resolves mounted folder aliases only.
/// During mount restoration, once the alias chain is fully resolved, the terminal protocol scheme is used
/// to navigate from the protocol handler's root to the target item by native ID.
/// </para>
/// <para>
/// <strong>Future: Virtual Subfolder Mounts</strong> (not yet implemented):
/// <list type="bullet">
///   <item>Mount multiple folders from multiple implementations into a unified virtual folder tree.</item>
///   <item>Folders with the same name are merged, appearing as a single folder with combined contents,
///         throughout the entire tree depth.</item>
///   <item>Auto-mount mechanism for non-browseable items (e.g., making an archive file appear as a
///         browseable subfolder within the virtual tree without explicit mount commands).</item>
///   <item>Non-browseable roots (http, ipfs) and non-browseable leafs (archives) would participate
///         through this auto-mount mechanism rather than requiring special-case handling.</item>
/// </list>
/// </para>
/// </remarks>
public static class ProtocolRegistry
{
    private static readonly ConcurrentDictionary<string, IProtocolHandler> _protocolHandlers = new();
    private static readonly ConcurrentDictionary<string, MountedFolderProtocolHandler> _mountedFolders = new();
    private static readonly ConcurrentDictionary<string, string> _mountedOriginalIds = new(); // originalId -> protocolScheme
    public static MountSettings MountSettings = null!; // Initialized in EnsureInitializedAsync
    private static bool _isInitialized = false;

    /// <summary>
    /// Initializes the protocol registry with IPFS client support
    /// </summary>
    public static void Initialize(IpfsClient ipfsClient)
    {
        if (_isInitialized)
            return;

        IpfsClient = ipfsClient;

        // Register built-in protocol handlers
        RegisterProtocol("mfs", new IpfsMfsProtocolHandler(ipfsClient));
        RegisterProtocol("http", new HttpProtocolHandler());
        RegisterProtocol("https", new HttpProtocolHandler());
        RegisterProtocol("ipfs", new IpfsProtocolHandler(ipfsClient));
        RegisterProtocol("ipns", new IpnsProtocolHandler(ipfsClient));
        RegisterProtocol("memory", new MemoryProtocolHandler());
        // Add more protocols here as needed
        // RegisterProtocol("azure-blob", new AzureBlobProtocolHandler());
        // RegisterProtocol("s3", new S3ProtocolHandler());

        _isInitialized = true;
    }

    /// <summary>
    /// The client to use for communicating with ipfs.
    /// </summary>
    public static ICoreApi IpfsClient { get; set; }

    /// <summary>
    /// Ensures mount settings are initialized and mounts are restored
    /// Call this once during application startup
    /// </summary>
    public static async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Already initialized
        if (MountSettings != null)
            return;

        await InitializeSettingsAndRestoreMountsAsync(cancellationToken);
    }

    /// <summary>
    /// Initializes the settings system and restores persisted mounts
    /// </summary>
    private static async Task InitializeSettingsAndRestoreMountsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // Use a shared location for mount settings that syncs across MCP instances
            // Use the user's AppData\Roaming folder for cross-instance persistence
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appDataFolder = new SystemFolder(appDataPath);
            var owlCoreFolder = (SystemFolder)await appDataFolder.CreateFolderAsync("OwlCore", overwrite: false, cancellationToken);
            var storageFolder = (SystemFolder)await owlCoreFolder.CreateFolderAsync("Storage", overwrite: false, cancellationToken);
            var mcpSettingsFolder = (SystemFolder)await storageFolder.CreateFolderAsync("Mcp", overwrite: false, cancellationToken);

            // Initialize mount settings
            MountSettings = new MountSettings(mcpSettingsFolder);
            await MountSettings.LoadAsync();

            // Restore mounts in dependency order
            var mountsToRestore = MountSettings.GetMountsInDependencyOrder();
            var restoredCount = 0;
            var failed = new List<string>();

            Logger.LogInformation($"Found {mountsToRestore.Count} persisted mounts to restore from {mcpSettingsFolder.Path}");

            foreach (var cfg in mountsToRestore)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var originalId = cfg.OriginalStorableId;
                    IStorable? storable = null;
                    
                    // Primary: resolve through the alias chain then use the terminal protocol handler.
                    // ResolveAliasToFullId handles any mounted-folder aliases (chained mounts where
                    // dependencies were already restored earlier in this loop). The result may still
                    // carry a built-in protocol scheme (e.g., mfs://) which we resolve via the handler.
                    try
                    {
                        var resolvedId = ResolveAliasToFullId(originalId);
                        var scheme = ExtractScheme(resolvedId);
                        if (scheme != null && _protocolHandlers.TryGetValue(scheme, out var protoHandler))
                        {
                            if (protoHandler.HasBrowsableRoot)
                            {
                                var rootUri = $"{scheme}://";
                                var root = await protoHandler.CreateRootAsync(rootUri, cancellationToken);
                                if (root is IFolder rootFolder)
                                {
                                    // Reverse the alias: replace scheme:// with root's native ID to get the native child ID.
                                    var remaining = resolvedId.Substring(rootUri.Length);
                                    var nativeChildId = root.Id + remaining;
                                    storable = await rootFolder.GetItemRecursiveAsync(nativeChildId, cancellationToken);
                                    StorageTools._storableRegistry[originalId] = storable;
                                }
                            }
                            else
                            {
                                // Non-browseable protocol — try direct resource access
                                storable = await protoHandler.CreateResourceAsync(resolvedId, cancellationToken);
                                if (storable != null)
                                    StorageTools._storableRegistry[originalId] = storable;
                            }
                        }
                    }
                    catch
                    {
                        storable = null;
                    }

                    // Last resort: direct registration (filesystem paths, etc.)
                    if (storable == null)
                    {
                        if (!await TryRegisterStorableAsync(originalId, cancellationToken))
                        {
                            failed.Add($"{cfg.ProtocolScheme} (not accessible)");
                            continue;
                        }
                        
                        if (!StorageTools._storableRegistry.TryGetValue(originalId, out storable))
                        {
                            failed.Add($"{cfg.ProtocolScheme} (not registered)");
                            continue;
                        }
                    }

                    IFolder? folder = null;

                    // Archive mounts are stored as File. Detect by flags.
                    bool isArchiveMount = (cfg.MountType == StorableType.File && storable is IFile);

                    if (isArchiveMount && storable is IFile archiveFile)
                        folder = await WrapArchiveFileAsync(archiveFile, cancellationToken);
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
                    Logger.LogInformation($"Restored mount: {cfg.ProtocolScheme}:// -> {originalId} (MountType: {cfg.MountType})");
                }
                catch (Exception ex)
                {
                    failed.Add($"{cfg.ProtocolScheme} ({ex.Message})");
                }
            }

            Logger.LogInformation($"Mount restoration complete: {restoredCount} restored, {failed.Count} failed");
            if (failed.Count > 0)
                Logger.LogInformation("Failed mounts: " + string.Join(", ", failed));
        }
        catch (Exception ex)
        {
            Logger.LogInformation($"Error initializing mount settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Tries to register a storable item if it's not already registered
    /// </summary>
    private static async Task<bool> TryRegisterStorableAsync(string id, CancellationToken cancellationToken)
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
                            var root = await handler.CreateRootAsync(id, cancellationToken);
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
                                var target = await mountHandler.MountedFolder.GetItemByRelativePathAsync(rel, cancellationToken);
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
    public static async Task<string> MountStorable(IStorable storable, string protocolScheme, string mountName, string? originalId = null)
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

        // Store the ID as provided — callers should pass the alias form (e.g., mfs://subfolder/)
        // so the alias chain is self-sufficient for restoration.
        var idToStore = originalId ?? (storable is IStorableChild sc ? sc.Id : storable.Id);

        MountSettings.AddOrUpdateMount(protocolScheme, idToStore, mountName, mountType);

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
    public static Task<string> MountFolder(IFolder folder, string protocolScheme, string mountName, string? originalFolderId = null)
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
    /// Resolves a mounted path to its final underlying storage location.
    /// Delegates to <see cref="ResolveAliasToFullId"/> — retained for backward compatibility.
    /// </summary>
    /// <param name="mountedPath">The mounted path to resolve</param>
    /// <param name="maxDepth">Maximum resolution depth to prevent infinite loops</param>
    /// <returns>The final resolved ID, or the original ID if not a mount</returns>
    public static string ResolveMountPath(string mountedPath, int maxDepth = 10)
        => ResolveAliasToFullId(mountedPath, maxDepth);

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
                    Logger.LogInformation($"Flushing changes for mounted folder {protocolScheme}");
                    await flushable.FlushAsync(CancellationToken.None);
                    Logger.LogInformation($"Successfully flushed changes for {protocolScheme}");
                }

                // Then dispose to release handles
                if (mountedHandler.MountedFolder is IDisposable d)
                {
                    Logger.LogInformation($"Disposing mounted folder for {protocolScheme}");
                    d.Dispose();
                    Logger.LogInformation($"Successfully disposed mounted folder for {protocolScheme}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogInformation($"Error flushing/disposing mounted folder for {protocolScheme}: {ex.Message}");
            }
        }

        // Remove originalId tracking
        var toRemove = _mountedOriginalIds.Where(kvp => kvp.Value == protocolScheme).Select(kvp => kvp.Key).ToList();
        foreach (var key in toRemove)
            _mountedOriginalIds.TryRemove(key, out _);

        _protocolHandlers.TryRemove(protocolScheme, out _);
        _mountedFolders.TryRemove(protocolScheme, out _);

        // Remove from persistent settings
        Logger.LogInformation($"Attempting to unmount protocol scheme: {protocolScheme}");
        Logger.LogInformation($"Mounted handler: {mountedHandler}");
        Logger.LogInformation($"Mounted folder ID: {mountedHandler?.MountedFolder.Id}");

        // Find the persisted config for this scheme whose resolved original ID matches the mounted folder's ID
        var candidates = MountSettings.Mounts.Where(m => m.ProtocolScheme == protocolScheme).ToList();
        var targetConfig = candidates.FirstOrDefault(cfg =>
        {
            var storedOriginal = cfg.OriginalStorableId;
            var resolvedStored = ResolveAliasToFullId(storedOriginal);
            return string.Equals(resolvedStored, mountedHandler?.MountedFolder.Id, StringComparison.OrdinalIgnoreCase);
        });

        if (targetConfig is null)
        {
            Logger.LogInformation($"No persisted mount configuration matched live mount for '{protocolScheme}'. Nothing to remove.");
            return true; // The live mount is gone; treat as success even if settings couldn't be pruned
        }

        Logger.LogInformation($"Target config found: ProtocolScheme={targetConfig.ProtocolScheme}, OriginalStorableId={targetConfig.OriginalStorableId}");
        MountSettings.Mounts.Remove(targetConfig);

        return true;
    }

    /// <summary>
    /// Gets information about all mounted folders
    /// </summary>
    /// <returns>Array of mounted folder information</returns>
    public static object[] GetMountedFolders()
    {
        var results = new List<object>();
        foreach (var h in _mountedFolders.Values)
        {
            try
            {
                StorableType mountType = StorableType.Folder;
                string originalId = string.Empty;

                if (MountSettings != null)
                {
                    var configs = MountSettings.Mounts.Where(x => x.ProtocolScheme == h.ProtocolScheme).ToList();
                    if (configs.Count > 0)
                    {
                        var match = configs.FirstOrDefault(cfg =>
                        {
                            try
                            {
                                return string.Equals(ResolveAliasToFullId(cfg.OriginalStorableId), h.MountedFolder.Id, StringComparison.OrdinalIgnoreCase);
                            }
                            catch
                            {
                                return false;
                            }
                        });

                        if (match != null)
                        {
                            mountType = match.MountType;
                            originalId = match.OriginalStorableId;
                        }
                    }
                }
                results.Add(new
                {
                    protocolScheme = h.ProtocolScheme,
                    mountName = h.MountName,
                    rootUri = $"{h.ProtocolScheme}://",
                    folderType = h.MountedFolder.GetType().Name,
                    mountType,
                    originalId
                });
            }
            catch (Exception ex)
            {
                Logger.LogInformation($"Failed to get info for mounted folder '{h.ProtocolScheme}': {ex.Message}");
                // Still include the mount with basic info so it's visible
                results.Add(new
                {
                    protocolScheme = h.ProtocolScheme,
                    mountName = h.MountName,
                    rootUri = $"{h.ProtocolScheme}://",
                    folderType = h.MountedFolder.GetType().Name,
                    mountType = StorableType.Folder,
                    originalId = string.Empty
                });
            }
        }
        return results.ToArray();
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
        foreach (var cfg in MountSettings.Mounts)
        {
            if (cfg.MountType != StorableType.File)
                continue;
            var storedOriginal = cfg.OriginalStorableId;
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
        return MountSettings; // Non-null after initialization
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
        var oldRootUri = $"{currentProtocolScheme}://";

        // Perform atomic update — re-key all dictionaries when scheme changes
        if (finalProtocolScheme != currentProtocolScheme)
        {
            _protocolHandlers.TryRemove(currentProtocolScheme, out _);
            _mountedFolders.TryRemove(currentProtocolScheme, out _);
            StorageTools._storableRegistry.TryRemove(oldRootUri, out _);
        }

        _protocolHandlers[finalProtocolScheme] = newHandler;
        _mountedFolders[finalProtocolScheme] = newHandler;
        StorageTools._storableRegistry[newRootUri] = currentHandler.MountedFolder;

        // Update persistent settings
        MountSettings.RenameMount(currentProtocolScheme, currentHandler.MountedFolder.Id, newProtocolScheme, newMountName);

        return newRootUri;
    }

    /// <summary>
    /// Finds all known protocol/mount aliases whose underlying (native) root ID matches the given ID.
    /// Used to disambiguate when a native ID collides across implementations.
    /// </summary>
    /// <param name="nativeId">The native storage ID to look up.</param>
    /// <returns>List of alias URIs (e.g., <c>mfs://</c>, <c>home://</c>) whose root maps to this native ID.</returns>
    public static async Task<List<string>> GetAllAliasesForNativeIdAsync(string nativeId)
    {
        var aliases = new List<string>();
        if (string.IsNullOrWhiteSpace(nativeId))
            return aliases;

        // Check built-in protocol handlers — their roots may share the same native ID
        foreach (var (scheme, handler) in _protocolHandlers)
        {
            if (handler is MountedFolderProtocolHandler mounted)
            {
                // For mounted folders, check if the mounted folder's ID matches
                if (mounted.MountedFolder is IStorableChild child &&
                    string.Equals(child.Id, nativeId, StringComparison.OrdinalIgnoreCase))
                {
                    aliases.Add($"{scheme}://");
                }
            }
            else if (handler.HasBrowsableRoot)
            {
                // For built-in protocols, check if the root's native ID matches.
                // Create the root on-demand if needed — can't rely on cache because
                // the root may not have been loaded yet in this session.
                var rootUri = $"{scheme}://";
                IStorable? root = null;
                if (StorageTools._storableRegistry.TryGetValue(rootUri, out root))
                {
                    // Already cached
                }
                else
                {
                    try
                    {
                        root = await handler.CreateRootAsync(rootUri, CancellationToken.None);
                        if (root != null)
                            StorageTools._storableRegistry[rootUri] = root;
                    }
                    catch { /* handler unavailable, skip */ }
                }

                if (root != null && string.Equals(root.Id, nativeId, StringComparison.OrdinalIgnoreCase))
                {
                    aliases.Add(rootUri);
                }
            }
        }

        // Also check mounts whose underlying ID starts with nativeId (subfolder mounts)
        foreach (var mount in _mountedFolders.Values)
        {
            if (mount.MountedFolder is IStorableChild mountedChild)
            {
                var mountedId = mountedChild.Id;
                // If the native ID starts with this mount's ID, it's reachable through it
                if (nativeId.StartsWith(mountedId, StringComparison.OrdinalIgnoreCase) && nativeId != mountedId)
                {
                    var remaining = nativeId.Substring(mountedId.Length);
                    var aliasPath = $"{mount.ProtocolScheme}://{remaining.TrimStart('/', '\\')}";
                    if (!aliases.Contains(aliasPath))
                        aliases.Add(aliasPath);
                }
            }
        }

        return aliases;
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

        // Also check built-in browsable protocol roots (e.g., mfs:// with native root ID "/")
        foreach (var (scheme, handler) in _protocolHandlers)
        {
            if (handler is MountedFolderProtocolHandler)
                continue; // Already checked above

            if (!handler.HasBrowsableRoot)
                continue;

            var rootUri = $"{scheme}://";
            if (!StorageTools._storableRegistry.TryGetValue(rootUri, out var root))
                continue;

            var rootId = root.Id;
            if (fullId.StartsWith(rootId, StringComparison.OrdinalIgnoreCase))
            {
                var matchLength = rootId.Length;
                if (matchLength > longestMatchLength)
                {
                    var remainingPart = fullId.Substring(matchLength);
                    var aliasId = string.IsNullOrEmpty(remainingPart) ?
                        rootUri :
                        $"{scheme}://{remainingPart.TrimStart('/', '\\')}";

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
    /// Resolves a potentially aliased ID back to its full underlying (native) ID.
    /// </summary>
    /// <param name="aliasId">The potentially aliased ID to resolve</param>
    /// <param name="maxDepth">Maximum resolution depth to prevent infinite loops</param>
    /// <returns>The fully resolved underlying ID</returns>
    /// <remarks>
    /// This is the inverse of <see cref="SubstituteWithMountAlias"/>.
    /// Replaces the mount scheme prefix (<c>scheme://</c>) with the mounted folder's native ID,
    /// reversing the string substitution that created the alias. Recurses to handle chained mounts.
    /// Does NOT assume IDs are filesystem paths — uses pure string concatenation, not Path.Combine.
    /// </remarks>
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

            // Reverse the alias substitution: replace "scheme://" with the folder's native ID.
            // This is the exact inverse of SubstituteWithMountAlias, which replaced the native ID
            // prefix with "scheme://". SubstituteWithMountAlias trims leading separators from the
            // remaining part, so we must re-add one if the native ID doesn't end with a separator
            // and there is a remaining part.
            var remaining = currentId.Substring($"{scheme}://".Length);
            if (remaining.Length > 0 && !child.Id.EndsWith('/') && !child.Id.EndsWith('\\'))
                currentId = child.Id + "/" + remaining;
            else
                currentId = child.Id + remaining;

            depth++;
        }

        if (depth >= maxDepth)
            throw new InvalidOperationException($"Alias resolution exceeded maximum depth of {maxDepth} for ID: {aliasId}");

        return currentId;
    }

    /// <summary>
    /// Resolves all protocol alias tokens (e.g., <c>quadra://home/source/</c>) found within a string,
    /// replacing each with its resolved native ID. Useful for processing CLI arguments or other
    /// free-text that may contain storage IDs.
    /// </summary>
    public static string ResolveAliasesInText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        foreach (var scheme in GetRegisteredProtocols())
        {
            var prefix = $"{scheme}://";
            if (!text.Contains(prefix))
                continue;

            int searchStart = 0;
            while (searchStart < text.Length)
            {
                int idx = text.IndexOf(prefix, searchStart, StringComparison.Ordinal);
                if (idx < 0) break;

                // Find the end of the token (next whitespace, quote, or end of string)
                int end = idx + prefix.Length;
                while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '"' && text[end] != '\'')
                    end++;

                var token = text[idx..end];
                var resolved = ResolveAliasToFullId(token);
                if (resolved != token)
                {
                    text = text[..idx] + resolved + text[end..];
                    searchStart = idx + resolved.Length;
                }
                else
                {
                    searchStart = end;
                }
            }
        }

        return text;
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
