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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string OriginalFolderId { get; set; } = string.Empty;
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
    /// Dictionary of mount configurations keyed by protocol scheme
    /// </summary>
    public Dictionary<string, MountConfiguration> Mounts
    {
        get => GetSetting(() => new Dictionary<string, MountConfiguration>());
        set => SetSetting(value);
    }

    /// <summary>
    /// Adds or updates a mount configuration (generalized for any storable)
    /// </summary>
    public void AddOrUpdateMount(string protocolScheme, string originalStorableId, string mountName, StorableType mountType)
    {
        var dependsOn = new List<string>();
        var scheme = ProtocolRegistry.ExtractScheme(originalStorableId);
        if (scheme != null && ProtocolRegistry.IsMountedFolder(scheme))
            dependsOn.Add(scheme);

        var mounts = Mounts;
        mounts[protocolScheme] = new MountConfiguration
        {
            ProtocolScheme = protocolScheme,
            // Do not populate deprecated OriginalFolderId anymore (kept for legacy reads)
            OriginalFolderId = string.Empty,
            OriginalStorableId = originalStorableId,
            MountName = mountName,
            CreatedAt = DateTime.UtcNow,
            DependsOn = dependsOn,
            MountType = mountType,
        };
        Mounts = mounts; // persist
    }

    internal void MigrateLegacyOriginalId()
    {
        var mounts = Mounts;
        var changed = false;
        foreach (var kvp in mounts)
        {
            var cfg = kvp.Value;
            if (string.IsNullOrEmpty(cfg.OriginalStorableId) && !string.IsNullOrEmpty(cfg.OriginalFolderId))
            {
                cfg.OriginalStorableId = cfg.OriginalFolderId;
                cfg.OriginalFolderId = string.Empty; // clear deprecated field
                changed = true;
            }
        }
        if (changed)
            Mounts = mounts; // triggers save
    }

    /// <summary>
    /// Removes a mount configuration
    /// </summary>
    public void RemoveMount(string protocolScheme)
    {
        var mounts = Mounts;
        if (mounts.Remove(protocolScheme))
        {
            Mounts = mounts; // persist
        }
    }

    /// <summary>
    /// Renames a mount configuration
    /// </summary>
    public void RenameMount(string currentProtocolScheme, string? newProtocolScheme = null, string? newMountName = null)
    {
        var mounts = Mounts;
        if (mounts.TryGetValue(currentProtocolScheme, out var config))
        {
            var finalProtocolScheme = newProtocolScheme ?? currentProtocolScheme;
            var finalMountName = newMountName ?? config.MountName;

            if (finalProtocolScheme != currentProtocolScheme)
            {
                mounts.Remove(currentProtocolScheme);
                config.ProtocolScheme = finalProtocolScheme;
            }

            config.MountName = finalMountName;
            mounts[finalProtocolScheme] = config;
            Mounts = mounts;
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

        foreach (var mount in mounts.Values.OrderBy(m => m.CreatedAt))
        {
            if (!visited.Contains(mount.ProtocolScheme))
            {
                if (VisitMount(mount.ProtocolScheme, visited, recursionStack, result, mounts))
                {
                    foreach (var remaining in mounts.Values.Where(m => !visited.Contains(m.ProtocolScheme)))
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

    private bool VisitMount(string protocolScheme, HashSet<string> visited, HashSet<string> recursionStack, 
                           List<MountConfiguration> result, Dictionary<string, MountConfiguration> mounts)
    {
        if (recursionStack.Contains(protocolScheme))
            return true;
        if (visited.Contains(protocolScheme))
            return false;
        if (!mounts.TryGetValue(protocolScheme, out var config))
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

    internal static string ResolveOriginalId(MountConfiguration config)
        => string.IsNullOrEmpty(config.OriginalStorableId) ? config.OriginalFolderId : config.OriginalStorableId;
}
