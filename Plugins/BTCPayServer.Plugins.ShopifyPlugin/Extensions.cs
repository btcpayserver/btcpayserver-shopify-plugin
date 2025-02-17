using BTCPayServer.Plugins.ShopifyPlugin.Services;
using BTCPayServer.Services.Invoices;
using MailKit.Search;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.ShopifyPlugin
{
    public static class Extensions
    {
		public const string SHOPIFY_ORDER_ID_PREFIX = "shopify-";
		public static long? GetShopifyOrderId(this InvoiceEntity e)
			=> e
			.GetInternalTags(SHOPIFY_ORDER_ID_PREFIX)
			.Select(e => long.TryParse(e, CultureInfo.InvariantCulture, out var v) ? v : (long?)null)
			.Where(e => e is not null)
			.FirstOrDefault();

	}
}
