using ModelContextProtocol.Server;
using ModelContextProtocol;
using System.ComponentModel;
using OwlCore.Storage;
using OwlCore.Kubo;
using OwlCore.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using System.IO;
using System.Threading;
using OwlCore.Storage.SharpCompress;
using SharpCompress.Common;

namespace OwlCore.Storage.Mcp;

public static partial class StorageWriteTools
{
    private static readonly ConcurrentDictionary<string, IStorable> _storableRegistry = StorageTools._storableRegistry;

    [Description("Creates a new folder in the specified parent folder by ID or path.")]
    public static async Task<StorableItemResult> CreateFolder(string parentFolderId, string folderName)
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            if (folderName.Contains('/') || folderName.Contains('\\'))
                throw new McpException(
                    $"Folder name '{folderName}' contains path separators. " +
                    $"Use create_relative_folder_path(startingItemId: '{parentFolderId}', relativePath: '{folderName}') to create nested folders.",
                    McpErrorCode.InvalidParams);

            await StorageTools.EnsureStorableRegistered(parentFolderId, cancellationToken);

            if (!_storableRegistry.TryGetValue(parentFolderId, out var storable) || storable is not IModifiableFolder modifiableFolder)
                throw new McpException($"Modifiable folder with ID '{parentFolderId}' not found or not modifiable", McpErrorCode.InvalidParams);

            var newFolder = await modifiableFolder.CreateFolderAsync(folderName);
            string newFolderId = ProtocolRegistry.IsCustomProtocol(parentFolderId) ? StorageTools.CreateCustomItemId(parentFolderId, folderName) : newFolder.Id;
            _storableRegistry[newFolderId] = newFolder;

            return new StorableItemResult(
                Id: newFolderId,
                Name: newFolder.Name,
                Type: "folder"
            );
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to create folder '{folderName}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [Description("Creates a new file in the specified parent folder by ID or path. Content writes MUST be a second write_file_text or write_file_text_ranged tool call after creation.")]
    public static async Task<StorableItemWithArchiveTypeResult> CreateFile(string parentFolderId, string fileName, bool overwrite = false)
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            if (fileName.Contains('/') || fileName.Contains('\\'))
                throw new McpException(
                    $"File name '{fileName}' contains path separators. " +
                    $"Use create_relative_folder_path to create parent directories first, then create the file in the leaf folder.",
                    McpErrorCode.InvalidParams);

            await StorageTools.EnsureStorableRegistered(parentFolderId, cancellationToken);

            if (!_storableRegistry.TryGetValue(parentFolderId, out var storable) || storable is not IModifiableFolder modifiableFolder)
                throw new McpException($"Modifiable folder with ID '{parentFolderId}' not found or not modifiable", McpErrorCode.InvalidParams);

            IFile? newFile = null;
            ArchiveType? archiveType = null;

            // Automatically detect if this should be an archive based on file extension
            bool looksLikeArchive = ArchiveSupport.IsSupportedArchiveExtension(fileName);
            if (looksLikeArchive)
            {
                // Use centralized logic to determine archive type for creation
                var archiveTypeForCreation = ArchiveSupport.GetArchiveTypeForCreation(fileName);
                if (archiveTypeForCreation == null)
                {
                    var readOnlyFormats = string.Join(", ", ArchiveSupport.GetReadOnlyArchiveExtensions());
                    var writableFormats = string.Join(", ", ArchiveSupport.GetWritableArchiveExtensions());
                    throw new McpException($"Archive creation not supported for '{fileName}'. " +
                                         $"Read-only formats: {readOnlyFormats}. " +
                                         $"Writable formats: {writableFormats}.", McpErrorCode.InvalidParams);
                }

                archiveType = archiveTypeForCreation.Value;

                // Use helper to create empty archive file
                var createdFile = await ArchiveSupport.CreateArchiveAsync(modifiableFolder, fileName, archiveType.Value, CancellationToken.None);
                newFile = createdFile;
            }
            else
            {
                newFile = await modifiableFolder.CreateFileAsync(fileName, overwrite);
            }
            string newFileId = ProtocolRegistry.IsCustomProtocol(parentFolderId) ? StorageTools.CreateCustomItemId(parentFolderId, fileName) : newFile.Id;
            _storableRegistry[newFileId] = newFile;

            return new StorableItemWithArchiveTypeResult(
                Id: newFileId,
                Name: newFile.Name,
                Type: "file",
                ArchiveType: archiveType?.ToString()
            );
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (FileNotFoundException ex)
        {
            throw new McpException(ex.Message, ex, McpErrorCode.InvalidParams);
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to create file '{fileName}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [Description("Writes text content to a file by file ID or path. The file must already exist — use create_file to create it first.")]
    public static async Task<string> WriteFileText(string fileId, string content)
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            await StorageTools.EnsureStorableRegistered(fileId, cancellationToken);

            if (!_storableRegistry.TryGetValue(fileId, out var item) || item is not IFile file)
                throw new McpException($"File with ID '{fileId}' not found", McpErrorCode.InvalidParams);

            // Use OpenWriteAsync with SetLength(0) to ensure proper truncation
            var fileSem = StorageTools._fileAccessSemaphores.GetOrAdd(file.Id, _ => new SemaphoreSlim(1, 1));
            await fileSem.WaitAsync(cancellationToken);
            try
            {
                using (var stream = await file.OpenWriteAsync(cancellationToken))
                {
                    stream.SetLength(0);  // Truncate old content
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: false);
                    await writer.WriteAsync(content);
                    await writer.FlushAsync();
                }
            }
            finally { fileSem.Release(); }
            
            return $"Successfully wrote {content.Length} characters to file '{file.Name}'";
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to write text to file '{fileId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [Description("Overwrites lines in an existing file (use create_file first if it doesn't exist). By default writes a single line at startLine; pass endLine to replace the inclusive 1-based range [startLine, endLine], which must satisfy startLine <= endLine <= line count. Strict by default: content must have exactly as many lines as the target range, preserving the file's line count. A write either grows or shrinks the range, never both — pass allowMoreLines=true when content has more lines than the range (grow), or allowLessLines=true when it has fewer (shrink). Setting both is rejected. No sentinel values: there is no position past the last line, so appending means replacing the last line (grow) with its existing content plus the new lines, which requires knowing the line count.")]
    public static async Task<string> WriteFileTextRange(string fileId, string content, int startLine, int? endLine = null, bool? allowMoreLines = null, bool? allowLessLines = null)
    {
        SemaphoreSlim? fileSem = null;
        bool semAcquired = false;
        try
        {
            var cancellationToken = CancellationToken.None;
            await StorageTools.EnsureStorableRegistered(fileId, cancellationToken);

            if (!_storableRegistry.TryGetValue(fileId, out var item) || item is not IFile file)
                throw new McpException($"File with ID '{fileId}' not found", McpErrorCode.InvalidParams);

            fileSem = StorageTools._fileAccessSemaphores.GetOrAdd(file.Id, _ => new SemaphoreSlim(1, 1));
            await fileSem.WaitAsync(cancellationToken);
            semAcquired = true;
            var originalContent = await file.ReadTextAsync(cancellationToken);
            var lines = originalContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            // startLine is a 1-based line that must exist in the file.
            if (startLine < 1 || startLine > lines.Length)
                throw new McpException($"Invalid startLine: {startLine}. Stop blindly writing and use get_storable_info up front for line count. Must be between 1 and {lines.Length} (file has {lines.Length} lines)", McpErrorCode.InvalidParams);

            // endLine is the inclusive end of the range being overwritten. Omitted => the single line at startLine.
            // The range is never empty and never inverted: startLine <= endLine <= line count.
            int effectiveEndLine = endLine ?? startLine;
            if (effectiveEndLine < startLine || effectiveEndLine > lines.Length)
                throw new McpException($"Invalid endLine: {effectiveEndLine}. Stop blindly writing and use get_storable_info up front for line count. Must be between {startLine} and {lines.Length} (file has {lines.Length} lines)", McpErrorCode.InvalidParams);

            int rangeLineCount = effectiveEndLine - startLine + 1; // always >= 1
            var newContentLines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            // A single write moves the line count in one direction only. Permitting both growth and shrink
            // at once erases the caller's stated intent, so it's a misfire rather than a valid free-form mode.
            if (allowMoreLines == true && allowLessLines == true)
                throw new McpException(
                    "Conflicting flags: allowMoreLines and allowLessLines cannot both be true. A write either grows or shrinks the range — check yourself for an erroneous write attempt, and pass the one matching your content's line count relative to the range. ",
                    McpErrorCode.InvalidParams);

            // Strict by default: content must exactly fill the range (structure-preserving replace).
            // The two flags lift the bounds independently: allowMoreLines permits growth, allowLessLines permits shrink.
            if (allowMoreLines != true && newContentLines.Length > rangeLineCount)
                throw new McpException(
                    $"Too many lines: content has {newContentLines.Length} line(s) but the range {startLine}-{effectiveEndLine} spans {rangeLineCount} line(s). Provide exactly {rangeLineCount} line(s), or if intentional (check yourself for an erroneous write attempt) pass allowMoreLines=true to add lines.",
                    McpErrorCode.InvalidParams);

            if (allowLessLines != true && newContentLines.Length < rangeLineCount)
                throw new McpException(
                    $"Too few lines: content has {newContentLines.Length} line(s) but the range {startLine}-{effectiveEndLine} spans {rangeLineCount} line(s). Provide exactly {rangeLineCount} line(s), or if intentional (check yourself for an erroneous write attempt) pass allowLessLines=true to remove lines.",
                    McpErrorCode.InvalidParams);

            // Build the new content: lines before the range + content + lines after the range.
            var newLines = new List<string>(lines.Length + newContentLines.Length);
            newLines.AddRange(lines[0..(startLine - 1)]);
            newLines.AddRange(newContentLines);
            if (effectiveEndLine < lines.Length)
                newLines.AddRange(lines[effectiveEndLine..]);

            var finalContent = string.Join(Environment.NewLine, newLines);
            int lineCountDelta = newContentLines.Length - rangeLineCount;

            // Use OpenWriteAsync with SetLength(0) to ensure proper truncation
            using (var stream = await file.OpenWriteAsync(cancellationToken))
            {
                stream.SetLength(0);  // Truncate old content
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: false);
                await writer.WriteAsync(finalContent);
                await writer.FlushAsync();
            }

            string action = lineCountDelta == 0
                ? $"replaced {rangeLineCount} line(s) (lines {startLine}-{effectiveEndLine})"
                : $"replaced {rangeLineCount} line(s) (lines {startLine}-{effectiveEndLine}) with {newContentLines.Length} line(s)";
            return $"Successfully {action} in file '{file.Name}'. Line count delta: {lineCountDelta:+#;-#;0}; Original: {originalContent.Length} characters, New: {finalContent.Length} characters";
        }
        catch (McpException) { throw; }
        catch (Exception ex)
        {
            Logger.LogError($"{nameof(WriteFileTextRange)} failed for '{fileId}': {ex}", ex);
            throw new McpException($"Failed to write text range in '{fileId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
        finally { if (semAcquired) fileSem!.Release(); }
    }

    [Description("Deletes a file or folder by ID or path from its parent folder.")]
    public static async Task<string> DeleteItem(string parentFolderId, string itemName)
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            await StorageTools.EnsureStorableRegistered(parentFolderId, cancellationToken);

            if (!_storableRegistry.TryGetValue(parentFolderId, out var parent) || parent is not IModifiableFolder modifiableParent)
                throw new McpException($"Parent folder with ID '{parentFolderId}' not found or not modifiable", McpErrorCode.InvalidParams);

            var itemToDelete = await modifiableParent.GetFirstByNameAsync(itemName);
            if (itemToDelete is not IStorableChild storableChild)
                throw new McpException($"Item '{itemName}' not found in folder or not deletable", McpErrorCode.InvalidParams);

            await modifiableParent.DeleteAsync(storableChild);
            
            // Remove from registry if it was registered
            string itemId = ProtocolRegistry.IsCustomProtocol(parentFolderId) ? StorageTools.CreateCustomItemId(parentFolderId, itemName) : itemToDelete.Id;
            _storableRegistry.TryRemove(itemId, out _);
            
            return $"Successfully deleted item '{itemName}' from folder '{parent.Name}'";
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to delete item '{itemName}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [Description("Creates a copy of any file or folder in the specified target folder. Works across all supported protocols.")]
    public static async Task<StorableItemResult> CopyItem(string sourceItemId, string targetParentFolderId, string? newName = null, bool overwrite = false)
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            await StorageTools.EnsureStorableRegistered(sourceItemId, cancellationToken);
            await StorageTools.EnsureStorableRegistered(targetParentFolderId, cancellationToken);

            if (!_storableRegistry.TryGetValue(sourceItemId, out var sourceItem))
                throw new McpException($"Source item with ID '{sourceItemId}' not found", McpErrorCode.InvalidParams);

            if (!_storableRegistry.TryGetValue(targetParentFolderId, out var targetParent) || targetParent is not IModifiableFolder targetModifiableFolder)
                throw new McpException($"Target folder with ID '{targetParentFolderId}' not found or not modifiable", McpErrorCode.InvalidParams);

            if (sourceItem is IFile sourceFile)
            {
                // File copy logic
                // Determine the target file name
                var targetFileName = !string.IsNullOrEmpty(newName) ? newName : sourceFile.Name;
                
                // Use the OwlCore.Storage extension method with rename support
                IChildFile copiedFile;
                if (!string.IsNullOrEmpty(newName) && newName != sourceFile.Name)
                {
                    // Use the 4-parameter overload with rename support
                    copiedFile = await targetModifiableFolder.CreateCopyOfAsync(sourceFile, overwrite, targetFileName);
                }
                else
                {
                    // Use the 3-parameter overload 
                    copiedFile = await targetModifiableFolder.CreateCopyOfAsync(sourceFile, overwrite);
                }
                
                string newFileId = ProtocolRegistry.IsCustomProtocol(targetParentFolderId) ? 
                    StorageTools.CreateCustomItemId(targetParentFolderId, copiedFile.Name) : 
                    copiedFile.Id;
                _storableRegistry[newFileId] = copiedFile;

                return new StorableItemResult(
                    Id: newFileId,
                    Name: copiedFile.Name,
                    Type: "file"
                );
            }
            else if (sourceItem is IFolder sourceFolder)
            {
                // Folder copy logic (recursive implementation)
                var targetFolderName = !string.IsNullOrEmpty(newName) ? newName : sourceFolder.Name;
                var targetFolder = await targetModifiableFolder.CreateFolderAsync(targetFolderName, overwrite);

                // Recursively copy all files from source folder
                await foreach (var file in new DepthFirstRecursiveFolder(sourceFolder).GetFilesAsync(CancellationToken.None))
                {
                    // Compute full relative path from source root to the file (includes the filename)
                    var relativePath = await sourceFolder.GetRelativePathToAsync((IStorableChild)file);
                    
                    // Strip filename to get parent folder path only
                    // CreateFolderByRelativePathAsync treats all segments as folders, including file-like names
                    var parentPath = string.Empty;
                    if (relativePath.Contains('/'))
                    {
                        var lastSlashIndex = relativePath.LastIndexOf('/');
                        parentPath = relativePath.Substring(0, lastSlashIndex);
                    }
                    
                    // Create parent folder structure (if any), otherwise use target root
                    var destinationFolder = string.IsNullOrEmpty(parentPath)
                        ? (IModifiableFolder)targetFolder
                        : (IModifiableFolder)await targetFolder.CreateFolderByRelativePathAsync(parentPath, overwrite: false, CancellationToken.None);
                    
                    await destinationFolder.CreateCopyOfAsync(file, overwrite);
                }

                string newFolderId = ProtocolRegistry.IsCustomProtocol(targetParentFolderId) ? 
                    StorageTools.CreateCustomItemId(targetParentFolderId, targetFolder.Name) : 
                    targetFolder.Id;
                _storableRegistry[newFolderId] = targetFolder;

                return new StorableItemResult(
                    Id: newFolderId,
                    Name: targetFolder.Name,
                    Type: "folder"
                );
            }
            else
            {
                throw new McpException($"Unsupported item type for copying: {sourceItem.GetType()}", McpErrorCode.InvalidParams);
            }
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to copy item from '{sourceItemId}' to '{targetParentFolderId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [Description("Moves a file or folder from source folder to target folder using efficient move operations.")]
    public static async Task<StorableItemResult> MoveItem(string sourceItemId, string sourceFolderId, string targetParentFolderId, string? newName = null, bool overwrite = false)
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            await StorageTools.EnsureStorableRegistered(sourceItemId, cancellationToken);
            await StorageTools.EnsureStorableRegistered(sourceFolderId, cancellationToken);
            await StorageTools.EnsureStorableRegistered(targetParentFolderId, cancellationToken);

            if (!_storableRegistry.TryGetValue(sourceItemId, out var sourceItem))
                throw new McpException($"Source item with ID '{sourceItemId}' not found", McpErrorCode.InvalidParams);

            if (!_storableRegistry.TryGetValue(sourceFolderId, out var sourceParent) || sourceParent is not IModifiableFolder sourceModifiableFolder)
                throw new McpException($"Source folder with ID '{sourceFolderId}' not found or not modifiable", McpErrorCode.InvalidParams);

            if (!_storableRegistry.TryGetValue(targetParentFolderId, out var targetParent) || targetParent is not IModifiableFolder targetModifiableFolder)
                throw new McpException($"Target folder with ID '{targetParentFolderId}' not found or not modifiable", McpErrorCode.InvalidParams);

            if (sourceItem is IChildFile sourceFile)
            {
                // File move logic (existing implementation)
                IChildFile movedFile;
                if (!string.IsNullOrEmpty(newName) && newName != sourceFile.Name)
                {
                    // Use the new 4-parameter overload with rename support
                    movedFile = await targetModifiableFolder.MoveFromAsync(sourceFile, sourceModifiableFolder, overwrite, newName);
                }
                else
                {
                    // Use the existing 3-parameter overload
                    movedFile = await targetModifiableFolder.MoveFromAsync(sourceFile, sourceModifiableFolder, overwrite);
                }
                
                // Remove old registration and add new one
                _storableRegistry.TryRemove(sourceItemId, out _);

                string newFileId = ProtocolRegistry.IsCustomProtocol(targetParentFolderId) ? 
                    StorageTools.CreateCustomItemId(targetParentFolderId, movedFile.Name) : 
                    movedFile.Id;
                _storableRegistry[newFileId] = movedFile;

                return new StorableItemResult(
                    Id: newFileId,
                    Name: movedFile.Name,
                    Type: "file"
                );
            }
            else if (sourceItem is IFolder sourceFolder && sourceItem is IStorableChild storableChild)
            {
                // Folder move logic (copy then delete)
                var targetFolderName = !string.IsNullOrEmpty(newName) ? newName : sourceFolder.Name;
                var targetFolder = await targetModifiableFolder.CreateFolderAsync(targetFolderName, overwrite);

                // Recursively copy all files from source folder
                await foreach (var file in new DepthFirstRecursiveFolder(sourceFolder).GetFilesAsync(CancellationToken.None))
                {
                    var relativePath = await sourceFolder.GetRelativePathToAsync((IStorableChild)file);
                    
                    // Strip filename to get parent folder path only
                    // CreateFolderByRelativePathAsync treats all segments as folders, including file-like names
                    var parentPath = string.Empty;
                    if (relativePath.Contains('/'))
                    {
                        var lastSlashIndex = relativePath.LastIndexOf('/');
                        parentPath = relativePath.Substring(0, lastSlashIndex);
                    }
                    
                    // Create parent folder structure (if any), otherwise use target root
                    var destinationFolder = string.IsNullOrEmpty(parentPath)
                        ? (IModifiableFolder)targetFolder
                        : (IModifiableFolder)await targetFolder.CreateFolderByRelativePathAsync(parentPath, overwrite: false, CancellationToken.None);
                    
                    await destinationFolder.CreateCopyOfAsync(file, overwrite);
                }

                // Delete the original folder
                await sourceModifiableFolder.DeleteAsync(storableChild);

                // Remove old registration and add new one
                _storableRegistry.TryRemove(sourceItemId, out _);

                string newFolderId = ProtocolRegistry.IsCustomProtocol(targetParentFolderId) ? 
                    StorageTools.CreateCustomItemId(targetParentFolderId, targetFolder.Name) : 
                    targetFolder.Id;
                _storableRegistry[newFolderId] = targetFolder;

                return new StorableItemResult(
                    Id: newFolderId,
                    Name: targetFolder.Name,
                    Type: "folder"
                );
            }
            else
            {
                throw new McpException($"Unsupported item type for moving: {sourceItem.GetType()}", McpErrorCode.InvalidParams);
            }
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to move item from '{sourceItemId}' to '{targetParentFolderId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    

    [Description("Creates any missing folders along a relative path from a starting item. If the last segment contains a dot and no trailing slash, it's treated as a file and the parent of the leaf is created. Supports '.' and '..' segments.")]
    public static async Task<StorableItemResult> CreateRelativeFolderPath(string startingItemId, string relativePath, bool overwrite = false)
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            await StorageTools.EnsureStorableRegistered(startingItemId, cancellationToken);

            if (!_storableRegistry.TryGetValue(startingItemId, out var startingItem))
                throw new McpException($"Starting item with ID '{startingItemId}' not found", McpErrorCode.InvalidParams);

            var result = await ((IFolder)startingItem).CreateFolderByRelativePathAsync(relativePath, overwrite, cancellationToken);

            if (result is not IFolder resultFolder)
                throw new McpException($"The resulting item is not a folder. Ensure the starting item is a folder or the path resolves to a folder.", McpErrorCode.InvalidParams);

            // Register by internal ID and also expose alias/substituted ID for friendlier external use
            // TODO: We need to do this for the entire chain of created folders from result to startingItem.
            // Maybe optimize by having `CreateRelativeFolderPathAsync` return IAsyncEnumerable<IFolder>?
            _storableRegistry[resultFolder.Id] = resultFolder;
            string externalId = ProtocolRegistry.SubstituteWithMountAlias(resultFolder.Id);
            if (externalId != resultFolder.Id)
                _storableRegistry[externalId] = resultFolder;
            externalId = StorageTools.NormalizeOutboundAliasId(externalId, resultFolder);
            _storableRegistry[externalId] = resultFolder;

            return new StorableItemResult(
                Id: externalId,
                Name: resultFolder.Name,
                Type: "folder"
            );
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to create relative folder path '{relativePath}' from '{startingItemId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }
}
