using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.ShopifyPlugin.Clients;

public enum OrderCancelReason
{
	CUSTOMER,
	DECLINED,
	FRAUD,
	INVENTORY,
	OTHER,
	STAFF
}
public class CancelOrderRequest
{
	public bool NotifyCustomer { get; set; }
	public ShopifyId OrderId { get; set; }
	public OrderCancelReason Reason { get; set; }
	public bool Refund { get; set; }
	public bool Restock { get; set; }
	public string StaffNote { get; set; }
}

