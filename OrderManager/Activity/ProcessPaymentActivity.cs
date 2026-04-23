using System;
using System.Threading.Tasks;
using Dapr.Workflow;
using Diagrid.Labs.Catalyst.OrderWorkflow.OrderManager.Model;
using Microsoft.Extensions.Logging;

namespace Diagrid.Labs.Catalyst.OrderWorkflow.OrderManager.Activity;

public class ProcessPaymentActivity(ILogger<ProcessPaymentActivity> logger) : WorkflowActivity<PaymentRequest, PaymentResult>
{
    public override async Task<PaymentResult> RunAsync(WorkflowActivityContext context, PaymentRequest request)
    {
        logger.LogInformation("Processing payment for Order ID: {OrderId}, Amount: ${Amount}", request.OrderId, request.Amount);

        if (request.Amount <= 0)
        {
            logger.LogWarning("Payment failed for Order ID: {OrderId} - Reason: Invalid payment amount", request.OrderId);
            return new(false, "Invalid payment amount");
        }

        await Task.Delay(TimeSpan.FromSeconds(1));

        if (request.Amount > 1000)
        {
            logger.LogWarning("Payment failed for Order ID: {OrderId} - Reason: Payment amount exceeds limit", request.OrderId);
            return new(false, "Payment amount exceeds limit");
        }

        logger.LogInformation("Payment processed successfully for Order ID: {OrderId}", request.OrderId);
        return new(true, "Payment processed successfully");
    }
}
