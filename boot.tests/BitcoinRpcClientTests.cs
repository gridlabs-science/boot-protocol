using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using boot_portal.Models;
using boot_portal.Services;

namespace boot.tests;

[TestClass]
public sealed class BitcoinRpcClientTests
{
    [TestMethod]
    public async Task ClientParsesBlockchainAndZmqResponsesWithoutEmbeddingCredentialsInUrl()
    {
        AuthenticationHeaderValue? observedAuthorization = null;
        var handler = new CallbackHandler(async request =>
        {
            observedAuthorization = request.Headers.Authorization;
            string body = await request.Content!.ReadAsStringAsync();
            using JsonDocument document = JsonDocument.Parse(body);
            string method = document.RootElement.GetProperty("method").GetString()!;
            string result = method switch
            {
                "getblockchaininfo" => """
                    {"chain":"regtest","blocks":100,"headers":100,"bestblockhash":"block-100","initialblockdownload":false,"verificationprogress":1.0}
                    """,
                "getzmqnotifications" => """
                    [{"type":"pubhashblock","address":"tcp://0.0.0.0:28332"},{"type":"pubrawblock","address":"tcp://0.0.0.0:28333"}]
                    """,
                _ => "\"block-100\""
            };
            return JsonResponse($"{{\"result\":{result},\"error\":null,\"id\":1}}");
        });
        var config = new PoolConfig
        {
            BitcoinRpcUrl = "http://bitcoin:8332",
            BitcoinRpcUsername = "rpc-user",
            BitcoinRpcPassword = "rpc-password"
        };
        var client = new BitcoinRpcClient(config, new HttpClient(handler));

        BitcoinBlockchainInfo info = await client.GetBlockchainInfoAsync(CancellationToken.None);
        IReadOnlyList<BitcoinZmqPublisher> topics =
            await client.GetZmqNotificationsAsync(CancellationToken.None);

        Assert.AreEqual(100L, info.Blocks);
        Assert.AreEqual("block-100", info.BestBlockHash);
        Assert.AreEqual("regtest", info.Chain);
        CollectionAssert.AreEquivalent(
            new[] { "pubhashblock", "pubrawblock" },
            topics.Select(topic => topic.Topic).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "tcp://0.0.0.0:28332", "tcp://0.0.0.0:28333" },
            topics.Select(topic => topic.Address).ToArray());
        Assert.AreEqual("Basic", observedAuthorization?.Scheme);
        Assert.AreEqual(
            "rpc-user:rpc-password",
            Encoding.UTF8.GetString(Convert.FromBase64String(observedAuthorization!.Parameter!)));
        Assert.IsFalse(config.BitcoinRpcUrl.Contains("rpc-password", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ClientReloadsCookieCredentialsForEveryRequest()
    {
        string cookiePath = Path.GetTempFileName();
        var observedCredentials = new List<string>();
        try
        {
            File.WriteAllText(cookiePath, "user:first");
            var handler = new CallbackHandler(request =>
            {
                observedCredentials.Add(Encoding.UTF8.GetString(Convert.FromBase64String(
                    request.Headers.Authorization!.Parameter!)));
                return Task.FromResult(JsonResponse(
                    "{\"result\":\"block-hash\",\"error\":null,\"id\":1}"));
            });
            var client = new BitcoinRpcClient(
                new PoolConfig
                {
                    BitcoinRpcUrl = "http://bitcoin:8332",
                    BitcoinRpcCookieFile = cookiePath
                },
                new HttpClient(handler));

            await client.GetBestBlockHashAsync(CancellationToken.None);
            File.WriteAllText(cookiePath, "user:second");
            await client.GetBestBlockHashAsync(CancellationToken.None);

            CollectionAssert.AreEqual(
                new[] { "user:first", "user:second" },
                observedCredentials);
        }
        finally
        {
            File.Delete(cookiePath);
        }
    }

    [TestMethod]
    public async Task ClientParsesPrivacySafeNetworkPeerAndHashrateFields()
    {
        var handler = new CallbackHandler(async request =>
        {
            using JsonDocument document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            string method = document.RootElement.GetProperty("method").GetString()!;
            string result = method switch
            {
                "getnetworkinfo" => "{\"connections\":3,\"connections_in\":1,\"connections_out\":2}",
                "getpeerinfo" => "[{\"id\":7,\"addr\":\"192.168.1.9:8333\",\"inbound\":false,\"pingtime\":0.042,\"connection_type\":\"outbound-full-relay\"}]",
                "getnetworkhashps" => "7.3e20",
                _ => "null"
            };
            return JsonResponse($"{{\"result\":{result},\"error\":null,\"id\":1}}");
        });
        var client = new BitcoinRpcClient(
            new PoolConfig { BitcoinRpcUrl = "http://bitcoin:8332" },
            new HttpClient(handler));

        BitcoinNetworkInfo network = await client.GetNetworkInfoAsync(CancellationToken.None);
        IReadOnlyList<BitcoinPeerInfo> peers = await client.GetPeerInfoAsync(CancellationToken.None);
        double? hashrate = await client.GetNetworkHashrateAsync(CancellationToken.None);

        Assert.AreEqual(3, network.Connections);
        Assert.AreEqual(1, peers.Count);
        Assert.AreEqual(7L, peers[0].Id);
        Assert.AreEqual(0.042, peers[0].PingTimeSeconds);
        Assert.AreEqual(7.3e20, hashrate);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class CallbackHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _callback;

        public CallbackHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback)
        {
            _callback = callback;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => _callback(request);
    }
}
