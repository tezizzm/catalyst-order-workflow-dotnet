using System.IO;
using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using CommunityToolkit.Aspire.Hosting.Dapr;
using Diagrid.Aspire.Hosting.Catalyst;
using Diagrid.Aspire.Hosting.Catalyst.ComponentSpec;

namespace Diagrid.Labs.Catalyst.OrderWorkflow.Development.AppHost;

public static class AppHostExtensions
{
    public static readonly string ExecutingPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? throw new("Where am I?");

    public static void ConfigureForLocal(this IDistributedApplicationBuilder builder, IResourceBuilder<ProjectResource> orderManager, IResourceBuilder<ProjectResource> inventoryService, IResourceBuilder<ProjectResource> notificationService)
    {
        builder.AddDapr();

        // Configure a cache to hold our state and workflows.
        var cachePassword = builder.AddParameter("cache-password", "zxczxc123", secret: true);
        var cache = builder
            .AddValkey("cache", 16379, cachePassword)
            .WithContainerName("catalyst-order-workflow-cache")
            .WithDataVolume("catalyst-order-workflow-cache-data")
        ;

        orderManager.WaitFor(cache);
        inventoryService.WaitFor(cache);
        notificationService.WaitFor(cache);

        orderManager.WithDaprSidecar(new DaprSidecarOptions
        {
            LogLevel = "debug",
            ResourcesPaths =
            [
                Path.Join(ExecutingPath, "Resources"),
            ],
        });

        inventoryService.WithDaprSidecar(new DaprSidecarOptions
        {
            LogLevel = "debug",
            ResourcesPaths =
            [
                Path.Join(ExecutingPath, "Resources"),
            ],
        });

        notificationService.WithDaprSidecar(new DaprSidecarOptions
        {
            LogLevel = "debug",
            ResourcesPaths =
            [
                Path.Join(ExecutingPath, "Resources"),
            ],
        });

        builder
            .AddContainer("diagrid-dashboard", "ghcr.io/diagridio/diagrid-dashboard:latest")
            .WithContainerName("catalyst-order-workflow-diagrid-dashboard")
            .WithBindMount(Path.Join(ExecutingPath, "Resources"), "/app/components")
            .WithEnvironment("COMPONENT_FILE", "/app/components/inventory-store-diagrid-dashboard.yaml")
            .WithEnvironment("APP_ID", "diagrid-dashboard")
            .WithHttpEndpoint(targetPort: 8080)
            .WithReference(cache)
        ;
    }

    public static void ConfigureForCatalyst(this IDistributedApplicationBuilder builder, IResourceBuilder<ProjectResource> orderManager, IResourceBuilder<ProjectResource> inventoryService, IResourceBuilder<ProjectResource> notificationService)
    {
        builder
            .AddCatalystProject("order-workflow", new()
            {
                EnableManagedWorkflow = true,
            })
            .WithCatalystKvStore("inventory-store")
            .WithCatalystPubSub("shop-activity")
            .WithComponent("inventory-store", new DiagridStateStore
            {
                Metadata = new()
                {
                    State = "inventory-store",
                    ActorStateStore = true,
                },
            })
            .WithComponent("shop-activity", new DiagridPubSub
            {
                Metadata = new()
                {
                    PubSubName = "shop-activity",
                },
            });

        orderManager.WithCatalyst();

        inventoryService.WithCatalyst();

        notificationService
            .WithCatalyst()
            .WithSubscription("shop-activity", "shop-activity", "orders", "/order-notification");
    }
}
