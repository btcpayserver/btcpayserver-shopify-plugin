#nullable enable

namespace BTCPayServer.Plugins.ShopifyPlugin
{
    public class ShopifyStoreSettings
    {
		public class AppCreds
		{
			public string? ClientId { get; set; }
			public string? ClientSecret { get; set; }
		}
		public AppCreds? App { get; set; }
		public string? ShopUrl { get; set; }
		public string? AccessToken { get; set; }
		public const string SettingsName = "ShopifyPluginSettings";
	}
}
