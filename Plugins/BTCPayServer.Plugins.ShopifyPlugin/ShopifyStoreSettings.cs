﻿#nullable enable

namespace BTCPayServer.Plugins.ShopifyPlugin
{
    public class ShopifyStoreSettings
    {
	    public ShopifySetupSettings? Setup { get; set; }
	    public string? PreferredAppName {
		    get;
		    set;
	    }
		public const string SettingsName = "ShopifyPluginSettings";
		public const string DefaultAppName = "BTCPay Server";
	}

    public class ShopifySetupSettings
    {
	    public string? ClientId { get; set; }
	    public string? ClientSecret { get; set; }
	    public string? ShopUrl { get; set; }
	    public string? AccessToken { get; set; }
	    public int Version { get; set; }
	    public string? DeployedCommit { get; set; }
    }
}
