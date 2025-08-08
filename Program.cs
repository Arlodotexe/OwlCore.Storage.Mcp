using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using ModelContextProtocol;
using System.ComponentModel;
using OwlCore.Storage;
using OwlCore.Storage.Mcp;
using System.Text;
using System.Collections.Concurrent;
using ModelContextProtocol.Protocol;
using System.Diagnostics;

// Ensure proper UTF-8 encoding for console output
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(consoleLogOptions =>
{
    // Configure all logs to go to stderr
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

// Initialize storage system and restore mounts before starting the server
await ProtocolRegistry.EnsureInitializedAsync();

await builder.Build().RunAsync();

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
    [McpServerTool, Description("Starts/launches a file with the system's default application. Supports protocol aliases (e.g., myproject://file.txt, mfs://document.pdf).")]
    public static string StartFile(string filePath, string verb = "open")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new McpException("File path cannot be empty", McpErrorCode.InvalidParams);

            // Resolve any protocol aliases to actual file paths
            var resolvedPath = ProtocolRegistry.ResolveAliasToFullId(filePath);

            // Check if the resolved path is a local file that exists
            if (!File.Exists(resolvedPath))
            {
                // If the original path was different from resolved, show both in error
                if (resolvedPath != filePath)
                    throw new McpException($"File not found. Original path: '{filePath}', Resolved path: '{resolvedPath}'", McpErrorCode.InvalidParams);
                else
                    throw new McpException($"File not found: '{filePath}'", McpErrorCode.InvalidParams);
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = resolvedPath,
                UseShellExecute = true,
                CreateNoWindow = true,
                // Allow callers to specify a Shell verb (e.g., "print", "edit").
                Verb = verb ?? "open",
            };

            var process = Process.Start(processStartInfo);
            if (process != null)
            {
                // Show both original and resolved paths if they differ
                if (resolvedPath != filePath)
                    return $"Successfully started file: '{filePath}' (resolved to: '{resolvedPath}')";
                else
                    return $"Successfully started file: '{filePath}'";
            }
            else
            {
                throw new McpException($"Failed to start file: '{filePath}'", McpErrorCode.InternalError);
            }
        }
        catch (McpException)
        {
            throw; // Re-throw MCP exceptions as-is
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to start file '{filePath}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }
}
