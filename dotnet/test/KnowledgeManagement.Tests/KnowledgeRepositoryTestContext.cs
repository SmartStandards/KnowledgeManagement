using KnowledgeManagement.SmartStandards.Endpoints.Joplin;
using KnowledgeManagement.SmartStandards.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using System.Text;

namespace KnowledgeManagement.SmartStandards.Tests {

  /// <summary>
  /// Provides isolated temporary storage and common helpers for knowledge repository tests.
  /// </summary>
  internal sealed class KnowledgeRepositoryTestContext : IDisposable {

    private readonly string _RootDirectory;
    private readonly string _KnowledgeDirectory;
    private readonly string _JoplinStateDirectory;
    private bool _Disposed;

    /// <summary>
    /// Creates one isolated test context.
    /// </summary>
    public KnowledgeRepositoryTestContext() {
      _RootDirectory = Path.Combine(
        Path.GetTempPath(),
        "KnowledgeRepositoryTests",
        Guid.NewGuid().ToString("N")
      );

      _KnowledgeDirectory = Path.Combine(
        _RootDirectory,
        "knowledge"
      );

      _JoplinStateDirectory = Path.Combine(
        _RootDirectory,
        "joplin"
      );

      Directory.CreateDirectory(
        _KnowledgeDirectory
      );

      Directory.CreateDirectory(
        _JoplinStateDirectory
      );

      _Disposed = false;
    }

    /// <summary>
    /// Gets the physical root directory used by the file-based knowledge repository.
    /// </summary>
    public string KnowledgeDirectory {
      get {
        return _KnowledgeDirectory;
      }
    }

    /// <summary>
    /// Creates one writable file-based repository.
    /// </summary>
    public FileBasedKnowledgeRepository CreateRepository() {
      return new FileBasedKnowledgeRepository(
        _KnowledgeDirectory,
        false,
        false
      );
    }

    /// <summary>
    /// Creates one writable file-based repository with soft-delete protection enabled.
    /// </summary>
    public FileBasedKnowledgeRepository CreateSoftDeleteRepository() {
      return new FileBasedKnowledgeRepository(
        _KnowledgeDirectory,
        false,
        true
      );
    }

    /// <summary>
    /// Creates one Joplin WebDAV handler backed by the supplied repository.
    /// </summary>
    public JoplinKnowledgeRepositoryWebDavHandler CreateJoplinHandler(
      IKnowledgeRepository repository
    ) {
      FileBasedJoplinSyncStateStore syncStateStore =
        new FileBasedJoplinSyncStateStore(
          _JoplinStateDirectory
        );

      JoplinKnowledgeRepositoryWebDavHandler handler =
        new JoplinKnowledgeRepositoryWebDavHandler(
          repository,
          syncStateStore
        );

      return handler;
    }

    /// <summary>
    /// Finds one direct child area by its provider-neutral logical display name.
    /// </summary>
    public string GetChildArea(
      IKnowledgeRepository repository,
      string parentArea,
      string name
    ) {
      string[] children = repository.GetAreas(
        false,
        parentArea
      );

      foreach (string child in children) {
        if (string.Equals(
              repository.GetAreaName(child),
              name,
              StringComparison.Ordinal
            )) {
          return child;
        }
      }

      return string.Empty;
    }

    /// <summary>
    /// Performs one Joplin PUT call using a fresh ASP.NET Core request context.
    /// </summary>
    public IActionResult PutText(
      JoplinKnowledgeRepositoryWebDavHandler handler,
      string path,
      string content
    ) {
      return this.PutBytes(
        handler,
        path,
        Encoding.UTF8.GetBytes(content)
      );
    }

    /// <summary>
    /// Performs one Joplin PUT call for binary content.
    /// </summary>
    public IActionResult PutBytes(
      JoplinKnowledgeRepositoryWebDavHandler handler,
      string path,
      byte[] content
    ) {
      DefaultHttpContext httpContext = this.PrepareHandler(
        handler
      );

      MemoryStream requestBody = new MemoryStream(
        content,
        false
      );

      httpContext.Request.Body = requestBody;
      httpContext.Request.ContentLength = content.LongLength;
      httpContext.Request.ContentType = "application/octet-stream";

      return handler.PutPath(
        path
      );
    }

    /// <summary>
    /// Performs one Joplin GET call using a fresh ASP.NET Core request context.
    /// </summary>
    public IActionResult Get(
      JoplinKnowledgeRepositoryWebDavHandler handler,
      string path
    ) {
      this.PrepareHandler(
        handler
      );

      return handler.GetPath(
        path
      );
    }

    /// <summary>
    /// Performs one Joplin DELETE call using a fresh ASP.NET Core request context.
    /// </summary>
    public IActionResult Delete(
      JoplinKnowledgeRepositoryWebDavHandler handler,
      string path
    ) {
      this.PrepareHandler(
        handler
      );

      return handler.DeletePath(
        path
      );
    }

    /// <summary>
    /// Returns the effective HTTP status code represented by one MVC action result.
    /// </summary>
    public int GetStatusCode(
      IActionResult result
    ) {
      StatusCodeResult statusCodeResult =
        result as StatusCodeResult;

      if (statusCodeResult != null) {
        return statusCodeResult.StatusCode;
      }

      ObjectResult objectResult =
        result as ObjectResult;

      if (objectResult != null) {
        if (objectResult.StatusCode.HasValue) {
          return objectResult.StatusCode.Value;
        }

        return StatusCodes.Status200OK;
      }

      return StatusCodes.Status200OK;
    }

    /// <summary>
    /// Creates a minimal Joplin folder sync item.
    /// </summary>
    public string CreateJoplinFolderItem(
      string id,
      string title,
      string parentId
    ) {
      StringBuilder builder = new StringBuilder();

      builder.Append(title);
      builder.Append("\n\n");
      builder.Append("id: ");
      builder.Append(id);
      builder.Append('\n');
      builder.Append("parent_id: ");
      builder.Append(parentId);
      builder.Append('\n');
      builder.Append("created_time: 2026-09-14T06:00:00.000Z\n");
      builder.Append("updated_time: 2026-09-14T06:00:00.000Z\n");
      builder.Append("user_created_time: 2026-09-14T06:00:00.000Z\n");
      builder.Append("user_updated_time: 2026-09-14T06:00:00.000Z\n");
      builder.Append("encryption_cipher_text: \n");
      builder.Append("encryption_applied: 0\n");
      builder.Append("is_shared: 0\n");
      builder.Append("share_id: \n");
      builder.Append("master_key_id: \n");
      builder.Append("user_data: \n");
      builder.Append("deleted_time: 0\n");
      builder.Append("type_: 2");

      return builder.ToString();
    }

    /// <summary>
    /// Creates a minimal Joplin note sync item.
    /// </summary>
    public string CreateJoplinNoteItem(
      string id,
      string title,
      string parentId,
      string body
    ) {
      StringBuilder builder = new StringBuilder();

      builder.Append(title);
      builder.Append("\n\n");

      if (!string.IsNullOrEmpty(body)) {
        builder.Append(body);
        builder.Append("\n\n");
      }

      builder.Append("id: ");
      builder.Append(id);
      builder.Append('\n');
      builder.Append("parent_id: ");
      builder.Append(parentId);
      builder.Append('\n');
      builder.Append("created_time: 2026-09-14T06:00:00.000Z\n");
      builder.Append("updated_time: 2026-09-14T06:00:00.000Z\n");
      builder.Append("is_conflict: 0\n");
      builder.Append("latitude: 0.00000000\n");
      builder.Append("longitude: 0.00000000\n");
      builder.Append("altitude: 0.0000\n");
      builder.Append("author: \n");
      builder.Append("source_url: \n");
      builder.Append("is_todo: 0\n");
      builder.Append("todo_due: 0\n");
      builder.Append("todo_completed: 0\n");
      builder.Append("source: test\n");
      builder.Append("source_application: test\n");
      builder.Append("application_data: \n");
      builder.Append("order: 0\n");
      builder.Append("user_created_time: 2026-09-14T06:00:00.000Z\n");
      builder.Append("user_updated_time: 2026-09-14T06:00:00.000Z\n");
      builder.Append("encryption_cipher_text: \n");
      builder.Append("encryption_applied: 0\n");
      builder.Append("markup_language: 1\n");
      builder.Append("is_shared: 0\n");
      builder.Append("share_id: \n");
      builder.Append("conflict_original_id: \n");
      builder.Append("master_key_id: \n");
      builder.Append("user_data: \n");
      builder.Append("deleted_time: 0\n");
      builder.Append("type_: 1");

      return builder.ToString();
    }

    /// <summary>
    /// Creates a minimal Joplin resource metadata sync item.
    /// </summary>
    public string CreateJoplinResourceItem(
      string id,
      string title,
      string fileName,
      string mime,
      string fileExtension
    ) {
      StringBuilder builder = new StringBuilder();

      builder.Append(title);
      builder.Append("\n\n");
      builder.Append("id: ");
      builder.Append(id);
      builder.Append('\n');
      builder.Append("mime: ");
      builder.Append(mime);
      builder.Append('\n');
      builder.Append("filename: ");
      builder.Append(fileName);
      builder.Append('\n');
      builder.Append("file_extension: ");
      builder.Append(fileExtension);
      builder.Append('\n');
      builder.Append("created_time: 2026-09-14T06:00:00.000Z\n");
      builder.Append("updated_time: 2026-09-14T06:00:00.000Z\n");
      builder.Append("user_created_time: 2026-09-14T06:00:00.000Z\n");
      builder.Append("user_updated_time: 2026-09-14T06:00:00.000Z\n");
      builder.Append("encryption_cipher_text: \n");
      builder.Append("encryption_applied: 0\n");
      builder.Append("is_shared: 0\n");
      builder.Append("share_id: \n");
      builder.Append("master_key_id: \n");
      builder.Append("user_data: \n");
      builder.Append("deleted_time: 0\n");
      builder.Append("type_: 4");

      return builder.ToString();
    }

    /// <summary>
    /// Releases the isolated test directories.
    /// </summary>
    public void Dispose() {
      if (_Disposed) {
        return;
      }

      _Disposed = true;

      if (Directory.Exists(_RootDirectory)) {
        Directory.Delete(
          _RootDirectory,
          true
        );
      }
    }

    /// <summary>
    /// Attaches a fresh minimal MVC context to one handler invocation.
    /// </summary>
    private DefaultHttpContext PrepareHandler(
      JoplinKnowledgeRepositoryWebDavHandler handler
    ) {
      DefaultHttpContext httpContext =
        new DefaultHttpContext();

      ControllerActionDescriptor actionDescriptor =
        new ControllerActionDescriptor();

      ActionContext actionContext =
        new ActionContext(
          httpContext,
          new RouteData(),
          actionDescriptor
        );

      handler.ControllerContext =
        new ControllerContext(
          actionContext
        );

      return httpContext;
    }
  }
}
