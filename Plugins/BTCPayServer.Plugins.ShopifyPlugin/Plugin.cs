using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.ShopifyPlugin.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.ShopifyPlugin;

public class Plugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.3.3" }
    };

    public override void Execute(IServiceCollection services)
    {
        services.AddUIExtension("header-nav", "ShopifyPluginHeaderNav");
        services.AddSingleton<EmailService>();
        services.AddSingleton<ShopifyClientFactory>();
        services.AddHostedService<ShopifyHostedService>();
    }
}
