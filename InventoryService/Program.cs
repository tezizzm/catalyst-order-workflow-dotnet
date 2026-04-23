using System;
using System.Text.Json;
using Dapr.Client;
using Diagrid.Labs.Catalyst.OrderWorkflow.Common.Domain;
using Diagrid.Labs.Catalyst.OrderWorkflow.Common.ServiceDefaults;
using Diagrid.Labs.Catalyst.OrderWorkflow.InventoryService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();

builder.Services.Configure<JsonOptions>((options) =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});

builder.Services.AddDaprClient((daprBuilder) =>
{
    daprBuilder.UseJsonSerializationOptions(new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    });
});

var app = builder.Build();

app.UseCloudEvents();

app.UseRouting();
app.MapSubscribeHandler();
app.MapHealthChecks("/healthz");
app.MapOpenApi();
app.MapScalarApiReference();

app.MapInventoryServiceEndpoints();

var daprClient = app.Services.GetRequiredService<DaprClient>();

foreach (var item in InventoryServiceEndpointExtensions.SampleInventory)
{
    var inventoryKey = $"inventory:{item.Key}";
    var inventoryData = new Product
    {
        ProductId = item.Key,
        Quantity = item.Value,
        LastUpdated = DateTime.UtcNow,
    };

    await daprClient.SaveStateAsync(ResourceNames.InventoryStore, inventoryKey, inventoryData);
}

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("InventoryService");
logger.LogInformation("Initialized {ProductCount} products", InventoryServiceEndpointExtensions.SampleInventory.Count);
logger.LogInformation("Inventory Service started...");

await app.RunAsync();
