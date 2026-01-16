using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Emails;
using BTCPayServer.Plugins.Emails.Views;
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
        services.AddSingleton<ShopifyClientFactory>();
        services.AddHostedService<ShopifyHostedService>();
        RegisterShopifyEmailTriggers(services);
    }


    private void RegisterShopifyEmailTriggers(IServiceCollection services)
    {
        var vm = new EmailTriggerViewModel()
        {
            Trigger = ShopifyMailTriggers.RefundCreated,
            DefaultEmail = new()
            {
                To = ["{Customer.Email}"],
                Subject = "Refund for Shopify Order #{Order.Id}",
                Body = EmailsPlugin.CreateEmail(
                    "Hello {Customer.Name},<br/><br/>A refund has been created for your shopify order #{Order.Id}.<br/><br/>You can claim your refund using the link below.",
                    "Claim Refund",
                    "{RefundLink}"
                ),
            },
            PlaceHolders = new()
            {
                new("{RefundLink}", "The link to claim the refund"),
                new("{Order.Id}", "The Shopify order ID"),
                new("{Customer.Email}", "The customer's email address"),
                new("{Customer.Name}", "The customer's name"),
            },
            Description = "Shopify: Refund Created",
        };
        services.AddSingleton(vm);
    }
}

public static class ShopifyMailTriggers
{
    public const string RefundCreated = "SRV-ShopifyRefundCreated";
}