using System.Text.Json;
using Foundation;

namespace PKForge.App;

/// <summary>
/// The iOS side of PKForge's opaque document ids. A file or folder picked in the Files app is
/// only reachable while its security scope is open, and only after a relaunch through a
/// bookmark. Ids stay stable across picks ("ios:" + path) so restore points and save
/// identities keep matching; the bookmark that reopens them is kept beside, by id.
/// Files inside a picked folder are "iosin:" + folder id + newline + relative path.
/// </summary>
internal static class IosFiles
{
    private const string FilePrefix = "ios:";
    private const string ChildPrefix = "iosin:";
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _bookmarks;

    private static string StorePath => Path.Combine(FileSystem.AppDataDirectory, "ios-bookmarks.json");

    private static Dictionary<string, string> Bookmarks
    {
        get
        {
            if (_bookmarks is not null) return _bookmarks;
            try
            {
                _bookmarks = File.Exists(StorePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath)) ?? []
                    : [];
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            {
                _bookmarks = [];
            }
            return _bookmarks;
        }
    }

    private static void Persist()
    {
        try { File.WriteAllText(StorePath, JsonSerializer.Serialize(Bookmarks)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Services.AppLog.Warn("ios", $"Bookmarks were not saved: {error.Message}");
        }
    }

    /// <summary>Remembers a picked url (keeping access across launches) and returns its stable id.</summary>
    public static string Remember(NSUrl url)
    {
        var path = url.Path ?? url.AbsoluteString ?? "";
        var id = FilePrefix + path;
        var scoped = url.StartAccessingSecurityScopedResource();
        try
        {
            var data = url.CreateBookmarkData((NSUrlBookmarkCreationOptions)0, [], null, out var error);
            if (data is not null)
            {
                lock (Gate)
                {
                    Bookmarks[id] = data.GetBase64EncodedString(NSDataBase64EncodingOptions.None);
                    Persist();
                }
            }
            else if (error is not null)
            {
                Services.AppLog.Warn("ios", $"No bookmark for {path}: {error.LocalizedDescription}");
            }
        }
        finally
        {
            if (scoped) url.StopAccessingSecurityScopedResource();
        }
        return id;
    }

    public static string ChildId(string folderId, string relativePath) => ChildPrefix + folderId + "\n" + relativePath;

    public static string DisplayName(NSUrl url) => url.LastPathComponent ?? Path.GetFileName(url.Path) ?? "file";

    /// <summary>Runs <paramref name="work"/> with the real path of <paramref name="id"/>, its scope open.</summary>
    public static T Access<T>(string id, Func<string, T> work)
    {
        if (id.StartsWith(ChildPrefix, StringComparison.Ordinal))
        {
            var body = id[ChildPrefix.Length..];
            var split = body.IndexOf('\n');
            if (split < 0) throw new FileNotFoundException("Unknown document.", id);
            var folder = body[..split];
            var relative = body[(split + 1)..];
            return Access(folder, root => work(Path.Combine(root, relative)));
        }
        if (!id.StartsWith(FilePrefix, StringComparison.Ordinal))
            return work(id); // a plain path inside the app's own container

        var path = id[FilePrefix.Length..];
        NSUrl? url = null;
        string? stored;
        lock (Gate) Bookmarks.TryGetValue(id, out stored);
        if (stored is not null)
        {
            using var data = new NSData(stored, NSDataBase64DecodingOptions.None);
            url = NSUrl.FromBookmarkData(data, NSUrlBookmarkResolutionOptions.WithoutUI, null, out var stale, out var error);
            if (url is null && error is not null)
                Services.AppLog.Warn("ios", $"Bookmark for {path} did not resolve: {error.LocalizedDescription}");
            if (url is not null && stale) Remember(url);
        }
        url ??= NSUrl.FromFilename(path);
        var scoped = url.StartAccessingSecurityScopedResource();
        try
        {
            return work(url.Path ?? path);
        }
        finally
        {
            if (scoped) url.StopAccessingSecurityScopedResource();
        }
    }

    public static void Access(string id, Action<string> work) => Access(id, path => { work(path); return 0; });

    /// <summary>Replaces the file's bytes, through a temporary sibling when the folder allows it.</summary>
    public static void WriteBytes(string path, byte[] bytes)
    {
        var temporary = path + ".pkforge-tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            return;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A single picked file grants that file only: no sibling can be created. Write in place.
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception) { /* nothing to clean */ }
        }
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
