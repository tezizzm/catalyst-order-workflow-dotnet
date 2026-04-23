using System;
using System.Linq;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Dapr.Client;
using Dapr.Workflow;
using Diagrid.Labs.Catalyst.OrderWorkflow.Common.Domain;
using Diagrid.Labs.Catalyst.OrderWorkflow.Common.ServiceDefaults;
using Diagrid.Labs.Catalyst.OrderWorkflow.OrderManager.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Diagrid.Labs.Catalyst.OrderWorkflow.OrderManager;

public static class OrderManagerEndpointExtensions
{
    public static void MapWorkerEndpoints(this WebApplication app)
    {
        app.MapPost("inventory/search", SearchInventory);
        app.MapGet("inventory/{productId}", ShowProduct);

        app.MapPost("order", CreateOrder);
        app.MapGet("order/{orderId}", ShowOrder);
    }

    public static async Task<Ok<CreateOrderResult>> CreateOrder(
        [FromServices] DaprWorkflowClient workflowClient,
        [FromServices] ILoggerFactory loggerFactory,
        [FromBody] CreateOrderRequest request
    )
    {
        var logger = loggerFactory.CreateLogger("OrderManager");
        var orderId = request.OrderId ?? Guid.CreateVersion7().ToString();
        logger.LogInformation("Received new order request - Customer: {CustomerId}, Items: {ItemCount}", request.CustomerId, request.Items.Count);

        var orderPayload = new OrderPayload
        {
            OrderId = orderId,
            CustomerId = request.CustomerId,
            Items = request.Items,
            TotalAmount = request.Items.Sum(i => i.Price * i.Quantity),
            CreatedAt = DateTime.UtcNow,
        };

        await workflowClient.ScheduleNewWorkflowAsync(name: nameof(OrderProcessingWorkflow), input: orderPayload, instanceId: orderId);
        logger.LogInformation("Started workflow for Order ID: {OrderId}, Total: ${TotalAmount}", orderId, orderPayload.TotalAmount);

        return TypedResults.Ok(new CreateOrderResult
        {
            OrderId = orderId,
            Message = "Order processing started",
            WorkflowInstanceId = orderId,
        });
    }

    public static async Task<Ok<InventorySearchResult>> SearchInventory(
        [FromServices] DaprClient daprClient,
        [FromBody] SearchInventoryRequest request
    )
    {
        var httpClient = daprClient.CreateInvokableHttpClient(ResourceNames.InventoryService);

        var response = await httpClient.PostAsJsonAsync("/inventory/search", request);

        if (! response.IsSuccessStatusCode) throw new("Failed to retrieve inventory.");

        var inventoryResponse = await response.Content.ReadFromJsonAsync<InventorySearchResult>(JsonSerializerOptions.Default);

        return TypedResults.Ok(inventoryResponse);
    }

    public static async Task<Results<Ok<Product>, NotFound>> ShowProduct(
        [FromServices] DaprClient daprClient,
        [FromRoute] string productId
    )
    {
        var httpClient = daprClient.CreateInvokableHttpClient(ResourceNames.InventoryService);

        var response = await httpClient.GetAsync($"/inventory/{productId}");

        if (! response.IsSuccessStatusCode) throw new(await response.Content.ReadAsStringAsync());

        var inventoryData = await response.Content.ReadFromJsonAsync<Product>(JsonSerializerOptions.Default);

        return TypedResults.Ok(inventoryData);
    }

    public static async Task<Results<Ok<ShowOrderResult>, NotFound>> ShowOrder(
        [FromServices] DaprWorkflowClient workflowClient,
        [FromRoute] string orderId
    )
    {
        var state = await workflowClient.GetWorkflowStateAsync(orderId);

        if (! state.Exists) return TypedResults.NotFound();

        return TypedResults.Ok(new ShowOrderResult
        {
            OrderId = orderId,
            Status = state.RuntimeStatus.ToString(),
            State = state,
        });
    }
}
