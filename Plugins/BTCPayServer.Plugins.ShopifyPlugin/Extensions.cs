using BTCPayServer.Services.Invoices;
using System.Globalization;
using System.Linq;

namespace BTCPayServer.Plugins.ShopifyPlugin
{
    public static class Extensions
    {
		public const string SHOPIFY_ORDER_ID_PREFIX = "shopify-";
		public static long? GetShopifyOrderId(this InvoiceEntity e)
			=> e
				.GetInternalTags(SHOPIFY_ORDER_ID_PREFIX)
				.Select(e => long.TryParse(e, CultureInfo.InvariantCulture, out var v) ? v : (long?)null)
				.FirstOrDefault(e => e is not null);

	}
}
