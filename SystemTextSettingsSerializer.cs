using CommunityToolkit.Diagnostics;
using OwlCore.ComponentModel;

namespace OwlCore.Storage.Mcp;

/// <summary>
/// An <see cref="IAsyncSerializer{TSerialized}"/> implementation for serializing and deserializing streams using System.Text.Json.
/// </summary>
public class SystemTextSettingsSerializer : IAsyncSerializer<Stream>, ISerializer<Stream>
{
    /// <summary>
    /// A singleton instance for <see cref="SystemTextSettingsSerializer"/>.
    /// </summary>
    public static SystemTextSettingsSerializer Singleton { get; } = new();

    /// <inheritdoc />
    public async Task<Stream> SerializeAsync<T>(T data, CancellationToken? cancellationToken = null)
    {
        var stream = new MemoryStream();
        await global::System.Text.Json.JsonSerializer.SerializeAsync(stream, data, typeof(T), context: MountSerializerContext.Default, cancellationToken: cancellationToken ?? CancellationToken.None);
        return stream;
    }

    /// <inheritdoc />
    public async Task<Stream> SerializeAsync(Type inputType, object data, CancellationToken? cancellationToken = null)
    {
        var stream = new MemoryStream();
        await global::System.Text.Json.JsonSerializer.SerializeAsync(stream, data, inputType, context: MountSerializerContext.Default, cancellationToken: cancellationToken ?? CancellationToken.None);
        return stream;
    }

    /// <inheritdoc />
    public async Task<TResult> DeserializeAsync<TResult>(Stream serialized, CancellationToken? cancellationToken = null)
    {
        var result = await global::System.Text.Json.JsonSerializer.DeserializeAsync(serialized, typeof(TResult), MountSerializerContext.Default);
        Guard.IsNotNull(result);
        return (TResult)result;
    }

    /// <inheritdoc />
    public async Task<object> DeserializeAsync(Type returnType, Stream serialized, CancellationToken? cancellationToken = null)
    {
        var result = await global::System.Text.Json.JsonSerializer.DeserializeAsync(serialized, returnType, MountSerializerContext.Default);
        Guard.IsNotNull(result);
        return result;
    }

    /// <inheritdoc />
    public Stream Serialize<T>(T data)
    {
        var stream = new MemoryStream();
        global::System.Text.Json.JsonSerializer.Serialize(stream, data, typeof(T), context: MountSerializerContext.Default);
        return stream;
    }

    /// <inheritdoc />
    public Stream Serialize(Type type, object data)
    {
        var stream = new MemoryStream();
        global::System.Text.Json.JsonSerializer.Serialize(stream, data, type, context: MountSerializerContext.Default);
        return stream;
    }

    /// <inheritdoc />
    public TResult Deserialize<TResult>(Stream serialized)
    {
        var result = global::System.Text.Json.JsonSerializer.Deserialize(serialized, typeof(TResult), MountSerializerContext.Default);
        Guard.IsNotNull(result);
        return (TResult)result;
    }

    /// <inheritdoc />
    public object Deserialize(Type type, Stream serialized)
    {
        var result = global::System.Text.Json.JsonSerializer.Deserialize(serialized, type, MountSerializerContext.Default);
        Guard.IsNotNull(result);
        return result;
    }
}
