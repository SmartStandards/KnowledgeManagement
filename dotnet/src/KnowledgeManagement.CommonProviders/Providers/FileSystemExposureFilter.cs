using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Logging.SmartStandards.CopyForKnowledgeManagement;

namespace KnowledgeManagement.SmartStandards.Providers {

  /// <summary>
  /// Evaluates whether physical files and directories below one file-based knowledge root
  /// may participate in the externally visible repository model.
  ///
  /// The filter combines three sources in deterministic order:
  ///
  /// 1. Hierarchical .gitignore files, from the repository root down to the candidate's
  ///    parent directory.
  /// 2. One global static blacklist.
  /// 3. One global static whitelist.
  ///
  /// Static rules intentionally run after .gitignore rules. This allows the provider
  /// configuration to enforce repository-wide exclusions and explicit repository-wide
  /// exceptions without modifying files on disk.
  ///
  /// Every .gitignore file is scoped to the directory that contains it. A rule discovered
  /// below one branch can therefore never affect a sibling branch or an ancestor.
  /// </summary>
  public sealed class FileSystemExposureFilter {

    private const string _GitIgnoreFileName = ".gitignore";
    private static readonly TimeSpan _IgnoreFileMetadataValidationInterval =
      TimeSpan.FromMilliseconds(
        250
      );

    private readonly string _RootDirectory;
    private readonly string[] _Blacklist;
    private readonly string[] _Whitelist;
    private readonly bool _UseGitIgnoreFiles;
    private readonly object _RuleCacheSyncRoot;
    private readonly Dictionary<string, IgnoreFileCacheEntry> _RuleCache;
    private readonly ExposureRule[] _StaticBlacklistRules;
    private readonly ExposureRule[] _StaticWhitelistRules;
    private readonly StringComparison _PathComparison;

    /// <summary>
    /// Creates one immutable exposure filter.
    /// </summary>
    /// <param name="rootDirectory">Physical repository root.</param>
    /// <param name="blacklist">Global exclusion patterns.</param>
    /// <param name="whitelist">Global inclusion patterns.</param>
    /// <param name="useGitIgnoreFiles">Whether hierarchical .gitignore files are evaluated.</param>
    public FileSystemExposureFilter(
      string rootDirectory,
      string[] blacklist,
      string[] whitelist,
      bool useGitIgnoreFiles
    ) {
      if (string.IsNullOrWhiteSpace(
            rootDirectory
          )) {
        throw new ArgumentException(
          "An exposure-filter root directory is required.",
          nameof(rootDirectory)
        );
      }

      string fullRootDirectory =
        Path.GetFullPath(
          rootDirectory
        );

      string fileSystemRoot =
        Path.GetPathRoot(
          fullRootDirectory
        );

      if (!string.IsNullOrEmpty(
            fileSystemRoot
          ) &&
          string.Equals(
            fullRootDirectory,
            fileSystemRoot,
            StringComparison.OrdinalIgnoreCase
          )) {
        _RootDirectory =
          fullRootDirectory;
      }
      else {
        _RootDirectory =
          fullRootDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
          );
      }

      _Blacklist =
        this.NormalizePatterns(
          blacklist
        );

      _Whitelist =
        this.NormalizePatterns(
          whitelist
        );

      _UseGitIgnoreFiles =
        useGitIgnoreFiles;

      _RuleCacheSyncRoot =
        new object();

      _RuleCache =
        new Dictionary<string, IgnoreFileCacheEntry>(
          StringComparer.OrdinalIgnoreCase
        );

      _StaticBlacklistRules =
        this.CreateStaticRules(
          _Blacklist,
          false
        );

      _StaticWhitelistRules =
        this.CreateStaticRules(
          _Whitelist,
          true
        );

      if (OperatingSystem.IsWindows()) {
        _PathComparison =
          StringComparison.OrdinalIgnoreCase;
      }
      else {
        _PathComparison =
          StringComparison.Ordinal;
      }
    }

    /// <summary>
    /// Gets the normalized global blacklist used by this immutable filter instance.
    /// </summary>
    public string[] Blacklist {
      get {
        return _Blacklist.ToArray();
      }
    }

    /// <summary>
    /// Gets the normalized global whitelist used by this immutable filter instance.
    /// </summary>
    public string[] Whitelist {
      get {
        return _Whitelist.ToArray();
      }
    }

    /// <summary>
    /// Gets whether hierarchical .gitignore files are evaluated.
    /// </summary>
    public bool UseGitIgnoreFiles {
      get {
        return _UseGitIgnoreFiles;
      }
    }

    /// <summary>
    /// Returns whether one existing or prospective physical path is visible through the
    /// repository.
    ///
    /// The decision is evaluated one path segment at a time. If a directory is excluded,
    /// descendants are excluded without reading ignore files from inside that directory.
    /// This matches the important Git rule that a file cannot be re-included while one of
    /// its parent directories is still excluded.
    /// </summary>
    public bool IsVisible(
      string physicalPath,
      bool isDirectory
    ) {
      string fullPath =
        Path.GetFullPath(
          physicalPath
        );

      string relativePath;

      if (string.Equals(
            fullPath.TrimEnd(
              Path.DirectorySeparatorChar,
              Path.AltDirectorySeparatorChar
            ),
            _RootDirectory,
            _PathComparison
          )) {
        return true;
      }

      if (!this.TryGetRepositoryRelativePath(
            fullPath,
            out relativePath
          )) {
        return false;
      }

      string[] segments =
        relativePath.Split(
          '/',
          StringSplitOptions.RemoveEmptyEntries
        );

      if (segments.Length == 0) {
        return true;
      }

      // Build the .gitignore rule chain only once while descending through the candidate
      // path. The previous implementation rebuilt the complete ancestor chain for every
      // individual path prefix which caused O(depth²) filesystem probes per visibility
      // check and became especially expensive on mounted/network filesystems.
      List<ExposureRule> applicableGitIgnoreRules =
        new List<ExposureRule>();

      if (_UseGitIgnoreFiles) {
        applicableGitIgnoreRules.AddRange(
          this.GetRulesFromIgnoreFile(
            string.Empty
          )
        );
      }

      StringBuilder prefixBuilder =
        new StringBuilder();

      StringBuilder parentDirectoryBuilder =
        new StringBuilder();

      for (int index = 0;
           index < segments.Length;
           index++) {
        if (index > 0 &&
            _UseGitIgnoreFiles) {
          if (parentDirectoryBuilder.Length > 0) {
            parentDirectoryBuilder.Append('/');
          }

          parentDirectoryBuilder.Append(
            segments[index - 1]
          );

          applicableGitIgnoreRules.AddRange(
            this.GetRulesFromIgnoreFile(
              parentDirectoryBuilder.ToString()
            )
          );
        }

        if (prefixBuilder.Length > 0) {
          prefixBuilder.Append('/');
        }

        prefixBuilder.Append(
          segments[index]
        );

        bool currentIsDirectory =
          index < segments.Length - 1 ||
          isDirectory;

        bool ignored =
          this.IsExactPathIgnored(
            prefixBuilder.ToString(),
            currentIsDirectory,
            applicableGitIgnoreRules
          );

        if (ignored) {
          return false;
        }
      }

      return true;
    }

    /// <summary>
    /// Evaluates the complete effective rule chain for exactly one repository-relative path.
    /// </summary>
    private bool IsExactPathIgnored(
      string repositoryRelativePath,
      bool isDirectory,
      List<ExposureRule> applicableGitIgnoreRules
    ) {
      bool ignored =
        false;

      if (_UseGitIgnoreFiles) {
        foreach (ExposureRule rule in applicableGitIgnoreRules) {
          if (!rule.Matches(
                repositoryRelativePath,
                isDirectory
              )) {
            continue;
          }

          ignored =
            !rule.IsInclude;
        }
      }

      foreach (ExposureRule rule in _StaticBlacklistRules) {
        if (rule.Matches(
              repositoryRelativePath,
              isDirectory
            )) {
          ignored =
            true;
        }
      }

      foreach (ExposureRule rule in _StaticWhitelistRules) {
        if (rule.Matches(
              repositoryRelativePath,
              isDirectory
            )) {
          ignored =
            false;
        }
      }

      return ignored;
    }

    /// <summary>
    /// Reads and caches one .gitignore file scoped to a repository-relative directory.
    /// Cache entries are refreshed automatically when timestamp or size changes.
    /// </summary>
    private ExposureRule[] GetRulesFromIgnoreFile(
      string baseRelativeDirectory
    ) {
      string physicalDirectory =
        this.GetPhysicalDirectory(
          baseRelativeDirectory
        );

      string ignoreFile =
        Path.Combine(
          physicalDirectory,
          _GitIgnoreFileName
        );

      // Repeated sibling checks during one repository navigation operation all probe the
      // same ancestor .gitignore files. On mounted/network filesystems even File.Exists can
      // be comparatively expensive. Cache both existing and missing ignore files for a very
      // short validation interval so one navigation pass normally performs only one physical
      // metadata probe per directory scope.
      lock (_RuleCacheSyncRoot) {
        IgnoreFileCacheEntry recentEntry;

        if (_RuleCache.TryGetValue(
              ignoreFile,
              out recentEntry
            ) &&
            DateTime.UtcNow - recentEntry.ValidatedUtc <
              _IgnoreFileMetadataValidationInterval) {
          return recentEntry.Rules;
        }
      }

      // A missing .gitignore is the overwhelmingly common case and must not use
      // exception-based probing. The negative result is cached briefly as well.
      if (!File.Exists(
            ignoreFile
          )) {
        IgnoreFileCacheEntry missingEntry =
          new IgnoreFileCacheEntry(
            DateTime.MinValue,
            -1,
            Array.Empty<ExposureRule>(),
            DateTime.UtcNow
          );

        lock (_RuleCacheSyncRoot) {
          _RuleCache[ignoreFile] =
            missingEntry;
        }

        return missingEntry.Rules;
      }

      FileInfo info =
        new FileInfo(
          ignoreFile
        );

      try {
        // Refresh immediately before reading metadata because the containing mount may have
        // been recycled since File.Exists was evaluated.
        info.Refresh();

        if (!info.Exists) {
          lock (_RuleCacheSyncRoot) {
            _RuleCache.Remove(
              ignoreFile
            );
          }

          return Array.Empty<ExposureRule>();
        }

        lock (_RuleCacheSyncRoot) {
          IgnoreFileCacheEntry existing;

          if (_RuleCache.TryGetValue(
                ignoreFile,
                out existing
              ) &&
              existing.LastWriteTimeUtc == info.LastWriteTimeUtc &&
              existing.Length == info.Length) {
            existing.RefreshValidationTime(
              DateTime.UtcNow
            );

            return existing.Rules;
          }
        }
      }
      catch (FileNotFoundException) {
        IgnoreFileCacheEntry missingEntry =
          new IgnoreFileCacheEntry(
            DateTime.MinValue,
            -1,
            Array.Empty<ExposureRule>(),
            DateTime.UtcNow
          );

        lock (_RuleCacheSyncRoot) {
          _RuleCache[ignoreFile] =
            missingEntry;
        }

        return missingEntry.Rules;
      }
      catch (DirectoryNotFoundException) {
        IgnoreFileCacheEntry missingEntry =
          new IgnoreFileCacheEntry(
            DateTime.MinValue,
            -1,
            Array.Empty<ExposureRule>(),
            DateTime.UtcNow
          );

        lock (_RuleCacheSyncRoot) {
          _RuleCache[ignoreFile] =
            missingEntry;
        }

        return missingEntry.Rules;
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(
          ex
        );

        throw new InvalidOperationException(
          "A .gitignore file could not be inspected safely. Exposure is denied for this branch.",
          ex
        );
      }
      catch (IOException ex) {
        DevLogger.LogError(
          ex
        );

        throw new InvalidOperationException(
          "A .gitignore file could not be inspected safely. Exposure is denied for this branch.",
          ex
        );
      }

      DateTime lastWriteTimeUtc =
        info.LastWriteTimeUtc;

      long ignoreFileLength =
        info.Length;

      ExposureRule[] rules;

      try {
        string[] lines =
          File.ReadAllLines(
            ignoreFile
          );

        List<ExposureRule> parsedRules =
          new List<ExposureRule>();

        foreach (string line in lines) {
          ExposureRule rule;

          if (!this.TryParseGitIgnoreRule(
                line,
                baseRelativeDirectory,
                out rule
              )) {
            continue;
          }

          parsedRules.Add(
            rule
          );
        }

        rules =
          parsedRules.ToArray();
      }
      catch (FileNotFoundException) {
        // The file may legitimately disappear when a mount is recycled or when .gitignore
        // is removed between metadata inspection and the actual read. Treat that exactly as
        // an absent ignore file and discard rules cached for the former filesystem target.
        IgnoreFileCacheEntry missingEntry =
          new IgnoreFileCacheEntry(
            DateTime.MinValue,
            -1,
            Array.Empty<ExposureRule>(),
            DateTime.UtcNow
          );

        lock (_RuleCacheSyncRoot) {
          _RuleCache[ignoreFile] =
            missingEntry;
        }

        return missingEntry.Rules;
      }
      catch (DirectoryNotFoundException) {
        IgnoreFileCacheEntry missingEntry =
          new IgnoreFileCacheEntry(
            DateTime.MinValue,
            -1,
            Array.Empty<ExposureRule>(),
            DateTime.UtcNow
          );

        lock (_RuleCacheSyncRoot) {
          _RuleCache[ignoreFile] =
            missingEntry;
        }

        return missingEntry.Rules;
      }
      catch (IOException ex) {
        DevLogger.LogError(
          ex
        );

        lock (_RuleCacheSyncRoot) {
          IgnoreFileCacheEntry existing;

          if (_RuleCache.TryGetValue(
                ignoreFile,
                out existing
              )) {
            return existing.Rules;
          }
        }

        throw new InvalidOperationException(
          "A .gitignore file could not be read safely. Exposure is denied for this branch.",
          ex
        );
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(
          ex
        );

        lock (_RuleCacheSyncRoot) {
          IgnoreFileCacheEntry existing;

          if (_RuleCache.TryGetValue(
                ignoreFile,
                out existing
              )) {
            return existing.Rules;
          }
        }

        throw new InvalidOperationException(
          "A .gitignore file could not be read safely. Exposure is denied for this branch.",
          ex
        );
      }

      IgnoreFileCacheEntry cacheEntry =
        new IgnoreFileCacheEntry(
          lastWriteTimeUtc,
          ignoreFileLength,
          rules,
          DateTime.UtcNow
        );

      lock (_RuleCacheSyncRoot) {
        _RuleCache[ignoreFile] =
          cacheEntry;
      }

      return rules;
    }

    /// <summary>
    /// Parses one .gitignore line into one scoped exposure rule.
    /// </summary>
    private bool TryParseGitIgnoreRule(
      string sourceLine,
      string baseRelativeDirectory,
      out ExposureRule rule
    ) {
      rule =
        null;

      if (sourceLine == null) {
        return false;
      }

      string line =
        sourceLine.TrimEnd(
          '\r',
          '\n'
        );

      line =
        this.TrimUnescapedTrailingSpaces(
          line
        );

      if (line.Length == 0) {
        return false;
      }

      if (line[0] == '#') {
        return false;
      }

      bool isInclude =
        false;

      if (line[0] == '!') {
        isInclude =
          true;

        line =
          line.Substring(
            1
          );
      }
      else if (line.StartsWith(
                 "\\!",
                 StringComparison.Ordinal
               )) {
        line =
          line.Substring(
            1
          );
      }

      if (line.StartsWith(
            "\\#",
            StringComparison.Ordinal
          )) {
        line =
          line.Substring(
            1
          );
      }

      if (line.Length == 0) {
        return false;
      }

      line =
        line.Replace(
          "\\ ",
          " ",
          StringComparison.Ordinal
        ).Replace(
          "\\#",
          "#",
          StringComparison.Ordinal
        ).Replace(
          "\\!",
          "!",
          StringComparison.Ordinal
        );

      rule =
        new ExposureRule(
          baseRelativeDirectory,
          line,
          isInclude
        );

      return true;
    }

    /// <summary>
    /// Creates repository-root-scoped rules from static blacklist or whitelist patterns.
    /// </summary>
    private ExposureRule[] CreateStaticRules(
      string[] patterns,
      bool isInclude
    ) {
      List<ExposureRule> result =
        new List<ExposureRule>();

      foreach (string pattern in patterns) {
        string normalizedPattern =
          pattern;

        if (normalizedPattern.StartsWith(
              "!",
              StringComparison.Ordinal
            )) {
          normalizedPattern =
            normalizedPattern.Substring(
              1
            );
        }

        if (normalizedPattern.Length == 0) {
          continue;
        }

        result.Add(
          new ExposureRule(
            string.Empty,
            normalizedPattern,
            isInclude
          )
        );
      }

      return result.ToArray();
    }

    /// <summary>
    /// Returns one normalized copy of an external pattern collection.
    /// </summary>
    private string[] NormalizePatterns(
      string[] patterns
    ) {
      if (patterns == null ||
          patterns.Length == 0) {
        return Array.Empty<string>();
      }

      return patterns
        .Where(
          (string pattern) => !string.IsNullOrWhiteSpace(
            pattern
          )
        )
        .Select(
          (string pattern) => pattern.Trim()
        )
        .Distinct(
          StringComparer.Ordinal
        )
        .ToArray();
    }

    /// <summary>
    /// Converts one physical path below the configured root into a slash-separated repository
    /// relative path.
    /// </summary>
    private bool TryGetRepositoryRelativePath(
      string physicalPath,
      out string relativePath
    ) {
      relativePath =
        string.Empty;

      string fullPath =
        Path.GetFullPath(
          physicalPath
        );

      string rootPrefix =
        _RootDirectory;

      if (!rootPrefix.EndsWith(
            Path.DirectorySeparatorChar
          ) &&
          !rootPrefix.EndsWith(
            Path.AltDirectorySeparatorChar
          )) {
        rootPrefix +=
          Path.DirectorySeparatorChar;
      }

      if (!fullPath.StartsWith(
            rootPrefix,
            _PathComparison
          )) {
        return false;
      }

      relativePath =
        Path.GetRelativePath(
          _RootDirectory,
          fullPath
        ).Replace(
          Path.DirectorySeparatorChar,
          '/'
        ).Replace(
          Path.AltDirectorySeparatorChar,
          '/'
        );

      if (relativePath.StartsWith(
            "../",
            StringComparison.Ordinal
          ) ||
          string.Equals(
            relativePath,
            "..",
            StringComparison.Ordinal
          )) {
        relativePath =
          string.Empty;

        return false;
      }

      return true;
    }

    /// <summary>
    /// Resolves one repository-relative directory without allowing parent traversal.
    /// </summary>
    private string GetPhysicalDirectory(
      string repositoryRelativeDirectory
    ) {
      if (string.IsNullOrEmpty(
            repositoryRelativeDirectory
          )) {
        return _RootDirectory;
      }

      string candidate =
        Path.GetFullPath(
          Path.Combine(
            _RootDirectory,
            repositoryRelativeDirectory.Replace(
              '/',
              Path.DirectorySeparatorChar
            )
          )
        );

      string rootPrefix =
        _RootDirectory;

      if (!rootPrefix.EndsWith(
            Path.DirectorySeparatorChar
          ) &&
          !rootPrefix.EndsWith(
            Path.AltDirectorySeparatorChar
          )) {
        rootPrefix +=
          Path.DirectorySeparatorChar;
      }

      if (!candidate.StartsWith(
            rootPrefix,
            _PathComparison
          )) {
        throw new InvalidOperationException(
          "The .gitignore scope resolves outside the configured repository root."
        );
      }

      return candidate;
    }

    /// <summary>
    /// Removes unescaped trailing spaces according to .gitignore text rules.
    /// </summary>
    private string TrimUnescapedTrailingSpaces(
      string value
    ) {
      int end =
        value.Length;

      while (end > 0 &&
             value[end - 1] == ' ') {
        int backslashCount =
          0;

        int probe =
          end - 2;

        while (probe >= 0 &&
               value[probe] == '\\') {
          backslashCount++;
          probe--;
        }

        if (backslashCount % 2 == 1) {
          break;
        }

        end--;
      }

      if (end == value.Length) {
        return value;
      }

      return value.Substring(
        0,
        end
      );
    }

    /// <summary>
    /// Represents one cached .gitignore parse result.
    /// </summary>
    private sealed class IgnoreFileCacheEntry {

      private readonly DateTime _LastWriteTimeUtc;
      private readonly long _Length;
      private readonly ExposureRule[] _Rules;
      private DateTime _ValidatedUtc;

      /// <summary>
      /// Creates one cached ignore-file state.
      /// </summary>
      public IgnoreFileCacheEntry(
        DateTime lastWriteTimeUtc,
        long length,
        ExposureRule[] rules,
        DateTime validatedUtc
      ) {
        _LastWriteTimeUtc =
          lastWriteTimeUtc;

        _Length =
          length;

        _Rules =
          rules;

        _ValidatedUtc =
          validatedUtc;
      }

      /// <summary>
      /// Gets the source file modification time.
      /// </summary>
      public DateTime LastWriteTimeUtc {
        get {
          return _LastWriteTimeUtc;
        }
      }

      /// <summary>
      /// Gets the source file length.
      /// </summary>
      public long Length {
        get {
          return _Length;
        }
      }

      /// <summary>
      /// Gets the immutable parsed rule array.
      /// </summary>
      public ExposureRule[] Rules {
        get {
          return _Rules;
        }
      }

      /// <summary>
      /// Gets the last time the cached filesystem state was physically validated.
      /// </summary>
      public DateTime ValidatedUtc {
        get {
          return _ValidatedUtc;
        }
      }

      /// <summary>
      /// Refreshes the short-lived metadata validation timestamp without reparsing rules.
      /// </summary>
      public void RefreshValidationTime(
        DateTime validatedUtc
      ) {
        _ValidatedUtc =
          validatedUtc;
      }
    }

    /// <summary>
    /// Represents one normalized static or .gitignore rule.
    /// </summary>
    private sealed class ExposureRule {

      private readonly string _BaseRelativeDirectory;
      private readonly bool _IsInclude;
      private readonly bool _DirectoryOnly;
      private readonly bool _Anchored;
      private readonly bool _ContainsSlash;
      private readonly Regex _PatternRegex;

      /// <summary>
      /// Creates one exposure rule.
      /// </summary>
      public ExposureRule(
        string baseRelativeDirectory,
        string sourcePattern,
        bool isInclude
      ) {
        _BaseRelativeDirectory =
          baseRelativeDirectory.Trim(
            '/'
          );

        _IsInclude =
          isInclude;

        string pattern =
          sourcePattern.Replace(
            '\\',
            '/'
          );

        _DirectoryOnly =
          pattern.EndsWith(
            "/",
            StringComparison.Ordinal
          );

        if (_DirectoryOnly) {
          pattern =
            pattern.TrimEnd(
              '/'
            );
        }

        _Anchored =
          pattern.StartsWith(
            "/",
            StringComparison.Ordinal
          );

        if (_Anchored) {
          pattern =
            pattern.TrimStart(
              '/'
            );
        }

        _ContainsSlash =
          pattern.Contains(
            "/",
            StringComparison.Ordinal
          );

        RegexOptions regexOptions =
          RegexOptions.Compiled |
          RegexOptions.CultureInvariant;

        if (OperatingSystem.IsWindows()) {
          regexOptions |=
            RegexOptions.IgnoreCase;
        }

        _PatternRegex =
          new Regex(
            "^"
            + this.ConvertGlobToRegex(
              pattern
            )
            + "$",
            regexOptions
          );
      }

      /// <summary>
      /// Gets whether this rule explicitly re-includes a matching path.
      /// </summary>
      public bool IsInclude {
        get {
          return _IsInclude;
        }
      }

      /// <summary>
      /// Returns whether this rule matches exactly one repository-relative candidate.
      /// </summary>
      public bool Matches(
        string repositoryRelativePath,
        bool isDirectory
      ) {
        if (_DirectoryOnly &&
            !isDirectory) {
          return false;
        }

        string localPath =
          this.GetLocalPath(
            repositoryRelativePath
          );

        if (localPath == null ||
            localPath.Length == 0) {
          return false;
        }

        if (_Anchored ||
            _ContainsSlash) {
          return _PatternRegex.IsMatch(
            localPath
          );
        }

        string[] segments =
          localPath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries
          );

        foreach (string segment in segments) {
          if (_PatternRegex.IsMatch(
                segment
              )) {
            return true;
          }
        }

        return false;
      }

      /// <summary>
      /// Converts one repository-relative path into the namespace rooted at this rule's
      /// containing .gitignore directory.
      /// </summary>
      private string GetLocalPath(
        string repositoryRelativePath
      ) {
        string normalizedPath =
          repositoryRelativePath.Trim(
            '/'
          );

        if (_BaseRelativeDirectory.Length == 0) {
          return normalizedPath;
        }

        StringComparison comparison =
          StringComparison.Ordinal;

        if (OperatingSystem.IsWindows()) {
          comparison =
            StringComparison.OrdinalIgnoreCase;
        }

        if (string.Equals(
              normalizedPath,
              _BaseRelativeDirectory,
              comparison
            )) {
          return string.Empty;
        }

        string prefix =
          _BaseRelativeDirectory
          + "/";

        if (!normalizedPath.StartsWith(
              prefix,
              comparison
            )) {
          return null;
        }

        return normalizedPath.Substring(
          prefix.Length
        );
      }

      /// <summary>
      /// Converts the supported Git-style glob syntax to one slash-aware regular expression.
      ///
      /// Supported constructs include <c>*</c>, <c>?</c>, <c>**</c>, <c>**/</c> and basic
      /// character classes. Single-star and question-mark never cross directory separators.
      /// </summary>
      private string ConvertGlobToRegex(
        string pattern
      ) {
        StringBuilder builder =
          new StringBuilder();

        for (int index = 0;
             index < pattern.Length;
             index++) {
          char character =
            pattern[index];

          if (character == '*') {
            bool doubleStar =
              index + 1 < pattern.Length &&
              pattern[index + 1] == '*';

            if (doubleStar) {
              bool followedBySlash =
                index + 2 < pattern.Length &&
                pattern[index + 2] == '/';

              if (followedBySlash) {
                builder.Append(
                  "(?:.*/)?"
                );

                index +=
                  2;
              }
              else {
                builder.Append(
                  ".*"
                );

                index++;
              }

              continue;
            }

            builder.Append(
              "[^/]*"
            );

            continue;
          }

          if (character == '?') {
            builder.Append(
              "[^/]"
            );

            continue;
          }

          if (character == '[') {
            int closingIndex =
              pattern.IndexOf(
                ']',
                index + 1
              );

            if (closingIndex > index + 1) {
              string characterClass =
                pattern.Substring(
                  index + 1,
                  closingIndex - index - 1
                );

              if (characterClass.StartsWith(
                    "!",
                    StringComparison.Ordinal
                  )) {
                characterClass =
                  "^"
                  + characterClass.Substring(
                    1
                  );
              }

              builder.Append(
                "["
              );

              builder.Append(
                characterClass
              );

              builder.Append(
                "]"
              );

              index =
                closingIndex;

              continue;
            }
          }

          builder.Append(
            Regex.Escape(
              character.ToString()
            )
          );
        }

        return builder.ToString();
      }
    }
  }
}
