using OwlCore.Storage;
using OwlCore.Storage.System.IO;
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
    public string OriginalFolderId { get; set; } = string.Empty;
    public string MountName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<string> DependsOn { get; set; } = new();
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
    /// Adds or updates a mount configuration
    /// </summary>
    public void AddOrUpdateMount(string protocolScheme, string originalFolderId, string mountName)
    {
        var dependsOn = new List<string>();
        var scheme = ProtocolRegistry.ExtractScheme(originalFolderId);
        if (scheme != null && ProtocolRegistry.IsMountedFolder(scheme))
        {
            dependsOn.Add(scheme);
        }

        var mounts = Mounts;
        mounts[protocolScheme] = new MountConfiguration
        {
            ProtocolScheme = protocolScheme,
            OriginalFolderId = originalFolderId,
            MountName = mountName,
            CreatedAt = DateTime.UtcNow,
            DependsOn = dependsOn
        };
        Mounts = mounts; // This triggers the setter and saves
    }

    /// <summary>
    /// Removes a mount configuration
    /// </summary>
    public void RemoveMount(string protocolScheme)
    {
        var mounts = Mounts;
        if (mounts.Remove(protocolScheme))
        {
            Mounts = mounts; // This triggers the setter and saves
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
                // Remove old entry and add new one
                mounts.Remove(currentProtocolScheme);
                config.ProtocolScheme = finalProtocolScheme;
            }

            config.MountName = finalMountName;
            mounts[finalProtocolScheme] = config;
            Mounts = mounts; // This triggers the setter and saves
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
                    // Cycle detected, add in creation order
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
            return true; // Cycle detected

        if (visited.Contains(protocolScheme))
            return false; // Already processed

        if (!mounts.TryGetValue(protocolScheme, out var config))
            return false; // Mount doesn't exist

        visited.Add(protocolScheme);
        recursionStack.Add(protocolScheme);

        // Visit dependencies first
        foreach (var dependency in config.DependsOn)
        {
            if (VisitMount(dependency, visited, recursionStack, result, mounts))
                return true; // Cycle detected
        }

        recursionStack.Remove(protocolScheme);
        result.Add(config);
        return false;
    }
}
