using System.IO;
using System.Text.Json;

namespace Tfx;

/// <summary>
/// One bookmarked folder. <see cref="Label"/> is the display name; when empty
/// the UI falls back to the folder name from <see cref="Path"/>.
/// </summary>
public sealed class BookmarkEntry
{
    public string Label { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>
/// A named, ordered group of bookmarks. Array order is display order.
/// </summary>
public sealed class BookmarkGroup
{
    public string Name { get; set; } = "";
    public List<BookmarkEntry> Bookmarks { get; set; } = [];
}

/// <summary>
/// GUI-managed bookmarks persisted to <c>%APPDATA%\tfx\bookmarks.json</c>.
/// Replaces the read-only config.toml <c>[[bookmarks]]</c> source (which is
/// only used once, to seed this store on first run). Group order and the order
/// of bookmarks within a group are the on-disk array order.
/// </summary>
public sealed class BookmarkStore
{
    public int Version { get; set; } = 1;
    public List<BookmarkGroup> Groups { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>
    /// Loads the store from <paramref name="path"/>. Returns null when the file
    /// does not exist, so the caller can seed and create it on first run. On a
    /// parse error returns an empty store and leaves the existing (malformed)
    /// file untouched, so corrupt data is never silently overwritten.
    /// </summary>
    public static BookmarkStore? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<BookmarkStore>(json, Options) ?? new BookmarkStore();
        }
        catch
        {
            return new BookmarkStore();
        }
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, Options);
        File.WriteAllText(path, json);
    }
}
