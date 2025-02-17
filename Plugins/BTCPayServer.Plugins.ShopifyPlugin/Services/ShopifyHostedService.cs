using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Shopify.ApiModels;
using BTCPayServer.Plugins.ShopifyPlugin.ViewModels;
using BTCPayServer.Plugins.ShopifyPlugin.ViewModels.Models;
using BTCPayServer.Services.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.ShopifyPlugin.Services;

public class ShopifyHostedService : EventHostedServiceBase
{
    private readonly InvoiceRepository _invoiceRepository;
	private readonly ShopifyClientFactory shopifyClientFactory;

    public ShopifyHostedService(EventAggregator eventAggregator,
        InvoiceRepository invoiceRepository,
        ShopifyClientFactory shopifyClientFactory,
        Logs logs) : base(eventAggregator, logs)
    {
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
        if (evt is InvoiceEvent invoiceEvent && new[]
            {
                    InvoiceEvent.MarkedCompleted,
                    InvoiceEvent.MarkedInvalid,
                    InvoiceEvent.Expired,
                    InvoiceEvent.Confirmed,
                    InvoiceEvent.Completed
                }.Contains(invoiceEvent.Name))
        {
            var invoice = invoiceEvent.Invoice;
            if (invoice.GetShopifyOrderId() is long shopifyOrderId)
            {
                bool? success = invoice.Status switch
                {
                    InvoiceStatus.Processing or InvoiceStatus.Settled => true,
					InvoiceStatus.Invalid or InvoiceStatus.Expired => false,
                    _ => (bool?)null
                };
                if (success.HasValue)
                    await RegisterTransaction(invoice, shopifyOrderId, success.Value);
            }
        }
        await base.ProcessEvent(evt, cancellationToken);
    }

    private async Task RegisterTransaction(InvoiceEntity invoice, long shopifyOrderId, bool success)
    {
		try
		{
			var resp = await Process(shopifyOrderId, invoice, success);
			if (resp != null)
			{
				await _invoiceRepository.AddInvoiceLogs(invoice.Id, resp);
			}
		}
		catch (Exception ex)
		{
			Logs.PayServer.LogError(ex,
				$"Shopify error while trying to register order transaction. " +
				$"Triggered by invoiceId: {invoice.Id}, Shopify orderId: {shopifyOrderId}");
		}
	}

    private static string[] _keywords = new[] { "bitcoin", "btc", "btcpayserver", "btcpay server" };

    public async Task<InvoiceLogs> Process(long shopifyOrderId, InvoiceEntity invoice, bool success)
    {
		var logs = new InvoiceLogs();
		var client = await shopifyClientFactory.CreateAPIClient(invoice.StoreId);
		if (client is null)
			return logs;
		if (await client.GetOrder(shopifyOrderId, true) is not { } order)
			return logs;

        var existingShopifyOrderTransactions = order.Transactions;
        //if there isn't a record for btcpay payment gateway, abort
        var baseParentTransaction = existingShopifyOrderTransactions.FirstOrDefault(holder =>
            _keywords.Any(a => holder.Gateway.Contains(a, StringComparison.InvariantCultureIgnoreCase)));
        if (baseParentTransaction is null)
        {
            logs.Write("Couldn't find the order on Shopify.", InvoiceEventData.EventSeverity.Error);
            return logs;
        }
        //technically, this exploit should not be possible as we use internal invoice tags to verify that the invoice was created by our controlled, dedicated endpoint.
        if (!invoice.Currency.Equals(baseParentTransaction.AmountSet.PresentmentMoney.CurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            // because of parent_id present, currency will always be the one from parent transaction
            // malicious attacker could potentially exploit this by creating invoice 
            // in different currency and paying that one, registering order on Shopify as paid
            // so if currency is supplied and is different from parent transaction currency we just won't register
            logs.Write("Currency mismatch on Shopify.", InvoiceEventData.EventSeverity.Error);
            return logs;
        }

        var kind = "capture";
        var parentId = baseParentTransaction.Id;
        var status = success ? "success" : "failure";
        //find all existing transactions recorded around this invoice id 
        var existingShopifyOrderTransactionsOnSameInvoice =
            existingShopifyOrderTransactions.Where(holder => holder.AuthorizationCode == invoice.Id);

        //filter out the successful ones
        var successfulActions =
            existingShopifyOrderTransactionsOnSameInvoice.Where(holder => holder.Status == "SUCCESS").ToArray();

        //of the successful ones, get the ones we registered as a valid payment
        var successfulCaptures = successfulActions.Where(holder => holder.Kind == "CAPTURE").ToArray();

        //of the successful ones, get the ones we registered as a voiding of a previous successful payment
        var refunds = successfulActions.Where(holder => holder.Kind == "REFUND").ToArray();

        //if we are working with a non-success registration, but see that we have previously registered this invoice as a success, we switch to creating a "void" transaction, which in shopify terms is a refund.
        if (!success && successfulCaptures.Length > 0 && (successfulCaptures.Length - refunds.Length) > 0)
        {
            kind = "void";
            parentId = successfulCaptures.Last().Id;
            status = "success";
            logs.Write(
                "A transaction was previously recorded against the Shopify order. Creating a void transaction.",
                InvoiceEventData.EventSeverity.Warning);
        }
        else if (!success)
        {
            kind = "void";
            status = "success";
            logs.Write("Attempting to void the payment on Shopify order due to failure in payment.",
                InvoiceEventData.EventSeverity.Warning);
        }
        //if we are working with a success registration, but can see that we have already had a successful transaction saved, get outta here
        else if (success && successfulCaptures.Length > 0 && (successfulCaptures.Length - refunds.Length) > 0)
        {
            logs.Write("A transaction was previously recorded against the Shopify order. Skipping.",
                InvoiceEventData.EventSeverity.Warning);
            return logs;
        }

        var createTransaction = new BTCPayServer.Plugins.ShopifyPlugin.ViewModels.Models.TransactionsCreateReq
		{
            transaction = new BTCPayServer.Plugins.ShopifyPlugin.ViewModels.Models.TransactionsCreateReq.DataHolder
            {
                parent_id = parentId.Id,
                currency = invoice.Currency,
                amount = invoice.PaidAmount.Net.ToString(CultureInfo.InvariantCulture),
                kind = kind,
                gateway = "BTCPayServer",
                source = "external",
                authorization = invoice.Id,
                status = status
            }
        };
        var createResp = await client.TransactionCreate(shopifyOrderId, createTransaction);
        if (createResp.transaction is null)
        {
            logs.Write("Failed to register the transaction on Shopify.", InvoiceEventData.EventSeverity.Error);
        }
        else
        {
            logs.Write(
                $"Successfully registered the transaction on Shopify. tx status:{createResp.transaction.status}, kind: {createResp.transaction.kind}, order id:{createResp.transaction.order_id}",
                InvoiceEventData.EventSeverity.Info);
        }
        if (!success)
        {
            try
            {
                await client.CancelOrder(new()
                {
                    OrderId = order.Id,
					NotifyCustomer = false,
                    Reason = OrderCancelReason.DECLINED,
					Restock = true,
                    StaffNote = $"BTCPay Invoice {invoice.Id} expired or invalid"
				});
                logs.Write("Cancelling the Shopify order.", InvoiceEventData.EventSeverity.Warning);
            }
            catch (Exception e)
            {
                logs.Write($"Failed to cancel the Shopify order. {e.Message}",
                    InvoiceEventData.EventSeverity.Error);
            }
        }
        return logs;
    }
}
