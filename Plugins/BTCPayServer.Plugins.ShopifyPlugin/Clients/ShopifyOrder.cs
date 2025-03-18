using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace BTCPayServer.Plugins.ShopifyPlugin.Clients;

public record ShopifyId(string Type, long Id)
{
	public static ShopifyId Order(long Id) => new("Order", Id);
	public static ShopifyId DraftOrder(long Id) => new("DraftOrder", Id);
	public static bool TryParse(string str, [MaybeNullWhen(false)] out ShopifyId id)
	{
		id = null;
		if (str == null)
			return false;
		if (!str.StartsWith("gid://shopify/"))
			return false;
		var parts = str.Substring("gid://shopify/".Length).Split('/');
		if (parts.Length != 2)
			return false;
		if (!long.TryParse(parts[1], out var lid))
			return false;
		id = new ShopifyId(parts[0], lid);
		return true;
	}

	public static ShopifyId Parse(string str)
	{
		if (!TryParse(str, out var id))
			throw new FormatException("Invalid ShopifyId");
		return id;
	}

	public override string ToString()
	{
		return $"gid://shopify/{Type}/{Id}";
	}
}
public class OrderTransaction
{
	public ShopifyId Id { get; set; }
	public string Gateway { get; set; }
	public bool ManuallyCapturable { get; set; }
	public string Kind { get; set; }
	public string AuthorizationCode { get; set; }
	public string Status { get; set; }
	public ShopifyMoneyBag AmountSet { get; set; }
}
public class ShopifyOrder
{
	public string StatusPageUrl { get; set; }
	public ShopifyId Id { get; set; }
	public string Name { get; set; }
	public DateTimeOffset? CancelledAt { get; set; }
	public ShopifyMoneyBag TotalOutstandingSet { get; set; }
	public OrderTransaction[] Transactions { get; set; }
    public string[] PaymentGatewayNames { get; set; }
}
public class ShopifyMoneyBag
{
	public ShopifyMoney PresentmentMoney { get; set; }
	public ShopifyMoney ShopMoney { get; set; }
}
public class ShopifyMoney
{
	public string CurrencyCode { get; set; }
	[JsonConverter(typeof(BTCPayServer.JsonConverters.NumericStringJsonConverter))]
	public decimal Amount { get; set; }
}
