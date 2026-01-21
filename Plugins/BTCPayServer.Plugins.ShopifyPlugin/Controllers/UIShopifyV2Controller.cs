#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.Filters;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning.LndHub;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.Emails;
using BTCPayServer.Plugins.Emails.Controllers;
using BTCPayServer.Plugins.Emails.HostedServices;
using BTCPayServer.Plugins.Emails.Services;
using BTCPayServer.Plugins.ShopifyPlugin.Clients;
using BTCPayServer.Plugins.ShopifyPlugin.Services;
using BTCPayServer.Plugins.ShopifyPlugin.ViewModels;
using BTCPayServer.Rating;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Plugins.ShopifyPlugin;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewProfile)]
[AutoValidateAntiforgeryToken]
public class UIShopifyV2Controller : Controller
{
    private readonly RateFetcher _rateProvider;
    private readonly StoreRepository _storeRepo;
    private readonly EventAggregator _eventAggregator;
    private readonly CurrencyNameTable _currencyNameTable;
    private readonly DefaultRulesCollection _defaultRules;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly EmailSenderFactory _emailSenderFactory;
    private readonly UIInvoiceController _invoiceController;
    private readonly PayoutMethodHandlerDictionary _payoutHandlers;
    private readonly ApplicationDbContextFactory _dbContextFactory;
    private readonly PullPaymentHostedService _paymentHostedService;

    public UIShopifyV2Controller
		(StoreRepository storeRepo,
        RateFetcher rateProvider,
        IConfiguration configuration,
        EventAggregator eventAggregator,
        CurrencyNameTable currencyNameTable,
        InvoiceRepository invoiceRepository,
        DefaultRulesCollection defaultRules,
        UIInvoiceController invoiceController,
        EmailSenderFactory emailSenderFactory,
        ShopifyClientFactory shopifyClientFactory,
        PayoutMethodHandlerDictionary payoutHandlers,
        ApplicationDbContextFactory dbContextFactory,
        PullPaymentHostedService paymentHostedService)
	{
		_storeRepo = storeRepo;
		_rateProvider = rateProvider;
		_defaultRules = defaultRules;
        _payoutHandlers = payoutHandlers;
        _eventAggregator = eventAggregator;
        _dbContextFactory = dbContextFactory;
		_currencyNameTable = currencyNameTable;
        _invoiceRepository = invoiceRepository;
		_invoiceController = invoiceController;
        _emailSenderFactory = emailSenderFactory;
        ShopifyClientFactory = shopifyClientFactory;
        _paymentHostedService = paymentHostedService;
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
				Step = ShopifySetupStep(settings)
            });
		}
	}

    private ShopifySettingsViewModel.State ShopifySetupStep(ShopifyStoreSettings settings)
    {
        return settings switch
        {
            { Setup: null } or { Setup: { ClientId: null, ClientSecret: null } } => ShopifySettingsViewModel.State.WaitingClientCreds,
            { Setup: { DeployedCommit: null } } => ShopifySettingsViewModel.State.WaitingForDeploy,
            { Setup: { AccessToken: null } } => ShopifySettingsViewModel.State.WaitingForInstall,
            _ => ShopifySettingsViewModel.State.Done
        };
    }

    [HttpGet("~/stores/{storeId}/plugins/shopify-v2/refunds/settings")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> RefundSettings(string storeId)
    {
        var settings = await _storeRepo.GetSettingAsync<ShopifyStoreSettings>(storeId, ShopifyStoreSettings.SettingsName) ?? new();
        settings.Setup ??= new();
        var setupState = ShopifySetupStep(settings);
        if (setupState != ShopifySettingsViewModel.State.Done)
        {
            this.TempData.SetStatusMessageModel(new StatusMessageModel()
            {
                Message = "You need to complete your plugin settings setup first",
                Severity = StatusMessageModel.StatusSeverity.Warning
            });
            return RedirectToAction(nameof(Settings), new { storeId });
        }

        var client = await this.ShopifyClientFactory.CreateAPIClient(storeId);
        if (client is null)
        {
            this.TempData.SetStatusMessageModel(new StatusMessageModel()
            {
                Message = "Shopify plugin isn't configured properly",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(Settings), new { storeId });
        }
        var shopifyAppScopes = await client.GetGrantedAccessScopes();
        var hasRequiredScope = ShopifyHostedService.HasRequiredShopifyScopes(shopifyAppScopes);
        if (!hasRequiredScope)
        {
            this.TempData.SetStatusMessageModel(new StatusMessageModel()
            {
                Message = "The Shopify plugin and app is missing required permissions for refunds. Kindly upgrade the shopify fragment to the latest and redeploy the app (Step 2)",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(Settings), new { storeId });
        }

        var emailSender = await _emailSenderFactory.GetEmailSender(storeId);
        var isEmailSettingsConfigured = (await emailSender.GetEmailSettings() ?? new EmailSettings()).IsComplete();
        ViewData["StoreEmailSettingsConfigured"] = isEmailSettingsConfigured;
        if (!isEmailSettingsConfigured)
        {
            this.TempData.SetStatusMessageModel(new StatusMessageModel()
            {
                Html = $"Kindly <a href='{Url.Action(action: nameof(UIStoresEmailController.StoreEmailSettings), controller: "UIStoresEmail",
                    values: new { area = EmailsPlugin.Area, storeId })}' class='alert-link'>configure Email SMTP</a> to configure shopify refunds",
                Severity = StatusMessageModel.StatusSeverity.Warning
            });
        }
        Enum.TryParse<ShopifyRefundWebhookSettingsViewModel.RefundOption>(settings.Setup?.SelectedRefundOption, ignoreCase: true, out var refundOption);
        var vm = new ShopifyRefundWebhookSettingsViewModel
        {
            WebhookUrl = Url.Action(nameof(Webhook), "UIShopifyV2", new { storeId }, Request.Scheme),
            WebhookSecret = settings.Setup?.WebhookSecret,
            SpreadPercentage = settings.Setup.SpreadPercentage,
            SelectedRefundOption = refundOption
        };
        return View("/Views/UIShopify/RefundSettings.cshtml", vm);
    }

    [HttpPost("~/stores/{storeId}/plugins/shopify-v2/refunds/settings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefundSettings(string storeId, ShopifyRefundWebhookSettingsViewModel vm)
    {
        if (!ModelState.IsValid)
        {
            vm.WebhookUrl = Url.Action(nameof(Webhook), "UIShopifyV2", new { storeId }, Request.Scheme);
            return View("/Views/UIShopify/RefundSettings.cshtml", vm);
        }
        var settings = await _storeRepo.GetSettingAsync<ShopifyStoreSettings>(storeId, ShopifyStoreSettings.SettingsName) ?? new();
        settings.Setup ??= new();
        settings.Setup.WebhookSecret = vm.WebhookSecret;
        settings.Setup.SpreadPercentage = vm.SpreadPercentage;
        settings.Setup.SelectedRefundOption = vm.SelectedRefundOption.ToString();
        await _storeRepo.UpdateSetting<ShopifyStoreSettings>(storeId, ShopifyStoreSettings.SettingsName, settings);
        this.TempData.SetStatusMessageModel(new StatusMessageModel()
        {
            Message = "Refund settings saved successfully",
            Severity = StatusMessageModel.StatusSeverity.Success
        });
        return RedirectToAction(nameof(RefundSettings), new { storeId });
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
				},
                new()
                {
                    Namespace = "custom",
                    Key = "btcpay_invoice_id",
                    Type = "single_line_text_field",
                    Value = invoice.Id
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
		if (GetHeader("X-Shopify-Hmac-SHA256") is string hmac && GetHeader("X-Shopify-Topic") is string topic)
			return new WebhookInfo(hmac, topic);
		return null;
	}

    static readonly AsyncDuplicateLock RefundLocks = new AsyncDuplicateLock();
    [AllowAnonymous]
	[IgnoreAntiforgeryToken]
	[HttpPost("~/stores/{storeId}/plugins/shopify-v2/webhooks")]
	// Handles refunds, plus all forms of webhook... plus shopify requires to still listen to it...
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

        if (string.IsNullOrEmpty(settings?.Setup?.WebhookSecret))
            return BadRequest("Webhook secret not saved yet");

        var client = await this.ShopifyClientFactory.CreateAppClient(storeId);
        if (client is null)
            return NotFound();
        if (!client.VerifyWebhookSignature(requestBody, webhookInfo.HMac, settings.Setup.WebhookSecret))
            return Unauthorized("Invalid HMAC signature");

        // https://shopify.dev/docs/api/webhooks?reference=toml#list-of-topics-orders/create
        //if (webhookInfo.FullTopicName == "orders/create")
        //{
        //	var order = JsonConvert.DeserializeObject<dynamic>(requestBody)!;
        //	checkoutTokens.Add(new(storeId, (string)order.checkout_token), (long)order.id);
        //}

        if (webhookInfo.FullTopicName == "refunds/create")
        {
            return await HandleRefundCreateWebhook(storeId, requestBody, settings);
        }
        return Ok();
    }

	private async Task<IActionResult> HandleRefundCreateWebhook(string storeId, string requestBody, ShopifyStoreSettings settings)
    {
        var store = await _storeRepo.FindStore(storeId);
        var client = await this.ShopifyClientFactory.CreateAPIClient(storeId);
        if (settings?.Setup == null || store == null || client == null)
            return BadRequest("Store isn't registered or refunds isn't configured with shopify plugin");

        // https://shopify.dev/docs/api/webhooks/latest?accordionItem=webhooks-refunds-create&reference=toml
        var refundPayload = JsonConvert.DeserializeObject<ShopifyRefundWebhook>(requestBody);
        if (refundPayload == null || refundPayload.OrderId <= 0)
            return BadRequest("Invalid refund payload");

        // refund_line_items contains the actual product to be refunded refunds
        var lineItemsRefundAmount = refundPayload.RefundLineItems.Sum(item => Math.Abs(item.Subtotal));

        // order_adjustments contains shipping refunds, restocking fees, and discrepancies
        var adjustmentsRefundAmount = refundPayload.OrderAdjustments
            .Where(adj => adj.RefundId.HasValue && adj.RefundId.Value > 0).Sum(adj => Math.Abs(adj.Amount));

        var totalRefundAmount = lineItemsRefundAmount + adjustmentsRefundAmount;
        if (totalRefundAmount <= 0)
            return BadRequest("No valid refund amount found in order adjustments");

        var emailSender = await _emailSenderFactory.GetEmailSender(storeId);
        var isEmailSettingsConfigured = (await emailSender.GetEmailSettings() ?? new EmailSettings()).IsComplete();
        if (!isEmailSettingsConfigured)
            return BadRequest("Email Server not configured for store");

        using var l = await RefundLocks.LockAsync(refundPayload.OrderId, CancellationToken.None);

        if (await client.GetOrder(refundPayload.OrderId, true) is not { } order)
            return BadRequest("Order is invalid");

        var containsKeyword = order.PaymentGatewayNames.Any(pgName => ShopifyHostedService.IsBTCPayServerGateway(pgName));
        if (!containsKeyword)
            return NotFound("Order wasn't fulfilled with BTCPay Server payment option");

        if (order.BtcpayInvoiceId == null || string.IsNullOrEmpty(order.BtcpayInvoiceId?.Value))
            return NotFound("BTCPay invoice ID not found in order metadata");

        var invoice = await _invoiceRepository.GetInvoice(order.BtcpayInvoiceId.Value);
        if (invoice == null)
            return BadRequest("No invoice matching this criteria");

        if ((invoice.Refunds != null && invoice.Refunds.Any()) || !invoice.GetInvoiceState().CanRefund())
            return BadRequest("Cannot process invoice refund at the moment, as invoice either has active refunds or invoice state cannot process refund");

        var supportedPmis = _payoutHandlers.GetSupportedPayoutMethods(store)?.ToArray();
        if (supportedPmis == null || !supportedPmis.Any())
            return BadRequest("No supported payout methods configured for store");

        var paymentMethodId = invoice.GetClosestPaymentMethodId(supportedPmis);
        var paymentMethod = paymentMethodId == null ? null : invoice.GetPaymentPrompt(paymentMethodId);
        if (paymentMethod?.Currency == null)
            return BadRequest("Invalid payout method");

        RateResult rateResult = await _rateProvider.FetchRate(new CurrencyPair(paymentMethod.Currency, invoice.Currency),
            store.GetStoreBlob().GetRateRules(_defaultRules), new StoreIdRateContext(store.Id), CancellationToken.None);
        if (rateResult.BidAsk == null)
            return BadRequest($"Unable to fetch rate {rateResult.EvaluatedRule}");

        CreatePullPaymentRequest createPullPayment = new CreatePullPaymentRequest
        {
            Name = $"Refund {invoice.Id}",
            PayoutMethods = supportedPmis.Select(c => c.ToString()).ToArray(),
            AutoApproveClaims = true,
            BOLT11Expiration = store.GetStoreBlob().RefundBOLT11Expiration,
            Description = $"Refund for shopify order {refundPayload.OrderId}. Amount refunded {totalRefundAmount} {invoice.Currency}",
        };
        switch (settings.Setup.SelectedRefundOption)
        {
            case "RateThen":
                createPullPayment.Currency = paymentMethod.Currency;
                createPullPayment.Amount = Math.Round(totalRefundAmount / paymentMethod.Rate, paymentMethod.Divisibility);
                break;

            case "CurrentRate":
                createPullPayment.Currency = paymentMethod.Currency;
                createPullPayment.Amount = Math.Round(totalRefundAmount / rateResult.BidAsk.Bid, paymentMethod.Divisibility);
                break;

            default:
                return BadRequest("No refund option configured in plugin");
        }

        if (settings.Setup.SpreadPercentage is > 0 and <= 99)
        {
            var reduceByAmount = createPullPayment.Amount * (settings.Setup.SpreadPercentage / 100);
            createPullPayment.Amount = Math.Round(createPullPayment.Amount - reduceByAmount, paymentMethod.Divisibility);
            if (createPullPayment.Amount <= 0)
            {
                return BadRequest($"Refund amount becomes zero or negative after applying spread percentage of {settings.Setup.SpreadPercentage}%. Please reduce the spread percentage.");
            }
        }
        await using var ctx = _dbContextFactory.CreateContext();
        var ppId = await _paymentHostedService.CreatePullPayment(store, createPullPayment);
        ctx.Refunds.Add(new RefundData
        {
            InvoiceDataId = invoice.Id,
            PullPaymentDataId = ppId
        });
        await ctx.SaveChangesAsync();

        var claimUrl = Url.Action(
            action: nameof(UIPullPaymentController.ViewPullPayment),
            controller: "UIPullPayment",
            values: new { pullPaymentId = ppId },
            protocol: Request.Scheme
        );
        var customer = order.Customer;
        var model = new JObject
        {
            ["RefundLink"] = claimUrl,
            ["Order"] = new JObject
            {
                ["Id"] = refundPayload.OrderId.ToString()
            },
            ["Customer"] = new JObject
            {
                ["Email"] = customer?.DefaultEmailAddress?.EmailAddress ?? "",
                ["Name"] = customer?.DisplayName ?? ""
            }
        };
        _eventAggregator.Publish(new TriggerEvent(storeId, ShopifyMailTriggers.RefundCreated, model, null));
        return Ok();
    }
}
