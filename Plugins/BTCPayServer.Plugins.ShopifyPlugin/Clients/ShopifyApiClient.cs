using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;
using Newtonsoft.Json.Serialization;
using BTCPayServer.Plugins.ShopifyPlugin.JsonConverters;
using Newtonsoft.Json.Converters;
using Microsoft.AspNetCore.WebUtilities;
using JArray = Newtonsoft.Json.Linq.JArray;

namespace BTCPayServer.Plugins.ShopifyPlugin.Clients
{
    public class ShopifyAppClient
    {
		private readonly HttpClient _httpClient;
		private readonly ShopifyAppCredentials _credentials;

		public ShopifyAppClient(HttpClient httpClient, ShopifyAppCredentials appCredentials)
		{
			_httpClient = httpClient;
			_credentials = appCredentials;
		}
		/// <summary>
		/// Validate a session token
		/// </summary>
		/// <param name="sessionId"></param>
		/// <returns></returns>
		/// <exception cref="SecurityTokenInvalidIssuerException">storeUrl</exception>
		public (string ShopUrl, string Issuer) ValidateSessionToken(string sessionId, bool skipLifeTimeCheck = false)
		{
			var handler = new JwtSecurityTokenHandler();
			var token = handler.ReadJwtToken(sessionId);
			handler.ValidateToken(sessionId, new TokenValidationParameters()
			{
				ValidateIssuer = false,
				ValidateAudience = true,
				ValidateLifetime = !skipLifeTimeCheck,
				ValidateIssuerSigningKey = true,
				IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_credentials.ClientSecret)),
				ValidAudiences = [_credentials.ClientId],
			}, out _);
			var storeUrl = token.Claims.FirstOrDefault(c => c.Type == "dest")?.Value;
			if (!token.Issuer.StartsWith(storeUrl))
				throw new SecurityTokenInvalidIssuerException("Invalid Issuer");
            return (storeUrl, token.Issuer);
		}
		public async Task<AccessTokenResponse> GetAccessToken(string shopUrl, string sessionId)
		{
			ValidateSessionToken(sessionId);
			var body = new JObject()
			{
				["client_id"] = _credentials.ClientId,
				["client_secret"] = _credentials.ClientSecret,
				["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
				["subject_token"] = sessionId,
				["subject_token_type"] = "urn:ietf:params:oauth:token-type:id_token",
				["requested_token_type"] = "urn:shopify:params:oauth:token-type:offline-access-token"
			};
			var req = new HttpRequestMessage(HttpMethod.Post, $"{shopUrl}/admin/oauth/access_token");
			req.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
			req.Headers.Add("Accept", "application/json");
			using var resp = await _httpClient.SendAsync(req);
			var strResp = await resp.Content?.ReadAsStringAsync();
			if (!resp.IsSuccessStatusCode)
				throw new ShopifyApiException($"Error while getting access token (HTTP {resp.StatusCode}): {strResp}");
			return JsonConvert.DeserializeObject<AccessTokenResponse>(strResp);
		}

		public bool VerifyWebhookSignature(string body, string hmac)
		{
			var keyBytes = Encoding.UTF8.GetBytes(_credentials.ClientSecret);
			using (var hmacObj = new HMACSHA256(keyBytes))
			{
				var hashBytes = hmacObj.ComputeHash(Encoding.UTF8.GetBytes(body));
				var hashString = Convert.ToBase64String(hashBytes);
				return hashString.Equals(hmac, StringComparison.OrdinalIgnoreCase);
			}
		}

		public bool ValidateQueryString(string queryString, bool skipLifeTimeCheck = false)
		{
            var query = QueryHelpers.ParseQuery(queryString);
            if (!query.TryGetValue("hmac", out var hmac))
                return false;
			if (!query.TryGetValue("timestamp", out var timestampStr) || !long.TryParse(timestampStr, out var timestamp))
				return false;

			DateTimeOffset date = default;
            try
            {
				date = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            }
            catch
            {
				return false;
			}
			if (!skipLifeTimeCheck && DateTimeOffset.UtcNow - date > TimeSpan.FromHours(1.0))
				return false;
			query.Remove("hmac");
            queryString = queryString.Substring(1).Replace($"hmac={hmac}", "").Replace("&&", "&");
			var keyBytes = Encoding.UTF8.GetBytes(_credentials.ClientSecret);
			using (var hmacObj = new HMACSHA256(keyBytes))
			{
				var hashBytes = hmacObj.ComputeHash(Encoding.UTF8.GetBytes(queryString));
				var hashString = BitConverter.ToString(hashBytes).Replace("-","").ToLowerInvariant();
				if (!hashString.Equals(hmac, StringComparison.OrdinalIgnoreCase))
					return false;
			}
            return true;
		}
	}
    public class ShopifyApiClient
    {
        private readonly HttpClient _httpClient;
		private readonly string _shopUrl;
		private readonly ShopifyApiClientCredentials _credentials;

        public ShopifyApiClient(
            HttpClient httpClient,
            string shopUrl,
            ShopifyApiClientCredentials credentials)
        {
			_httpClient = httpClient;
			_shopUrl = shopUrl;
			_credentials = credentials;

            if (credentials is ShopifyApiClientCredentials.Basic b)
            {
                var bearer = $"{b.ApiKey}:{b.ApiPassword}";
                bearer = NBitcoin.DataEncoders.Encoders.Base64.EncodeData(Encoding.UTF8.GetBytes(bearer));
                _httpClient.DefaultRequestHeaders.Add("Authorization", "Basic " + bearer);
            }
            else if (credentials is ShopifyApiClientCredentials.AccessToken a)
            {
                _httpClient.DefaultRequestHeaders.Add("X-Shopify-Access-Token", a.Token);
            }
            else
                throw new NotSupportedException(credentials.ToString());
        }

		private HttpRequestMessage CreateRequest(HttpMethod method, string action, string relativeUrl = null,
			string apiVersion = "2024-07")
		{
            relativeUrl ??= ($"admin/api/{apiVersion}/" + action);
            var req = new HttpRequestMessage(method, $"{_shopUrl}/{relativeUrl}");
            return req;
        }

        private async Task<string> SendRequest(HttpRequestMessage req)
        {
            using var resp = await _httpClient.SendAsync(req);

            var strResp = await resp.Content.ReadAsStringAsync();
            if (strResp.StartsWith("{", StringComparison.OrdinalIgnoreCase) && JObject.Parse(strResp)["errors"]?.Value<string>() is string error)
            {
                if (error == "Not Found")
                    error = "Shop or Order not found";
                throw new ShopifyApiException(error);
            }
            return strResp;
        }

        public async Task<CreateWebhookResponse> CreateWebhook(string topic, string address, string format = "json")
        {
            var req = CreateRequest(HttpMethod.Post, $"webhooks.json");
            var payload = new
            {
                webhook = new { address, topic, format }
            };
            req.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
            var strResp = await SendRequest(req);
            return JsonConvert.DeserializeObject<CreateWebhookResponse>(strResp);
        }

        public async Task<List<CreateWebhookResponse>> RetrieveWebhooks()
        {
            var req = CreateRequest(HttpMethod.Get, $"webhooks.json");
            var strResp = await SendRequest(req);
            return JsonConvert.DeserializeObject<List<CreateWebhookResponse>>(strResp);
        }

        public async Task<CreateWebhookResponse> RetrieveWebhook(string id)
        {
            var req = CreateRequest(HttpMethod.Get, $"webhooks/{id}.json");
            var strResp = await SendRequest(req);
            return JsonConvert.DeserializeObject<CreateWebhookResponse>(strResp);
        }

        public async Task RemoveWebhook(string id)
        {
            var req = CreateRequest(HttpMethod.Delete, $"webhooks/{id}.json");
            await SendRequest(req);
        }

        public async Task<string[]> CheckScopes()
        {
            var req = CreateRequest(HttpMethod.Get, null, "admin/oauth/access_scopes.json");
            var c = JObject.Parse(await SendRequest(req));
            return c["access_scopes"].Values<JToken>()
                .Select(token => token["handle"].Value<string>()).ToArray();
        }

        public async Task<ShopifyOrder> GetOrderByCheckoutToken(string checkoutToken, bool withTransactions = false)
        {
            var req = """
                query getByCheckoutId($query: String!, $includeTxs: Boolean!) {
                orders(first: 1, query: $query) {
                    edges {
                      node {
                      ...order
                      }
                    }
                  }
                }
                """ + "\n" + OrderData;
            var resp = await SendGraphQL(req, new JObject() { ["query"] = $"checkout_token:{checkoutToken}", ["includeTxs"] = withTransactions });
            return resp["data"]["orders"]["edges"] switch
            {
				JArray a when a.Count > 0 => a[0]["node"].ToObject<ShopifyOrder>(JsonSerializer),
                _ => null
            };
		}

        const string OrderData =
            """
            fragment order on Order {
                id
                name
                cancelledAt
                statusPageUrl
                totalOutstandingSet {
                    shopMoney {
                        amount
                        currencyCode
                        }
                    presentmentMoney {
                        amount
                        currencyCode
                        }
                    }
                transactions @include(if: $includeTxs) {
                    ...orderTransaction
                }
                ...orderPaymentProcess
            }
            """ + "\n" + TransactionData + "\n" + PaymentProcessData;

        private const string TransactionData =
	        """
	        fragment orderTransaction on OrderTransaction {
	            id
	            gateway
	            kind
	            authorizationCode
	            status
	            manuallyCapturable
	            amountSet {
	            presentmentMoney {
	                amount
	                currencyCode
	                }
	             shopMoney {
	                 amount
	                 currencyCode
	                }
	            }
	        }
	        """;

        private const string PaymentProcessData =
            """
            fragment orderPaymentProcess on Order {
                paymentGatewayNames
            }
            """;
        public async Task<ShopifyOrder> GetOrder(long orderId, bool withTransactions = false)
		{
			// https://shopify.dev/docs/api/admin-graphql/2024-10/queries/order
			var req = """
            query getOrderDetails($orderId: ID!, $includeTxs: Boolean!) {
              order(id: $orderId) {
                ...order
              }
            }
            """ + "\n" + OrderData;

            var resp = await SendGraphQL(req,
				new JObject()
				{
					["orderId"] = ShopifyId.Order(orderId).ToString(),
					["includeTxs"] = withTransactions
				});
            var d = Unwrap(resp, "order");
			return d?.ToObject<ShopifyOrder>(JsonSerializer);
		}

		private HttpRequestMessage CreateGraphQLRequest(string req, JObject variables = null)
		{
			var jobj = new JObject() { ["query"] = req };
			if (variables is not null)
				jobj.Add("variables", variables);
			return new HttpRequestMessage(HttpMethod.Post, $"{_shopUrl}/admin/api/2024-10/graphql.json")
			{
				Content = new StringContent(jobj.ToString(), Encoding.UTF8, "application/json")
			};
		}

		JsonSerializer JsonSerializer = new JsonSerializer()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Converters = { new ShopifyIdJsonConverter(), new StringEnumConverter() }
        };

        // https://shopify.dev/docs/api/admin-graphql/2024-04/mutations/orderCapture
        public async Task<OrderTransaction> CaptureOrder(CaptureOrderRequest captureOrder)
        {
            var req = """
                mutation M($input: OrderCaptureInput!){
                    orderCapture(input: $input) {
                        transaction {
                            ...orderTransaction
                        }
                        userErrors {
                          field
                          message
                        }
                    }
                }
                """ + "\n" + TransactionData;
			JObject respObj = await SendGraphQL(req, new JObject() { ["input"] = JObject.FromObject(captureOrder, JsonSerializer) });
			var d = Unwrap(respObj, "orderCapture");
			return d["transaction"]?.ToObject<OrderTransaction>(JsonSerializer);
		}

        private JObject Unwrap(JObject respObj, string function)
        {
	        if (respObj["errors"] is JArray {  Count: > 0 } arr)
				throw new ShopifyApiException(arr[0]!["message"]!.Value<string>());
	        var d = respObj["data"]?[function] as JObject;
	        if (d is null)
		        return null;
	        if (d["userErrors"] is JArray {  Count: > 0 } arr1)
		        throw new ShopifyApiException(arr1[0]!["message"]!.Value<string>());
	        return d;
        }

        public async Task CancelOrder(CancelOrderRequest cancelOrder)
		{
			string req = """
                  mutation orderCancel($notifyCustomer: Boolean, $orderId: ID!, $reason: OrderCancelReason!, $refund: Boolean!, $restock: Boolean!, $staffNote: String) {
                  orderCancel(notifyCustomer: $notifyCustomer, orderId: $orderId, reason: $reason, refund: $refund, restock: $restock, staffNote: $staffNote) {
                    orderCancelUserErrors {
                      code
                      field
                      message
                    }
                  }
                }
                """;
			JObject respObj = await SendGraphQL(req, JObject.FromObject(cancelOrder, JsonSerializer));
			var errors = respObj["data"]["orderCancel"]["orderCancelUserErrors"] as JArray;
			if (errors.Count != 0)
				throw new ShopifyApiException(errors[0]["message"].Value<string>());
		}
		private async Task<JObject> SendGraphQL(string req, JObject variables = null)
		{
			var httpReq = CreateGraphQLRequest(req, variables);
			using var resp = await _httpClient.SendAsync(httpReq);
			var strResp = await resp.Content.ReadAsStringAsync();
			resp.EnsureSuccessStatusCode();
			return JObject.Parse(strResp);
		}

        public async Task<ShopifyId> CompleteDraftOrder(long orderId)
        {
			var req = """
                mutation M($id: ID!) {
                draftOrderComplete(id: $id) {
                  draftOrder {
                    order
                    {
                        id
                    }
                  }
                }
                }
                """;
			JObject respObj = await SendGraphQL(req, new JObject() { ["id"] = ShopifyId.DraftOrder(orderId).ToString() });
			return ShopifyId.Parse(respObj["data"]["draftOrderComplete"]["draftOrder"]["order"]["id"].Value<string>());
		}
		public async Task<ShopifyId> DuplicateOrder(long orderId)
		{
            var req = """
                mutation M($id: ID) {
                draftOrderDuplicate(id: $id) {
                  draftOrder {
                    id
                  }
                }
                }
                """;
			JObject respObj = await SendGraphQL(req, new JObject() { ["id"] = ShopifyId.DraftOrder(orderId).ToString() });
            return ShopifyId.Parse(respObj["data"]["draftOrderDuplicate"]["draftOrder"]["id"].Value<string>());
        }

        // https://shopify.dev/docs/api/admin-graphql/latest/mutations/orderUpdate
        public async Task<ShopifyId> UpdateOrderMetafields(UpdateMetafields update)
        {
            var req = """
                mutation updateOrderMetafields($input: OrderInput!) {
                  orderUpdate(input: $input) {
                    order {
                      id
                    }
                    userErrors {
                      message
                      field
                    }
                  }
                }
                """;
            JObject respObj = await SendGraphQL(req, new JObject { ["input"] = JObject.FromObject(update, JsonSerializer) });
			var d = Unwrap(respObj, "orderUpdate");
			return ShopifyId.Parse(d["order"]["id"].Value<string>());
        }

    }


    public record ShopifyApiClientCredentials
    {
        public record Basic (string ApiKey, string ApiPassword) : ShopifyApiClientCredentials;
        public record AccessToken(string Token) : ShopifyApiClientCredentials;
    }
    public record ShopifyAppCredentials(string ClientId, string ClientSecret);
}

