using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ShopifyPlugin.ViewModels;

public class ShopifyRefundWebhookSettingsViewModel
{
    public enum RefundOption
    {
        CurrentRate,
        RateThen
    }

    public string WebhookUrl { get; set; }
    public string WebhookSecret { get; set; }
    [Required(ErrorMessage = "Please select a refund option")]
    public RefundOption SelectedRefundOption { get; set; } = RefundOption.CurrentRate;

    [Range(0, 99, ErrorMessage = "Spread must be between 0 and 99")]
    public decimal SpreadPercentage { get; set; } = 0;
}