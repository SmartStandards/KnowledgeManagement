
namespace KnowledgeManagement.SmartStandards.Endpoints.Joplin {

  /// <summary>
  /// Provides persistent opaque storage required by the Joplin WebDAV synchronization
  /// facade in addition to the provider-neutral <see cref="IKnowledgeRepository"/>.
  /// 
  /// Joplin synchronization requires files that are not knowledge content, including
  /// locks, temporary files, sync-target metadata and binary resources. These artifacts
  /// deliberately do not belong in <see cref="IKnowledgeRepository"/>.
  /// </summary>
  public interface IJoplinSyncStateStore {
     
    /// <summary>
    /// Determines whether an opaque WebDAV file exists.
    /// </summary>
    bool FileExists(string path);

    /// <summary>
    /// Determines whether an opaque WebDAV collection exists.
    /// </summary>
    bool CollectionExists(string path);

    /// <summary>
    /// Reads an opaque WebDAV file.
    /// </summary>
    byte[] ReadFile(string path);

    /// <summary>
    /// Writes or replaces an opaque WebDAV file.
    /// </summary>
    void WriteFile(string path, byte[] content);

    /// <summary>
    /// Deletes an opaque WebDAV file or collection recursively.
    /// </summary>
    bool Delete(string path);

    /// <summary>
    /// Creates an opaque WebDAV collection.
    /// </summary>
    bool CreateCollection(string path);

    /// <summary>
    /// Moves an opaque WebDAV file or collection.
    /// </summary>
    bool Move(string sourcePath, string targetPath, bool overwrite);

    /// <summary>
    /// Returns direct opaque child entries of one collection.
    /// </summary>
    JoplinSyncStateEntry[] GetChildren(string collectionPath);

    /// <summary>
    /// Gets metadata for one opaque WebDAV file or collection.
    /// </summary>
    JoplinSyncStateEntry GetEntry(string path);

    /// <summary>
    /// Reads one provider-internal metadata document that is never exposed through
    /// WebDAV listing.
    /// </summary>
    string ReadInternalText(string name);

    /// <summary>
    /// Writes one provider-internal metadata document that is never exposed through
    /// WebDAV listing.
    /// </summary>
    void WriteInternalText(string name, string content);
  }

  /// <summary>
  /// Describes one opaque Joplin synchronization storage entry.
  /// </summary>
  public sealed class JoplinSyncStateEntry {

    private string _Path;
    private bool _IsCollection;
    private long _Length;
    private DateTime _LastModifiedUtc;

    /// <summary>
    /// Gets or sets the normalized WebDAV-relative path.
    /// </summary>
    public string Path {
      get {
        return _Path;
      }
      set {
        _Path = value;
      }
    }

    /// <summary>
    /// Gets or sets whether the entry represents a collection.
    /// </summary>
    public bool IsCollection {
      get {
        return _IsCollection;
      }
      set {
        _IsCollection = value;
      }
    }

    /// <summary>
    /// Gets or sets the content length for file entries.
    /// </summary>
    public long Length {
      get {
        return _Length;
      }
      set {
        _Length = value;
      }
    }

    /// <summary>
    /// Gets or sets the UTC modification time.
    /// </summary>
    public DateTime LastModifiedUtc {
      get {
        return _LastModifiedUtc;
      }
      set {
        _LastModifiedUtc = value;
      }
    }
  }

  /// <summary>
  /// Implements <see cref="IJoplinSyncStateStore"/> in one isolated file-system
  /// directory.
  /// 
  /// This store contains Joplin-specific synchronization state only. Knowledge content
  /// remains in the injected <see cref="IKnowledgeRepository"/>.
  /// </summary>
  public sealed class FileBasedJoplinSyncStateStore : IJoplinSyncStateStore {

    private const string _InternalDirectoryName = ".internal";

    private readonly object _SyncRoot;
    private readonly string _RootDirectory;

    /// <summary>
    /// Creates a persistent Joplin synchronization state store.
    /// </summary>
    /// <param name="rootDirectory">
    /// The directory used exclusively for Joplin synchronization metadata, resources,
    /// locks and temporary files.
    /// </param>
    public FileBasedJoplinSyncStateStore(string rootDirectory) {
      if (string.IsNullOrWhiteSpace(rootDirectory)) {
        throw new ArgumentException("A Joplin synchronization state directory is required.", nameof(rootDirectory));
      }

      _SyncRoot = new object();
      _RootDirectory = Path.GetFullPath(rootDirectory);

      Directory.CreateDirectory(_RootDirectory);
      Directory.CreateDirectory(Path.Combine(_RootDirectory, _InternalDirectoryName));
    }

    /// <summary>
    /// Determines whether an opaque WebDAV file exists.
    /// </summary>
    public bool FileExists(string path) {
      lock (_SyncRoot) {
        return File.Exists(this.ResolvePath(path));
      }
    }

    /// <summary>
    /// Determines whether an opaque WebDAV collection exists.
    /// </summary>
    public bool CollectionExists(string path) {
      lock (_SyncRoot) {
        return Directory.Exists(this.ResolvePath(path));
      }
    }

    /// <summary>
    /// Reads an opaque WebDAV file.
    /// </summary>
    public byte[] ReadFile(string path) {
      lock (_SyncRoot) {
        return File.ReadAllBytes(this.ResolvePath(path));
      }
    }

    /// <summary>
    /// Writes or replaces an opaque WebDAV file.
    /// </summary>
    public void WriteFile(string path, byte[] content) {
      lock (_SyncRoot) {
        string physicalPath = this.ResolvePath(path);
        string parentDirectory = Path.GetDirectoryName(physicalPath);

        if (string.IsNullOrEmpty(parentDirectory)) {
          throw new InvalidOperationException("Cannot resolve the parent directory.");
        }

        Directory.CreateDirectory(parentDirectory);
        File.WriteAllBytes(physicalPath, content);
      }
    }

    /// <summary>
    /// Deletes an opaque WebDAV file or collection recursively.
    /// </summary>
    public bool Delete(string path) {
      lock (_SyncRoot) {
        string physicalPath = this.ResolvePath(path);

        if (File.Exists(physicalPath)) {
          File.Delete(physicalPath);
          return true;
        }

        if (Directory.Exists(physicalPath)) {
          Directory.Delete(physicalPath, true);
          return true;
        }

        return false;
      }
    }

    /// <summary>
    /// Creates an opaque WebDAV collection.
    /// </summary>
    public bool CreateCollection(string path) {
      lock (_SyncRoot) {
        string physicalPath = this.ResolvePath(path);

        if (Directory.Exists(physicalPath)) {
          return false;
        }

        if (File.Exists(physicalPath)) {
          return false;
        }

        Directory.CreateDirectory(physicalPath);
        return true;
      }
    }

    /// <summary>
    /// Moves an opaque WebDAV file or collection.
    /// </summary>
    public bool Move(string sourcePath, string targetPath, bool overwrite) {
      lock (_SyncRoot) {
        string sourcePhysicalPath = this.ResolvePath(sourcePath);
        string targetPhysicalPath = this.ResolvePath(targetPath);

        if (File.Exists(sourcePhysicalPath)) {
          string targetParent = Path.GetDirectoryName(targetPhysicalPath);

          if (!string.IsNullOrEmpty(targetParent)) {
            Directory.CreateDirectory(targetParent);
          }

          if (File.Exists(targetPhysicalPath)) {
            if (!overwrite) {
              return false;
            }

            File.Delete(targetPhysicalPath);
          }

          File.Move(sourcePhysicalPath, targetPhysicalPath);
          return true;
        }

        if (Directory.Exists(sourcePhysicalPath)) {
          if (Directory.Exists(targetPhysicalPath)) {
            if (!overwrite) {
              return false;
            }

            Directory.Delete(targetPhysicalPath, true);
          }

          Directory.Move(sourcePhysicalPath, targetPhysicalPath);
          return true;
        }

        return false;
      }
    }

    /// <summary>
    /// Returns direct opaque child entries of one collection.
    /// </summary>
    public JoplinSyncStateEntry[] GetChildren(string collectionPath) {
      lock (_SyncRoot) {
        string physicalPath = this.ResolvePath(collectionPath);

        if (!Directory.Exists(physicalPath)) {
          return Array.Empty<JoplinSyncStateEntry>();
        }

        string internalDirectory = Path.Combine(_RootDirectory, _InternalDirectoryName);

        string[] directories = Directory.GetDirectories(physicalPath);
        string[] files = Directory.GetFiles(physicalPath);

        JoplinSyncStateEntry[] entries = new JoplinSyncStateEntry[
          directories.Length + files.Length
        ];

        int index = 0;

        foreach (string directory in directories) {
          if (string.Equals(
                Path.GetFullPath(directory),
                Path.GetFullPath(internalDirectory),
                StringComparison.OrdinalIgnoreCase
              )) {
            continue;
          }

          DirectoryInfo info = new DirectoryInfo(directory);

          JoplinSyncStateEntry entry = new JoplinSyncStateEntry();
          entry.Path = this.ToLogicalPath(directory);
          entry.IsCollection = true;
          entry.Length = 0;
          entry.LastModifiedUtc = info.LastWriteTimeUtc;

          entries[index] = entry;
          index++;
        }

        foreach (string file in files) {
          FileInfo info = new FileInfo(file);

          JoplinSyncStateEntry entry = new JoplinSyncStateEntry();
          entry.Path = this.ToLogicalPath(file);
          entry.IsCollection = false;
          entry.Length = info.Length;
          entry.LastModifiedUtc = info.LastWriteTimeUtc;

          entries[index] = entry;
          index++;
        }

        if (index == entries.Length) {
          return entries;
        }

        JoplinSyncStateEntry[] compact = new JoplinSyncStateEntry[index];
        Array.Copy(entries, compact, index);
        return compact;
      }
    }

    /// <summary>
    /// Gets metadata for one opaque WebDAV file or collection.
    /// </summary>
    public JoplinSyncStateEntry GetEntry(string path) {
      lock (_SyncRoot) {
        string physicalPath = this.ResolvePath(path);

        if (File.Exists(physicalPath)) {
          FileInfo info = new FileInfo(physicalPath);

          JoplinSyncStateEntry entry = new JoplinSyncStateEntry();
          entry.Path = this.NormalizePath(path);
          entry.IsCollection = false;
          entry.Length = info.Length;
          entry.LastModifiedUtc = info.LastWriteTimeUtc;
          return entry;
        }

        if (Directory.Exists(physicalPath)) {
          DirectoryInfo info = new DirectoryInfo(physicalPath);

          JoplinSyncStateEntry entry = new JoplinSyncStateEntry();
          entry.Path = this.NormalizePath(path);
          entry.IsCollection = true;
          entry.Length = 0;
          entry.LastModifiedUtc = info.LastWriteTimeUtc;
          return entry;
        }

        return null;
      }
    }

    /// <summary>
    /// Reads one provider-internal metadata document.
    /// </summary>
    public string ReadInternalText(string name) {
      lock (_SyncRoot) {
        string path = this.ResolveInternalPath(name);

        if (!File.Exists(path)) {
          return string.Empty;
        }

        return File.ReadAllText(path);
      }
    }

    /// <summary>
    /// Writes one provider-internal metadata document.
    /// </summary>
    public void WriteInternalText(string name, string content) {
      lock (_SyncRoot) {
        string path = this.ResolveInternalPath(name);
        File.WriteAllText(path, content);
      }
    }

    /// <summary>
    /// Resolves one logical opaque-storage path without allowing traversal outside the
    /// configured synchronization-state root.
    /// </summary>
    private string ResolvePath(string path) {
      string normalized = this.NormalizePath(path);
      string relative = normalized.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

      string physical = Path.GetFullPath(
        Path.Combine(_RootDirectory, relative)
      );

      string rootPrefix = _RootDirectory
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;

      if (!physical.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
          !string.Equals(physical, _RootDirectory, StringComparison.OrdinalIgnoreCase)) {
        throw new InvalidOperationException("The Joplin synchronization path escapes the configured state root.");
      }

      return physical;
    }

    /// <summary>
    /// Resolves one internal metadata file name.
    /// </summary>
    private string ResolveInternalPath(string name) {
      string safeName = Path.GetFileName(name);

      if (!string.Equals(name, safeName, StringComparison.Ordinal)) {
        throw new InvalidOperationException("Invalid internal Joplin state file name.");
      }

      return Path.Combine(
        _RootDirectory,
        _InternalDirectoryName,
        safeName
      );
    }

    /// <summary>
    /// Converts one physical path to a normalized WebDAV-relative path.
    /// </summary>
    private string ToLogicalPath(string physicalPath) {
      string relative = Path.GetRelativePath(_RootDirectory, physicalPath)
        .Replace(Path.DirectorySeparatorChar, '/');

      return "/" + relative;
    }

    /// <summary>
    /// Normalizes one opaque WebDAV path.
    /// </summary>
    private string NormalizePath(string path) {
      if (string.IsNullOrWhiteSpace(path)) {
        return "/";
      }

      string normalized = path.Trim().Replace('\\', '/');

      if (!normalized.StartsWith("/", StringComparison.Ordinal)) {
        normalized = "/" + normalized;
      }

      while (normalized.Contains("//", StringComparison.Ordinal)) {
        normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
      }

      if (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal)) {
        normalized = normalized.TrimEnd('/');
      }

      return normalized;
    }
  }

  /// <summary>
  /// Creates or reuses persistent Joplin synchronization-state stores addressed by one
  /// deterministic synchronization identifier.
  /// </summary>
  public interface IJoplinSyncStateStoreFactory {

    /// <summary>
    /// Returns the persistent synchronization-state store for one synchronization scope.
    /// Implementations must return the same logical store for the same identifier across
    /// requests and process restarts.
    /// </summary>
    IJoplinSyncStateStore GetOrCreate(string syncId);
  }

  /// <summary>
  /// Implements profile-scoped Joplin synchronization-state storage on the local file
  /// system.
  ///
  /// Each synchronization identifier becomes one direct child directory below the supplied
  /// root directory. The identifier is already a deterministic SHA-256 value generated by
  /// the WebDAV middleware and is therefore safe and stable as a directory name.
  /// </summary>
  public sealed class FileBasedJoplinSyncStateStoreFactory :
    IJoplinSyncStateStoreFactory {

    private readonly object _SyncRoot;
    private readonly string _RootDirectory;
    private readonly System.Collections.Generic.Dictionary<
      string,
      IJoplinSyncStateStore
    > _Stores;

    /// <summary>
    /// Creates one file-system-backed synchronization-state store factory.
    /// </summary>
    /// <param name="rootDirectory">
    /// The common parent directory below which one subdirectory per synchronization scope
    /// is created on demand.
    /// </param>
    public FileBasedJoplinSyncStateStoreFactory(string rootDirectory) {
      if (string.IsNullOrWhiteSpace(rootDirectory)) {
        throw new ArgumentException(
          "A Joplin synchronization-state root directory is required.",
          nameof(rootDirectory)
        );
      }

      _SyncRoot = new object();
      _RootDirectory = Path.GetFullPath(
        rootDirectory
      );
      _Stores =
        new System.Collections.Generic.Dictionary<
          string,
          IJoplinSyncStateStore
        >(StringComparer.Ordinal);

      Directory.CreateDirectory(
        _RootDirectory
      );
    }

    /// <summary>
    /// Returns the persistent synchronization-state store for one synchronization scope.
    /// The physical directory is created only when this method is called.
    /// </summary>
    public IJoplinSyncStateStore GetOrCreate(string syncId) {
      if (string.IsNullOrWhiteSpace(syncId)) {
        throw new ArgumentException(
          "A Joplin synchronization identifier is required.",
          nameof(syncId)
        );
      }

      lock (_SyncRoot) {
        if (_Stores.ContainsKey(syncId)) {
          return _Stores[syncId];
        }

        string directory = Path.Combine(
          _RootDirectory,
          syncId
        );

        FileBasedJoplinSyncStateStore store =
          new FileBasedJoplinSyncStateStore(
            directory
          );

        _Stores.Add(
          syncId,
          store
        );

        return store;
      }
    }

  }

}
