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
    [McpServerTool, Description("Opens a file in the operating system's default application (e.g., opens a .pdf in a PDF viewer, a .png in an image viewer). This does NOT read or return file contents — use read_file_as_text or read_file_text_range to read file contents instead.")]
    public static async Task<object> StartFile(
        [Description("Path to the file or executable to start.")] string filePath,
        string verb = "open",
        [Description("Arguments to pass to the process.")] string arguments = "",
        [Description("Working directory for the process. Defaults to the file's directory.")] string? workingDirectory = null,
        [Description("Text to write to the process's standard input (stdin). Null to not send any input.")] string? stdin = null,
        [Description("Maximum time in milliseconds to wait for the process to exit. Default 30000 (30s). Set to 0 to not wait (fire-and-forget for GUI apps).")] int timeoutMs = 30000)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new McpException("File path cannot be empty", McpErrorCode.InvalidParams);

            var resolvedPath = ProtocolRegistry.ResolveAliasToFullId(filePath);

            // Fire-and-forget mode (timeoutMs == 0): open with shell, no stdio capture
            if (timeoutMs == 0)
            {
                if (!File.Exists(resolvedPath))
                {
                    if (resolvedPath != filePath)
                        throw new McpException($"File not found. Original path: '{filePath}', Resolved path: '{resolvedPath}'", McpErrorCode.InvalidParams);
                    else
                        throw new McpException($"File not found: '{filePath}'", McpErrorCode.InvalidParams);
                }

                var shellPsi = new ProcessStartInfo
                {
                    FileName = resolvedPath,
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    Verb = verb ?? "open",
                    Arguments = arguments ?? "",
                };

                var shellProcess = Process.Start(shellPsi);
                if (shellProcess == null)
                    throw new McpException($"Failed to start file: '{filePath}'", McpErrorCode.InternalError);

                return new
                {
                    started = true,
                    message = resolvedPath != filePath
                        ? $"Started: '{filePath}' (resolved to: '{resolvedPath}')"
                        : $"Started: '{filePath}'"
                };
            }

            // Captured mode: redirect stdio, wait for exit
            var psi = new ProcessStartInfo
            {
                FileName = resolvedPath,
                Arguments = arguments ?? "",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin != null,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
                    ?? (File.Exists(resolvedPath) ? Path.GetDirectoryName(resolvedPath) : null)
                    ?? Path.GetTempPath(),
            };

            using var process = Process.Start(psi);
            if (process == null)
                throw new McpException($"Failed to start process: '{filePath}'", McpErrorCode.InternalError);

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
            throw new McpException($"Failed to start file '{filePath}': {ex.Message}", ex, McpErrorCode.InternalError);
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
