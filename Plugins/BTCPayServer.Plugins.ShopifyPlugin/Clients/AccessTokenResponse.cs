using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.ShopifyPlugin.Clients;

public class AccessTokenResponse
{
	[JsonProperty("access_token")] public string AccessToken { get; set; }
	[JsonProperty("scope")] public string Scope { get; set; }
}

