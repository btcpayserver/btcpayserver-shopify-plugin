#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
using BTCPayServer.Plugins.ShopifyPlugin.Clients;
using BTCPayServer.Plugins.ShopifyPlugin.Services;
using BTCPayServer.Plugins.ShopifyPlugin.ViewModels;
using BTCPayServer.Rating;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Mails;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json.Linq;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Plugins.ShopifyPlugin;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewProfile)]
[AutoValidateAntiforgeryToken]
public class UIShopifyV2Controller : Controller
{
    private readonly RateFetcher _rateProvider;
    private readonly StoreRepository _storeRepo;
    private readonly EmailService _emailService;
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
        EmailService emailService,
        IConfiguration configuration,
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
		_emailService = emailService;
		_rateProvider = rateProvider;
		_defaultRules = defaultRules;
        _payoutHandlers = payoutHandlers;
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
            var client = await this.ShopifyClientFactory.CreateAPIClient(storeId);
            if (client is not null)
			{
				var order = await client.GetOrder(7304009351501);
            }
            var invoice = await _invoiceRepository.GetInvoices(new InvoiceQuery {  StoreId = new[] { storeId }, TextSearch = "7303905182029" });

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

    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [HttpPost("~/stores/{storeId}/plugins/shopify-v2/btcpay/webhook/order-transactions")]
    public async Task<IActionResult> OrderTransactionsWebhook([FromRoute] string storeId)
    {
        var pmi = PayoutMethodId.Parse("BTC-CHAIN");
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var requestBody = await reader.ReadToEndAsync();
        Request.Body.Position = 0;

        var hmacHeader = Request.Headers["X-Shopify-Hmac-Sha256"].FirstOrDefault();
        if (string.IsNullOrEmpty(hmacHeader))
            return Unauthorized(new { error = "Missing HMAC header" });

        try
        {
            var settings = await _storeRepo.GetSettingAsync<ShopifyStoreSettings>(storeId, ShopifyStoreSettings.SettingsName);
            var store = await _storeRepo.FindStore(storeId);
            var client = await this.ShopifyClientFactory.CreateAPIClient(storeId);
            if (string.IsNullOrEmpty(settings?.Setup?.WebhookSignature) || store == null || client == null)
                return BadRequest("Store isn't registered or refunds isn't configured with shopify plugin");

            if (!ValidateShopifyWebhook(requestBody, hmacHeader, settings.Setup.WebhookSignature))
                return Unauthorized(new { error = "Invalid HMAC signature" });

            var payload = JObject.Parse(requestBody);
            if (payload?["order_id"] == null)
                return BadRequest(new { error = "Payload does not contain order_id" });

            if (!long.TryParse(payload["order_id"]!.ToString(), out var shopifyOrderId))
                return BadRequest(new { error = "Order ID is invalid" });

            if (await client.GetOrder(shopifyOrderId, true) is not { } order)
                return BadRequest(new { error = "Order is invalid" });

            var emailSender = await _emailSenderFactory.GetEmailSender(storeId);
            var isEmailSettingsConfigured = (await emailSender.GetEmailSettings() ?? new EmailSettings()).IsComplete();
			if (!isEmailSettingsConfigured)
                return BadRequest(new { error = "Email Server not configured for store" });

            // Todo: Shopify meta data so that the get invoice would be based on metadata and not text search
            var orderInvoices = await _invoiceRepository.GetInvoices(new InvoiceQuery
            {
                StoreId = new[] { storeId },
                TextSearch = shopifyOrderId.ToString()
            });
            var invoice = orderInvoices?.FirstOrDefault();
            if (invoice == null)
                return BadRequest(new { error = "No invoice matching this criteria" });

            if (invoice.Refunds.Any() || !invoice.GetInvoiceState().CanRefund())
            return BadRequest(new
            {
                error = "Cannot process invoice refund at the moment, as invoice either has active refunds or invoice state cannot process refund"
            });

            var supportedPmis = _payoutHandlers.GetSupportedPayoutMethods(store);
            if (!supportedPmis.Contains(pmi))
                return BadRequest("Invalid payout method");

            var paymentMethodId = invoice.GetClosestPaymentMethodId(new[] { pmi });
            var paymentMethod = paymentMethodId == null ? null : invoice.GetPaymentPrompt(paymentMethodId);
            if (paymentMethod?.Currency == null)
                return BadRequest("Invalid payout method");

            int ppDivisibility = paymentMethod.Divisibility;
            var accounting = paymentMethod.Calculate();
            var cryptoPaid = accounting.Paid;
            var dueAmount = accounting.TotalDue;
            var cdCurrency = _currencyNameTable.GetCurrencyData(invoice.Currency, true);

            RateResult rateResult = await _rateProvider.FetchRate(new CurrencyPair(paymentMethod.Currency, invoice.Currency),
                store.GetStoreBlob().GetRateRules(_defaultRules), new StoreIdRateContext(store.Id), CancellationToken.None);
            if (rateResult.BidAsk == null)
                return BadRequest($"to fetch rate {rateResult.EvaluatedRule}");

            var paidCurrency = Math.Round(cryptoPaid * paymentMethod.Rate, cdCurrency.Divisibility);
            CreatePullPayment createPullPayment = new CreatePullPayment
            {
                Name = $"Refund {invoice.Id}",
                StoreId = invoice.StoreId,
                PayoutMethods = new[] { pmi },
                BOLT11Expiration = store.GetStoreBlob().RefundBOLT11Expiration
            };
            switch (settings.Setup.SelectedRefundOption)
            {
                case "RateThen":
                    createPullPayment.Currency = paymentMethod.Currency;
                    createPullPayment.Amount = cryptoPaid.RoundToSignificant(paymentMethod.Divisibility);
                    createPullPayment.AutoApproveClaims = true;
                    break;

                case "CurrentRate":
                    createPullPayment.Currency = paymentMethod.Currency;
                    createPullPayment.Amount = Math.Round(paidCurrency / rateResult.BidAsk.Bid, paymentMethod.Divisibility);
                    createPullPayment.AutoApproveClaims = true;
                    break;

                case "Fiat":
                    ppDivisibility = cdCurrency.Divisibility;
                    createPullPayment.Currency = invoice.Currency;
                    createPullPayment.Amount = paidCurrency;
                    createPullPayment.AutoApproveClaims = false;
                    break;

                default:
					return BadRequest("No refund option configured in plugin");
            }

            if (settings.Setup.SpreadPercentage is > 0 and <= 100)
            {
                var reduceByAmount = createPullPayment.Amount * (settings.Setup.SpreadPercentage / 100);
                createPullPayment.Amount = Math.Round(createPullPayment.Amount - reduceByAmount, ppDivisibility);
            }
            await using var ctx = _dbContextFactory.CreateContext();
            var ppId = await _paymentHostedService.CreatePullPayment(createPullPayment);
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
            await _emailService.SendRefundOrderEmail(storeId, order.Customer?.DefaultEmailAddress?.EmailAddress, shopifyOrderId.ToString(), claimUrl);
            return Ok();
        }
        catch (Exception)
        {
			return NotFound();
        }
    }

    private bool ValidateShopifyWebhook(string requestBody, string hmacHeader, string secret)
    {
		if (string.IsNullOrEmpty(requestBody) || string.IsNullOrEmpty(secret)) return false;
        try
        {
            var secretBytes = Encoding.UTF8.GetBytes(secret);
            using var hmac = new HMACSHA256(secretBytes);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(requestBody));
            var computedHmac = Convert.ToBase64String(hash);
            return hmacHeader == computedHmac;
        }
        catch
        {
            return false;
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
				        ["orderId"] = orderId,
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
