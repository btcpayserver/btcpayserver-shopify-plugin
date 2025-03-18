﻿using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.ShopifyPlugin.Clients;
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

    async Task<InvoiceLogs> Process(long shopifyOrderId, InvoiceEntity invoice, bool paidPartial = false)
    {
		var logs = new InvoiceLogs();
		var client = await shopifyClientFactory.CreateAPIClient(invoice.StoreId);
		if (client is null)
			return logs;
		if (await client.GetOrder(shopifyOrderId, true) is not { } order)
			return logs;
        
        var saleTx = order.Transactions.FirstOrDefault(h => h is { Kind: "SALE", Status: "PENDING" });
        if (saleTx is null)
            return logs;
        //technically, this exploit should not be possible as we use internal invoice tags to verify that the invoice was created by our controlled, dedicated endpoint.
        if (!invoice.Currency.Equals(saleTx.AmountSet.PresentmentMoney.CurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            // because of parent_id present, currency will always be the one from parent transaction
            // malicious attacker could potentially exploit this by creating invoice 
            // in different currency and paying that one, registering order on Shopify as paid
            // so if currency is supplied and is different from parent transaction currency we just won't register
            logs.Write("Currency mismatch on Shopify.", InvoiceEventData.EventSeverity.Error);
            return logs;
        }

        //of the successful ones, get the ones we registered as a valid payment
        var captures = 
            order.Transactions
            .Where(h => h is { Kind: "SALE", Status: "SUCCESS" }).ToArray();
        
        
        //of the successful ones, get the ones we registered as a voiding of a previous successful payment
        var refunds = 
            order.Transactions
                .Where(h => h is { Kind: "REFUND", Status: "SUCCESS" }).ToArray();

        bool canRefund = captures.Length > 0 && captures.Length > refunds.Length;
        if (invoice.Status is InvoiceStatus.Settled || invoice.ExceptionStatus == InvoiceExceptionStatus.PaidPartial)
        {
            if (canRefund)
            {
                if (order.CancelledAt is not null)
                {
                    logs.Write("The shopify order has already been cancelled, but the BTCPay Server has been successfully paid.",
                        InvoiceEventData.EventSeverity.Warning);
                }
                else
                {
                    logs.Write("A transaction was previously recorded against the Shopify order. Skipping.",
                        InvoiceEventData.EventSeverity.Warning);
                }
                return logs;
            }

            if (saleTx.ManuallyCapturable)
            {
                try
                {
                    await client.CaptureOrder(new()
                    {
                        Currency = invoice.Currency,
                        Amount = invoice.Price,
                        Id = order.Id,
                        ParentTransactionId = saleTx.Id
                    });
                    logs.Write(
                        $"Successfully captured the order on Shopify.",
                        InvoiceEventData.EventSeverity.Info);
                }
                catch (Exception e)
                {
                    logs.Write($"Failed to capture the Shopify order. {e.Message}",
                        InvoiceEventData.EventSeverity.Error);
                }
            }
        }
        if (invoice.Status == InvoiceStatus.Expired && invoice.ExceptionStatus == InvoiceExceptionStatus.PaidPartial)
        {
            // Mark order as paid partial
        }
        else if(order.CancelledAt is null)
        {
            try
            {
                await client.CancelOrder(new()
                {
                    OrderId = order.Id,
                    NotifyCustomer = false,
                    Reason = OrderCancelReason.DECLINED,
                    Restock = true,
                    Refund = canRefund,
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
}
