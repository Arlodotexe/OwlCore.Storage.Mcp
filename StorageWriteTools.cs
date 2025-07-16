using ModelContextProtocol.Server;
using ModelContextProtocol;
using System.ComponentModel;
using OwlCore.Storage;
using OwlCore.Kubo;
using System.Collections.Concurrent;
using System.Text;

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
    public static async Task<object> CreateFile(string parentFolderId, string fileName)
    {
        try
        {
            await StorageTools.EnsureStorableRegistered(parentFolderId);

            if (!_storableRegistry.TryGetValue(parentFolderId, out var storable) || storable is not IModifiableFolder modifiableFolder)
                throw new McpException($"Modifiable folder with ID '{parentFolderId}' not found or not modifiable", McpErrorCode.InvalidParams);

            var newFile = await modifiableFolder.CreateFileAsync(fileName);
            string newFileId = ProtocolRegistry.IsCustomProtocol(parentFolderId) ? StorageTools.CreateCustomItemId(parentFolderId, fileName) : newFile.Id;
            _storableRegistry[newFileId] = newFile;

            return new
            {
                id = newFileId,
                name = newFile.Name,
                type = "file"
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

    [McpServerTool, Description("Writes text content to a specific line range in a file (1-based indexing). To INSERT content at startLine, omit endLine parameter entirely. To REPLACE lines startLine through endLine, provide both parameters. endLine must be >= startLine and <= total lines. Do NOT use endLine=0, use null or omit it.")]
    public static async Task<string> WriteFileTextRange(string fileId, string content, int startLine, int? endLine = null)
    {
        await StorageTools.EnsureStorableRegistered(fileId);

        if (!_storableRegistry.TryGetValue(fileId, out var item) || item is not IFile file)
            throw new McpException($"File with ID '{fileId}' not found", McpErrorCode.InvalidParams);

        // Explicitly reject endLine of 0 since it's invalid (1-based indexing)
        if (endLine.HasValue && endLine.Value <= 0)
            throw new McpException($"Invalid endLine value: {endLine.Value}. endLine must be >= 1 (1-based indexing) or null for insertion. To insert content, omit endLine parameter entirely.", McpErrorCode.InvalidParams);

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

    [McpServerTool, Description("Writes binary content to a file by file ID or path.")]
    public static async Task<string> WriteFileAsBytes(string fileId, byte[] content)
    {
        try
        {
            await StorageTools.EnsureStorableRegistered(fileId);

            if (!_storableRegistry.TryGetValue(fileId, out var item) || item is not IFile file)
                throw new McpException($"File with ID '{fileId}' not found", McpErrorCode.InvalidParams);

            await file.WriteBytesAsync(content);
            return $"Successfully wrote {content.Length} bytes to file '{file.Name}'";
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to write bytes to file '{fileId}': {ex.Message}", ex, McpErrorCode.InternalError);
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

    [McpServerTool, Description("Moves or renames an item from source folder to target folder.")]
    public static async Task<object> MoveItem(string sourceFolderId, string itemName, string targetParentFolderId, string? newName = null)
    {
        try
        {
            await StorageTools.EnsureStorableRegistered(sourceFolderId);
            await StorageTools.EnsureStorableRegistered(targetParentFolderId);

            if (!_storableRegistry.TryGetValue(sourceFolderId, out var sourceParent) || sourceParent is not IModifiableFolder sourceModifiableFolder)
                throw new McpException($"Source folder with ID '{sourceFolderId}' not found or not modifiable", McpErrorCode.InvalidParams);

            if (!_storableRegistry.TryGetValue(targetParentFolderId, out var targetParent) || targetParent is not IModifiableFolder targetModifiableFolder)
                throw new McpException($"Target folder with ID '{targetParentFolderId}' not found or not modifiable", McpErrorCode.InvalidParams);

            var item = await sourceModifiableFolder.GetFirstByNameAsync(itemName);
            if (item is not IStorableChild storableChild)
                throw new McpException($"Item '{itemName}' not found in source folder or not movable", McpErrorCode.InvalidParams);

            // For moves, prefer using the efficient extension methods when possible
            IStorable newItem;
            string finalName = newName ?? item.Name;

            if (item is IChildFile childFile)
            {
                // Use the efficient MoveFromAsync extension method
                var movedFile = await targetModifiableFolder.MoveFromAsync(childFile, sourceModifiableFolder, false);
                newItem = movedFile;
                
                // If renaming is needed and the name is different, we'd need additional logic
                if (finalName != item.Name)
                {
                    // Note: Renaming during move is not directly supported by the extension method
                    // For now, we'll keep the original name
                    finalName = movedFile.Name;
                }
            }
            else if (item is IFile sourceFile)
            {
                // Fallback for IFile that's not IChildFile - use copy and delete
                var copiedFile = await targetModifiableFolder.CreateCopyOfAsync(sourceFile, false);
                newItem = copiedFile;
                
                // Delete the original after successful copy
                await sourceModifiableFolder.DeleteAsync(storableChild);
            }
            else if (item is IFolder sourceFolder)
            {
                var newFolder = await targetModifiableFolder.CreateFolderAsync(finalName);
                // Note: This is a simple move - for recursive copying, you'd need to implement that logic
                newItem = newFolder;
                
                // Delete the original folder (this will only work if it's empty)
                await sourceModifiableFolder.DeleteAsync(storableChild);
            }
            else
            {
                throw new McpException($"Unsupported item type for moving: {item.GetType()}", McpErrorCode.InvalidParams);
            }

            // Note: For IChildFile moves using MoveFromAsync, the original is already deleted by the extension method
            // For other types, we manually deleted above
            
            // Remove old registration and add new one
            string oldItemId = ProtocolRegistry.IsCustomProtocol(sourceFolderId) ? StorageTools.CreateCustomItemId(sourceFolderId, itemName) : item.Id;
            _storableRegistry.TryRemove(oldItemId, out _);

            string newItemId = ProtocolRegistry.IsCustomProtocol(targetParentFolderId) ? StorageTools.CreateCustomItemId(targetParentFolderId, finalName) : newItem.Id;
            _storableRegistry[newItemId] = newItem;

            return new
            {
                id = newItemId,
                name = newItem.Name,
                type = newItem switch
                {
                    IFile => "file",
                    IFolder => "folder",
                    _ => "unknown"
                }
            };
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to move item '{itemName}' from '{sourceFolderId}' to '{targetParentFolderId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Creates a copy of a file in the specified target folder.")]
    public static async Task<object> CopyFile(string sourceFileId, string targetParentFolderId, string? newName = null, bool overwrite = false)
    {
        try
        {
            await StorageTools.EnsureStorableRegistered(sourceFileId);
            await StorageTools.EnsureStorableRegistered(targetParentFolderId);

            if (!_storableRegistry.TryGetValue(sourceFileId, out var sourceItem) || sourceItem is not IFile sourceFile)
                throw new McpException($"Source file with ID '{sourceFileId}' not found or not a file", McpErrorCode.InvalidParams);

            if (!_storableRegistry.TryGetValue(targetParentFolderId, out var targetParent) || targetParent is not IModifiableFolder targetModifiableFolder)
                throw new McpException($"Target folder with ID '{targetParentFolderId}' not found or not modifiable", McpErrorCode.InvalidParams);

            try
            {
                // Try the efficient method first
                var copiedFile = await targetModifiableFolder.CreateCopyOfAsync(sourceFile, overwrite);
                
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
            catch (Exception)
            {
                // If the efficient method fails, fall back to manual copy
                
                // Determine the target file name
                var targetFileName = !string.IsNullOrEmpty(newName) ? newName : sourceFile.Name;
                
                // Read the source file content
                var fileContent = await sourceFile.ReadBytesAsync(CancellationToken.None);
                
                // Create a new file in the target folder
                var newFile = await targetModifiableFolder.CreateFileAsync(targetFileName, overwrite);
                
                // Write the content to the new file
                await newFile.WriteBytesAsync(fileContent, CancellationToken.None);
                
                string newFileId = ProtocolRegistry.IsCustomProtocol(targetParentFolderId) ? 
                    StorageTools.CreateCustomItemId(targetParentFolderId, newFile.Name) : 
                    newFile.Id;
                _storableRegistry[newFileId] = newFile;

                return new
                {
                    id = newFileId,
                    name = newFile.Name,
                    type = "file"
                };
            }
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to copy file from '{sourceFileId}' to '{targetParentFolderId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool, Description("Moves a file from source folder to target folder using efficient move operations.")]
    public static async Task<object> MoveFile(string sourceFileId, string sourceFolderId, string targetParentFolderId, string? newName = null, bool overwrite = false)
    {
        try
        {
            await StorageTools.EnsureStorableRegistered(sourceFileId);
            await StorageTools.EnsureStorableRegistered(sourceFolderId);
            await StorageTools.EnsureStorableRegistered(targetParentFolderId);

            if (!_storableRegistry.TryGetValue(sourceFileId, out var sourceItem) || sourceItem is not IChildFile sourceFile)
                throw new McpException($"Source file with ID '{sourceFileId}' not found or not a child file", McpErrorCode.InvalidParams);

            if (!_storableRegistry.TryGetValue(sourceFolderId, out var sourceParent) || sourceParent is not IModifiableFolder sourceModifiableFolder)
                throw new McpException($"Source folder with ID '{sourceFolderId}' not found or not modifiable", McpErrorCode.InvalidParams);

            if (!_storableRegistry.TryGetValue(targetParentFolderId, out var targetParent) || targetParent is not IModifiableFolder targetModifiableFolder)
                throw new McpException($"Target folder with ID '{targetParentFolderId}' not found or not modifiable", McpErrorCode.InvalidParams);

            // Use the OwlCore.Storage extension method for efficient moving
            var movedFile = await targetModifiableFolder.MoveFromAsync(sourceFile, sourceModifiableFolder, overwrite);
            
            // If a new name was specified, the extension method doesn't support renaming during move
            // So we'd need to rename after the move, but for now we'll use the original name
            
            // Remove old registration and add new one
            _storableRegistry.TryRemove(sourceFileId, out _);

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
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to move file from '{sourceFileId}' to '{targetParentFolderId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }



}
