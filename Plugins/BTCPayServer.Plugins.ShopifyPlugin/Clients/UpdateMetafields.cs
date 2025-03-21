namespace BTCPayServer.Plugins.ShopifyPlugin.Clients;

public class UpdateMetafields
{
	public class Metafield
	{
		public string Namespace { get; set; }
		public string Key { get; set; }
		public string Type { get; set; }
		public string Value { get; set; }
	}
	public ShopifyId Id { get; set; }
	public Metafield[] Metafields { get; set; }
}
