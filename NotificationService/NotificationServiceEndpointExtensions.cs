using System.Net.Http.Headers;
using System.Text.Json;
using Dapr.Client;
using Diagrid.Labs.Catalyst.OrderWorkflow.Common.ServiceDefaults;
using Diagrid.Labs.Catalyst.OrderWorkflow.NotificationService.Hubs;
using Diagrid.Labs.Catalyst.OrderWorkflow.NotificationService.Models;
using Microsoft.AspNetCore.SignalR;

namespace Diagrid.Labs.Catalyst.OrderWorkflow.NotificationService;

public static class NotificationServiceEndpointExtensions
{
    private static readonly List<NotificationViewModel> NotificationHistory = new();
    private static readonly object LockObject = new();
    private static string? _chaosExperimentUid;

    public static void MapNotificationServiceEndpoints(this IEndpointRouteBuilder app)
    {
        // Pub/Sub subscription handler for order notifications
        app.MapPost("/order-notification", async (OrderStatusNotification notification, IHubContext<NotificationHub> hubContext) =>
        {
            Console.WriteLine($"Received order notification - Order ID: {notification.OrderId}, Status: {notification.Status}");
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
            Console.WriteLine($"Broadcasted order notification to all connected clients");

            return Results.Ok();
        })
        .WithName("OrderNotification")
        .WithOpenApi()
        .WithTopic(ShopActivityPubSub.PubSubName, ShopActivityPubSub.OrderTopic);

        // Get notification history
        app.MapGet("/notifications/history", () =>
        {
            lock (LockObject)
            {
                var count = NotificationHistory.Count;
                Console.WriteLine($"History requested - Returning {count} notifications");
                return Results.Ok(NotificationHistory.OrderByDescending(n => n.Timestamp).ToList());
            }
        })
        .WithName("GetNotificationHistory")
        .WithOpenApi();

        // Service status endpoint
        app.MapGet("/status", async (IHttpClientFactory httpClientFactory, IConfiguration config) =>
        {
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

            // Check if an inventory chaos experiment is active
            if (string.IsNullOrEmpty(_chaosExperimentUid))
            {
                var token = config["CHAOS_MESH_TOKEN"];
                if (!string.IsNullOrEmpty(token))
                {
                    try
                    {
                        var experimentPath = config["ChaosExperiment:Path"] ?? "/etc/chaos/experiment.json";
                        string expName = "killinventory", expKind = "PodChaos";
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
                                _chaosExperimentUid = item.TryGetProperty("uid", out var uid) ? uid.GetString() : null;
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Could not check chaos experiment status: {ex.Message}");
                    }
                }
            }

            return Results.Ok(new
            {
                notificationService    = "running",
                inventoryService       = inventoryStatus,
                orderManager           = orderManagerStatus,
                chaosExperimentActive  = !string.IsNullOrEmpty(_chaosExperimentUid),
            });
        })
        .WithName("GetStatus")
        .WithOpenApi();

        // Create a new order by calling OrderManager via Dapr service invocation
        app.MapPost("/order", async (CreateOrderRequest request, DaprClient daprClient) =>
        {
            Console.WriteLine($"Creating new order - Customer: {request.CustomerId}, Items: {request.Items.Count}");

            var httpClient = daprClient.CreateInvokableHttpClient(ResourceNames.OrderManager);

            try
            {
                var response = await httpClient.PostAsJsonAsync("/order", request);

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<CreateOrderResult>();
                    Console.WriteLine($"Order created successfully - Order ID: {result?.OrderId}");
                    return Results.Ok(result);
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Failed to create order - Status: {response.StatusCode}, Error: {errorContent}");
                    return Results.Problem(
                        detail: errorContent,
                        statusCode: (int)response.StatusCode,
                        title: "Failed to create order"
                    );
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating order: {ex.Message}");
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Error creating order"
                );
            }
        })
        .WithName("CreateOrder")
        .WithOpenApi();

        // Start inventory chaos experiment
        app.MapPost("/chaos/start", async (IHttpClientFactory httpClientFactory, IConfiguration config) =>
        {
            var token = config["CHAOS_MESH_TOKEN"];
            if (string.IsNullOrEmpty(token))
            {
                Console.WriteLine("Chaos Mesh token not configured");
                return Results.Problem(detail: "CHAOS_MESH_TOKEN is not configured", statusCode: 500, title: "Chaos Mesh token missing");
            }

            var experimentPath = config["ChaosExperiment:Path"] ?? "/etc/chaos/experiment.json";
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
                    Console.WriteLine($"Chaos Mesh start failed: {response.StatusCode} {body}");
                    return Results.Problem(detail: body, statusCode: (int)response.StatusCode, title: "Failed to start chaos experiment");
                }

                var result = JsonSerializer.Deserialize<ChaosMeshExperimentResult>(body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                _chaosExperimentUid = result?.Uid;
                Console.WriteLine($"Chaos experiment started - UID: {_chaosExperimentUid}");
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting chaos experiment: {ex.Message}");
                return Results.Problem(detail: ex.Message, statusCode: 500, title: "Error starting chaos experiment");
            }
        })
        .WithName("StartChaos")
        .WithOpenApi();

        // Stop (delete) inventory chaos experiment
        app.MapDelete("/chaos/stop", async (IHttpClientFactory httpClientFactory, IConfiguration config) =>
        {
            var token = config["CHAOS_MESH_TOKEN"];
            if (string.IsNullOrEmpty(token))
                return Results.Problem(detail: "CHAOS_MESH_TOKEN is not configured", statusCode: 500, title: "Chaos Mesh token missing");

            // Resolve UID: use cached value or look it up by name from the experiment file
            if (string.IsNullOrEmpty(_chaosExperimentUid))
            {
                var experimentPath = config["ChaosExperiment:Path"] ?? "/etc/chaos/experiment.json";
                string expName = "killinventory", expKind = "PodChaos";
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
                        _chaosExperimentUid = item.TryGetProperty("uid", out var uid) ? uid.GetString() : null;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(_chaosExperimentUid))
            {
                Console.WriteLine("Chaos experiment not found");
                return Results.NotFound("No active chaos experiment found");
            }

            var client = httpClientFactory.CreateClient("chaos-mesh");
            var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/experiments/{_chaosExperimentUid}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            try
            {
                var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Chaos Mesh stop failed: {response.StatusCode} {body}");
                    return Results.Problem(detail: body, statusCode: (int)response.StatusCode, title: "Failed to stop chaos experiment");
                }

                Console.WriteLine($"Chaos experiment stopped - UID: {_chaosExperimentUid}");
                _chaosExperimentUid = null;
                return Results.Ok();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping chaos experiment: {ex.Message}");
                return Results.Problem(detail: ex.Message, statusCode: 500, title: "Error stopping chaos experiment");
            }
        })
        .WithName("StopChaos")
        .WithOpenApi();
    }
}
