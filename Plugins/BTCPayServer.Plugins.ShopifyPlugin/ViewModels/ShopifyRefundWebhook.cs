using System.Collections.Generic;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ShopifyPlugin.ViewModels;

public class ShopifyRefundWebhook
{
    [JsonProperty("order_id")]
    public long OrderId { get; set; }

    [JsonProperty("refund_line_items")]
    public List<ShopifyRefundLineItem> RefundLineItems { get; set; } = new();

    [JsonProperty("order_adjustments")]
    public List<ShopifyOrderAdjustment> OrderAdjustments { get; set; } = new();
}

public class ShopifyRefundLineItem
{
    [JsonProperty("subtotal")]
    public decimal Subtotal { get; set; }
}

public class ShopifyOrderAdjustment
{
    [JsonProperty("refund_id")]
    public long? RefundId { get; set; }

    [JsonProperty("amount")]
    public decimal Amount { get; set; }
}

public class AccessScopeHandle
{
    [JsonProperty("handle")]
    public string Handle { get; set; }
}
