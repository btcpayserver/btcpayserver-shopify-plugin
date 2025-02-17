#nullable enable
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using BTCPayServer.Data;
using Microsoft.AspNetCore.Identity;
using System.Linq;
using Microsoft.AspNetCore.Http;
using BTCPayServer.Abstractions.Extensions;
using System;
using Microsoft.AspNetCore.Authorization;
using BTCPayServer.Controllers;
using BTCPayServer.Client;
using BTCPayServer.Abstractions.Constants;
using Microsoft.Extensions.Logging;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Invoices;
using System.Collections.Generic;
using BTCPayServer.Plugins.ShopifyPlugin.Services;
using System.Net.Http;
using BTCPayServer.Plugins.ShopifyPlugin.ViewModels.Models;
using BTCPayServer.Client.Models;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Microsoft.Extensions.Primitives;
using System.IO;
using System.Text;
using StoreData = BTCPayServer.Data.StoreData;
using BTCPayServer.Services;
using BTCPayServer.Abstractions.Models;
using System.Text.RegularExpressions;
using static Dapper.SqlMapper;
using BTCPayServer.Plugins.Shopify.Models;
using NBitpayClient;
using System.Globalization;
using BTCPayServer.Lightning.LndHub;
using System.Threading;

namespace BTCPayServer.Plugins.ShopifyPlugin;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewProfile)]
[AutoValidateAntiforgeryToken]
public class UIShopifyController : Controller
{
	private readonly StoreRepository _storeRepo;
	private readonly InvoiceRepository _invoiceRepository;
	private readonly UIInvoiceController _invoiceController;
	public UIShopifyController
		(
		ShopifyClientFactory shopifyClientFactory,
		StoreRepository storeRepo,
		UIInvoiceController invoiceController,
		InvoiceRepository invoiceRepository)
	{
		_storeRepo = storeRepo;
		ShopifyClientFactory = shopifyClientFactory;
		_invoiceRepository = invoiceRepository;
		_invoiceController = invoiceController;
	}
	public StoreData CurrentStore => HttpContext.GetStoreData();

	public ShopifyClientFactory ShopifyClientFactory { get; }

	[AllowAnonymous]
	[HttpGet("~/stores/{storeId}/plugins/shopify")]
	public async Task<IActionResult> Index(string storeId, string? id_token = null)
	{
		if (id_token is not null)
		{	
			var appClient = await ShopifyClientFactory.CreateAppClient(storeId);
			if (appClient is null)
				return NotFound();
			if (!appClient.ValidateQueryString(this.HttpContext.Request.QueryString.ToString()))
				return NotFound();
			var t = appClient.ValidateSessionToken(id_token);
			var accessToken = await appClient.GetAccessToken(t.ShopUrl, id_token);
			var settings = await _storeRepo.GetSettingAsync<ShopifyStoreSettings>(storeId, ShopifyStoreSettings.SettingsName) ?? new ShopifyStoreSettings(); // Should not be null as we have appClient
			if (settings.ShopUrl is null || settings.AccessToken is null)
			{
				this.TempData.SetStatusMessageModel(new StatusMessageModel()
				{
					Message = "Shopify plugin successfully configured",
					Severity = StatusMessageModel.StatusSeverity.Success
				});
				settings.ShopUrl = t.ShopUrl;
				settings.AccessToken = accessToken.AccessToken;
				await _storeRepo.UpdateSetting(storeId, ShopifyStoreSettings.SettingsName, settings);
			}
			else
			{
				if (settings.ShopUrl != t.ShopUrl)
				{
					this.TempData.SetStatusMessageModel(new StatusMessageModel()
					{
						Message = "The Shopify plugin is configured with a different store. Reset this configuration if you want to re-configure the plugin.",
						Severity = StatusMessageModel.StatusSeverity.Error
					});
				}
				else
				{
					this.TempData.SetStatusMessageModel(new StatusMessageModel()
					{
						Message = "The Shopify plugin is already configured",
						Severity = StatusMessageModel.StatusSeverity.Success
					});
					if (settings.AccessToken != accessToken.AccessToken)
					{
						settings.AccessToken = accessToken.AccessToken;
						await _storeRepo.UpdateSetting(storeId, ShopifyStoreSettings.SettingsName, settings);
					}
				}
			}
		}
		return RedirectToAction(nameof(Settings), new { storeId });
	}

	[Route("~/stores/{storeId}/plugins/shopify/settings")]
	[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
	public async Task<IActionResult> Settings(string storeId,
			ShopifySettingsViewModel vm, [FromForm] string? command = null)
	{
		if (command == "SaveAppSettings")
		{
			if (vm.ClientId is not null)
			{
				vm.ClientId = vm.ClientId.Trim();
				if (!Regex.IsMatch(vm.ClientId, "[a-f0-9]{32,32}"))
				{
					ModelState.AddModelError(nameof(vm.ClientId), "Invalid client id");
				}
			}
			if (vm.ClientSecret is not null)
			{
				vm.ClientSecret = vm.ClientSecret.Trim();
				if (!Regex.IsMatch(vm.ClientSecret, "[a-f0-9]{32,32}"))
				{
					ModelState.AddModelError(nameof(vm.ClientSecret), "Invalid client secret");
				}
			}
			if (!ModelState.IsValid)
				return View("/Views/UIShopify/Settings.cshtml", vm);
			var settings = new ShopifyStoreSettings();
			settings.App = new ShopifyStoreSettings.AppCreds
			{
				ClientId = vm.ClientId,
				ClientSecret = vm.ClientSecret
			};
			await _storeRepo.UpdateSetting(storeId, ShopifyStoreSettings.SettingsName, settings);
			this.TempData.SetStatusMessageModel(new StatusMessageModel()
			{
				Message = "App settings saved",
				Severity = StatusMessageModel.StatusSeverity.Success
			});
			return RedirectToAction(nameof(Settings), new { storeId });
		}
		if (command == "Reset")
		{
			await _storeRepo.UpdateSetting<ShopifyStoreSettings>(storeId, ShopifyStoreSettings.SettingsName, null!);
			this.TempData.SetStatusMessageModel(new StatusMessageModel()
			{
				Message = "App settings reset",
				Severity = StatusMessageModel.StatusSeverity.Success
			});
			return RedirectToAction(nameof(Settings), new { storeId });
		}
		else // (command is null)
		{
			var settings = await _storeRepo.GetSettingAsync<ShopifyStoreSettings>(storeId, ShopifyStoreSettings.SettingsName);
			return View("/Views/UIShopify/Settings.cshtml", new ShopifySettingsViewModel()
			{
				ClientId = settings?.App?.ClientId,
				ClientSecret = settings?.App?.ClientSecret,
				ShopUrl = settings?.ShopUrl
			});
		}
	}
	static AsyncDuplicateLock OrderLocks = new AsyncDuplicateLock();
	[AllowAnonymous]
	[HttpGet("~/stores/{storeId}/plugins/shopify/checkout")]
	public async Task<IActionResult> Checkout(string storeId, string? checkout_token, CancellationToken cancellationToken)
	{
		if (checkout_token is null)
			return BadRequest("Invalid checkout token");
		var client = await this.ShopifyClientFactory.CreateAPIClient(storeId);
		if (client is null)
			return BadRequest("Shopify plugin isn't configured properly");
		var order = await client.GetOrderByCheckoutToken(checkout_token);
		var store = await _storeRepo.FindStore(storeId);
		if (order is null || store is null)
			return BadRequest("Invalid checkout token");

		var orderId = order.Id.Id;
		var searchTerm = $"{Extensions.SHOPIFY_ORDER_ID_PREFIX}{orderId}";
		var invoices = await _invoiceRepository.GetInvoices(new InvoiceQuery()
		{
			TextSearch = searchTerm,
			StoreId = new[] { storeId }
		});

		// This prevent a race condition where two invoices get created for same order
		using var l = await OrderLocks.LockAsync(orderId, cancellationToken);

		var orderInvoices = 
			invoices.Where(e => e.GetShopifyOrderId() == orderId).ToArray();
		var currentInvoice =  orderInvoices.FirstOrDefault();
		if (currentInvoice != null)
			return RedirectToInvoiceCheckout(currentInvoice.Id);
		var amount = order.TotalOutstandingSet.PresentmentMoney;
		var invoice = await _invoiceController.CreateInvoiceCoreRaw(
				new CreateInvoiceRequest()
				{
					Amount = amount.Amount,
					Currency = amount.CurrencyCode,
					Metadata = new JObject
					{
						["orderId"] = order.Name,
						["shopifyOrderId"] = orderId,
						["shopifyOrderName"] = order.Name
					},
					AdditionalSearchTerms = new[]
					{
						order.Name,
						orderId.ToString(CultureInfo.InvariantCulture),
						searchTerm
					}
				}, store,
				Request.GetAbsoluteRoot(), new List<string>() { searchTerm });
		return RedirectToInvoiceCheckout(invoice.Id);
	}

	private IActionResult RedirectToInvoiceCheckout(string invoiceId)
	{
		return RedirectToAction(nameof(UIInvoiceController.Checkout), "UIInvoice",
					new { invoiceId });
	}

	record WebhookInfo(string HMac, string FullTopicName);
	static WebhookInfo? GetWebhookInfoFromHeader(HttpRequest request)
	{
		string? GetHeader(string name)
		{
			if (!request.Headers.TryGetValue(name, out StringValues o))
				return null;
			return o.ToString();
		}
		if (GetHeader("X-Shopify-Hmac-SHA256") is string hmac &&
			GetHeader("X-Shopify-Topic") is string topic &&
			GetHeader("X-Shopify-Sub-Topic") is string subtopic)
			return new WebhookInfo(hmac, $"{topic}/{subtopic}");
		return null;
	}

	[AllowAnonymous]
	[IgnoreAntiforgeryToken]
	[HttpPost("~/stores/{storeId}/plugins/shopify/webhooks")]
	// We actually do not use it, but shopify requires to still listen to it...
	// leaving it here.
	public async Task<IActionResult> Webhook(string storeId)
	{
		var settings = await _storeRepo.GetSettingAsync<ShopifyStoreSettings>(storeId, ShopifyStoreSettings.SettingsName);
		string requestBody;
		using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
		{
			requestBody = await reader.ReadToEndAsync();
		}
		var webhookInfo = GetWebhookInfoFromHeader(Request);
		if (webhookInfo is null)
			return BadRequest("Missing webhook info in HTTP headers");

		var client = await this.ShopifyClientFactory.CreateAppClient(storeId);
		if (client is null)
			return NotFound();
		if (!client.VerifyWebhookSignature(requestBody, webhookInfo.HMac))
			return Unauthorized("Invalid HMAC signature");

		// https://shopify.dev/docs/api/webhooks?reference=toml#list-of-topics-orders/create
		//if (webhookInfo.FullTopicName == "orders/create")
		//{
		//	var order = JsonConvert.DeserializeObject<dynamic>(requestBody)!;
		//	checkoutTokens.Add(new(storeId, (string)order.checkout_token), (long)order.id);
		//}

		return Ok();
	}
}
