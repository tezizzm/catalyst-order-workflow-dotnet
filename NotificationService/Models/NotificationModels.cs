namespace Diagrid.Labs.Catalyst.OrderWorkflow.NotificationService.Models;

public record OrderStatusNotification
{
    public string OrderId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

public record NotificationViewModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Type { get; set; } = string.Empty; // "order"
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

// Models for creating orders via OrderManager
public record CreateOrderRequest(string? OrderId, string CustomerId, List<OrderItem> Items);
public record OrderItem(string ProductId, int Quantity, decimal Price);
public record CreateOrderResult(string OrderId, string Message, string WorkflowInstanceId);

// Chaos Mesh models
public record ChaosMeshExperimentResult(string Uid, string Name, string Namespace, string Kind);
