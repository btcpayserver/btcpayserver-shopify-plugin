using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.ShopifyPlugin.Clients;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.ShopifyPlugin.Services;

public class ShopifyHostedService : EventHostedServiceBase
{
    private readonly CurrencyNameTable _currencyNameTable; 
    private readonly InvoiceRepository _invoiceRepository;
	private readonly ShopifyClientFactory shopifyClientFactory;

    public ShopifyHostedService(EventAggregator eventAggregator,
        InvoiceRepository invoiceRepository,
        CurrencyNameTable currencyNameTable,
        ShopifyClientFactory shopifyClientFactory,
        Logs logs) : base(eventAggregator, logs)
    {
        _currencyNameTable = currencyNameTable;
        _invoiceRepository = invoiceRepository;
		this.shopifyClientFactory = shopifyClientFactory;
    }

    protected override void SubscribeToEvents()
    {
        Subscribe<InvoiceEvent>();
        base.SubscribeToEvents();
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is InvoiceEvent
            {
                Name:
                InvoiceEvent.MarkedCompleted or
                InvoiceEvent.MarkedInvalid or
                InvoiceEvent.Expired or
                InvoiceEvent.Confirmed or
                InvoiceEvent.FailedToConfirm,
                Invoice:
                {
                    Status:
                    InvoiceStatus.Settled or
                    InvoiceStatus.Invalid or
                    InvoiceStatus.Expired
                } invoice
            } && invoice.GetShopifyOrderId() is { } shopifyOrderId)
        {
            try
            {
                var resp = await Process(shopifyOrderId, invoice);
                await _invoiceRepository.AddInvoiceLogs(invoice.Id, resp);
            }
            catch (Exception ex)
            {
                Logs.PayServer.LogError(ex,
                    $"Shopify error while trying to register order transaction. " +
                    $"Triggered by invoiceId: {invoice.Id}, Shopify orderId: {shopifyOrderId}");
            }
        }
    }

    private static string[] _keywords = new[] { "bitcoin", "btc", "btcpayserver", "btcpay server" };

    public static bool IsBTCPayServerGateway(string gateway)
    {
        return _keywords.Any(keyword => gateway.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    async Task<InvoiceLogs> Process(long shopifyOrderId, InvoiceEntity invoice)
	{
		var logs = new InvoiceLogs();
		var client = await shopifyClientFactory.CreateAPIClient(invoice.StoreId);
		if (client is null)
			return logs;
		if (await client.GetOrder(shopifyOrderId, true) is not { } order)
			return logs;

		var saleTx = order.Transactions
			.Where(h => h is { Kind: "SALE", Status: "PENDING" })
			.Where(h => h.AmountSet.PresentmentMoney.CurrencyCode.Equals(invoice.Currency, StringComparison.OrdinalIgnoreCase))
			.FirstOrDefault();
		if (saleTx is null)
			return logs;

		var shopifyPaid =
			order.Transactions
			.Where(h => h is { Kind: "SALE", Status: "SUCCESS" })
			.Select(h => h.AmountSet.PresentmentMoney.Amount)
			.Sum();

		decimal? btcpayPaid = invoice switch
		{
			{ Status: InvoiceStatus.Settled } => invoice.Price,
			{ Status: InvoiceStatus.Expired, ExceptionStatus: InvoiceExceptionStatus.PaidPartial } => NetSettled(invoice),
			{ Status: InvoiceStatus.Invalid, ExceptionStatus: InvoiceExceptionStatus.Marked } => 0.0m,
			{ Status: InvoiceStatus.Invalid } => NetSettled(invoice),
			_ => null
		};
		if (btcpayPaid is not null)
		{
			var capture = btcpayPaid.Value - shopifyPaid;
			if (capture > 0m)
			{
				if (order.CancelledAt is not null)
				{
					logs.Write("The shopify order has already been cancelled, but the BTCPay Server has been successfully paid.",
						InvoiceEventData.EventSeverity.Warning);
					return logs;
				}

				if (saleTx.ManuallyCapturable)
				{
					try
					{
						await client.CaptureOrder(new()
						{
							Currency = invoice.Currency,
							Amount = capture,
							Id = order.Id,
							ParentTransactionId = saleTx.Id
						});
						logs.Write(
							$"Successfully captured the order on Shopify. ({capture} {invoice.Currency})",
							InvoiceEventData.EventSeverity.Info);
					}
					catch (Exception e)
					{
						logs.Write($"Failed to capture the Shopify order. ({capture} {invoice.Currency}) {e.Message} ",
							InvoiceEventData.EventSeverity.Error);
					}
				}
			}
		}
		else if (order.CancelledAt is null)
		{
			try
			{
				await client.CancelOrder(new()
				{
					OrderId = order.Id,
					NotifyCustomer = false,
					Reason = OrderCancelReason.DECLINED,
					Restock = true,
					Refund = false,
					StaffNote = $"BTCPay Invoice {invoice.Id} is {invoice.Status}"
				});
				logs.Write($"Shopify order cancelled. (Invoice Status: {invoice.Status})", InvoiceEventData.EventSeverity.Warning);
			}
			catch (Exception e)
			{
				logs.Write($"Failed to cancel the Shopify order. {e.Message}",
					InvoiceEventData.EventSeverity.Error);
			}
		}
		return logs;
	}

	private decimal NetSettled(InvoiceEntity invoice)
	{
		decimal netSettled = netSettled = invoice.GetPayments(true)
						.Where(payment => payment.Status == PaymentStatus.Settled)
						.Sum(payment => payment.InvoicePaidAmount.Net);
		// Later we can just use this instead of calculating ourselves
		// decimal netSettled = invoice.NetSettled;
		return Math.Round(netSettled, _currencyNameTable.GetNumberFormatInfo(invoice.Currency)?.CurrencyDecimalDigits ?? 2);
	}
}
