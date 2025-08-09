using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Diagnostics;
using OwlCore.Storage.SharpCompress;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace OwlCore.Storage.Mcp;

/// <summary>
/// Centralized archive support helpers (extensions list + detection logic) used for both mounting and archive creation.
/// </summary>
public static class ArchiveSupport
{
    private static readonly HashSet<string> _readWriteExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".tar", ".tar.gz", ".tgz", ".gz" // BZip2 not available in current ArchiveType enum
    };

    private static readonly HashSet<string> _readOnlyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rar", ".7z", ".tar.xz", ".txz", ".bz2", ".tar.bz2", ".tbz2" // BZip2 and 7z read-only
    };

    private static readonly HashSet<string> _allArchiveExtensions = new(StringComparer.OrdinalIgnoreCase);

    static ArchiveSupport()
    {
        // Combine all supported extensions
        foreach (var ext in _readWriteExtensions)
            _allArchiveExtensions.Add(ext);
        foreach (var ext in _readOnlyExtensions)
            _allArchiveExtensions.Add(ext);
    }

    /// <summary>
    /// True if the provided file name or path matches a supported archive extension.
    /// Handles multi-part extensions (e.g. .tar.gz) by checking longest matches first.
    /// </summary>
    public static bool IsSupportedArchiveExtension(string path)
    {
        var fileName = Path.GetFileName(path);
        foreach (var ext in _allArchiveExtensions.Where(e => e.Contains('.')).OrderByDescending(e => e.Length))
        {
            if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        var single = Path.GetExtension(fileName);
        return _allArchiveExtensions.Contains(single);
    }

    /// <summary>
    /// True if the provided file name or path supports read/write operations.
    /// </summary>
    public static bool IsWritableArchiveExtension(string path)
    {
        var fileName = Path.GetFileName(path);
        foreach (var ext in _readWriteExtensions.Where(e => e.Contains('.')).OrderByDescending(e => e.Length))
        {
            if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        var single = Path.GetExtension(fileName);
        return _readWriteExtensions.Contains(single);
    }

    /// <summary>
    /// Enumerates all supported archive extensions.
    /// </summary>
    public static IEnumerable<string> GetSupportedArchiveExtensions() => _allArchiveExtensions;

    /// <summary>
    /// Enumerates read/write archive extensions.
    /// </summary>
    public static IEnumerable<string> GetWritableArchiveExtensions() => _readWriteExtensions;

    /// <summary>
    /// Maps a file extension to the corresponding ArchiveType for creation.
    /// Only returns ArchiveType for formats that support read/write operations.
    /// </summary>
    /// <param name="fileName">The file name with extension</param>
    /// <returns>The appropriate ArchiveType, or null if creation is not supported</returns>
    public static ArchiveType? GetArchiveTypeForCreation(string fileName)
    {
        // Only consider writable archive extensions
        if (!IsWritableArchiveExtension(fileName))
            return null;

        // Check multi-part extensions first (longest match)
        if (fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || 
            fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            return ArchiveType.Tar; // Create as TAR, compression will be applied via WriterOptions
        
        // Single extensions - only writable ones
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".zip" => ArchiveType.Zip,
            ".tar" => ArchiveType.Tar,
            ".gz" => null, // Pure .gz files are not supported for creation (would need existing content to compress)
            _ => null // Not supported for creation (read-only formats like .rar, .7z, .bz2)
        };
    }

    /// <summary>
    /// Enumerates read-only archive extensions.
    /// </summary>
    public static IEnumerable<string> GetReadOnlyArchiveExtensions() => _readOnlyExtensions;
    
    /// <summary>
    /// Creates an empty archive within the <paramref name="parentFolder"/>.
    /// </summary>
    /// <param name="parentFolder">The folder to create the archive in.</param>
    /// <param name="name">The name of the new archive.</param>
    /// <param name="archiveType">The type of archive to create.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    /// <returns>A task containing the created archive file.</returns>
    public static async Task<IFile> CreateArchiveAsync(IModifiableFolder parentFolder, string name,
        ArchiveType archiveType, CancellationToken cancellationToken)
    {
        // Create the archive file directly and use ArchiveFolder's FlushToAsync method
        var archiveFile = await parentFolder.CreateFileAsync(name, overwrite: true, cancellationToken);
        var archive = ArchiveFactory.Create(archiveType);

        await OwlCore.Storage.SharpCompress.ArchiveFolder.FlushToAsync(archiveFile, archive, cancellationToken);

        // Return just the file - mounting will handle creating ArchiveFolder instances as needed
        return archiveFile;
    }
}
