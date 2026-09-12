using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gci.Core.Services;

/// <summary>JSON files under %LOCALAPPDATA%\GCI (or a custom root), written atomically.</summary>
public sealed class DataStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public DataStore(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GCI");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathFor(string name) => System.IO.Path.Combine(Root, name);

    public T Load<T>(string name, Func<T> fallback)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return fallback();
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? fallback();
        }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection and start fresh.
            File.Copy(path, path + ".corrupt", overwrite: true);
            return fallback();
        }
    }

    public void Save<T>(string name, T value)
    {
        var path = PathFor(name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, overwrite: true);
    }

    public byte[]? LoadBytes(string name) => File.Exists(PathFor(name)) ? File.ReadAllBytes(PathFor(name)) : null;

    public void SaveBytes(string name, byte[] bytes)
    {
        var path = PathFor(name);
        File.WriteAllBytes(path + ".tmp", bytes);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public void Delete(string name)
    {
        if (File.Exists(PathFor(name))) File.Delete(PathFor(name));
    }
}
