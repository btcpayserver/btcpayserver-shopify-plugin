#nullable enable
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
using Microsoft.AspNetCore.Http;
using BTCPayServer.Abstractions.Extensions;
using System;
using Microsoft.AspNetCore.Authorization;
using BTCPayServer.Controllers;
using BTCPayServer.Client;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Plugins.ShopifyPlugin.Services;
using BTCPayServer.Client.Models;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Primitives;
using System.IO;
using System.Text;
using StoreData = BTCPayServer.Data.StoreData;
using BTCPayServer.Abstractions.Models;
using System.Text.RegularExpressions;
using System.Globalization;
using BTCPayServer.Lightning.LndHub;
using System.Threading;
using BTCPayServer.Filters;
using BTCPayServer.Plugins.ShopifyPlugin.Clients;
using BTCPayServer.Plugins.ShopifyPlugin.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Cors;

namespace BTCPayServer.Plugins.ShopifyPlugin;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewProfile)]
[AutoValidateAntiforgeryToken]
public class UIShopifyV2Controller : Controller
{
    private readonly StoreRepository _storeRepo;
	private readonly InvoiceRepository _invoiceRepository;
	private readonly UIInvoiceController _invoiceController;

	public UIShopifyV2Controller
		(
		ShopifyClientFactory shopifyClientFactory,
		StoreRepository storeRepo,
		UIInvoiceController invoiceController,
		IConfiguration configuration,
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
	[HttpGet("~/stores/{storeId}/plugins/shopify-v2")]
	[XFrameOptions(XFrameOptionsAttribute.XFrameOptions.Unset)]
	public async Task<IActionResult> Index(string storeId, string? id_token = null)
	{
		if (id_token is not null)
		{	
			var appClient = await ShopifyClientFactory.CreateAppClient(storeId);
			if (appClient is null)
			{
				this.TempData.SetStatusMessageModel(new StatusMessageModel()
				{
					Message = "The Shopify plugin's ClientId or ClientSecret isn't configured",
					Severity = StatusMessageModel.StatusSeverity.Error
				});
				return ShopifyAdminView();
			}

			if (!appClient.ValidateQueryString(this.HttpContext.Request.QueryString.ToString()))
			{
				this.TempData.SetStatusMessageModel(new StatusMessageModel()
				{
					Message = "The Shopify plugin's couldn't validate the query string. The ClientSecret might be incorrect. Reset the setup and start the app installation again.",
					Severity = StatusMessageModel.StatusSeverity.Error
				});
				return ShopifyAdminView();
			}

			(string ShopUrl, string Issuer) t;
			try
			{
				t = appClient.ValidateSessionToken(id_token);
			}
			catch (Exception e)
			{
				this.TempData.SetStatusMessageModel(new StatusMessageModel()
				{
					Message = "Failure to validate the session token: " + e.Message,
					Severity = StatusMessageModel.StatusSeverity.Error
				});
				return ShopifyAdminView();
			}

			AccessTokenResponse accessToken;
			try
			{
				accessToken = await appClient.GetAccessToken(t.ShopUrl, id_token);
			}
			catch (Exception e)
			{
				this.TempData.SetStatusMessageModel(new StatusMessageModel()
				{
					Message = "Failure to get the access token from shopify: " + e.Message,
					Severity = StatusMessageModel.StatusSeverity.Error
				});
				return ShopifyAdminView();
			}

			var vm = new ShopifyAdminViewModel() { ShopName = GetShopName(t.ShopUrl) };
			var settings = await _storeRepo.GetSettingAsync<ShopifyStoreSettings>(storeId, ShopifyStoreSettings.SettingsName) ?? new ShopifyStoreSettings(); // Should not be null as we have appClient
			if (settings.Setup?.ShopUrl is null || settings.Setup?.AccessToken is null)
			{
				this.TempData.SetStatusMessageModel(new StatusMessageModel()
				{
					Message = "Shopify plugin successfully configured",
					Severity = StatusMessageModel.StatusSeverity.Success
				});
				settings.Setup ??= new ();
				settings.Setup.ShopUrl = t.ShopUrl;
				settings.Setup.AccessToken = accessToken.AccessToken;
				vm.Configured = true;
				await _storeRepo.UpdateSetting(storeId, ShopifyStoreSettings.SettingsName, settings);
			}
			else
			{
				if (settings.Setup?.ShopUrl != t.ShopUrl)
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
					vm.Configured = true;
					if (settings.Setup?.AccessToken != accessToken.AccessToken)
					{
						settings.Setup ??= new ();
						settings.Setup.AccessToken = accessToken.AccessToken;
						await _storeRepo.UpdateSetting(storeId, ShopifyStoreSettings.SettingsName, settings);
					}
				}
			}
			return ShopifyAdminView(vm);
		}
		return RedirectToAction(nameof(Settings), new { storeId });
	}

	private string? GetShopName(string? shopUrl) => shopUrl?.Split('.').FirstOrDefault()?.Replace("https://", "");

	private ViewResult ShopifyAdminView(ShopifyAdminViewModel? vm = null) => View("/Views/UIShopify/ShopifyAdmin.cshtml", vm ?? new());

	[Route("~/stores/{storeId}/plugins/shopify-v2/settings")]
	[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
	public async Task<IActionResult> Settings(string storeId,
			ShopifySettingsViewModel vm, [FromForm] string? command = null)
	{
		var settings = await _storeRepo.GetSettingAsync<ShopifyStoreSettings>(storeId, ShopifyStoreSettings.SettingsName) ?? new();
		if (command == "SaveAppCredentials")
		{
			vm.ClientId ??= "";
			vm.ClientId = vm.ClientId.Trim();
			if (!Regex.IsMatch(vm.ClientId, "[a-f0-9]{32,32}"))
			{
				ModelState.AddModelError(nameof(vm.ClientId), "Invalid client id");
			}
			vm.ClientSecret ??= "";
			vm.ClientSecret = vm.ClientSecret.Trim();
			if (!Regex.IsMatch(vm.ClientSecret, "[a-f0-9]{32,32}"))
			{
				ModelState.AddModelError(nameof(vm.ClientSecret), "Invalid client secret");
			}
			if (!ModelState.IsValid)
				return View("/Views/UIShopify/Settings.cshtml", vm);
			settings.Setup = new()
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
			settings.Setup = null;
			// We do not reset `settings.PreferredAppName` on purpose.
			// The name is just cosmetic, the user who resets probably just want to setup again
			// the same app.
			await _storeRepo.UpdateSetting<ShopifyStoreSettings>(storeId, ShopifyStoreSettings.SettingsName, settings);
			this.TempData.SetStatusMessageModel(new StatusMessageModel()
			{
				Message = "App settings reset",
				Severity = StatusMessageModel.StatusSeverity.Success
			});
			return RedirectToAction(nameof(Settings), new { storeId });
		}
		else // (command is null)
		{
			return View("/Views/UIShopify/Settings.cshtml", new ShopifySettingsViewModel()
			{
				ClientId = settings.Setup?.ClientId,
				ClientSecret = settings.Setup?.ClientSecret,
				ShopUrl = settings.Setup?.ShopUrl,
				ShopName = GetShopName(settings.Setup?.ShopUrl),
				ClientCredsConfigured = settings.Setup is { ClientId: {}, ClientSecret: {} },
				AppDeployed = settings.Setup is { DeployedCommit: {} },
				AppInstalled = settings.Setup is { AccessToken: {} },
				AppName = settings.PreferredAppName ?? ShopifyStoreSettings.DefaultAppName,
				Step = settings switch
				{
					{ Setup: null } or { Setup: { ClientId: null, ClientSecret: null } } => ShopifySettingsViewModel.State.WaitingClientCreds,
					{ Setup: { DeployedCommit: null } } => ShopifySettingsViewModel.State.WaitingForDeploy,
					{ Setup: { AccessToken: null } } => ShopifySettingsViewModel.State.WaitingForInstall,
					_ => ShopifySettingsViewModel.State.Done
				}
			});
		}
	}

    static AsyncDuplicateLock OrderLocks = new AsyncDuplicateLock();
    [AllowAnonymous]
	[EnableCors(CorsPolicies.All)]
	[HttpGet("~/stores/{storeId}/plugins/shopify-v2/checkout")]
    public async Task<IActionResult> Checkout(string storeId, string? checkout_token, CancellationToken cancellationToken, bool redirect = true)
    {
        if (checkout_token is null)
            return BadRequest("Invalid checkout token");
        var client = await this.ShopifyClientFactory.CreateAPIClient(storeId);
        if (client is null)
            return BadRequest("Shopify plugin isn't configured properly");
        var order = await client.GetOrderByCheckoutToken(checkout_token, true);
        var store = await _storeRepo.FindStore(storeId);
        if (order is null || store is null)
            return BadRequest("Invalid checkout token");

        var containsKeyword = order.PaymentGatewayNames.Any(pgName => ShopifyHostedService.IsBTCPayServerGateway(pgName));
        if (!containsKeyword)
            return NotFound("Order wasn't fulfilled with BTCPay Server payment option");

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
        var currentInvoice = orderInvoices.FirstOrDefault();
        if (currentInvoice != null)
            return redirect ? RedirectToInvoiceCheckout(currentInvoice.Id) : Ok();

        var baseTx = order.Transactions.FirstOrDefault(t => t is { Kind: "SALE", ManuallyCapturable: true });
        if (baseTx is null)
            return BadRequest("The shopify order is not capturable");
        var settings = await _storeRepo.GetSettingAsync<ShopifyStoreSettings>(storeId, ShopifyStoreSettings.SettingsName);
        var amount = order.TotalOutstandingSet.PresentmentMoney;
        InvoiceEntity invoice;
        try
        {
	        invoice = await _invoiceController.CreateInvoiceCoreRaw(
		        new CreateInvoiceRequest()
		        {
			        Amount = amount.Amount,
			        Currency = amount.CurrencyCode,
			        Metadata = new JObject
			        {
				        ["orderId"] = order.Name,
				        ["orderUrl"] = GetOrderUrl(settings?.Setup?.ShopUrl, orderId),
				        ["shopifyOrderId"] = orderId,
				        ["shopifyOrderName"] = order.Name,
				        ["gateway"] = baseTx.Gateway
			        },
			        AdditionalSearchTerms =
			        [
				        order.Name,
				        orderId.ToString(CultureInfo.InvariantCulture),
				        searchTerm
			        ],
			        Checkout = new()
			        {
				        RedirectURL = order.StatusPageUrl
			        }
		        }, store,
		        Request.GetAbsoluteRoot(), [searchTerm], cancellationToken);
        }
        catch (BitpayHttpException e)
        {
	        return BadRequest(e.Message);
        }

        await client.UpdateOrderMetafields(new()
		{
			Id = ShopifyId.Order(orderId),
			Metafields = [
				new()
				{
					Namespace = "custom",
					Key = "btcpay_checkout_url",
					Type = "single_line_text_field",
					Value = Url.Action(nameof(Checkout), "UIShopifyV2", new { storeId, checkout_token }, Request.Scheme)
				}
			]
		});
        return redirect ? RedirectToInvoiceCheckout(invoice.Id) : Ok();
    }

    private string? GetOrderUrl(string? shopUrl, long shopifyOrderId)
	{
		var shopName = GetShopName(shopUrl);
		if (shopName is null)
			return null;
		return $"https://admin.shopify.com/store/{shopName}/orders/{shopifyOrderId}";
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
	[HttpPost("~/stores/{storeId}/plugins/shopify-v2/webhooks")]
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
