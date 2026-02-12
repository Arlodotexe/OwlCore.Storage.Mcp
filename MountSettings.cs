using OwlCore.Storage;
using OwlCore.ComponentModel;

namespace OwlCore.Storage.Mcp;

/// <summary>
/// Represents a persisted mount configuration
/// </summary>
public class MountConfiguration
{
    public string ProtocolScheme { get; set; } = string.Empty;
    public string OriginalStorableId { get; set; } = string.Empty;
    public string MountName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<string> DependsOn { get; set; } = new();

    // For archive mounts we store StorableType.File (the folder presentation is implicit once mounted).
    public StorableType MountType { get; set; } = StorableType.Folder;
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
    /// A flat list of mount configurations. One entry per protocol scheme.
    /// </summary>
    /// <remarks>
    /// Each protocol scheme maps to exactly one mount configuration.
    /// When a mount is re-created with the same scheme, the previous entry is replaced.
    /// </remarks>
    public List<MountConfiguration> Mounts
    {
        get => GetSetting(() => new List<MountConfiguration>());
        set => SetSetting(value);
    }

    public override async Task LoadAsync(CancellationToken? cancellationToken = null)
    {
        await base.LoadAsync(cancellationToken);
    }

    /// <summary>
    /// Adds or updates a mount configuration (generalized for any storable).
    /// Removes any existing entries for the same protocol scheme before adding.
    /// </summary>
    public void AddOrUpdateMount(string protocolScheme, string originalStorableId, string mountName, StorableType mountType)
    {
        var dependsOn = new List<string>();
        var scheme = ProtocolRegistry.ExtractScheme(originalStorableId);
        if (scheme != null && ProtocolRegistry.IsMountedFolder(scheme))
            dependsOn.Add(scheme);

        var mounts = Mounts;

        // Remove any existing entries for this protocol scheme to prevent accumulation of stale entries
        mounts.RemoveAll(m => m.ProtocolScheme == protocolScheme);

        mounts.Add(new MountConfiguration
        {
            ProtocolScheme = protocolScheme,
            OriginalStorableId = originalStorableId,
            MountName = mountName,
            CreatedAt = DateTime.UtcNow,
            DependsOn = dependsOn,
            MountType = mountType,
        });
        Mounts = mounts; // persist
    }

    /// <summary>
    /// Renames a mount configuration
    /// </summary>
    public void RenameMount(string currentProtocolScheme, string originalStorableId, string? newProtocolScheme = null, string? newMountName = null)
    {
        // Match using normalized underlying IDs so alias forms in settings still match live mount IDs
        var targetConfig = Mounts.FirstOrDefault(m => m.ProtocolScheme == currentProtocolScheme &&
            ProtocolRegistry.ResolveAliasToFullId(m.OriginalStorableId).Equals(
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
}
