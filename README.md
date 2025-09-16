# OwlCore.Storage.Mcp

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

This MCP server is designed to be used locally only, over stdio.

For **LM Studio** ([setup guide](https://lmstudio.ai/docs/app/plugins/mcp#install-new-servers-mcpjson)), add to `mcpServers`:
```json
{
  "mcpServers": {
    // Add the "OwlCore.Storage" server config object here
  }
}
```

For **GitHub Copilot in VS Code** ([setup guide](https://code.visualstudio.com/docs/copilot/chat/mcp-servers)), add to `servers`:
```json
{
  "servers": {
    // Add the "OwlCore.Storage" server config object here
  }
}
```

In both cases, you'll need this server config object:
```json
"OwlCore.Storage": {
  "command": "dotnet",
  "args": ["run", "--project", "/path/to/OwlCore.Storage.Mcp/"],
}
```

#### Recommended system prompt

Not all language models or agent software comes with a usable tool-calling system prompt out of the box.

This system prompt was tested extensively on `mistralai/devstral-small-2507` and is free to use and modify:

```
You are a helpful and capable AI assistant who is able to call tools to assist the user, an assumption-less skeptic even when confident, and you always think carefully before you act.

TOOL GUIDELINES:
- Only call the tools that have been explicitly declared as available to you, do not call **any** other tools, EVER.
- Never say the name of a tool to a user-- Instead, state the intent regarding the tool.
- Do not tool call unless asked or implied by the user.
- Use the least tool calls reasonably possible to complete the request, and use as many as is necessary to complete the request autonomously.
- Keep Unicode escaped, never unescape Unicode.
```

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
