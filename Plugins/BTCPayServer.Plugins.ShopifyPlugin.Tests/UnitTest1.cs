

using BTCPayServer.Plugins.ShopifyPlugin.Clients;
using BTCPayServer.Plugins.ShopifyPlugin.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace BTCPayServer.Plugins.ShopifyPlugin.Tests
{
	public class UnitTest1
	{
		[Fact]
		// This test fail if the token_id is too old
		public async Task CanRequestOfflineToken()
		{
			ShopifyAppClient client = CreateAppClient();
			// This token is probably too old now. You can get that from the query string when "Go to the app" on shopify store's admin interface
			var token_id = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJodHRwczpcL1wvYW5qMWJzLXB1Lm15c2hvcGlmeS5jb21cL2FkbWluIiwiZGVzdCI6Imh0dHBzOlwvXC9hbmoxYnMtcHUubXlzaG9waWZ5LmNvbSIsImF1ZCI6ImMyZjUyZDBkOWYwNDBjOGRlMmU5MmQ5NzhhYzcwNmE4Iiwic3ViIjoiMTE2Mzc0MzM5OTA5IiwiZXhwIjoxNzM5Nzc0Nzk1LCJuYmYiOjE3Mzk3NzQ3MzUsImlhdCI6MTczOTc3NDczNSwianRpIjoiYTVlMWE1ZjEtN2JmOS00NzE0LWFhMzQtYWNmYTAyNmIxMjE5Iiwic2lkIjoiNTYwMGUxOGQtNzNmNS00ZjM4LWFhMTgtMTM5NzZiNjZkODllIiwic2lnIjoiODVmNjRmNzgxODI1ODZmYWI4ZWI0NTU0NjY0NWY3OGYxNjkxMDA5ZTU4MDkzYTljZWVhYzkyZTQ5YzA1ZTM0MCJ9.jjjERtdCAXGxL5WB0YlyaorVWgF190bU6UoyVeyfN0U";
			var v = client.ValidateSessionToken(token_id);
			var access = await client.GetAccessToken(v.ShopUrl, token_id);
		}

		[Fact]
		public void CanValidateTokenId()
		{
			ShopifyAppClient client = CreateAppClient();
			var token_id = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJodHRwczpcL1wvYW5qMWJzLXB1Lm15c2hvcGlmeS5jb21cL2FkbWluIiwiZGVzdCI6Imh0dHBzOlwvXC9hbmoxYnMtcHUubXlzaG9waWZ5LmNvbSIsImF1ZCI6ImMyZjUyZDBkOWYwNDBjOGRlMmU5MmQ5NzhhYzcwNmE4Iiwic3ViIjoiMTE2Mzc0MzM5OTA5IiwiZXhwIjoxNzM5Nzc0Nzk1LCJuYmYiOjE3Mzk3NzQ3MzUsImlhdCI6MTczOTc3NDczNSwianRpIjoiYTVlMWE1ZjEtN2JmOS00NzE0LWFhMzQtYWNmYTAyNmIxMjE5Iiwic2lkIjoiNTYwMGUxOGQtNzNmNS00ZjM4LWFhMTgtMTM5NzZiNjZkODllIiwic2lnIjoiODVmNjRmNzgxODI1ODZmYWI4ZWI0NTU0NjY0NWY3OGYxNjkxMDA5ZTU4MDkzYTljZWVhYzkyZTQ5YzA1ZTM0MCJ9.jjjERtdCAXGxL5WB0YlyaorVWgF190bU6UoyVeyfN0U";
			var v = client.ValidateSessionToken(token_id, skipLifeTimeCheck: true);
		}

		[Fact]
		public void CanValidateQueryString()
		{
			ShopifyAppClient client = CreateAppClient();
			var queryString = "embedded=1&hmac=76f8f87414ad8fd7feb0f38f127b8aeee18faa9964804d5332cdd51d1317aba1&host=YWRtaW4uc2hvcGlmeS5jb20vc3RvcmUvYW5qMWJzLXB1&id_token=eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJodHRwczpcL1wvYW5qMWJzLXB1Lm15c2hvcGlmeS5jb21cL2FkbWluIiwiZGVzdCI6Imh0dHBzOlwvXC9hbmoxYnMtcHUubXlzaG9waWZ5LmNvbSIsImF1ZCI6ImMyZjUyZDBkOWYwNDBjOGRlMmU5MmQ5NzhhYzcwNmE4Iiwic3ViIjoiMTE2Mzc0MzM5OTA5IiwiZXhwIjoxNzM5Nzc1MzMyLCJuYmYiOjE3Mzk3NzUyNzIsImlhdCI6MTczOTc3NTI3MiwianRpIjoiZTQzMzg0ZTItOTNmMy00NGJjLWFkMjMtMzMxMzVmYWUxOWQyIiwic2lkIjoiNTYwMGUxOGQtNzNmNS00ZjM4LWFhMTgtMTM5NzZiNjZkODllIiwic2lnIjoiZDI4MzQ2OTc2Zjg4NjE1M2EwODUzNDNjNTYzMDJlNWM5MmQ1ZGE5NTE3NjJkZjg5MWFiMGZmYmQ4YTRmMzYwNSJ9.ZYzI8TGAd0jZ9C1itKPjgfIESokqrP8CTo36v7dxSwE&locale=en&session=4873652bcf48a37f927137f1a1121dfdafd70bebbbdd1490c23e721a9337b5e8&shop=anj1bs-pu.myshopify.com&timestamp=1739775272";
			Assert.True(client.ValidateQueryString(queryString, skipLifeTimeCheck: true));
		}

		[Fact]
		public async Task CanQueryOrderByCheckoutToken()
		{
			ShopifyApiClient client = CreateApiClient();
			var checkoutToken = "ac0565ed327a6fc0011d0dbed8186c16";
			var order = await client.GetOrderByCheckoutToken(checkoutToken);
			Assert.NotNull(order);
			Assert.Null(await client.GetOrderByCheckoutToken("lol"));
		}

		[Fact]
		public async Task CanQueryOrder2()
		{
			ShopifyApiClient client = CreateApiClient();
			// 9756325314825L
			// 9756273606921L
			// 9758178115849L
			var o = await client.GetOrder(9759887556873L, true);
			Assert.NotNull(o);
			await client.CancelOrder(new()
			{
				OrderId = o.Id,
				Refund = true,
				Restock = true,
				Reason =  OrderCancelReason.DECLINED
			});
			// await client.CaptureOrder(new()
			// {
			// 	Amount = 39.0m,
			// 	Currency = "USD",
			// 	Id = o.Id,
			// 	ParentTransactionId = o.Transactions[0].Id
			// });
		}

		[Fact]
		public async Task CanQueryOrder()
		{
			ShopifyApiClient client = CreateApiClient();
			var draftTemplate = 1203089604873L;

			var newDraft = await client.DuplicateOrder(draftTemplate);
			var orderId = await client.CompleteDraftOrder(newDraft.Id);


			var o = await client.GetOrder(orderId.Id);
			Assert.Null(o.Transactions);
			Assert.Equal(ShopifyId.Order(orderId.Id), o.Id);
			Assert.NotNull(o);
			Assert.Null((await client.GetOrder(1)));

			o = await client.GetOrder(orderId.Id, true);
			Assert.NotNull(o.Transactions);

			await client.CaptureOrder(new CaptureOrderRequest()
			{
				Id = o.Id,
				Amount = o.TotalOutstandingSet.ShopMoney.Amount,
				Currency = o.TotalOutstandingSet.ShopMoney.CurrencyCode,
				ParentTransactionId = o.Transactions.First().Id
			});
			o = await client.GetOrder(orderId.Id, true);

			var cancelReq = new CancelOrderRequest()
			{
				OrderId = o.Id,
				NotifyCustomer = false,
				Reason = OrderCancelReason.DECLINED,
				Refund = false,
				Restock = true,
				StaffNote = "lol"
			};
			await client.CancelOrder(cancelReq);
			// Doesn't throw if done fast enough... but with a delay that throw as expected
			// await Assert.ThrowsAsync<ShopifyApiException>(() => client.CancelOrder(cancelReq));


		}

		private ShopifyAppClient CreateAppClient()
		{
			// dotnet user-secrets set "API_CLIENT" "YOUR_API_CLIENT"
			// dotnet user-secrets set "API_SECRET" "YOUR_API_SECRET"
			var conf = GetConf();
			if (conf["API_CLIENT"] is not string apiClient)
				throw new InvalidOperationException("Please, set your dev environment with: dotnet user-secrets set \"API_CLIENT\" \"YOUR_API_CLIENT\"");
			if (conf["API_SECRET"] is not string apiSecret)
				throw new InvalidOperationException("Please, set your dev environment with: dotnet user-secrets set \"API_SECRET\" \"YOUR_API_SECRET\"");
			return new ShopifyAppClient(new HttpClient(), new ShopifyAppCredentials(apiClient, apiSecret));
		}

		private static IConfigurationRoot GetConf()
		{
			var builder = new ConfigurationBuilder()
							.AddUserSecrets<UnitTest1>();
			var conf = builder.Build();
			return conf;
		}

		private static ShopifyApiClient CreateApiClient()
		{
			// Test need write/read access to order and draft orders
			// write_payment_sessions 
			// See https://shopify.dev/docs/api/payments-apps/2024-04/mutations/paymentSessionResolve

			// dotnet user-secrets set "ACCESS_TOKEN" "YOU_ACCESS_TOKEN"

			ShopifyApiClientCredentials? creds = null;
			var conf = GetConf();
			if (conf["ACCESS_TOKEN"] is string accessToken)
				creds = new ShopifyApiClientCredentials.AccessToken(accessToken);
			if (creds is null && conf["API_KEY"] is string apiKey && conf["API_SECRET"] is string apiSecret)
				creds = new ShopifyApiClientCredentials.Basic(apiKey, apiSecret);
			if (creds is null)
				throw new InvalidOperationException("Please, set your dev environment with: dotnet user-secrets set \"ACCESS_TOKEN\" \"YOUR_ACCESS_TOKEN\" or use API_KEY and API_SECRET");
			if (conf["SHOP_URL"] is not string shopUrl)
				throw new InvalidOperationException("Please, set your dev environment with: dotnet user-secrets set \"SHOP_URL\" \"https://XXXXX.myshopify.com/\"");

			return new ShopifyApiClient(new HttpClient(),
				shopUrl,
				creds);
		}
	}
}