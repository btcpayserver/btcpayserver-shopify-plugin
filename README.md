# Shopify plugin

## For maintainers

If you are a developer maintaining this plugin, in order to maintain this plugin, you need to clone this repository with ``--recurse-submodules``:

```bash
git clone --recurse-submodules https://github.com/btcpayserver/btcpayserver-shopify-plugin
```

Then create the `appsettings.dev.json` file in `submodules\btcpayserver\BTCPayServer`, with the following content:
```json
{
  "DEBUG_PLUGINS": "..\\..\\..\\Plugins\\BTCPayServer.Plugins.ShopifyPlugin\\bin\\Debug\\net8.0\\BTCPayServer.Plugins.ShopifyPlugin.dll",
}
```

This will ensure that BTCPay Server loads the plugin when it starts.

Then start the development dependencies via docker-compose:

```
docker-compose up -d dev
```

Finally, set up BTCPay Server as the startup project in [Rider](https://www.jetbrains.com/rider/) or Visual Studio.

If you want to reset the environment you can run:

```bash
docker-compose down -v
docker-compose up -d dev
```

Note: Running or compiling the BTCPay Server project will not automatically recompile the plugin project. Therefore, if you make any changes to the project, do not forget to build it before running BTCPay Server in debug mode.

We recommend using Rider for plugin development, as it supports hot reload with plugins. You can edit .cshtml files, save, and refresh the page to see the changes.

Visual Studio does not support this feature.