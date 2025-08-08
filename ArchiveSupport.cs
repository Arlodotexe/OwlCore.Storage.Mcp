using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Diagnostics;
using OwlCore.Storage.SharpCompress;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace OwlCore.Storage.Mcp;

/// <summary>
/// Centralized archive support helpers (extensions list + detection logic) used for both mounting and archive creation.
/// </summary>
internal static class ArchiveSupport
{
    private static readonly HashSet<string> _archiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tar.xz", ".txz"
    };

    /// <summary>
    /// True if the provided file name or path matches a supported archive extension.
    /// Handles multi-part extensions (e.g. .tar.gz) by checking longest matches first.
    /// </summary>
    public static bool IsSupportedArchiveExtension(string path)
    {
        var fileName = Path.GetFileName(path);
        foreach (var ext in _archiveExtensions.Where(e => e.Contains('.')).OrderByDescending(e => e.Length))
        {
            if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        var single = Path.GetExtension(fileName);
        return _archiveExtensions.Contains(single);
    }

    /// <summary>
    /// Enumerates all supported archive extensions.
    /// </summary>
    public static IEnumerable<string> GetSupportedArchiveExtensions() => _archiveExtensions;
    
    /// <summary>
    /// Creates an empty archive within the <paramref name="parentFolder"/>.
    /// </summary>
    /// <param name="parentFolder">The folder to create the archive in.</param>
    /// <param name="name">The name of the new archive.</param>
    /// <param name="archiveType">The type of archive to create.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    /// <returns>A task containing the new <see cref="ArchiveFolder"/>.</returns>
    public static async Task<ArchiveFolder> CreateArchiveAsync(IModifiableFolder parentFolder, string name,
        ArchiveType archiveType, CancellationToken cancellationToken)
    {
        var archiveFile = await parentFolder.CreateFileAsync(name, overwrite: true, cancellationToken);
        var archive = ArchiveFactory.Create(archiveType);

        await FlushToAsync(archiveFile, archive, cancellationToken);

        var archiveFolder = new ArchiveFolder(archive, archiveFile.Name, archiveFile.Name);
        return archiveFolder;
    }
    
    /// <summary>
    /// Writes an archive to the supplied file.
    /// </summary>
    /// <param name="archiveFile">The file to save the archive to.</param>
    /// <param name="archive">The archive to save.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    public static async Task FlushToAsync(IFile archiveFile, IWritableArchive archive, CancellationToken cancellationToken)
    {
        using var archiveFileStream = await archiveFile.OpenReadWriteAsync(cancellationToken);
        Guard.IsEqualTo(archiveFileStream.Position, 0);
        archive.SaveTo(archiveFileStream, new WriterOptions(CompressionType.Deflate));
    }
}
