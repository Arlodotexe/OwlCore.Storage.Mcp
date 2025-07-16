# OwlCore.Storage.Mcp [![Version](https://img.shields.io/nuget/v/OwlCore.Storage.Mcp.svg)](https://www.nuget.org/packages/OwlCore.Storage.Mcp)

A Model Context Protocol (MCP) server that provides unified storage operations across multiple protocols including local files, IPFS MFS, HTTP/HTTPS, and custom mounted folders.

This project bridges the gap between different storage systems by providing a consistent MCP interface for file operations. Whether you're working with local files, IPFS content, web resources, or custom storage protocols, OwlCore.Storage.Mcp offers a unified API that abstracts away the underlying complexity. The plugin-style architecture makes it easy to extend support for new storage protocols while maintaining backward compatibility.

## Featuring:
- **Multi-protocol support**: Local files, IPFS MFS, HTTP/HTTPS, IPFS/IPNS content, memory storage
- **Custom mount system**: Mount any folder with a custom protocol scheme for organized access
- **Persistent mounts**: Mount configurations are saved and restored across sessions
- **Plugin architecture**: Easy to extend with new storage protocols via `IProtocolHandler`
- **Performance optimized**: Storage registry pattern prevents object recreation
- **Cross-platform**: Built on .NET 9.0 for maximum compatibility

## Usage

### Setup

1. **Clone and build the project**:
   ```bash
   git clone https://github.com/Arlodotexe/OwlCore.Storage.Mcp.git
   cd OwlCore.Storage.Mcp
   dotnet build
   ```

2. **Configure with MCP clients**:
   - The server communicates via stdio (no console output visible)
   - Configured and launched automatically by MCP clients
   - Server provides unified storage tools accessible through MCP client interfaces

### Client Configuration

For **LM Studio**, see the [MCP server installation guide](https://lmstudio.ai/docs/app/plugins/mcp#install-new-servers-mcpjson) to configure:
- Command: `dotnet`
- Args: `["run", "--project", "/path/to/OwlCore.Storage.Mcp.csproj"]`

For **GitHub Copilot in VS Code**, follow the [MCP servers documentation](https://code.visualstudio.com/docs/copilot/chat/mcp-servers) to set up the server integration.

### Available Storage Operations

Once connected, you can use MCP tools to:
- Browse local files, IPFS MFS, and HTTP resources
- Mount custom folders with personalized protocol schemes
- Read/write files across different storage systems
- Navigate folder hierarchies with unified commands

## Financing

We accept donations [here](https://github.com/sponsors/Arlodotexe) and [here](https://www.patreon.com/arlodotexe), and we do not have any active bug bounties.

## Versioning

Version numbering follows the Semantic versioning approach. However, if the major version is `0`, the code is considered alpha and breaking changes may occur as a minor update.

## License

All OwlCore code is licensed under the MIT License. OwlCore is licensed under the MIT License. See the [LICENSE](./src/LICENSE.txt) file for more details.
