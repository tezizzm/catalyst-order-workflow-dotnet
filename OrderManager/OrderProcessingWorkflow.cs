using System;
using System.Linq;
using System.Threading.Tasks;
using Dapr.Workflow;
using Diagrid.Labs.Catalyst.OrderWorkflow.Common.Domain;
using Diagrid.Labs.Catalyst.OrderWorkflow.OrderManager.Activity;
using Diagrid.Labs.Catalyst.OrderWorkflow.OrderManager.Model;
using Microsoft.Extensions.Logging;
using InventoryItem = Diagrid.Labs.Catalyst.OrderWorkflow.OrderManager.Model.InventoryItem;

namespace Diagrid.Labs.Catalyst.OrderWorkflow.OrderManager;

public class OrderProcessingWorkflow(ILogger<OrderProcessingWorkflow> logger) : Workflow<OrderPayload, OrderResult>
{
    public override async Task<OrderResult> RunAsync(WorkflowContext context, OrderPayload order)
    {
        var retryPolicy = new WorkflowRetryPolicy(
            maxNumberOfAttempts: 8,
            firstRetryInterval: TimeSpan.FromSeconds(1),
            backoffCoefficient: 2.0,
            maxRetryInterval: TimeSpan.FromSeconds(60)
        );
        var activityOptions = new WorkflowTaskOptions(retryPolicy);

        var orderId = order.OrderId;

        await context.CallActivityAsync(
            nameof(SendNotificationActivity),
            new NotificationRequest(orderId, "created", $"Order {orderId} has been created and is being processed"),
            activityOptions
        );

        var validationResult = await context.CallActivityAsync<ValidationResult>(
            nameof(ValidateOrderActivity),
            new OrderValidationRequest(orderId, order.CustomerId, order.Items),
            activityOptions
        );

        if (! validationResult.IsValid)
        {
            await context.CallActivityAsync(
                nameof(SendNotificationActivity),
                new NotificationRequest(orderId, "failed", $"Order {orderId} validation failed: {validationResult.Reason}"),
                activityOptions
            );

            return new(false, $"Order validation failed: {validationResult.Reason}");
        }

        var paymentResult = await context.CallActivityAsync<PaymentResult>(
            nameof(ProcessPaymentActivity),
            new PaymentRequest(orderId, order.CustomerId, order.TotalAmount),
            activityOptions
        );

        if (! paymentResult.Success)
        {
            await context.CallActivityAsync(
                nameof(SendNotificationActivity),
                new NotificationRequest(orderId, "failed", $"Order {orderId} payment failed: {paymentResult.Reason}"),
                activityOptions
            );

            return new(false, $"Payment processing failed: {paymentResult.Reason}");
        }

        var paymentNotificationActivity = context.CallActivityAsync(
            nameof(SendNotificationActivity),
            new NotificationRequest(orderId, "payment_processed", $"Payment for order {orderId} has been processed successfully"),
            activityOptions
        );

        var subscribeToCampaignActivity = context.CallActivityAsync(nameof(StartCampaignActivity), new StartCampaignInput
        {
            OrderId = orderId,
        });

        await Task.WhenAll([
            paymentNotificationActivity,
            subscribeToCampaignActivity,
        ]);

        var inventoryCheckRequest = new InventoryCheckRequest
        {
            OrderId = orderId,
            Items = order.Items.Select(item => new InventoryItem(item.ProductId, item.Quantity)).ToList(),
        };

        var inventoryCheckResponse = await context.CallActivityAsync<InventorySearchResult>(
            nameof(CheckInventoryActivity),
            inventoryCheckRequest,
            activityOptions
        );

        if (inventoryCheckResponse.OutOfStockItems is { Count: >= 1 } outOfStockItems)
        {
            await context.CallActivityAsync(
                nameof(SendNotificationActivity),
                new NotificationRequest(orderId, "failed", $"Items {string.Join(", ", outOfStockItems.Select(item => item.ProductId))} are out of stock for order {orderId}"),
                activityOptions
            );
        }

        var inventoryResult = await context.CallActivityAsync<InventoryUpdateResult>(
            nameof(UpdateInventoryActivity),
            new InventoryUpdateRequest(orderId, order.Items, "reserve"),
            activityOptions
        );

        if (! inventoryResult.Success)
        {
            await context.CallActivityAsync(
                nameof(SendNotificationActivity),
                new NotificationRequest(orderId, "failed", $"Order {orderId} inventory update failed: {inventoryResult.Reason}"),
                activityOptions
            );

            return new(false, $"Inventory update failed: {inventoryResult.Reason}");
        }

        await context.CallActivityAsync(
            nameof(SendNotificationActivity),
            new NotificationRequest(orderId, "shipped", $"Order {orderId} has been shipped and is on its way"),
            activityOptions
        );

        await context.CallChildWorkflowAsync<ShippingWorkflowOutput>(nameof(ShippingWorkflow), new ShippingWorkflowInput
        {
            OrderId = orderId,
        });

        // note: This demonstrates the importance of keeping any volatile state inside activities.
        var delay = await context.CallActivityAsync<int>(nameof(CustomerFeedbackDelay), true, activityOptions);

        await context.CreateTimer(TimeSpan.FromSeconds(delay));

        // todo: We could generate this using AI in the future!
        await context.CallActivityAsync(
            nameof(SendNotificationActivity),
            new NotificationRequest(orderId, "feedback", $"Order {orderId} received customer feedback!"),
            activityOptions
        );

        logger.LogInformation("Order processing completed successfully for Order ID: {OrderId}", orderId);

        return new(true, $"Order {order.OrderId} processed successfully");
    }
}
