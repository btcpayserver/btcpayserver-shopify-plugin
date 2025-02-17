using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.ShopifyPlugin.ViewModels.Models
{
    public class ShopifySettingsViewModel
    {
		public string ClientId { get; set; }
		public string ClientSecret { get; set; }
		public string ShopUrl { get; set; }
	}
}
