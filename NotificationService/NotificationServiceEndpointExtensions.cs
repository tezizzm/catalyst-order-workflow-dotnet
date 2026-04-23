using System.Net.Http.Headers;
using System.Text.Json;
using Dapr.Client;
using Diagrid.Labs.Catalyst.OrderWorkflow.Common.ServiceDefaults;
using Diagrid.Labs.Catalyst.OrderWorkflow.NotificationService.Hubs;
using Diagrid.Labs.Catalyst.OrderWorkflow.NotificationService.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Diagrid.Labs.Catalyst.OrderWorkflow.NotificationService;

public static class NotificationServiceEndpointExtensions
{
    private static readonly List<NotificationViewModel> NotificationHistory = new();
    private static readonly object LockObject = new();
    private static readonly Dictionary<string, string?> _chaosExperimentUids = new();

    public static void MapNotificationServiceEndpoints(this IEndpointRouteBuilder app)
    {
        // Pub/Sub subscription handler for order notifications
        app.MapPost("/order-notification", async (OrderStatusNotification notification, IHubContext<NotificationHub> hubContext, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("NotificationService");
            logger.LogInformation("Received order notification - Order ID: {OrderId}, Status: {Status}", notification.OrderId, notification.Status);
            var viewModel = new NotificationViewModel
            {
                Type = "order",
                Title = $"Order {notification.Status}",
                Message = notification.Message,
                Timestamp = notification.Timestamp,
                Metadata = new Dictionary<string, string>
                {
                    { "OrderId", notification.OrderId },
                    { "Status", notification.Status },
                },
            };

            lock (LockObject)
            {
                NotificationHistory.Add(viewModel);
                // Keep only the last 100 notifications
                if (NotificationHistory.Count > 100)
                {
                    NotificationHistory.RemoveAt(0);
                }
            }

            // Broadcast to all connected clients
            await hubContext.Clients.All.SendAsync("ReceiveNotification", viewModel);
            logger.LogInformation("Broadcasted order notification to all connected clients");

            return Results.Ok();
        })
        .WithName("OrderNotification")
        .WithOpenApi()
        .WithTopic(ShopActivityPubSub.PubSubName, ShopActivityPubSub.OrderTopic);

        // Get notification history
        app.MapGet("/notifications/history", (ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("NotificationService");
            lock (LockObject)
            {
                var count = NotificationHistory.Count;
                logger.LogInformation("History requested - Returning {Count} notifications", count);
                return Results.Ok(NotificationHistory.OrderByDescending(n => n.Timestamp).ToList());
            }
        })
        .WithName("GetNotificationHistory")
        .WithOpenApi();

        // Service status endpoint
        app.MapGet("/status", async (IHttpClientFactory httpClientFactory, IConfiguration config, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("NotificationService");
            static async Task<string> CheckHealth(HttpClient client, string path = "/healthz")
            {
                try
                {
                    var response = await client.GetAsync(path);
                    return response.IsSuccessStatusCode ? "running" : "stopped";
                }
                catch
                {
                    return "stopped";
                }
            }

            var inventoryStatus    = await CheckHealth(httpClientFactory.CreateClient("inventory-service"));
            var orderManagerStatus = await CheckHealth(httpClientFactory.CreateClient("order-manager"));

            // Check active state for each known service experiment (lazy lookup)
            var token = config["CHAOS_MESH_TOKEN"];
            if (!string.IsNullOrEmpty(token))
            {
                string[] trackedServices = ["inventory-service", "order-manager"];
                foreach (var svc in trackedServices)
                {
                    if (!string.IsNullOrEmpty(_chaosExperimentUids.GetValueOrDefault(svc))) continue;

                    try
                    {
                        var experimentPath = $"/etc/chaos/experiment-{svc}.json";
                        string expName = $"kill{svc.Replace("-", "")}", expKind = "PodChaos";
                        if (File.Exists(experimentPath))
                        {
                            using var expDoc = JsonDocument.Parse(await File.ReadAllTextAsync(experimentPath));
                            expName = expDoc.RootElement.GetProperty("metadata").GetProperty("name").GetString() ?? expName;
                            expKind = expDoc.RootElement.GetProperty("kind").GetString() ?? expKind;
                        }

                        var chaosClient = httpClientFactory.CreateClient("chaos-mesh");
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        var listRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/experiments?kind={expKind}");
                        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        var listResponse = await chaosClient.SendAsync(listRequest, cts.Token);
                        var listBody = await listResponse.Content.ReadAsStringAsync(cts.Token);

                        using var doc = JsonDocument.Parse(listBody);
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            if (item.TryGetProperty("name", out var name) && name.GetString() == expName)
                            {
                                _chaosExperimentUids[svc] = item.TryGetProperty("uid", out var uid) ? uid.GetString() : null;
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning("Could not check chaos experiment status for {Service}: {Error}", svc, ex.Message);
                    }
                }
            }

            var chaosActiveStatus = new Dictionary<string, bool>();
            try
            {
                chaosActiveStatus["inventory-service"] = !string.IsNullOrEmpty(_chaosExperimentUids.GetValueOrDefault("inventory-service"));
                chaosActiveStatus["order-manager"]     = !string.IsNullOrEmpty(_chaosExperimentUids.GetValueOrDefault("order-manager"));
            }
            catch (Exception ex)
            {
                logger.LogWarning("Could not build chaos active status: {Error}", ex.Message);
            }

            return Results.Ok(new
            {
                notificationService   = "running",
                inventoryService      = inventoryStatus,
                orderManager          = orderManagerStatus,
                chaosActive           = chaosActiveStatus,
                chaosMeshDashboardUrl = config["ChaosMesh:DashboardUrl"] ?? "http://localhost:2333",
            });
        })
        .WithName("GetStatus")
        .WithOpenApi();

        // Create a new order by calling OrderManager via Dapr service invocation
        app.MapPost("/order", async (CreateOrderRequest request, DaprClient daprClient, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("NotificationService");
            logger.LogInformation("Creating new order - Customer: {CustomerId}, Items: {ItemCount}", request.CustomerId, request.Items.Count);

            var httpClient = daprClient.CreateInvokableHttpClient(ResourceNames.OrderManager);

            try
            {
                var response = await httpClient.PostAsJsonAsync("/order", request);

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<CreateOrderResult>();
                    logger.LogInformation("Order created successfully - Order ID: {OrderId}", result?.OrderId);
                    return Results.Ok(result);
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    logger.LogError("Failed to create order - Status: {StatusCode}, Error: {ErrorContent}", response.StatusCode, errorContent);
                    return Results.Problem(
                        detail: errorContent,
                        statusCode: (int)response.StatusCode,
                        title: "Failed to create order"
                    );
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error creating order");
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Error creating order"
                );
            }
        })
        .WithName("CreateOrder")
        .WithOpenApi();

        // Start chaos experiment for a service
        app.MapPost("/chaos/{service}/start", async (string service, IHttpClientFactory httpClientFactory, IConfiguration config, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("NotificationService");
            var token = config["CHAOS_MESH_TOKEN"];
            if (string.IsNullOrEmpty(token))
            {
                logger.LogWarning("Chaos Mesh token not configured");
                return Results.Problem(detail: "CHAOS_MESH_TOKEN is not configured", statusCode: 500, title: "Chaos Mesh token missing");
            }

            var experimentPath = $"/etc/chaos/experiment-{service}.json";
            if (!File.Exists(experimentPath))
                return Results.Problem(detail: $"Experiment file not found: {experimentPath}", statusCode: 500, title: "Chaos experiment not configured");

            var experimentJson = await File.ReadAllTextAsync(experimentPath);

            var client = httpClientFactory.CreateClient("chaos-mesh");
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/experiments")
            {
                Content = new StringContent(experimentJson, System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            try
            {
                var response = await client.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogError("Chaos Mesh start failed for {Service}: {StatusCode} {Body}", service, response.StatusCode, body);
                    return Results.Problem(detail: body, statusCode: (int)response.StatusCode, title: "Failed to start chaos experiment");
                }

                var result = JsonSerializer.Deserialize<ChaosMeshExperimentResult>(body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                _chaosExperimentUids[service] = result?.Uid;
                logger.LogInformation("Chaos experiment started for {Service} - UID: {Uid}", service, _chaosExperimentUids[service]);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error starting chaos experiment for {Service}", service);
                return Results.Problem(detail: ex.Message, statusCode: 500, title: "Error starting chaos experiment");
            }
        })
        .WithName("StartChaos")
        .WithOpenApi();

        // Stop (delete) chaos experiment for a service
        app.MapDelete("/chaos/{service}", async (string service, IHttpClientFactory httpClientFactory, IConfiguration config, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("NotificationService");
            var token = config["CHAOS_MESH_TOKEN"];
            if (string.IsNullOrEmpty(token))
                return Results.Problem(detail: "CHAOS_MESH_TOKEN is not configured", statusCode: 500, title: "Chaos Mesh token missing");

            // Resolve UID: use cached value or look it up by name from the experiment file
            if (string.IsNullOrEmpty(_chaosExperimentUids.GetValueOrDefault(service)))
            {
                var experimentPath = $"/etc/chaos/experiment-{service}.json";
                string expName = $"kill{service.Replace("-", "")}", expKind = "PodChaos";
                if (File.Exists(experimentPath))
                {
                    using var expDoc = JsonDocument.Parse(await File.ReadAllTextAsync(experimentPath));
                    expName = expDoc.RootElement.GetProperty("metadata").GetProperty("name").GetString() ?? expName;
                    expKind = expDoc.RootElement.GetProperty("kind").GetString() ?? expKind;
                }

                var listClient = httpClientFactory.CreateClient("chaos-mesh");
                var listRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/experiments?kind={expKind}");
                listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var listResponse = await listClient.SendAsync(listRequest);
                var listBody = await listResponse.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(listBody);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var name) && name.GetString() == expName)
                    {
                        _chaosExperimentUids[service] = item.TryGetProperty("uid", out var uidProp) ? uidProp.GetString() : null;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(_chaosExperimentUids.GetValueOrDefault(service)))
            {
                logger.LogWarning("Chaos experiment not found for {Service}", service);
                return Results.NotFound($"No active chaos experiment found for {service}");
            }

            var uid = _chaosExperimentUids[service];
            var client = httpClientFactory.CreateClient("chaos-mesh");
            var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/experiments/{uid}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            try
            {
                var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    logger.LogError("Chaos Mesh stop failed for {Service}: {StatusCode} {Body}", service, response.StatusCode, body);
                    return Results.Problem(detail: body, statusCode: (int)response.StatusCode, title: "Failed to stop chaos experiment");
                }

                logger.LogInformation("Chaos experiment stopped for {Service} - UID: {Uid}", service, uid);
                _chaosExperimentUids[service] = null;
                return Results.Ok();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error stopping chaos experiment for {Service}", service);
                return Results.Problem(detail: ex.Message, statusCode: 500, title: "Error stopping chaos experiment");
            }
        })
        .WithName("StopChaos")
        .WithOpenApi();
    }
}
