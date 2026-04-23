using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Dapr.Workflow;
using Diagrid.Labs.Catalyst.OrderWorkflow.Common.ServiceDefaults;
using Diagrid.Labs.Catalyst.OrderWorkflow.OrderManager;
using Diagrid.Labs.Catalyst.OrderWorkflow.OrderManager.Activity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();

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

builder.Services.AddDaprWorkflow((options) =>
{
    options.UseGrpcChannelOptions(new()
    {
        HttpHandler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            EnableMultipleHttp2Connections = true,
        },
    });

    options.RegisterWorkflow<OrderProcessingWorkflow>();
        options.RegisterActivity<ValidateOrderActivity>();
        options.RegisterActivity<ProcessPaymentActivity>();
        options.RegisterActivity<CheckInventoryActivity>();
        options.RegisterActivity<UpdateInventoryActivity>();
        options.RegisterActivity<SendNotificationActivity>();
        options.RegisterActivity<CustomerFeedbackDelay>();
        options.RegisterActivity<StartCampaignActivity>();

    options.RegisterWorkflow<ShippingWorkflow>();
});

var app = builder.Build();

app.UseCloudEvents();

app.MapHealthChecks("/healthz");
app.MapOpenApi();
app.MapScalarApiReference();

app.MapWorkerEndpoints();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OrderManager");
logger.LogInformation("Workflow engine configured with 5 activities");
logger.LogInformation("Order Manager Service ready!");

app.Run();
