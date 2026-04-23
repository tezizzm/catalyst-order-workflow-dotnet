using System.Linq;
using System.Threading.Tasks;
using Dapr.Workflow;
using Diagrid.Labs.Catalyst.OrderWorkflow.OrderManager.Model;
using Microsoft.Extensions.Logging;

namespace Diagrid.Labs.Catalyst.OrderWorkflow.OrderManager.Activity;

public class ValidateOrderActivity(ILogger<ValidateOrderActivity> logger) : WorkflowActivity<OrderValidationRequest, ValidationResult>
{
    public override Task<ValidationResult> RunAsync(WorkflowActivityContext context, OrderValidationRequest request)
    {
        logger.LogInformation("Validating order for Order ID: {OrderId}", request.OrderId);

        if (string.IsNullOrEmpty(request.CustomerId))
        {
            logger.LogWarning("Order validation failed for Order ID: {OrderId} - Reason: Customer ID is required", request.OrderId);
            return Task.FromResult(new ValidationResult(false, "Customer ID is required"));
        }

        if (! request.Items.Any())
        {
            logger.LogWarning("Order validation failed for Order ID: {OrderId} - Reason: Order must contain at least one item", request.OrderId);
            return Task.FromResult(new ValidationResult(false, "Order must contain at least one item"));
        }

        foreach (var item in request.Items)
        {
            if (item.Quantity <= 0)
            {
                logger.LogWarning("Order validation failed for Order ID: {OrderId} - Reason: Invalid quantity for product {ProductId}", request.OrderId, item.ProductId);
                return Task.FromResult(new ValidationResult(false, $"Invalid quantity for product {item.ProductId}"));
            }

            if (item.Price <= 0)
            {
                logger.LogWarning("Order validation failed for Order ID: {OrderId} - Reason: Invalid price for product {ProductId}", request.OrderId, item.ProductId);
                return Task.FromResult(new ValidationResult(false, $"Invalid price for product {item.ProductId}"));
            }
        }

        logger.LogInformation("Order validation successful for Order ID: {OrderId}", request.OrderId);
        return Task.FromResult(new ValidationResult(true, "Order validation successful"));
    }
}
