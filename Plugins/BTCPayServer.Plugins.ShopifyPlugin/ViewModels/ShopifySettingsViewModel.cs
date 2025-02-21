using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.ShopifyPlugin.ViewModels
{
    public class ShopifySettingsViewModel
    {
	    public enum State
	    {
		    WaitingClientCreds,
		    WaitingForDeploy,
		    WaitingForInstall,
		    Done
	    }

	    public bool ClientCredsConfigured { get; set; }
	    public bool AppDeployed { get; set; }
	    public bool AppInstalled { get; set; }
	    public State Step { get; set; }
		public string ClientId { get; set; }
		public string ClientSecret { get; set; }
		public string AppName { get; set; }
		public string CLIToken { get; set; }
		public string ShopUrl { get; set; }
		public string ShopName { get; set; }
    }
}
