#nullable enable
using BTCPayServer.Services.Stores;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.ShopifyPlugin.Services
{
    public class ShopifyClientFactory
    {
		public ShopifyClientFactory(IHttpClientFactory httpClientFactory, StoreRepository storeRepository)
		{
			HttpClientFactory = httpClientFactory;
			StoreRepository = storeRepository;
		}

		public IHttpClientFactory HttpClientFactory { get; }
		public StoreRepository StoreRepository { get; }

		public async Task<ShopifyApiClient?> CreateAPIClient(string storeId)
        {
			var settings = await GetSettings(storeId);
			if (settings is { ShopUrl: string shopUrl, AccessToken: string accessToken })
				return new ShopifyApiClient(HttpClientFactory.CreateClient("SHOPIFY_API_CLIENT"), shopUrl, new ShopifyApiClientCredentials.AccessToken(accessToken));
			return null;
		}
		public async Task<ShopifyAppClient?> CreateAppClient(string storeId)
		{
			var settings = await GetSettings(storeId);
			if (settings is { App: { ClientId: string id, ClientSecret: string secret } })
				return new ShopifyAppClient(HttpClientFactory.CreateClient("SHOPIFY_APP_CLIENT"), new ShopifyAppCredentials(id, secret));
			return null;
		}

		private async Task<ShopifyStoreSettings?> GetSettings(string storeId)
		{
			return await StoreRepository.GetSettingAsync<ShopifyStoreSettings>(storeId, ShopifyStoreSettings.SettingsName);
		}
	}
}
