using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ShopifyPlugin.ViewModels;

public class ShopifyRefundWebhookSettingsViewModel
{
    public enum RefundOption
    {
        RateThen,
        CurrentRate
    }

    public string WebhookUrl { get; set; }
    [Required(ErrorMessage = "Webhook secret is required")]
    public string WebhookSecret { get; set; }
    [Required(ErrorMessage = "Please select a refund option")]
    public RefundOption? SelectedRefundOption { get; set; }

    [Range(0, 100, ErrorMessage = "Spread must be between 0 and 100")]
    public decimal SpreadPercentage { get; set; } = 0;
}