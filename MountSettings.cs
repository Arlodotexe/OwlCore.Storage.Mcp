using OwlCore.Storage;
using OwlCore.Storage.System.IO;
using OwlCore.Diagnostics;
using OwlCore.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OwlCore.Storage.Mcp;

/// <summary>
/// Represents a persisted mount configuration
/// </summary>
public class MountConfiguration
{
    public string ProtocolScheme { get; set; } = string.Empty;

    [Obsolete("Use OriginalStorableId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public string OriginalFolderId { get; set; } = string.Empty;
    public string OriginalStorableId { get; set; } = string.Empty;
    public string MountName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<string> DependsOn { get; set; } = new();

    // For archive mounts we store StorableType.File (the folder presentation is implicit once mounted).
    public StorableType MountType { get; set; } = StorableType.Folder;

    /// <summary>
    /// The protocol scheme of the browseable root that the OriginalStorableId belongs to (e.g., "mfs").
    /// Used during mount restoration to get the correct protocol handler's root for navigation.
    /// </summary>
    public string? BrowsableRootProtocolScheme { get; set; } = null;
}

/// <summary>
/// Settings class for persisting mount configurations
/// </summary>
public class MountSettings : SettingsBase
{
    public MountSettings(IModifiableFolder settingsFolder)
        : base(settingsFolder, SystemTextSettingsSerializer.Singleton)
    {
    }

    /// <summary>
    /// Dictionary of mount configurations keyed by protocol scheme
    /// </summary>
    [Obsolete("Use Mounts. This is kept for migration reads only.")]
    internal Dictionary<string, MountConfiguration> MountsLegacy
    {
        get => GetSetting(() => new Dictionary<string, MountConfiguration>(), "Mounts");
        set => SetSetting(value, "Mounts");
    }

    /// <summary>
    /// A flat list of mount configurations.
    /// </summary>
    /// <remarks>
    /// This is preferred as it allows for multiple mounts of the same protocol scheme which each may be valid or invalid at different points in space or time, depending on the user.
    /// </remarks>
    public List<MountConfiguration> Mounts
    {
        get => GetSetting(() => new List<MountConfiguration>());
        set => SetSetting(value);
    }

    public override async Task LoadAsync(CancellationToken? cancellationToken = null)
    {
        await base.LoadAsync(cancellationToken);
        MigrateMountConfigurationDictionaryToList();
        MigrateLegacyOriginalFolderIdToStorableId();
    }

    /// <summary>
    /// Adds or updates a mount configuration (generalized for any storable)
    /// </summary>
    public void AddOrUpdateMount(string protocolScheme, string originalStorableId, string mountName, StorableType mountType, string? browsableRootProtocolScheme = null)
    {
        var dependsOn = new List<string>();
        var scheme = ProtocolRegistry.ExtractScheme(originalStorableId);
        if (scheme != null && ProtocolRegistry.IsMountedFolder(scheme))
            dependsOn.Add(scheme);

        var mounts = Mounts;
        mounts.Add(new MountConfiguration
        {
            ProtocolScheme = protocolScheme,
            // Do not populate deprecated OriginalFolderId anymore (kept for legacy reads)
            OriginalFolderId = string.Empty,
            OriginalStorableId = originalStorableId,
            MountName = mountName,
            CreatedAt = DateTime.UtcNow,
            DependsOn = dependsOn,
            MountType = mountType,
            BrowsableRootProtocolScheme = browsableRootProtocolScheme,
        });
        Mounts = mounts; // persist
    }

    internal void MigrateMountConfigurationDictionaryToList()
    {
        try
        {

            Logger.LogInformation("Checking for legacy mount configuration dictionary to migrate...");
            Logger.LogTrace($"Legacy mount configurations found: {MountsLegacy.Count}");
            if (MountsLegacy.Count == 0)
                return;

            // Legacy mount migration must be run before superseded list-based property is accessed.
            // If data exists in the "Mounts" file, it will be loaded by the legacy property and migrated.
            // When this happens, the type file must also be updated to reflect the new type.
            // Saving should do this automatically, so we stop there and wait for an explicit save to be requested.
            var mounts = MountsLegacy.Values.ToList();
            base.ResetSetting("Mounts"); // clear legacy setting

            Mounts = mounts;
            Logger.LogCritical("Migrated legacy mount configuration dictionary to list. Please save settings to complete migration.");
        }
        catch (InvalidCastException ex) when (ex.Message.Contains($"Unable to cast object of type '{typeof(List<MountConfiguration>)}' to type '{typeof(Dictionary<string, MountConfiguration>)}'"))
        {
            Logger.LogInformation("Legacy mount dictionary not present or already migrated. Skipping migration.");
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to migrate legacy mount configuration dictionary to list.", ex);
        }
    }

    internal void MigrateLegacyOriginalFolderIdToStorableId()
    {
        #pragma warning disable CS0612 // Suppress 'obsolete' warnings for legacy migration
        var mounts = Mounts;
        var changed = false;
        foreach (var cfg in mounts)
        {
            if (string.IsNullOrEmpty(cfg.OriginalStorableId) && !string.IsNullOrEmpty(cfg.OriginalFolderId))
            {
                cfg.OriginalStorableId = cfg.OriginalFolderId;
                cfg.OriginalFolderId = string.Empty; // clear deprecated field
                changed = true;
            }
        }
        if (changed)
            Mounts = mounts; // triggers save
        #pragma warning restore CS0612
    }

    /// <summary>
    /// Renames a mount configuration
    /// </summary>
    public void RenameMount(string currentProtocolScheme, string originalStorableId, string? newProtocolScheme = null, string? newMountName = null)
    {
        // Match using normalized underlying IDs so alias forms in settings still match live mount IDs
        var targetConfig = Mounts.FirstOrDefault(m => m.ProtocolScheme == currentProtocolScheme &&
            ProtocolRegistry.ResolveAliasToFullId(ResolveOriginalId(m)).Equals(
                ProtocolRegistry.ResolveAliasToFullId(originalStorableId), StringComparison.OrdinalIgnoreCase));

        if (targetConfig != null)
        {
            // Either mount name or protocol scheme (or both) can be changed.
            var finalProtocolScheme = newProtocolScheme ?? currentProtocolScheme;
            var finalMountName = newMountName ?? targetConfig.MountName;

            // Update protocol scheme if changed
            targetConfig.ProtocolScheme = finalProtocolScheme;

            // Update mount name if changed
            targetConfig.MountName = finalMountName;
        }
    }

    /// <summary>
    /// Gets mount configurations in dependency order (dependencies first)
    /// </summary>
    public List<MountConfiguration> GetMountsInDependencyOrder()
    {
        var mounts = Mounts;
        var result = new List<MountConfiguration>();
        var visited = new HashSet<string>();
        var recursionStack = new HashSet<string>();

        foreach (var mount in mounts.OrderBy(m => m.CreatedAt))
        {
            if (!visited.Contains(mount.ProtocolScheme))
            {
                if (VisitMount(mount.ProtocolScheme, visited, recursionStack, result, mounts))
                {
                    foreach (var remaining in mounts.Where(m => !visited.Contains(m.ProtocolScheme)))
                    {
                        result.Add(remaining);
                        visited.Add(remaining.ProtocolScheme);
                    }
                    break;
                }
            }
        }

        return result;
    }

    private bool VisitMount(string protocolScheme, HashSet<string> visited, HashSet<string> recursionStack, List<MountConfiguration> result, List<MountConfiguration> mounts)
    {
        if (recursionStack.Contains(protocolScheme))
            return true;

        if (visited.Contains(protocolScheme))
            return false;

        var config = mounts.FirstOrDefault(m => m.ProtocolScheme == protocolScheme);
        if (config == null)
            return false;

        visited.Add(protocolScheme);
        recursionStack.Add(protocolScheme);

        foreach (var dependency in config.DependsOn)
        {
            if (VisitMount(dependency, visited, recursionStack, result, mounts))
                return true;
        }

        recursionStack.Remove(protocolScheme);
        result.Add(config);

        return false;
    }

    #pragma warning disable CS0612
    internal static string ResolveOriginalId(MountConfiguration config)
        => string.IsNullOrEmpty(config.OriginalStorableId) ? config.OriginalFolderId : config.OriginalStorableId;
    #pragma warning restore CS0612
}
