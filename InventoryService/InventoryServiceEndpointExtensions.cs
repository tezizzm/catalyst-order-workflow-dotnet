using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapr.Client;
using Diagrid.Labs.Catalyst.OrderWorkflow.Common.Domain;
using Diagrid.Labs.Catalyst.OrderWorkflow.Common.ServiceDefaults;
using Diagrid.Labs.Catalyst.OrderWorkflow.InventoryService.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Diagrid.Labs.Catalyst.OrderWorkflow.InventoryService;

public static class InventoryServiceEndpointExtensions
{
    public static readonly IDictionary<string, int> SampleInventory = new Dictionary<string, int>
    {
        { "prod-001", 50 },
        { "prod-002", 30 },
        { "prod-003", 25 },
        { "prod-004", 40 },
        { "prod-005", 15 },
    };

    public static void MapInventoryServiceEndpoints(this WebApplication app)
    {
        app.MapPost("inventory/search", SearchInventory);
        app.MapGet("inventory/{productId}", ShowProduct);
        app.MapPost("inventory/initialize", InitializeInventory);
        app.MapPost("inventory/update", UpdateInventory);

        app
            .MapPost("order-notification", CreateOrder)
            .WithTopic(ShopActivityPubSub.PubSubName, ShopActivityPubSub.OrderTopic);
    }

    public static async Task<Results<Ok<InventorySearchResult>, NotFound>> SearchInventory(
        [FromServices] DaprClient daprClient,
        [FromServices] ILoggerFactory loggerFactory,
        [FromBody] InventorySearchRequest request
    )
    {
        var logger = loggerFactory.CreateLogger("InventoryService");
        logger.LogInformation("Searching inventory for {ItemCount} items", request.Items.Count);
        var currentInventoryItems = new List<ItemStatus>();
        foreach (var item in request.Items)
        {
            var key = $"inventory:{item.ProductId}";

            var inventoryData = await daprClient.GetStateAsync<Product?>(ResourceNames.InventoryStore, key);

            if (inventoryData is null) return TypedResults.NotFound();

            var currentQuantity = inventoryData.Quantity;
            currentInventoryItems.Add(item with { Quantity = currentQuantity });
            logger.LogInformation("Product {ProductId}: {Quantity} units available", item.ProductId, currentQuantity);
        }

        return TypedResults.Ok(new InventorySearchResult
        {
            Statuses = currentInventoryItems,
        });
    }

    public static async Task<Results<Ok<Product>, NotFound>> ShowProduct(
        [FromServices] DaprClient daprClient,
        [FromRoute] string productId
    )
    {
        var inventoryKey = $"inventory:{productId}";
        var inventoryData = await daprClient.GetStateAsync<Product?>(ResourceNames.InventoryStore, inventoryKey);

        if (inventoryData is null) return TypedResults.NotFound();

        return TypedResults.Ok(new Product
        {
            ProductId = inventoryData.ProductId,
            Quantity = inventoryData.Quantity,
            LastUpdated = inventoryData.LastUpdated,
        });
    }

    public static async Task<Ok> InitializeInventory(
        [FromServices] DaprClient daprClient,
        [FromServices] ILoggerFactory loggerFactory
    )
    {
        var logger = loggerFactory.CreateLogger("InventoryService");
        logger.LogInformation("Initializing inventory with {ProductCount} products", SampleInventory.Count);
        foreach (var item in SampleInventory)
        {
            var inventoryKey = $"inventory:{item.Key}";
            var inventoryData = new Product
            {
                ProductId = item.Key,
                Quantity = item.Value,
                LastUpdated = DateTime.UtcNow,
            };

            await daprClient.SaveStateAsync(ResourceNames.InventoryStore, inventoryKey, inventoryData);
            logger.LogInformation("Initialized {ProductId} with {Quantity} units", item.Key, item.Value);
        }

        return TypedResults.Ok();
    }

    public static async Task<Ok<UpdateInventoryResult>> UpdateInventory(
        [FromServices] DaprClient daprClient,
        [FromServices] ILoggerFactory loggerFactory,
        [FromBody] UpdateInventoryRequest request
    )
    {
        var logger = loggerFactory.CreateLogger("InventoryService");
        logger.LogInformation("Updating inventory - Operation: {Operation}, Items: {ItemCount}", request.Operation, request.Items.Count);
        foreach (var item in request.Items)
        {
            var inventoryKey = $"inventory:{item.ProductId}";

            var currentInventory = await daprClient.GetStateAsync<Product?>(ResourceNames.InventoryStore, inventoryKey);

            var currentQuantity = currentInventory?.Quantity ?? 0;

            var newQuantity = request.Operation.ToLower() switch
            {
                "reserve" => Math.Max(0, currentQuantity - item.Quantity),
                "release" or "restock" => currentQuantity + item.Quantity,
                _ => currentQuantity,
            };

            var updatedInventory = new Product
            {
                ProductId = item.ProductId,
                Quantity = newQuantity,
                LastUpdated = DateTime.UtcNow,
            };

            await daprClient.SaveStateAsync(ResourceNames.InventoryStore, inventoryKey, updatedInventory);
            logger.LogInformation("{ProductId}: {OldQuantity} → {NewQuantity} units ({Operation})", item.ProductId, currentQuantity, newQuantity, request.Operation);
        }

        return TypedResults.Ok(new UpdateInventoryResult
        {
            Success = true,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    public static async Task<NoContent> CreateOrder(
        [FromServices] ILoggerFactory loggerFactory,
        [FromBody] OrderStatusNotification notification
    )
    {
        var logger = loggerFactory.CreateLogger("InventoryService");
        await Task.CompletedTask;
        logger.LogInformation("Received order notification - Order ID: {OrderId}, Status: {Status}", notification.OrderId, notification.Status);

        switch (notification.Status.ToLower())
        {
            case "created":
                logger.LogInformation("Order {OrderId} created and being processed", notification.OrderId);
                break;

            case "payment_processed":
                logger.LogInformation("Payment processed for order {OrderId}", notification.OrderId);
                break;

            case "shipped":
                logger.LogInformation("Order {OrderId} shipped", notification.OrderId);
                break;

            case "delivered":
                logger.LogInformation("Order {OrderId} delivered", notification.OrderId);
                break;

            case "completed":
                logger.LogInformation("Order {OrderId} completed", notification.OrderId);
                break;

            default:
                throw new($"Unknown order status: {notification.Status}");
        }

        return TypedResults.NoContent();
    }
}
