using ModelContextProtocol.Server;
using ModelContextProtocol;
using System.ComponentModel;
using OwlCore.Storage;
using OwlCore.Kubo;
using System.Collections.Concurrent;
using System.Text;
using System.IO;
using System.Threading;
using OwlCore.Storage.SharpCompress;
using SharpCompress.Common;

namespace OwlCore.Storage.Mcp;

[McpServerToolType]
public static partial class StorageWriteTools
{
    private static readonly ConcurrentDictionary<string, IStorable> _storableRegistry = StorageTools._storableRegistry;

    [McpServerTool, Description("Creates a new folder in the specified parent folder by ID or path.")]
    public static async Task<object> CreateFolder(string parentFolderId, string folderName)
    {
        try
        {
            await StorageTools.EnsureStorableRegistered(parentFolderId);

            if (!_storableRegistry.TryGetValue(parentFolderId, out var storable) || storable is not IModifiableFolder modifiableFolder)
                throw new McpException($"Modifiable folder with ID '{parentFolderId}' not found or not modifiable", McpErrorCode.InvalidParams);

            var newFolder = await modifiableFolder.CreateFolderAsync(folderName);
            string newFolderId = ProtocolRegistry.IsCustomProtocol(parentFolderId) ? StorageTools.CreateCustomItemId(parentFolderId, folderName) : newFolder.Id;
            _storableRegistry[newFolderId] = newFolder;

            return new
            {
                id = newFolderId,
                name = newFolder.Name,
                type = "folder"
            };
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

    [McpServerTool, Description("Creates a new file in the specified parent folder by ID or path.")]
    public static async Task<object> CreateFile(string parentFolderId, string fileName, bool overwrite = false)
    {
        try
        {
            await StorageTools.EnsureStorableRegistered(parentFolderId);

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

            return new
            {
                id = newFileId,
                name = newFile.Name,
                type = "file",
                archiveType = archiveType?.ToString()
            };
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to create file '{fileName}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Writes text content to a file by file ID or path.")]
    public static async Task<string> WriteFileText(string fileId, string content)
    {
        try
        {
            await StorageTools.EnsureStorableRegistered(fileId);

            if (!_storableRegistry.TryGetValue(fileId, out var item) || item is not IFile file)
                throw new McpException($"File with ID '{fileId}' not found", McpErrorCode.InvalidParams);

            await file.WriteTextAsync(content);
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

    [McpServerTool, Description("Writes text content to a specific line range in a file (1-based indexing). To INSERT or APPEND content at startLine, omit endLine parameter entirely. To REPLACE lines startLine through endLine, provide both parameters. Do NOT use endLine=0, use null or omit it.")]
    public static async Task<string> WriteFileTextRange(string fileId, string content, int startLine, int? endLine = null)
    {
        await StorageTools.EnsureStorableRegistered(fileId);

        if (!_storableRegistry.TryGetValue(fileId, out var item) || item is not IFile file)
            throw new McpException($"File with ID '{fileId}' not found", McpErrorCode.InvalidParams);

        // Explicitly reject endLine of 0 since it's invalid (1-based indexing)
        if (endLine.HasValue && endLine.Value <= 0)
            throw new McpException($"Invalid endLine value: {endLine.Value}. endLine must be >= 1 (1-based indexing) or null for insertion. To insert or append content, omit endLine parameter entirely.", McpErrorCode.InvalidParams);

        var originalContent = await file.ReadTextAsync(CancellationToken.None);
        var lines = originalContent.Split('\n');
        
        // Validate line numbers (1-based)
        if (startLine < 1 || startLine > lines.Length + 1)
            throw new McpException($"Invalid startLine: {startLine}. Must be between 1 and {lines.Length + 1} (file has {lines.Length} lines)", McpErrorCode.InvalidParams);
        
        int actualEndLine = endLine ?? startLine - 1; // If no endLine, insert before startLine
        if (actualEndLine < startLine - 1 || actualEndLine > lines.Length)
            throw new McpException($"Invalid endLine range: {actualEndLine}. Must be between {startLine - 1} and {lines.Length}", McpErrorCode.InvalidParams);

        // Build the new content
        var newLines = new List<string>();
        
        // Add lines before the range
        newLines.AddRange(lines[0..(startLine - 1)]);
        
        // Add the new content (split into lines if it contains line breaks)
        var newContentLines = content.Split('\n');
        newLines.AddRange(newContentLines);
        
        // Add lines after the range
        if (actualEndLine < lines.Length)
            newLines.AddRange(lines[actualEndLine..]);

        var finalContent = string.Join('\n', newLines);
        await file.WriteTextAsync(finalContent);

        if (endLine.HasValue)
        {
            var linesReplaced = actualEndLine - startLine + 1;
            var linesAdded = newContentLines.Length;
            return $"Successfully replaced {linesReplaced} line(s) (lines {startLine}-{actualEndLine}) with {linesAdded} line(s) in file '{file.Name}'. " +
                   $"Original content: {originalContent.Length} characters, New content: {finalContent.Length} characters";
        }
        else
        {
            return $"Successfully inserted {newContentLines.Length} line(s) at line {startLine} in file '{file.Name}'. " +
                   $"Original: {originalContent.Length} characters, New: {finalContent.Length} characters";
        }
    }

    [McpServerTool, Description("Writes text content to a file with specified encoding by file ID or path.")]
    public static async Task<string> WriteFileAsTextWithEncoding(string fileId, string content, string encoding = "UTF-8")
    {
        try
        {
            await StorageTools.EnsureStorableRegistered(fileId);

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

            await file.WriteTextAsync(content, textEncoding);
            return $"Successfully wrote {content.Length} characters to file '{file.Name}' using {encoding} encoding";
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to write text with encoding '{encoding}' to file '{fileId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Deletes a file or folder by ID or path from its parent folder.")]
    public static async Task<string> DeleteItem(string parentFolderId, string itemName)
    {
        try
        {
            await StorageTools.EnsureStorableRegistered(parentFolderId);

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

    [McpServerTool, Description("Creates a copy of a file or folder in the specified target folder.")]
    public static async Task<object> CopyItem(string sourceItemId, string targetParentFolderId, string? newName = null, bool overwrite = false)
    {
        try
        {
            await StorageTools.EnsureStorableRegistered(sourceItemId);
            await StorageTools.EnsureStorableRegistered(targetParentFolderId);

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

                return new
                {
                    id = newFileId,
                    name = copiedFile.Name,
                    type = "file"
                };
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
                    // Let CreateRelativeFolderPathAsync ignore the filename and create only parent folders
                    var destinationFolder = (IModifiableFolder)await targetFolder.CreateFolderByRelativePathAsync(relativePath, overwrite: false, CancellationToken.None);
                    await destinationFolder.CreateCopyOfAsync(file, overwrite);
                }

                string newFolderId = ProtocolRegistry.IsCustomProtocol(targetParentFolderId) ? 
                    StorageTools.CreateCustomItemId(targetParentFolderId, targetFolder.Name) : 
                    targetFolder.Id;
                _storableRegistry[newFolderId] = targetFolder;

                return new
                {
                    id = newFolderId,
                    name = targetFolder.Name,
                    type = "folder"
                };
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

    [McpServerTool, Description("Moves a file or folder from source folder to target folder using efficient move operations.")]
    public static async Task<object> MoveItem(string sourceItemId, string sourceFolderId, string targetParentFolderId, string? newName = null, bool overwrite = false)
    {
        try
        {
            await StorageTools.EnsureStorableRegistered(sourceItemId);
            await StorageTools.EnsureStorableRegistered(sourceFolderId);
            await StorageTools.EnsureStorableRegistered(targetParentFolderId);

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

                return new
                {
                    id = newFileId,
                    name = movedFile.Name,
                    type = "file"
                };
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
                    var destinationFolder = (IModifiableFolder)await targetFolder.CreateFolderByRelativePathAsync(relativePath, overwrite: false, CancellationToken.None);
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

                return new
                {
                    id = newFolderId,
                    name = targetFolder.Name,
                    type = "folder"
                };
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

    

    [McpServerTool, Description("Creates any missing folders along a relative path from a starting item. If the last segment contains a dot and no trailing slash, it's treated as a file and the parent of the leaf is created. Supports '.' and '..' segments.")]
    public static async Task<object> CreateRelativeFolderPath(string startingItemId, string relativePath, bool overwrite = false)
    {
        try
        {
            await StorageTools.EnsureStorableRegistered(startingItemId);

            if (!_storableRegistry.TryGetValue(startingItemId, out var startingItem))
                throw new McpException($"Starting item with ID '{startingItemId}' not found", McpErrorCode.InvalidParams);

            var result = await ((IFolder)startingItem).CreateFolderByRelativePathAsync(relativePath, overwrite, CancellationToken.None);

            if (result is not IFolder resultFolder)
                throw new McpException($"The resulting item is not a folder. Ensure the starting item is a folder or the path resolves to a folder.", McpErrorCode.InvalidParams);

            // Register by internal ID and also expose alias/substituted ID for friendlier external use
            // TODO: We need to do this for the entire chain of created folders from result to startingItem.
            // Maybe optimize by having `CreateRelativeFolderPathAsync` return IAsyncEnumerable<IFolder>?
            _storableRegistry[resultFolder.Id] = resultFolder;
            string externalId = ProtocolRegistry.SubstituteWithMountAlias(resultFolder.Id);
            if (externalId != resultFolder.Id)
                _storableRegistry[externalId] = resultFolder;

            return new
            {
                id = externalId,
                name = resultFolder.Name,
                type = "folder"
            };
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
