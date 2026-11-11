using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using NicolasDorier.RateLimits;

namespace BTCPayServer.Plugins.ShopifyPlugin.Services;

public class ShopifyRateLimitStartupTask(IRateLimitService rateLimits) : IStartupTask
{
    public const string CheckoutZone = "shopifycheckout";

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        rateLimits.SetZone($"zone={CheckoutZone} rate=20r/min burst=20 nodelay");
        return Task.CompletedTask;
    }
}
