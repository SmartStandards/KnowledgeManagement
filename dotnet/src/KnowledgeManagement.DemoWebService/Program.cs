using CyclicTriggering;
using KnowledgeManagement.SmartStandards;
using KnowledgeManagement.SmartStandards.Endpoints.Joplin;
using KnowledgeManagement.SmartStandards.Providers;
using KnowledgeManagement.SmartStandards.Wrappers;
using Logging.SmartStandards;
using Logging.SmartStandards.AspSupport;
using Microsoft.AspNetCore;
using System.Reflection;
using System.Web.UJMW;

namespace KnowledgeManagement.SmartStandards.DemoWebService {

  public static partial class Program {

    static partial void OnConfigureServices(IServiceCollection services, IConfiguration config) {

      services.AddSmartStandardsLogging(config);

      string outDir = AppDomain.CurrentDomain.BaseDirectory;

      services.AddControllers();

      UjmwHostConfiguration.EnableApiGroupNameFallback = true;

      UjmwHostConfiguration.AuthHeaderEvaluator = (
        (string rawAuthHeader, Type contractType, MethodInfo targetContractMethod, string callingMachine, ref int httpReturnCode, ref string failedReason) => {
         
          if(contractType == typeof(ICyclicTriggerReceiver)) {
            return true;
          }

          //in this demo - any auth header is ok - but there must be one ;-)
          if (string.IsNullOrWhiteSpace(rawAuthHeader)) {
            httpReturnCode = 403;
            failedReason = "This demo requires at least ANY string as authheader!";
            return false;
          }
          return true;
        }
      );

      AggregatedKnowledgeRepository agg = new AggregatedKnowledgeRepository();

      //////////////////////////////////////////////////////////////////////////////////////////
      //FILESYSTEM

      agg.Add(new FileBasedKnowledgeRepository("C:\\Temp\\_OneNoteExport", false, true));

      //////////////////////////////////////////////////////////////////////////////////////////
      //GITHUB

      //agg.Add(
      //  new GitBasedKnowledgeRepository(
      //    "https://github.com/SmartStandards/FUSE-fx.RepositoryContract",
      //    true, "","/doc/"
      //  ),
      //  "/FUSE-fx.RepositoryContract/"
      //);

      //////////////////////////////////////////////////////////////////////////////////////////
      // UJMW-REMOTE (mit cache)

      //IKnowledgeRepository smartStandardsUjmwRemoteSource = DynamicClientFactory.CreateInstance<IKnowledgeRepository>(
      //  "https://re-define-it.de/wiki/ujmw/IKnowledgeRepository",
      //);

      //IKnowledgeRepository smartStandardsCached = new KnowledgeRepositoryCacheWrapper(
      //  smartStandardsUjmwRemoteSource, 5, "C:\\Temp\\_KnowledgeCache\\SmartStandards"
      //);

      //agg.Add(smartStandardsCached, "/SmartStandards/" );

      //////////////////////////////////////////////////////////////////////////////////////////
      // OneNote (via AzureGraph

      //  1 OPEN: https://developer.microsoft.com/en-us/graph/graph-explorer
      //  2 SIGN IN (upper right) -> COMPANY-Account -> follow MFA-flow
      //  3 QUERY: https://graph.microsoft.com/v1.0/me/onenote/notebooks
      //  4 Modify Permission -> Consent for Notes.Read / Notes.Read.All
      //  5 Copy access token from the "Access Token"-Tab and paste it into a new config-file:
      /*     appsettings.EXCLUDED-FROM-COMMIT.json:       
             {
               "oneNoteOrSiteUrl": "...",
               "graphApiToken": "..."
               "entraTenantId": "..."
             }
       */

      StaticTokenCredential credential = new StaticTokenCredential(
        config.GetValue<string>("graphApiToken")
      );

      IOneNoteGraphAuthenticationProvider authenticationProvider = new TokenCredentialOneNoteGraphAuthenticationProvider(
        credential,
        new string[] { "https://graph.microsoft.com/.default" },
        new string[] { "https://graph.microsoft.com/.default" }
      );

      IKnowledgeRepository oneNoteUjmwRemoteSource = new OneNoteKnowledgeRepositoryProxy(
        oneNoteOrSiteUrl: config.GetValue<string>("oneNoteOrSiteUrl"),
        authenticationProvider: authenticationProvider,
        readOnly: true,
        notebookName: "1 x 1 der Programmierung"
      );

      //IKnowledgeRepository oneNoteCached = new KnowledgeRepositoryCacheWrapper(
      //  oneNoteUjmwRemoteSource, 5, "C:\\Temp\\_KnowledgeCache\\OneNote"
      //);

      agg.Add(oneNoteUjmwRemoteSource, "/OneNote/1 x 1 der Programmierung");

      //////////////////////////////////////////////////////////////////////////////////////////

      services.AddSingleton<IKnowledgeRepository>(agg);

      services.AddSingleton<IJoplinWebDavAuthenticationValidator>(
        new DelegateBasedJoplinWebDavAuthenticationValidator((userName, password, syncId, context) => {
          if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password)) {
            return false;
          }
          return true;
        })
      );

      //services.AddSingleton<IJoplinSyncStateStore>(
      //  new FileBasedJoplinSyncStateStore("C:\\Temp\\Joplin")
      //);

      services.AddSingleton<IJoplinSyncStateStoreFactory>(
        new FileBasedJoplinSyncStateStoreFactory("C:\\Temp\\Joplin")
      );

      //services.AddKnowledgeManagement((ai) => { 

      //  //TODO: ...

      //});


      services.AddSwaggerGenSmartStandardsFlavored();

      int tid = Thread.CurrentThread.ManagedThreadId;
      DevLogger.LogDebug($"  [#{tid}]  OnConfigureServices COMPLETED!");
    }

    static partial void OnRunApplication(
      WebApplication app, IConfiguration config, IServiceProvider services,
      IWebHostEnvironment environment, IHostApplicationLifetime lifetime
    ) {

      app.UseAmbientFieldAdapterMiddleware();


      /*
       * "WebDAV" (NICHT Joplin Server)
       http://localhost:55202/api/knowledge/joplin
       
       */
      app.UseJoplinKnowledgeRepositoryWebDav();

      //required for the www-root
      app.UseStaticFiles();

      if (!config.GetValue<bool>("ProdMode")) {
        app.UseDeveloperExceptionPage();
      }

      string baseUrl = config.GetValue<string>("BaseUrl");

      app.UseHttpsRedirection();

      app.UseRouting();

      //CORS: muss zwischen 'UseRouting' und 'UseEndpoints' liegen!
      app.UseCors((p) => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

      app.UseAuthentication(); //<< WINDOWS-AUTH
      app.UseAuthorization();

      app.UseEndpoints((endpoints) => {
        endpoints.MapControllers();
      });

      int tid = Thread.CurrentThread.ManagedThreadId;
      DevLogger.LogDebug($"  [#{tid}]  OnRunApplication COMPLETED!");
    }

  }

}
