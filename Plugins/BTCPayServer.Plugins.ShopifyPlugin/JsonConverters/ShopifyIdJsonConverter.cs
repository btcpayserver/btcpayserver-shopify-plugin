#nullable enable
using BTCPayServer.Plugins.ShopifyPlugin.Clients;
using Newtonsoft.Json;
using System;

namespace BTCPayServer.Plugins.ShopifyPlugin.JsonConverters
{
	public class ShopifyIdJsonConverter : JsonConverter<ShopifyId>
	{
		public override ShopifyId? ReadJson(JsonReader reader, Type objectType, ShopifyId? existingValue, bool hasExistingValue, JsonSerializer serializer)
		=> reader switch
		{
			{ TokenType: JsonToken.Null } => null,
			{ TokenType: JsonToken.String, Value: string str } when ShopifyId.TryParse(str, out var id) => id,
			_ => null
		};

		public override void WriteJson(JsonWriter writer, ShopifyId? value, JsonSerializer serializer)
		{
			if (value is { } v)
				writer.WriteValue(v.ToString());
		}
	}
}
