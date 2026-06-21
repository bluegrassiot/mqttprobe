using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using MqttProbe.Pages;

namespace MqttProbe.Web.Services;

public static class LoginRateLimiting
{
    public const int PermitLimit = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static void AddLoginRateLimitPolicy(this RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = static async (context, cancellationToken) =>
        {
            context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
            await context.HttpContext.Response.WriteAsync(
                "Too many login attempts. Please try again later.",
                cancellationToken);
        };

        options.AddPolicy(LoginModel.RateLimitPolicyName, context =>
        {
            if (!HttpMethods.IsPost(context.Request.Method))
                return RateLimitPartition.GetNoLimiter("login-get");

            var partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PermitLimit,
                    Window = Window,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true
                });
        });
    }
}
