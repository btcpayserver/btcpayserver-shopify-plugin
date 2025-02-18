using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ShopifyPlugin.Clients;

public class AppDeployerClient
{
    private readonly HttpClient _httpClient;

    public AppDeployerClient(HttpClient httpClient, Uri requestUrl)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = requestUrl;
    }

    public async Task<HttpResponseMessage> Deploy(AppDeploymentRequest request)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "deploy")
        {
            Content = new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json"),
        };
        return await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    }
}
public class AppDeploymentRequest
{
    [JsonProperty("cliToken")]
    public string CLIToken { get; set; }
    [JsonProperty("clientId")]
    public string ClientId { get; set; }
    [JsonProperty("pluginUrl")]
    public string PluginUrl { get; set; }
    [JsonProperty("appName")]
    public string AppName { get; set; }
}