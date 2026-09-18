using Microsoft.Extensions.DependencyInjection;
using System.Web.UJMW;

namespace KnowledgeManagement.SmartStandards {

  public static partial class KnowledgeManagementSetupExtensions {

    public static void AddKnowledgeManagement(
      this IServiceCollection services, Action<IAiSupportSetup> onRegisteringTargets
    ) {

      //AspWorkerBasedCyclicTriggeringService service = new AspWorkerBasedCyclicTriggeringService();
      //onRegisteringTargets.Invoke(service);

      ////register the executor into the asp DI framework:
      //services.AddSingleton<ICyclicTriggerReceiver>(service);
      //services.AddSingleton<ITriggerTargetRegistrar>(service);

      //if (service._AmbientHttpInfoProviderShouldBeRegistered) {
      //  services.AddSingleton<IAmbientHttpContextProvider>(service);
      //}

      ////asp-special type of service-registration for background-workers
      //services.AddHostedService<AspWorkerBasedCyclicTriggeringService.HostedServiceProxyForExternalTriggerReceiver>();

      ////external trigger endpoint
      //service.RegisterUjmwEndpointIfRequired(services);

      ////request-interceptor to grap the current HttpContext + trigger special events
      //service.RegisterAsyncRequstFilterIfRequired(services);

    }

  }


  public interface IAiSupportSetup {

  }
  
}
