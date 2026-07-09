using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MusicBar.功能;

public sealed class NteMusicLibrary
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3",
        ".wav",
        ".ogg",
        ".flac",
        ".m4a",
        ".aac",
        ".wma"
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly List<NteMusicSong> _songs = new();
    private readonly List<NteStoredFolder> _folders = new();
    private readonly HashSet<string> _favoriteSongIds = new(StringComparer.Ordinal);
    private readonly string _songsPath;
    private readonly string _favoritesPath;
    private readonly string _legacyFavoritesPath;

    public IReadOnlyList<NteMusicSong> Songs => _songs;
    public IReadOnlySet<string> FavoriteSongIds => _favoriteSongIds;
    public IReadOnlyList<NteMusicFolderGroup> FolderGroups => BuildFolderGroups(_songs).ToList();
    public IReadOnlyList<NteMusicFolderGroup> QueueFolderGroups => BuildFolderGroups(Queue).ToList();
    public bool FavoritesOnly { get; private set; }

    public IEnumerable<NteMusicSong> Queue => FavoritesOnly
        ? _songs.Where(song => song.IsFavorite)
        : _songs;

    public NteMusicLibrary(string? storageRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(storageRoot) ? ResolveDefaultStorageRoot() : storageRoot;
        var dataDirectory = Path.Combine(root, "NteData");
        Directory.CreateDirectory(dataDirectory);

        _songsPath = Path.Combine(dataDirectory, "songs.json");
        _favoritesPath = Path.Combine(dataDirectory, "favorites.json");
        _legacyFavoritesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MusicBar",
            "nte_favorites.json");

        LoadFavorites();
        LoadSongs();
    }

    public int ImportFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return 0;
        }

        var files = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(IsSupportedAudioPath)
            .OrderBy(path => Path.GetDirectoryName(path), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);

        return ImportFilesCore(files);
    }

    public int ImportFolders(IEnumerable<string> folderPaths)
    {
        if (folderPaths is null)
        {
            return 0;
        }

        var total = 0;
        foreach (var folder in folderPaths)
        {
            total += ImportFolder(folder);
        }

        return total;
    }

    public int ImportFiles(IEnumerable<string> filePaths)
    {
        return ImportFilesCore(filePaths);
    }

    public void ToggleFavoritesOnly()
    {
        FavoritesOnly = !FavoritesOnly;
    }

    public void ToggleFavorite(string songId)
    {
        var index = _songs.FindIndex(song => song.Id == songId);
        if (index < 0)
        {
            return;
        }

        var song = _songs[index];
        var isFavorite = !song.IsFavorite;
        _songs[index] = song with { IsFavorite = isFavorite };

        if (isFavorite)
        {
            _favoriteSongIds.Add(songId);
        }
        else
        {
            _favoriteSongIds.Remove(songId);
        }

        SaveFavorites();
    }

    public bool RenameFolder(string folderId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(folderId) || string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        var folder = _folders.FirstOrDefault(item => string.Equals(item.Id, folderId, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
        {
            return false;
        }

        folder.DisplayName = displayName.Trim();
        SaveSongs();
        return true;
    }

    public bool RemoveSong(string songId)
    {
        var removedFromSongs = _songs.RemoveAll(song => song.Id == songId) > 0;
        foreach (var folder in _folders)
        {
            folder.Songs.RemoveAll(path => string.Equals(NormalizeSongId(path), songId, StringComparison.Ordinal));
        }

        _folders.RemoveAll(folder => folder.Songs.Count == 0);
        var removedFromFavorites = _favoriteSongIds.Remove(songId);

        SaveSongs();
        SaveFavorites();
        return removedFromSongs || removedFromFavorites;
    }

    private int ImportFilesCore(IEnumerable<string>? filePaths)
    {
        if (filePaths is null)
        {
            return 0;
        }

        var existingPaths = new HashSet<string>(_songs.Select(song => song.Path), StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var filePath in filePaths)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || !IsSupportedAudioPath(filePath))
            {
                continue;
            }

            var normalizedPath = NormalizeSongId(filePath);
            if (!existingPaths.Add(normalizedPath))
            {
                continue;
            }

            var folderPath = Path.GetDirectoryName(normalizedPath);
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                continue;
            }

            var folder = GetOrCreateFolder(folderPath);
            if (!folder.Songs.Any(path => string.Equals(NormalizeSongId(path), normalizedPath, StringComparison.Ordinal)))
            {
                folder.Songs.Add(normalizedPath);
            }

            _songs.Add(CreateSong(normalizedPath, folder.Id));
            added++;
        }

        if (added > 0)
        {
            SaveSongs();
        }

        SaveFavorites();
        return added;
    }

    private IEnumerable<NteMusicFolderGroup> BuildFolderGroups(IEnumerable<NteMusicSong> songs)
    {
        var songsByFolder = songs
            .GroupBy(song => song.FolderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var folder in _folders)
        {
            if (!songsByFolder.TryGetValue(folder.Id, out var folderSongs) || folderSongs.Count == 0)
            {
                continue;
            }

            yield return new NteMusicFolderGroup(
                folder.Id,
                folder.Path,
                folder.DisplayName,
                folder.Path,
                folderSongs);
        }
    }

    private NteStoredFolder GetOrCreateFolder(string folderPath)
    {
        var normalizedPath = Path.GetFullPath(folderPath);
        var id = NormalizeFolderId(normalizedPath);
        var existing = _folders.FirstOrDefault(folder => string.Equals(folder.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            return existing;
        }

        var folder = new NteStoredFolder
        {
            Id = id,
            Path = normalizedPath,
            DisplayName = Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        };
        if (string.IsNullOrWhiteSpace(folder.DisplayName))
        {
            folder.DisplayName = normalizedPath;
        }

        _folders.Add(folder);
        return folder;
    }

    private NteMusicSong CreateSong(string path, string folderId)
    {
        var id = NormalizeSongId(path);
        return new NteMusicSong(
            Id: id,
            Title: Path.GetFileNameWithoutExtension(path),
            Path: path,
            CoverPath: FindSidecarCover(path),
            IsFavorite: _favoriteSongIds.Contains(id),
            FolderId: folderId);
    }

    private static bool IsSupportedAudioPath(string path)
    {
        return SupportedExtensions.Contains(Path.GetExtension(path));
    }

    private static string NormalizeSongId(string path)
    {
        return Path.GetFullPath(path);
    }

    private static string NormalizeFolderId(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? FindSidecarCover(string audioPath)
    {
        var directory = Path.GetDirectoryName(audioPath);
        var name = Path.GetFileNameWithoutExtension(audioPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        foreach (var extension in new[] { ".png", ".jpg", ".jpeg" })
        {
            var candidate = Path.Combine(directory, name + extension);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ResolveDefaultStorageRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MusicBar.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MusicBar.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private void LoadSongs()
    {
        try
        {
            if (!File.Exists(_songsPath))
            {
                return;
            }

            var json = File.ReadAllText(_songsPath);
            var document = JsonSerializer.Deserialize<NteSongsDocument>(json);
            if (document?.Folders is null)
            {
                return;
            }

            var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var storedFolder in document.Folders)
            {
                if (string.IsNullOrWhiteSpace(storedFolder.Path))
                {
                    continue;
                }

                var folder = GetOrCreateFolder(storedFolder.Path);
                if (!string.IsNullOrWhiteSpace(storedFolder.DisplayName))
                {
                    folder.DisplayName = storedFolder.DisplayName.Trim();
                }

                foreach (var songPath in storedFolder.Songs ?? Enumerable.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(songPath) || !IsSupportedAudioPath(songPath))
                    {
                        continue;
                    }

                    var normalizedPath = NormalizeSongId(songPath);
                    if (!existingPaths.Add(normalizedPath))
                    {
                        continue;
                    }

                    if (!folder.Songs.Any(path => string.Equals(NormalizeSongId(path), normalizedPath, StringComparison.Ordinal)))
                    {
                        folder.Songs.Add(normalizedPath);
                    }

                    _songs.Add(CreateSong(normalizedPath, folder.Id));
                }
            }
        }
        catch
        {
        }
    }

    private void SaveSongs()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_songsPath)!);
            var document = new NteSongsDocument
            {
                Version = 1,
                Folders = _folders.Select(folder => new NteStoredFolderDocument
                {
                    Id = folder.Id,
                    Path = folder.Path,
                    DisplayName = folder.DisplayName,
                    Songs = folder.Songs.ToList()
                }).ToList()
            };
            var json = JsonSerializer.Serialize(document, JsonOptions);
            File.WriteAllText(_songsPath, json);
        }
        catch
        {
        }
    }

    private void LoadFavorites()
    {
        LoadFavoritesFrom(_legacyFavoritesPath);
        LoadFavoritesFrom(_favoritesPath);
    }

    private void LoadFavoritesFrom(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var favs = JsonSerializer.Deserialize<List<string>>(json);
                if (favs != null)
                {
                    foreach (var fav in favs)
                    {
                        _favoriteSongIds.Add(fav);
                    }
                }
            }
        }
        catch
        {
        }
    }

    private void SaveFavorites()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_favoritesPath)!);
            var json = JsonSerializer.Serialize(_favoriteSongIds.ToList(), JsonOptions);
            File.WriteAllText(_favoritesPath, json);
        }
        catch
        {
        }
    }

    private sealed class NteStoredFolder
    {
        public string Id { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public List<string> Songs { get; } = new();
    }

    private sealed class NteSongsDocument
    {
        public int Version { get; set; }
        public List<NteStoredFolderDocument>? Folders { get; set; }
    }

    private sealed class NteStoredFolderDocument
    {
        public string Id { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public List<string>? Songs { get; set; }
    }
}

public sealed record NteMusicFolderGroup(
    string Id,
    string Path,
    string DisplayName,
    string DirectoryMarker,
    IReadOnlyList<NteMusicSong> Songs);

public sealed record NteMusicSong(
    string Id,
    string Title,
    string Path,
    string? CoverPath,
    bool IsFavorite,
    string FolderId);
