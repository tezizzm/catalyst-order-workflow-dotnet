using System;
using Aspire.Hosting;
using Diagrid.Labs.Catalyst.OrderWorkflow.Development.AppHost;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var useCatalyst = Environment.GetEnvironmentVariable("USE_CATALYST") switch { "1" or "true" => true, _ => false, };

// note: These are the three core services that make up this solution.
var orderManager = builder.AddProject<OrderManager>("order-manager");
var inventoryService = builder.AddProject<InventoryService>("inventory-service");
var notificationService = builder.AddProject<NotificationService>("notification-service");

// note: This extension method further configures the services to run against Catalyst
if (useCatalyst) builder.ConfigureForCatalyst(orderManager, inventoryService, notificationService);
// note: This extension method further configures the services to run locally with Dapr sidecar processes
else builder.ConfigureForLocal(orderManager, inventoryService, notificationService);

builder.Build().Run();
