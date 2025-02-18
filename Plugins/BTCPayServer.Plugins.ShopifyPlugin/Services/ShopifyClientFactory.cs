﻿#nullable enable
using BTCPayServer.Plugins.ShopifyPlugin.Clients;
using BTCPayServer.Services.Stores;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using Microsoft.Extensions.Configuration;

namespace BTCPayServer.Plugins.ShopifyPlugin.Services
{
    public class ShopifyClientFactory
    {
	    public IConfiguration Configuration { get; set; }
		public ShopifyClientFactory(IHttpClientFactory httpClientFactory, StoreRepository storeRepository, IConfiguration configuration)
		{
			HttpClientFactory = httpClientFactory;
			StoreRepository = storeRepository;
			Configuration = configuration;
		}

		public IHttpClientFactory HttpClientFactory { get; }
		public StoreRepository StoreRepository { get; }

		public AppDeployerClient CreateAppDeployer()
		{
			var deployerUrl = Configuration["SHOPIFY_PLUGIN_DEPLOYER"];
			if (string.IsNullOrEmpty(deployerUrl))
				throw new ConfigException("BTCPAY_SHOPIFY_PLUGIN_DEPLOYER is not configured");
			if (!Uri.TryCreate(deployerUrl, UriKind.Absolute, out var deployerUri))
				throw new ConfigException("BTCPAY_SHOPIFY_PLUGIN_DEPLOYER should be a valid URL");
			return new AppDeployerClient(this.HttpClientFactory.CreateClient("SHOPIFY_DEPLOYER"), deployerUri);
		}

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
