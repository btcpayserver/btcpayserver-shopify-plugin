# Shopify plugin

## Configuration

| Environment variable               | Description                             | Example                |
|------------------------------------|-----------------------------------------|------------------------|
| **BTCPAY_SHOPIFY_PLUGIN_DEPLOYER** | The URL of the [shopify-app-deployer](https://github.com/btcpayserver/shopify-app) | http://localhost:5000/ |

## For maintainers

If you are a developer maintaining this plugin, in order to maintain this plugin, you need to clone this repository with ``--recurse-submodules``:

```bash
git clone --recurse-submodules https://github.com/btcpayserver/btcpayserver-shopify-plugin
```

Then create the `appsettings.dev.json` file in `submodules\btcpayserver\BTCPayServer`, with the following content:
```json
{
  "DEBUG_PLUGINS": "../../../Plugins/BTCPayServer.Plugins.ShopifyPlugin/bin/Debug/net8.0/BTCPayServer.Plugins.ShopifyPlugin.dll",
  "SHOPIFY_PLUGIN_DEPLOYER": "http://localhost:32204/"
}
```

This will ensure that BTCPay Server loads the plugin when it starts.

Next, Shopify requires a public domain in order to integrate with it. The `docker-compose` contains [cloudflared] for this purpose.

Create a `.env` file at the root of the project, with the following content:
```bash
CLOUDFLARE_TUNNEL_TOKEN="<token>"
```

To get the `token`, follow [this documentation](https://github.com/btcpayserver/btcpayserver-docker/blob/master/docs/cloudflare-tunnel.md).

1. In the `Edit public hostname` part, `Service` should be `https://host.docker.internal:14142`.
2. Disable TLS check: `Additional application settings` => `TLS` => Check `No TLS Verify`. 

Finally, start the development dependencies via docker-compose:

```
docker-compose up -d dev
```

Finally:
1. Set up BTCPay Server as the startup project in [Rider](https://www.jetbrains.com/rider/) or Visual Studio.
2. Make sure to select the `Bitcoin-HTTPS` launch settings.

If you want to reset the environment you can run:

```bash
docker-compose down -v
docker-compose up -d dev
```

Note: Running or compiling the BTCPay Server project will not automatically recompile the plugin project. Therefore, if you make any changes to the project, do not forget to build it before running BTCPay Server in debug mode.

We recommend using Rider for plugin development, as it supports hot reload with plugins. You can edit .cshtml files, save, and refresh the page to see the changes.

Visual Studio does not support this feature.


