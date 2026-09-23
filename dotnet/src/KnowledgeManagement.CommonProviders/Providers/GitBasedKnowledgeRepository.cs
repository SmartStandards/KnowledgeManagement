using LibGit2Sharp;
using Logging.SmartStandards;
using Logging.SmartStandards.CopyForKnowledgeManagement;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KnowledgeManagement.SmartStandards.Providers {

  /// <summary>
  /// Implements <see cref="IKnowledgeRepository"/> for a remote Git repository by
  /// inheriting the complete file-system and Markdown behavior from
  /// <see cref="FileBasedKnowledgeRepository"/>.
  /// 
  /// The provider creates an isolated GUID-based working session below the system
  /// temporary directory and clones the remote repository into that session. The
  /// logical knowledge root is configurable through <c>knowledgeRoot</c> and defaults
  /// to the cloned repository root.
  /// 
  /// No global Git configuration, credential helper, user profile file or operating
  /// system Git configuration is modified.
  /// 
  /// HTTPS credentials are supplied directly to LibGit2Sharp callbacks. An optional
  /// personal access token can therefore be used with GitHub and Azure DevOps without
  /// persisting the token in the remote URL or `.git/config`.
  /// 
  /// Before every mutation the provider fetches the current remote branch and hard-resets
  /// the local working session to the current remote tip. The logical knowledge operation
  /// is then applied, committed as exactly one Git commit, and pushed immediately.
  /// 
  /// If a concurrent remote update causes a non-fast-forward push rejection, the provider
  /// discards its local attempt, synchronizes again, replays the original logical
  /// knowledge operation against the new remote state, creates a new commit and retries.
  /// It never performs an automatic textual Git merge and never force-pushes.
  /// 
  /// The inherited knowledge semantics remain provider-neutral. In particular, logical
  /// moves are expressed only through <see cref="IKnowledgeRepository.TryMoveContent(string, string, out KnowledgeResourceIdChange[])"/>;
  /// this provider merely persists the resulting filesystem/Markdown changes as Git
  /// changes and MUST NOT depend on any protocol or exposure mechanism used by consumers.
  /// 
  /// The temporary session is removed during <see cref="Dispose"/>. The constructor also
  /// performs best-effort cleanup of stale provider session directories left by abnormal
  /// process termination.
  /// </summary>
  public class GitBasedKnowledgeRepository : FileBasedKnowledgeRepository, IDisposable {

    private const string _TemporaryRootDirectoryName = ".knowledge-repository-git";
    private const string _DefaultCommitAuthorName = "Knowledge Repository";
    private const string _DefaultCommitAuthorEmail = "knowledge-repository@localhost";
    private const int _MaximumPushAttempts = 3;
    private static readonly TimeSpan _ReadFreshnessWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _StaleSessionAge = TimeSpan.FromDays(1);

    private readonly string _RepositoryUrl;
    private readonly string _AccessToken;
    private readonly string _SessionDirectory;
    private readonly string _RepositoryDirectory;
    private readonly string _KnowledgeRoot;
    private readonly Repository _Repository;
    private readonly string _BranchName;
    private readonly FileStream _SessionLock;
    private DateTime _LastRemoteRefreshUtc;
    private bool _Disposed;

    /// <summary>
    /// Creates a Git-backed knowledge repository for a public remote repository that
    /// does not require explicit credentials.
    ///
    /// The complete cloned Git repository is exposed as the logical knowledge root.
    /// </summary>
    /// <param name="repositoryUrl">The HTTPS Git repository URL.</param>
    /// <param name="readOnly">Whether the knowledge repository is read-only.</param>
    public GitBasedKnowledgeRepository(
      string repositoryUrl,
      bool readOnly
    ) : this(
      repositoryUrl,
      readOnly,
      string.Empty,
      "/"
    ) {
    }

    /// <summary>
    /// Creates a Git-backed knowledge repository for the specified remote repository.
    ///
    /// The complete cloned Git repository is exposed as the logical knowledge root.
    /// The access token is kept only in process memory and supplied through LibGit2Sharp
    /// credential callbacks. It is not appended to the repository URL and is not written
    /// to Git configuration files.
    /// </summary>
    /// <param name="repositoryUrl">The HTTPS Git repository URL.</param>
    /// <param name="readOnly">Whether all knowledge mutations are disabled.</param>
    /// <param name="accessToken">
    /// Optional GitHub or Azure DevOps personal access token. Pass an empty string for a
    /// public repository that requires no credentials.
    /// </param>
    public GitBasedKnowledgeRepository(
      string repositoryUrl,
      bool readOnly,
      string accessToken
    ) : this(
      repositoryUrl,
      readOnly,
      accessToken,
      "/"
    ) {
    }

    /// <summary>
    /// Creates a Git-backed knowledge repository for the specified remote repository and
    /// exposes the configured repository-relative subdirectory as the logical knowledge root.
    ///
    /// Examples:
    ///
    /// <c>/</c> exposes the complete cloned repository.
    /// <c>/docs/</c> exposes only the repository's <c>docs</c> directory.
    /// <c>docs</c> is normalized identically to <c>/docs/</c>.
    ///
    /// The configured knowledge root is strictly repository-relative. Parent traversal and
    /// paths that would escape the cloned repository are rejected.
    ///
    /// The access token is kept only in process memory and supplied through LibGit2Sharp
    /// credential callbacks. It is not appended to the repository URL and is not written
    /// to Git configuration files.
    ///
    /// The provider clones the repository into:
    ///
    /// <c>Path.GetTempPath()/.knowledge-repository-git/&lt;GUID&gt;/repository</c>
    ///
    /// and then initializes the inherited FileBased repository at the resolved knowledge
    /// root below that clone.
    /// </summary>
    /// <param name="repositoryUrl">The HTTPS Git repository URL.</param>
    /// <param name="readOnly">Whether all knowledge mutations are disabled.</param>
    /// <param name="accessToken">
    /// Optional GitHub or Azure DevOps personal access token. Pass an empty string for a
    /// public repository that requires no credentials.
    /// </param>
    /// <param name="knowledgeRoot">
    /// Repository-relative knowledge root. Use <c>/</c> for the complete repository or a
    /// path such as <c>/docs/</c> to expose a deeper subtree.
    /// </param>
    public GitBasedKnowledgeRepository(
      string repositoryUrl,
      bool readOnly,
      string accessToken,
      string knowledgeRoot = "/"
    ) : base(readOnly) {
      if (string.IsNullOrWhiteSpace(repositoryUrl)) {
        throw new ArgumentException(
          "A Git repository URL is required.",
          nameof(repositoryUrl)
        );
      }

      _RepositoryUrl = repositoryUrl.Trim();
      _AccessToken = accessToken;

      if (_AccessToken == null) {
        _AccessToken = string.Empty;
      }

      _KnowledgeRoot = this.NormalizeKnowledgeRoot(
        knowledgeRoot
      );

      _Disposed = false;
      _LastRemoteRefreshUtc = DateTime.MinValue;

      this.CleanupStaleSessions();

      string temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        _TemporaryRootDirectoryName
      );

      Directory.CreateDirectory(
        temporaryRoot
      );

      this.TryMarkDirectoryHidden(
        temporaryRoot
      );

      _SessionDirectory = Path.Combine(
        temporaryRoot,
        Guid.NewGuid().ToString("N")
      );

      _RepositoryDirectory = Path.Combine(
        _SessionDirectory,
        "repository"
      );

      Directory.CreateDirectory(
        _SessionDirectory
      );

      this.TryMarkDirectoryHidden(
        _SessionDirectory
      );

      string sessionLockPath = Path.Combine(
        _SessionDirectory,
        ".active.lock"
      );

      _SessionLock = new FileStream(
        sessionLockPath,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None
      );

      try {
        this.CloneRepository();

        _Repository = new Repository(
          _RepositoryDirectory
        );

        _BranchName = _Repository.Head.FriendlyName;

        string knowledgeDirectory = this.ResolveKnowledgeDirectory();

        if (!Directory.Exists(knowledgeDirectory)) {
          Directory.CreateDirectory(
            knowledgeDirectory
          );
        }

        this.InitializeRootDirectory(
          knowledgeDirectory
        );

        _LastRemoteRefreshUtc = DateTime.UtcNow;
      }
      catch {
        _SessionLock.Dispose();
        this.TryDeleteSessionDirectory();
        throw;
      }
    }

    /// <summary>
    /// Normalizes and validates one repository-relative knowledge root.
    /// </summary>
    private string NormalizeKnowledgeRoot(string knowledgeRoot) {
      string value = knowledgeRoot;

      if (string.IsNullOrWhiteSpace(value)) {
        value = "/";
      }

      value = value
        .Trim()
        .Replace(
          '\\',
          '/'
        );

      if (!value.StartsWith("/", StringComparison.Ordinal)) {
        value = "/" + value;
      }

      string[] rawSegments = value.Split(
        '/',
        StringSplitOptions.RemoveEmptyEntries
      );

      List<string> normalizedSegments =
        new List<string>();

      foreach (string rawSegment in rawSegments) {
        string segment = rawSegment.Trim();

        if (segment.Length == 0 ||
            string.Equals(
              segment,
              ".",
              StringComparison.Ordinal
            )) {
          continue;
        }

        if (string.Equals(
              segment,
              "..",
              StringComparison.Ordinal
            )) {
          throw new ArgumentException(
            "The Git knowledge root must not contain parent traversal segments.",
            nameof(knowledgeRoot)
          );
        }

        if (segment.IndexOfAny(
              Path.GetInvalidFileNameChars()
            ) >= 0) {
          throw new ArgumentException(
            "The Git knowledge root contains an invalid path segment.",
            nameof(knowledgeRoot)
          );
        }

        normalizedSegments.Add(
          segment
        );
      }

      if (normalizedSegments.Count == 0) {
        return "/";
      }

      return "/"
        + string.Join(
          "/",
          normalizedSegments
        );
    }

    /// <summary>
    /// Resolves the configured repository-relative knowledge root to its physical path below
    /// the active cloned repository and verifies that the result cannot escape the clone.
    /// </summary>
    private string ResolveKnowledgeDirectory() {
      string repositoryRoot = Path.GetFullPath(
        _RepositoryDirectory
      ).TrimEnd(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar
      );

      if (string.Equals(
            _KnowledgeRoot,
            "/",
            StringComparison.Ordinal
          )) {
        return repositoryRoot;
      }

      string relativePath = _KnowledgeRoot
        .TrimStart('/')
        .Replace(
          '/',
          Path.DirectorySeparatorChar
        );

      string candidate = Path.GetFullPath(
        Path.Combine(
          repositoryRoot,
          relativePath
        )
      );

      string repositoryPrefix =
        repositoryRoot
        + Path.DirectorySeparatorChar;

      if (!candidate.StartsWith(
            repositoryPrefix,
            StringComparison.OrdinalIgnoreCase
          )) {
        throw new InvalidOperationException(
          "The configured Git knowledge root resolves outside the cloned repository."
        );
      }

      return candidate;
    }

    /// <summary>
    /// Gets the remote repository URL.
    /// </summary>
    public string RepositoryUrl {
      get {
        return _RepositoryUrl;
      }
    }

    /// <summary>
    /// Gets the normalized repository-relative knowledge root.
    ///
    /// The root is always represented with a leading slash and without a trailing slash,
    /// except for the repository root itself which is represented as <c>/</c>.
    /// </summary>
    public string KnowledgeRoot {
      get {
        return _KnowledgeRoot;
      }
    }

    /// <summary>
    /// Gets the active remote branch name discovered from the cloned repository.
    /// </summary>
    public string BranchName {
      get {
        this.ThrowIfDisposed();
        return _BranchName;
      }
    }

    /// <summary>
    /// Gets the private temporary working-session directory.
    /// 
    /// This property is protected so specialized providers can inspect the session while
    /// the public API does not expose it.
    /// </summary>
    protected string SessionDirectory {
      get {
        return _SessionDirectory;
      }
    }

    /// <summary>
    /// Refreshes the local clone before reads when the short freshness window has expired.
    /// 
    /// Reads therefore remain fast during a burst of repository operations while still
    /// observing remote changes within a short bounded interval.
    /// </summary>
    protected override void PrepareForRead() {
      this.ThrowIfDisposed();

      DateTime now = DateTime.UtcNow;

      if (now - _LastRemoteRefreshUtc <= _ReadFreshnessWindow) {
        return;
      }

      try {
        this.SynchronizeToRemoteTip();
        _LastRemoteRefreshUtc = DateTime.UtcNow;
      }
      catch (LibGit2SharpException ex) {
        DevLogger.LogError(ex);
        throw;
      }
    }

    /// <summary>
    /// Executes one logical knowledge mutation as one Git commit.
    /// 
    /// Every attempt starts from the latest fetched remote tip. The file-based knowledge
    /// mutation is replayed against that exact state, all changes below `/doc` are staged,
    /// one commit is created, and the branch is pushed immediately.
    /// 
    /// A non-fast-forward race never causes an automatic Git merge. Instead the local
    /// attempt is discarded and the original logical operation is replayed against the
    /// newly fetched remote tip.
    /// </summary>
    protected override bool ExecuteMutation(
      string operationDescription,
      Func<MutationContext, bool> mutation
    ) {
      if (this.IsReadOnly) {
        return false;
      }

      lock (_SyncRoot) {
        this.ThrowIfDisposed();

        for (int attempt = 1; attempt <= _MaximumPushAttempts; attempt++) {
          try {
            this.SynchronizeToRemoteTip();

            bool applied = this.ApplyMutation(mutation);

            if (!applied) {
              this.SynchronizeToRemoteTip();
              return false;
            }

            if (!this.HasKnowledgeChanges()) {
              return false;
            }

            this.StageKnowledgeDirectory();

            Signature signature = this.CreateCommitSignature();

            _Repository.Commit(
              operationDescription + " [skip ci]",
              signature,
              signature
            );

            bool pushed = this.TryPushCurrentBranch();

            if (pushed) {
              _LastRemoteRefreshUtc = DateTime.UtcNow;
              return true;
            }

            if (attempt < _MaximumPushAttempts) {
              continue;
            }

            return false;
          }
          catch (NonFastForwardException ex) {
            DevLogger.LogError(ex);

            if (attempt >= _MaximumPushAttempts) {
              this.TrySynchronizeAfterFailure();
              return false;
            }
          }
          catch (LibGit2SharpException ex) {
            DevLogger.LogError(ex);
            this.TrySynchronizeAfterFailure();
            return false;
          }
          catch (IOException ex) {
            DevLogger.LogError(ex);
            this.TrySynchronizeAfterFailure();
            return false;
          }
          catch (UnauthorizedAccessException ex) {
            DevLogger.LogError(ex);
            this.TrySynchronizeAfterFailure();
            return false;
          }
        }

        return false;
      }
    }

    /// <summary>
    /// Disposes the Git repository handle and removes the complete GUID-based temporary
    /// session directory.
    /// </summary>
    public void Dispose() {
      if (_Disposed) {
        return;
      }

      _Disposed = true;

      _Repository.Dispose();
      _SessionLock.Dispose();
      this.TryDeleteSessionDirectory();

      GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Creates credentials for GitHub, Azure DevOps or another HTTPS Git server.
    /// 
    /// Derived classes may override this method to provide short-lived credentials,
    /// Azure Entra tokens or another authentication strategy without changing the Git
    /// synchronization logic.
    /// </summary>
    protected virtual Credentials CreateCredentials() {
      if (string.IsNullOrEmpty(_AccessToken)) {
        return new DefaultCredentials();
      }

      UsernamePasswordCredentials credentials = new UsernamePasswordCredentials();
      credentials.Username = "git";
      credentials.Password = _AccessToken;
      return credentials;
    }

    /// <summary>
    /// Creates the author and committer identity for automatically generated knowledge
    /// commits.
    /// 
    /// Derived classes may override this method when another explicit commit identity is
    /// required.
    /// </summary>
    protected virtual Signature CreateCommitSignature() {
      return new Signature(
        _DefaultCommitAuthorName,
        _DefaultCommitAuthorEmail,
        DateTimeOffset.Now
      );
    }

    private void CloneRepository() {
      FetchOptions fetchOptions = new FetchOptions();

      if (!string.IsNullOrEmpty(_AccessToken)) {
        fetchOptions.CredentialsProvider =
          (string url, string usernameFromUrl, SupportedCredentialTypes types) =>
            this.CreateCredentials();
      }

      CloneOptions cloneOptions = new CloneOptions(fetchOptions);

      Repository.Clone(
        _RepositoryUrl,
        _RepositoryDirectory,
        cloneOptions
      );
    }

    private void SynchronizeToRemoteTip() {
      Remote origin = _Repository.Network.Remotes["origin"];

      if (origin == null) {
        throw new InvalidOperationException("The cloned repository does not define the 'origin' remote.");
      }

      FetchOptions fetchOptions = new FetchOptions();

      if (!string.IsNullOrEmpty(_AccessToken)) {
        fetchOptions.CredentialsProvider =
          (string url, string usernameFromUrl, SupportedCredentialTypes types) =>
            this.CreateCredentials();
      }

      IEnumerable<string> refSpecs = origin.FetchRefSpecs
        .Select((RefSpec refSpec) => refSpec.Specification)
        .ToArray();

      Commands.Fetch(
        _Repository,
        origin.Name,
        refSpecs,
        fetchOptions,
        "Refresh knowledge repository"
      );

      Branch remoteBranch = _Repository.Branches[
        origin.Name + "/" + _BranchName
      ];

      if (remoteBranch == null || remoteBranch.Tip == null) {
        throw new InvalidOperationException(
          "The remote branch '" + origin.Name + "/" + _BranchName + "' could not be resolved."
        );
      }

      _Repository.Reset(
        ResetMode.Hard,
        remoteBranch.Tip
      );

      this.RemoveUntrackedKnowledgeFiles();
      Directory.CreateDirectory(this.GetPhysicalRootDirectory());
    }

    private bool TryPushCurrentBranch() {
      PushOptions pushOptions = new PushOptions();

      if (!string.IsNullOrEmpty(_AccessToken)) {
        pushOptions.CredentialsProvider =
          (string url, string usernameFromUrl, SupportedCredentialTypes types) =>
            this.CreateCredentials();
      }

      Branch localBranch = _Repository.Branches[_BranchName];

      if (localBranch == null) {
        throw new InvalidOperationException(
          "The local branch '" + _BranchName + "' could not be resolved."
        );
      }

      _Repository.Network.Push(
        localBranch,
        pushOptions
      );

      return true;
    }

    /// <summary>
    /// Returns whether the current Git working tree contains changes inside the configured
    /// knowledge root.
    /// </summary>
    private bool HasKnowledgeChanges() {
      RepositoryStatus status = _Repository.RetrieveStatus(
        new StatusOptions()
      );

      foreach (StatusEntry entry in status) {
        if (this.IsRepositoryPathInsideKnowledgeRoot(
              entry.FilePath
            )) {
          return true;
        }
      }

      return false;
    }

    /// <summary>
    /// Stages all Git changes below the configured knowledge root.
    /// </summary>
    private void StageKnowledgeDirectory() {
      string repositoryRelativeKnowledgeRoot =
        this.GetRepositoryRelativeKnowledgeRoot();

      if (string.IsNullOrEmpty(repositoryRelativeKnowledgeRoot)) {
        Commands.Stage(
          _Repository,
          "*"
        );
        return;
      }

      Commands.Stage(
        _Repository,
        repositoryRelativeKnowledgeRoot
      );
    }

    /// <summary>
    /// Removes untracked files below the configured knowledge root before replaying a
    /// mutation against a freshly synchronized remote state.
    /// </summary>
    private void RemoveUntrackedKnowledgeFiles() {
      RepositoryStatus status = _Repository.RetrieveStatus(
        new StatusOptions()
      );

      foreach (StatusEntry entry in status) {
        if ((entry.State & FileStatus.NewInWorkdir) == 0) {
          continue;
        }

        if (!this.IsRepositoryPathInsideKnowledgeRoot(
              entry.FilePath
            )) {
          continue;
        }

        string physicalPath = Path.Combine(
          _RepositoryDirectory,
          entry.FilePath.Replace(
            '/',
            Path.DirectorySeparatorChar
          )
        );

        if (File.Exists(physicalPath)) {
          File.Delete(
            physicalPath
          );
        }
      }

      this.DeleteEmptyDirectories(
        this.ResolveKnowledgeDirectory()
      );
    }

    /// <summary>
    /// Returns the configured knowledge root as a Git repository-relative path.
    ///
    /// An empty string represents the complete repository root.
    /// </summary>
    private string GetRepositoryRelativeKnowledgeRoot() {
      if (string.Equals(
            _KnowledgeRoot,
            "/",
            StringComparison.Ordinal
          )) {
        return string.Empty;
      }

      return _KnowledgeRoot.TrimStart('/');
    }

    /// <summary>
    /// Returns whether one Git repository-relative path belongs to the configured knowledge
    /// root.
    /// </summary>
    private bool IsRepositoryPathInsideKnowledgeRoot(string repositoryPath) {
      string normalizedPath = repositoryPath.Replace(
        '\\',
        '/'
      );

      string repositoryRelativeKnowledgeRoot =
        this.GetRepositoryRelativeKnowledgeRoot();

      if (string.IsNullOrEmpty(repositoryRelativeKnowledgeRoot)) {
        return true;
      }

      if (string.Equals(
            normalizedPath,
            repositoryRelativeKnowledgeRoot,
            StringComparison.OrdinalIgnoreCase
          )) {
        return true;
      }

      return normalizedPath.StartsWith(
        repositoryRelativeKnowledgeRoot + "/",
        StringComparison.OrdinalIgnoreCase
      );
    }

    private void DeleteEmptyDirectories(string rootDirectory) {
      if (!Directory.Exists(rootDirectory)) {
        return;
      }

      string[] directories = Directory.GetDirectories(
        rootDirectory,
        "*",
        SearchOption.AllDirectories
      );

      Array.Sort(
        directories,
        (string left, string right) => right.Length.CompareTo(left.Length)
      );

      foreach (string directory in directories) {
        if (Directory.GetFileSystemEntries(directory).Length == 0) {
          Directory.Delete(directory);
        }
      }
    }

    private void TrySynchronizeAfterFailure() {
      try {
        this.SynchronizeToRemoteTip();
      }
      catch (LibGit2SharpException ex) {
        DevLogger.LogError(ex);
      }
      catch (IOException ex) {
        DevLogger.LogError(ex);
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(ex);
      }
    }

    private void CleanupStaleSessions() {
      string temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        _TemporaryRootDirectoryName
      );

      if (!Directory.Exists(temporaryRoot)) {
        return;
      }

      string[] sessionDirectories = Directory.GetDirectories(temporaryRoot);

      foreach (string sessionDirectory in sessionDirectories) {
        DirectoryInfo info = new DirectoryInfo(sessionDirectory);

        if (DateTime.UtcNow - info.LastWriteTimeUtc <= _StaleSessionAge) {
          continue;
        }

        string sessionLockPath = Path.Combine(sessionDirectory, ".active.lock");
        FileStream cleanupLock = null;

        try {
          cleanupLock = new FileStream(
            sessionLockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
          );

          cleanupLock.Dispose();
          cleanupLock = null;

          Directory.Delete(sessionDirectory, true);
        }
        catch (IOException ex) {
          if (cleanupLock != null) {
            cleanupLock.Dispose();
          }

          DevLogger.LogError(ex);
        }
        catch (UnauthorizedAccessException ex) {
          if (cleanupLock != null) {
            cleanupLock.Dispose();
          }

          DevLogger.LogError(ex);
        }
      }
    }

    private void TryDeleteSessionDirectory() {
      if (string.IsNullOrEmpty(_SessionDirectory)) {
        return;
      }

      if (!Directory.Exists(_SessionDirectory)) {
        return;
      }

      try {
        Directory.Delete(_SessionDirectory, true);
      }
      catch (IOException ex) {
        DevLogger.LogError(ex);
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(ex);
      }
    }

    private void TryMarkDirectoryHidden(string directory) {
      try {
        FileAttributes attributes = File.GetAttributes(directory);

        if ((attributes & FileAttributes.Hidden) == 0) {
          File.SetAttributes(
            directory,
            attributes | FileAttributes.Hidden
          );
        }
      }
      catch (IOException ex) {
        DevLogger.LogError(ex);
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(ex);
      }
      catch (PlatformNotSupportedException ex) {
        DevLogger.LogError(ex);
      }
    }

    private void ThrowIfDisposed() {
      if (_Disposed) {
        throw new ObjectDisposedException(nameof(GitBasedKnowledgeRepository));
      }
    }
  }
}
