using System;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.Payment;
using NBitcoin.RPC;
using Xunit;

namespace WalletWasabi.IntegrationTests.Payjoin;

/// <summary>
/// BIP77 async payjoin integration scenarios against payjoin-cli through a local
/// payjoin-mailroom directory + OHTTP relay on regtest. The cli↔cli scenarios prove the harness
/// itself and mirror payjoin-cli's tests/e2e.rs choreography; the Wasabi-side scenarios un-skip
/// as the W1 (sender) and W2 (receiver) branches land.
/// </summary>
[Collection("Payjoin harness")]
[Trait("Category", "PayjoinHarness")]
public class PayjoinCliHarnessTests
{
	private const long InvoiceAmountSats = 100_000;
	private static readonly TimeSpan MarkerTimeout = TimeSpan.FromSeconds(30);

	private readonly PayjoinHarnessFixture _fixture;

	public PayjoinCliHarnessTests(PayjoinHarnessFixture fixture)
	{
		_fixture = fixture;
	}

	[Fact]
	public async Task CliToCli_PayjoinRoundTrip_TransactionHasReceiverContribution()
	{
		using HarnessRoles roles = await SetUpRolesAsync("roundtrip").ConfigureAwait(true);

		using LineBufferedProcess receiver = roles.ReceiverDriver.StartReceive(InvoiceAmountSats);
		string bip21 = await PayjoinCliDriver.WaitForBip21Async(receiver).ConfigureAwait(true);

		using LineBufferedProcess sender = roles.SenderDriver.StartSend(bip21);
		await sender.WaitForExitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);
		Assert.True(sender.ExitCode == 0, $"payjoin-cli send failed.{sender.DescribeBuffers()}");
		string txid = PayjoinCliDriver.ParseSentTxid(sender.StdoutText);

		// The receiver announces the same txid when its proposal is accepted.
		await receiver.WaitForStdoutLineAsync(
			line => line.Contains(PayjoinCliDriver.ResponseSuccessfulMarker, StringComparison.Ordinal) && line.Contains(txid, StringComparison.Ordinal),
			MarkerTimeout,
			$"receiver '{PayjoinCliDriver.ResponseSuccessfulMarker}' with txid {txid}").ConfigureAwait(true);

		await AssertPayjoinTransactionShapeAsync(roles.SenderRpc, txid, bip21).ConfigureAwait(true);
	}

	[Fact]
	public async Task CliToCli_KilledMidSessionOnBothSides_ResumesFromPersistedStateAndCompletes()
	{
		using HarnessRoles roles = await SetUpRolesAsync("killresume").ConfigureAwait(true);
		PayjoinCliDriver senderDriver = roles.SenderDriver;
		PayjoinCliDriver receiverDriver = roles.ReceiverDriver;

		// Receiver initializes a session (URI shown) and dies before any sender request arrives.
		string bip21;
		using (LineBufferedProcess receiver = receiverDriver.StartReceive(InvoiceAmountSats))
		{
			bip21 = await PayjoinCliDriver.WaitForBip21Async(receiver).ConfigureAwait(true);
			receiver.Kill();
		}

		// Sender posts the original PSBT, polls into the void (receiver offline), then dies.
		using (LineBufferedProcess sender = senderDriver.StartSend(bip21))
		{
			await sender.WaitForStdoutLineAsync(
				line => line.Contains(PayjoinCliDriver.NoResponseYetMarker, StringComparison.Ordinal),
				MarkerTimeout,
				$"sender '{PayjoinCliDriver.NoResponseYetMarker}'").ConfigureAwait(true);
			sender.Kill();
		}

		// Receiver resumes from the event log, finds the original payload and posts its proposal.
		using (LineBufferedProcess receiverResume = receiverDriver.StartResume())
		{
			await receiverResume.WaitForStdoutLineAsync(
				line => line.Contains(PayjoinCliDriver.ResponseSuccessfulMarker, StringComparison.Ordinal),
				MarkerTimeout,
				$"receiver resume '{PayjoinCliDriver.ResponseSuccessfulMarker}'").ConfigureAwait(true);
			receiverResume.Kill();
		}

		// The payjoin transaction is not broadcast yet, so a further resume must NOT complete the session.
		using (LineBufferedProcess receiverNotDone = receiverDriver.StartResume())
		{
			await receiverNotDone.WaitForExitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
			Assert.DoesNotContain(PayjoinCliDriver.SessionCompletedMarker, receiverNotDone.StdoutText, StringComparison.Ordinal);
		}

		// Re-running send with the same BIP21 auto-resumes the persisted sender session and completes.
		string txid;
		using (LineBufferedProcess senderResume = senderDriver.StartSend(bip21))
		{
			await senderResume.WaitForExitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);
			Assert.True(senderResume.ExitCode == 0, $"payjoin-cli send (resume) failed.{senderResume.DescribeBuffers()}");
			txid = PayjoinCliDriver.ParseSentTxid(senderResume.StdoutText);
		}

		await _fixture.MineAsync(1).ConfigureAwait(true);

		// Receiver's monitor sees the confirmed payjoin transaction and closes the session.
		using (LineBufferedProcess receiverDone = receiverDriver.StartResume())
		{
			await receiverDone.WaitForStdoutLineAsync(
				line => line.EndsWith(PayjoinCliDriver.SessionCompletedMarker, StringComparison.Ordinal),
				MarkerTimeout,
				$"receiver resume '{PayjoinCliDriver.SessionCompletedMarker}'").ConfigureAwait(true);
		}

		// Both sides are drained: no open sessions remain.
		foreach (PayjoinCliDriver driver in new[] { receiverDriver, senderDriver })
		{
			using LineBufferedProcess drained = driver.StartResume();
			await drained.WaitForStdoutLineAsync(
				line => line.Contains(PayjoinCliDriver.NoSessionsToResumeMarker, StringComparison.Ordinal),
				MarkerTimeout,
				$"'{PayjoinCliDriver.NoSessionsToResumeMarker}'").ConfigureAwait(true);
		}

		await AssertPayjoinTransactionShapeAsync(roles.SenderRpc, txid, bip21).ConfigureAwait(true);
	}

	[Fact]
	public async Task CliSender_InfraUnreachable_SessionFailsResumableAndCancelBroadcastsFallback()
	{
		using HarnessRoles roles = await SetUpRolesAsync("dirdown").ConfigureAwait(true);

		string bip21;
		using (LineBufferedProcess receiver = roles.ReceiverDriver.StartReceive(InvoiceAmountSats))
		{
			bip21 = await PayjoinCliDriver.WaitForBip21Async(receiver).ConfigureAwait(true);
			receiver.Kill();
		}

		// Point the sender at a relay that is not listening: payjoin infra down at send time.
		using var deadInfraSenderDriver = new PayjoinCliDriver(
			_fixture.CreateDriverWorkDir("dirdown-deadrelay"),
			_fixture.GetWalletRpcUrl("dirdown_sender"),
			_fixture.RpcUser,
			_fixture.RpcPassword,
			ohttpRelayUrls: ["http://127.0.0.1:1"],
			pjDirectoryUrls: [_fixture.Directory.Url]);

		string sessionId;
		using (LineBufferedProcess failingSend = deadInfraSenderDriver.StartSend(bip21))
		{
			int exitCode = await failingSend.WaitForExitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);
			Assert.NotEqual(0, exitCode);

			// The user-visible degradation contract: the session fails with an explicit reason and
			// a cancel/fallback instruction rather than silently losing the payment.
			Assert.Contains(PayjoinCliDriver.SessionFailedMarker, failingSend.StdoutText, StringComparison.Ordinal);
			Assert.Contains("No valid relays available", failingSend.StderrText, StringComparison.Ordinal);
			sessionId = PayjoinCliDriver.ParseSessionId(failingSend.StdoutText);
		}

		// Cancel broadcasts the original (fallback) transaction: funds still move as a plain send.
		string fallbackTxid;
		using (LineBufferedProcess cancel = deadInfraSenderDriver.StartCancel(sessionId))
		{
			string broadcastLine = await cancel.WaitForStdoutLineAsync(
				line => line.Contains(PayjoinCliDriver.FallbackBroadcastMarker, StringComparison.Ordinal),
				MarkerTimeout,
				$"'{PayjoinCliDriver.FallbackBroadcastMarker}'").ConfigureAwait(true);
			fallbackTxid = broadcastLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
		}

		// The fallback is a plain send: single sender input, one output paying exactly the invoice.
		Transaction fallbackTx = await roles.SenderRpc.GetRawTransactionAsync(uint256.Parse(fallbackTxid)).ConfigureAwait(true);
		var url = new BitcoinUrlBuilder(bip21, Network.RegTest);
		TxOut invoiceOutput = Assert.Single(fallbackTx.Outputs, o => o.ScriptPubKey == url.Address!.ScriptPubKey);
		Assert.Equal(url.Amount!, invoiceOutput.Value);
		Assert.Single(fallbackTx.Inputs);
	}

	[Fact(Skip = "Blocked on W2: requires Wasabi PayjoinManager (receiver) to produce a BIP21 pj= URI and complete the session. Choreography: start Wasabi receiver session -> capture URI -> payjoin-cli send pays it (receiver may be offline at send; comes up and completes) -> assert receiver input contribution and settlement detection.")]
	public void CliSendsToWasabiReceiver_AsyncCompletion()
	{
	}

	[Fact(Skip = "Blocked on W1: requires Wasabi BIP77 sender behind IPayjoinClient. Choreography: payjoin-cli receive -> BIP21 -> Wasabi sends -> assert 'Response successful' on cli receiver and payjoin tx shape; directory-down variant must degrade to plain send with a user-visible reason.")]
	public void WasabiSendsToCliReceiver_RoundTrip()
	{
	}

	private async Task<HarnessRoles> SetUpRolesAsync(string testName)
	{
		string senderWallet = $"{testName}_sender";
		string receiverWallet = $"{testName}_receiver";
		RPCClient senderRpc = await _fixture.CreateFundedWalletAsync(senderWallet, Money.Coins(1m)).ConfigureAwait(true);
		RPCClient receiverRpc = await _fixture.CreateFundedWalletAsync(receiverWallet, Money.Coins(1m)).ConfigureAwait(true);

#pragma warning disable CA2000 // Dispose objects before losing scope - driver ownership transferred to HarnessRoles
		var senderDriver = new PayjoinCliDriver(
			_fixture.CreateDriverWorkDir($"{testName}-sender"),
			_fixture.GetWalletRpcUrl(senderWallet),
			_fixture.RpcUser,
			_fixture.RpcPassword,
			ohttpRelayUrls: [_fixture.Relay.Url],
			pjDirectoryUrls: [_fixture.Directory.Url]);

		var receiverDriver = new PayjoinCliDriver(
			_fixture.CreateDriverWorkDir($"{testName}-receiver"),
			_fixture.GetWalletRpcUrl(receiverWallet),
			_fixture.RpcUser,
			_fixture.RpcPassword,
			ohttpRelayUrls: [_fixture.Relay.Url],
			pjDirectoryUrls: [_fixture.Directory.Url],
			ohttpKeysPath: _fixture.OhttpKeysPath);
#pragma warning restore CA2000

		return new HarnessRoles(senderDriver, receiverDriver, senderRpc, receiverRpc);
	}

	private sealed record HarnessRoles(PayjoinCliDriver SenderDriver, PayjoinCliDriver ReceiverDriver, RPCClient SenderRpc, RPCClient ReceiverRpc) : IDisposable
	{
		public void Dispose()
		{
			SenderDriver.Dispose();
			ReceiverDriver.Dispose();
		}
	}

	/// <summary>
	/// Asserts the transaction is a payjoin in the unified-output model payjoin-cli produces:
	/// more than one input (the receiver contributed) and exactly one output paying the invoice
	/// script with MORE than the invoice amount (payment output merged with the receiver's input value).
	/// </summary>
	private async Task AssertPayjoinTransactionShapeAsync(RPCClient rpc, string txid, string bip21)
	{
		Transaction tx = await rpc.GetRawTransactionAsync(uint256.Parse(txid)).ConfigureAwait(true);

		Assert.True(tx.Inputs.Count > 1, $"Expected a receiver input contribution (inputs > 1), got {tx.Inputs.Count}.");

		var url = new BitcoinUrlBuilder(bip21, Network.RegTest);
		Script invoiceScript = url.Address!.ScriptPubKey;
		Money invoiceAmount = url.Amount!;

		TxOut receiverOutput = Assert.Single(tx.Outputs, o => o.ScriptPubKey == invoiceScript);
		Assert.True(
			receiverOutput.Value > invoiceAmount,
			$"Receiver output should exceed the invoice amount (unified output model): {receiverOutput.Value} <= {invoiceAmount}.");
	}
}
