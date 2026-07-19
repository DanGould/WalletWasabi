using NBitcoin;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Mempool;
using WalletWasabi.Blockchain.TransactionBroadcasting;
using WalletWasabi.Payjoin;
using WalletWasabi.Services;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Payjoin;

/// <summary>
/// The OHTTP-keys bootstrap must never reveal the client IP to the directory: with Tor
/// enabled it fetches straight from the directory through the (Tor-riding) HTTP factory;
/// with Tor disabled it goes through a relay acting as a CONNECT proxy and must not touch
/// the factory at all.
/// </summary>
public class PayjoinOhttpBootstrapTests
{
	private class RecordingHandler : HttpMessageHandler
	{
		public List<HttpRequestMessage> Requests { get; } = new();

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(request);
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new ByteArrayContent(Bip77PayjoinTests.TestOhttpKeys),
			});
		}
	}

	private class StubHttpClientFactory : IHttpClientFactory
	{
		private readonly HttpMessageHandler _handler;

		public StubHttpClientFactory(HttpMessageHandler handler)
		{
			_handler = handler;
		}

		public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
	}

	private static PayjoinManager CreateManager(string dataDir, IHttpClientFactory httpClientFactory, bool torEnabled, string[]? relays = null)
	{
		var configuration = new PayjoinConfiguration(
			DirectoryUrl: "https://payjo.in",
			OhttpRelayUrls: relays ?? ["https://relay.example"],
			MaxFeeRateSatPerVb: 1000,
			TorEnabled: torEnabled);

#pragma warning disable CA2000 // Ownership of the broadcaster's mempool service ends with the test process.
		return new PayjoinManager(
			dataDir,
			Network.Main,
			configuration,
			() => Task.FromResult(Enumerable.Empty<WalletWasabi.Wallets.Wallet>()),
			httpClientFactory,
			new TransactionBroadcaster([], new MempoolService(new EventBus())));
#pragma warning restore CA2000
	}

	[Fact]
	public async Task TorEnabled_FetchesKeysFromDirectoryThroughFactory()
	{
		string workDir = await Common.GetEmptyWorkDirAsync();
		using var handler = new RecordingHandler();
		using PayjoinManager manager = CreateManager(workDir, new StubHttpClientFactory(handler), torEnabled: true);

		using var keys = await manager.GetOhttpKeysAsync(CancellationToken.None);

		Assert.NotNull(keys);
		HttpRequestMessage request = Assert.Single(handler.Requests);
		Assert.Equal(HttpMethod.Get, request.Method);
		Assert.Equal("https://payjo.in/.well-known/ohttp-gateway", request.RequestUri?.ToString());

		// Second fetch is served from the 12 h cache without another request.
		using var cachedKeys = await manager.GetOhttpKeysAsync(CancellationToken.None);
		Assert.Single(handler.Requests);
	}

	[Fact]
	public async Task TorDisabled_UsesRelayProxyAndNeverTouchesTheFactory()
	{
		string workDir = await Common.GetEmptyWorkDirAsync();
		using var handler = new RecordingHandler();

		// Unreachable relay: the CONNECT-proxy bootstrap must fail with a transport error
		// after trying the relays — without ever hitting the factory-provided client.
		using PayjoinManager manager = CreateManager(workDir, new StubHttpClientFactory(handler), torEnabled: false, relays: ["http://127.0.0.1:1"]);

		await Assert.ThrowsAsync<HttpRequestException>(() => manager.GetOhttpKeysAsync(CancellationToken.None));
		Assert.Empty(handler.Requests);
	}
}
