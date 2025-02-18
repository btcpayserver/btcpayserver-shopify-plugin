using System.Collections.Generic;

namespace BTCPayServer.Plugins.ShopifyPlugin.Clients;

public class TransactionsListResp
{
    public List<TransactionDataHolder> transactions { get; set; }
}
