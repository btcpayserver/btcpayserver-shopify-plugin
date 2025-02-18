using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ShopifyPlugin.Clients;

public class CountResponse
{
    [JsonProperty("count")]
    public long Count { get; set; }
}
