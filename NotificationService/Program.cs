using System.Text.Json;
using Diagrid.Labs.Catalyst.OrderWorkflow.Common.ServiceDefaults;
using Diagrid.Labs.Catalyst.OrderWorkflow.NotificationService;
using Diagrid.Labs.Catalyst.OrderWorkflow.NotificationService.Hubs;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
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

// Add HttpClient for Chaos Mesh dashboard API
builder.Services.AddHttpClient("chaos-mesh", (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var baseUrl = config["ChaosMesh:BaseUrl"] ?? "http://chaos-dashboard.chaos-mesh.svc.cluster.local";
    client.BaseAddress = new Uri(baseUrl);
});

builder.Services.AddHttpClient("inventory-service", (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(config["Services:InventoryService"] ?? "http://inventory-service.catalyst-order-workflow-demo.svc.cluster.local");
    client.Timeout = TimeSpan.FromSeconds(3);
});

builder.Services.AddHttpClient("order-manager", (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(config["Services:OrderManager"] ?? "http://order-manager.catalyst-order-workflow-demo.svc.cluster.local");
    client.Timeout = TimeSpan.FromSeconds(3);
});

// Add SignalR for real-time notifications
builder.Services.AddSignalR();

// Add CORS for web UI
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCloudEvents();

app.UseCors("AllowAll");
app.UseRouting();
app.MapSubscribeHandler();
app.MapHealthChecks("/healthz");
app.MapOpenApi();
app.MapScalarApiReference();

// Map SignalR hub
app.MapHub<NotificationHub>("/notificationHub");

// Serve static files for the UI
app.UseDefaultFiles();
app.UseStaticFiles();

// Map notification endpoints
app.MapNotificationServiceEndpoints();

app.MapPost("blah", async (HttpContext context) =>
{
    var bodyString = await new StreamReader(context.Request.Body).ReadToEndAsync();

    context.Request.Body.Position = 0;

    var boundary = context.Request.GetMultipartBoundary();
    if (string.IsNullOrWhiteSpace(boundary))
    {
        return Results.BadRequest("Not a multipart request");
    }

    var reader = new MultipartReader(boundary, context.Request.Body);
    var section = await reader.ReadNextSectionAsync();

    if (section == null)
    {
        return Results.BadRequest("No file found");
    }

    var fileStream = new MemoryStream();
    await section.Body.CopyToAsync(fileStream);
    fileStream.Position = 0;

    var size = fileStream.Length;

    return Results.Ok(new { size });
});

var run = app.RunAsync();

Console.WriteLine("Notification service started...");

await run;
