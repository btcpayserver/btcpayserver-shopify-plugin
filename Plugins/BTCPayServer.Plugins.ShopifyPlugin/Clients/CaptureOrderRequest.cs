using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.ShopifyPlugin.Clients;

public class CaptureOrderRequest
{
	[JsonConverter(typeof(BTCPayServer.JsonConverters.NumericStringJsonConverter))]
	public decimal Amount { get; set; }
	public string Currency { get; set; }
	public ShopifyId Id { get; set; }
	public ShopifyId ParentTransactionId { get; set; }
}
