using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using ModelContextProtocol;
using System.ComponentModel;
using OwlCore.Storage;
using OwlCore.Storage.System.IO;
using OwlCore.Storage.Mcp;
using System.Text;
using System.Collections.Concurrent;
using ModelContextProtocol.Protocol;
using System.Diagnostics;
using OwlCore.Diagnostics;
using OwlCore.Kubo;
using Ipfs.Http;
using OwlCore.Extensions;
using Ipfs;
using Ipfs.CoreApi;

var startTime = DateTime.Now;

// Cancellation
var cancellationTokenSource = new CancellationTokenSource();
var cancellationToken = cancellationTokenSource.Token;

Console.CancelKeyPress += (sender, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationTokenSource.Cancel();
};

// Ensure proper UTF-8 encoding for console output
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

// Working data
var appData = new SystemFolder(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
var owlCoreFolder = (SystemFolder)await appData.CreateFolderAsync("OwlCore", overwrite: false, cancellationToken);
var ocKuboFolder = (SystemFolder)await owlCoreFolder.CreateFolderAsync("Kubo", overwrite: false, cancellationToken);
var storageFolder = (SystemFolder)await owlCoreFolder.CreateFolderAsync("Storage", overwrite: false, cancellationToken);
var mcpWorkingFolder = (SystemFolder)await storageFolder.CreateFolderAsync("Mcp", overwrite: false, cancellationToken);

Logger.LogInformation($"Using data folder at {owlCoreFolder.Id}");

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(consoleLogOptions =>
{
    // Configure all logs to go to stderr
    consoleLogOptions.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace;
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

// Logging
var logFile = await mcpWorkingFolder.CreateFileAsync($"OwlCore.Storage.Mcp.{startTime.Ticks}.log", overwrite: false, cancellationToken);
var logStream = await logFile.OpenWriteAsync(cancellationToken);
using var logWriter = new StreamWriter(logStream) { AutoFlush = true };
Logger.MessageReceived += Logger_MessageReceived;
void Logger_MessageReceived(object? sender, LoggerMessageEventArgs e)
{
    if (e.Level == OwlCore.Diagnostics.LogLevel.Trace)
        return;

    // Set console color based on log level
    var originalColor = Console.ForegroundColor;
    switch (e.Level)
    {
        case OwlCore.Diagnostics.LogLevel.Warning:
            Console.ForegroundColor = ConsoleColor.Yellow;
            break;
        case OwlCore.Diagnostics.LogLevel.Error:
        case OwlCore.Diagnostics.LogLevel.Critical:
            Console.ForegroundColor = ConsoleColor.Red;
            break;
    }

    try
    {
        var msg = $"+{Math.Round((DateTime.Now - startTime).TotalMilliseconds)}ms {Path.GetFileNameWithoutExtension(e.CallerFilePath)} {e.CallerMemberName}  [{e.Level}] {e.Exception} {e.Message}";
        logWriter.WriteLine(msg);
        Console.WriteLine(msg);    
    }
    finally
    {
        // Always restore the original color
        Console.ForegroundColor = originalColor;
    }
}

/* Custom startup args
Move closing comment up to enable (fully or partially) 
var customStartupArgs = "";
args = customStartupArgs.Split(' ').ToArray();
*/

AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) => Logger.LogError(e.ExceptionObject?.ToString() ?? "Error message not found", e.ExceptionObject as Exception);
//AppDomain.CurrentDomain.FirstChanceException += (object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e) => Logger.LogError(e.Exception?.ToString() ?? "Error message not found", e.Exception);
//TaskScheduler.UnobservedTaskException += (object? sender, UnobservedTaskExceptionEventArgs e) => Logger.LogError(e.Exception?.ToString() ?? "Error message not found", e.Exception);

// Set up KuboBootstrapper and IpfsClient
var userProfileFolder = new SystemFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
var kuboRepoFolder = (SystemFolder)await userProfileFolder.CreateFolderAsync(".ipfs", overwrite: false, cancellationToken);
var kubo = new KuboBootstrapper(kuboRepoFolder.Path)
{
    BinaryWorkingFolder = ocKuboFolder,
    LaunchConflictMode = BootstrapLaunchConflictMode.Attach,
    ApiUri = new Uri("http://127.0.0.1:5001"),
    GatewayUri = new Uri("http://127.0.0.1:8080"),
    ApiUriMode = ConfigMode.UseExisting,
    GatewayUriMode = ConfigMode.UseExisting,
};

await kubo.StartAsync();

// Initialize protocol registry with IPFS client
ProtocolRegistry.Initialize(kubo.Client);

// Initialize storage system and restore mounts before starting the server
await ProtocolRegistry.EnsureInitializedAsync(cancellationToken);

await builder.Build().RunAsync(cancellationToken);

// [McpServerToolType]
public static class EchoTool
{
    [McpServerTool, Description("Echoes the message back to the client.")]
    public static string Echo(string message) => $"hello {message}";
}

[McpServerToolType]
public static class TimeTool
{
    [McpServerTool, Description("Gets the current date and time.")]
    public static string GetCurrentTime() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
}

// [McpServerToolType]
public static class UuidTool
{
    [McpServerTool, Description("Generates a new UUID.")]
    public static string GenerateUuid() => Guid.NewGuid().ToString();
}

// [McpServerToolType]
public static class CalculatorTool
{
    [McpServerTool, Description("Adds two numbers together.")]
    public static double Add(double a, double b) => a + b;

    [McpServerTool, Description("Subtracts the second number from the first.")]
    public static double Subtract(double a, double b) => a - b;

    [McpServerTool, Description("Multiplies two numbers.")]
    public static double Multiply(double a, double b) => a * b;

    [McpServerTool, Description("Divides the first number by the second.")]
    public static double Divide(double a, double b) => b != 0 ? a / b : throw new ArgumentException("Division by zero is not allowed");
}

[McpServerToolType]
public static class FileLauncherTool
{
    [McpServerTool, Description("Executes a program or opens a file. Runs executables, opens documents, images, audio, video — any file the OS can handle. Accepts any storage ID — non-local files are copied locally first. Returns { exitCode, stdout, stderr, timedOut }. Set timeoutMs=0 for fire-and-forget (GUI apps, media).")]
    public static async Task<object> StartFile(
        [Description("Storage ID of the file to start.")] string fileId,
        [Description("Shell verb for fire-and-forget mode (timeoutMs=0). Ignored in captured mode.")] string verb = "open",
        [Description("Arguments to pass to the process.")] string arguments = "",
        [Description("Working directory for the process.")] string? workingDirectory = null,
        [Description("Text to write to stdin.")] string? stdin = null,
        [Description("Timeout in ms. Default 30000. Set to 0 for fire-and-forget.")] int timeoutMs = 30000,
        [Description("Whether to overwrite any existing local copy of the started non-local file. Default false.")] bool overwrite = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fileId))
                throw new McpException("File ID cannot be empty", McpErrorCode.InvalidParams);

            // Reject bare command names — must be a storage ID
            if (!fileId.Contains('/') && !fileId.Contains('\\') && !fileId.Contains("://"))
                throw new McpException(
                    $"'{fileId}' is not a valid storage ID. Provide the full filesystem ID (e.g., '/usr/bin/{fileId}') or a protocol ID (e.g., 'mfs://{fileId}'). " +
                    $"Bare command names are not supported.",
                    McpErrorCode.InvalidParams);

            // Resolve working directory — must be a local filesystem directory
            if (workingDirectory != null && workingDirectory.Contains("://"))
            {
                var resolvedWd = ProtocolRegistry.ResolveAliasToFullId(workingDirectory);
                if (Directory.Exists(resolvedWd))
                    workingDirectory = resolvedWd;
                else
                    throw new McpException(
                        $"Working directory '{workingDirectory}' resolved to '{resolvedWd}' which is not a local filesystem directory.",
                        McpErrorCode.InvalidParams);
            }

            // Resolve the file ID to a local filesystem ID
            var resolvedId = ProtocolRegistry.ResolveAliasToFullId(fileId);
            string localId;

            if (File.Exists(resolvedId))
            {
                localId = resolvedId;
            }
            else
            {
                // Non-local file — copy to a deterministic local location via storage API
                var cancellationToken = CancellationToken.None;
                await StorageTools.EnsureStorableRegistered(fileId, cancellationToken);

                IFile? storageFile = null;
                if (StorageTools._storableRegistry.TryGetValue(fileId, out var storable) && storable is IFile f1)
                    storageFile = f1;
                else if (StorageTools._storableRegistry.TryGetValue(resolvedId, out storable) && storable is IFile f2)
                    storageFile = f2;

                if (storageFile == null)
                    throw new McpException($"File not found: '{fileId}'", McpErrorCode.InvalidParams);

                // Create download folder under temp: ./owlcore/storage/mcp/downloads/startfile/
                var tempRoot = new SystemFolder(Path.GetTempPath());
                var downloadFolder = (SystemFolder)await tempRoot.CreateFolderByRelativePathAsync(
                    "owlcore/storage/mcp/downloads/startfile/", overwrite: false, cancellationToken);

                // Deterministic file name: hash of underlying file ID + original extension
                var idHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(storageFile.Id)))[..16];
                var ext = Path.GetExtension(storageFile.Name);
                var localFileName = $"{idHash}{ext}";

                await downloadFolder.CreateCopyOfAsync(storageFile, overwrite, localFileName);

                localId = Path.Combine(downloadFolder.Path, localFileName);

                // On Linux, ensure the file is executable
                if (!OperatingSystem.IsWindows())
                    ProcessHelpers.EnableExecutablePermissions(localId);
            }

            // Fire-and-forget mode (timeoutMs == 0): open with shell, no stdio capture
            if (timeoutMs == 0)
            {
                var shellPsi = new ProcessStartInfo
                {
                    FileName = localId,
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    Verb = verb ?? "open",
                    Arguments = arguments ?? "",
                };

                var shellProcess = Process.Start(shellPsi);
                if (shellProcess == null)
                    throw new McpException($"Failed to start: '{fileId}'", McpErrorCode.InternalError);

                return new
                {
                    started = true,
                    message = localId != resolvedId || resolvedId != fileId
                        ? $"Started: '{fileId}' (local: '{localId}')"
                        : $"Started: '{fileId}'"
                };
            }

            // Captured mode: redirect stdio, wait for exit
            var psi = new ProcessStartInfo
            {
                FileName = localId,
                Arguments = arguments ?? "",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin != null,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
                    ?? Path.GetDirectoryName(localId)
                    ?? Path.GetTempPath(),
            };

            using var process = Process.Start(psi);
            if (process == null)
                throw new McpException($"Failed to start process: '{fileId}'", McpErrorCode.InternalError);

            if (stdin != null)
            {
                await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            var exited = process.WaitForExit(timeoutMs);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new
                {
                    exitCode = -1,
                    stdout,
                    stderr,
                    timedOut = true,
                    error = $"Process timed out after {timeoutMs}ms and was killed."
                };
            }

            return new
            {
                exitCode = process.ExitCode,
                stdout,
                stderr = string.IsNullOrEmpty(stderr) ? null : stderr,
                timedOut = false,
            };
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to start file '{fileId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }
}

[McpServerToolType]
public static class IpfsGetCidTool
{
    [McpServerTool, Description("Gets the Content Identifier (CID) for a file or folder. If the item isn't already addressable in IPFS, it will be added using provided options.")]
    public static async Task<string> GetCidAsync(string fileId, bool? allowAdd = null, bool? pin = null, bool? onlyHash = null, int? cidVersion = null, bool? noCopy = null, bool? fsCache = null)
    {
        try
        {
            // Resolve protocol aliases to actual file paths
            var resolvedPath = ProtocolRegistry.ResolveAliasToFullId(fileId);

            // Get the storable item
            var item = StorageTools._storableRegistry.TryGetValue(resolvedPath, out var storable) ? storable : throw new McpException($"File not found: '{fileId}'", McpErrorCode.InvalidParams);

            // Account for known implementations that use GetCidAsync wrappers to add support.
            {
                // System.IO
                if (item is not IAddFileToGetCid && item is SystemFile systemFile)
                    item = new ContentAddressedSystemFile(systemFile.Path, ProtocolRegistry.IpfsClient);

                if (item is not IAddFileToGetCid && item is SystemFolder systemFolder)
                    item = new ContentAddressedSystemFolder(systemFolder.Path, ProtocolRegistry.IpfsClient);
            }

            // Create AddFileOptions if needed (only for items requiring IPFS add)
            if (item is IAddFileToGetCid && allowAdd != true && noCopy != true && onlyHash != true)
                throw new McpException($"This operation requires adding data to the IPFS blockstore, but allowAdd is false. User must be aware that this will take additional disk space and they must proactively request or reactively approve those implications. If they haven't done explicitly this already, ask them now.", McpErrorCode.InvalidParams);

            AddFileOptions? addOptions = null;
            if (allowAdd != true && (pin.HasValue || onlyHash.HasValue || cidVersion.HasValue || noCopy.HasValue || fsCache.HasValue))
            {
                addOptions = new AddFileOptions
                {
                    Pin = pin,
                    OnlyHash = onlyHash,
                    CidVersion = cidVersion,
                    NoCopy = noCopy,
                    FsCache = fsCache
                };
            }

            // Get CID using OwlCore.Kubo extension method            
            Cid cid;
            try
            {
                // Try parameterless overload first (for MFS, IPNS, etc.)
                cid = await item.GetCidAsync(ProtocolRegistry.IpfsClient, addOptions ?? new AddFileOptions(), CancellationToken.None);
            }
            catch (NotSupportedException)
            {
                throw new McpException($"Item requires adding data to IPFS but allowAdd is false", McpErrorCode.InvalidParams);
            }

            return cid.ToString();
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to get CID for '{fileId}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }
}
