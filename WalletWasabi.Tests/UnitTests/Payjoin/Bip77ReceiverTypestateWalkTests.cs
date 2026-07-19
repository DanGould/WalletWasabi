using NBitcoin;
using Payjoin;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Payjoin;
using WalletWasabi.Tests.Helpers;
using Xunit;
using OutPoint = NBitcoin.OutPoint;
using Transaction = NBitcoin.Transaction;
using TxIn = NBitcoin.TxIn;
using TxOut = NBitcoin.TxOut;
using Uri = Payjoin.Uri;

namespace WalletWasabi.Tests.UnitTests.Payjoin;

/// <summary>
/// Drives the full BIP 77 receiver typestate chain against payjoin-ffi's in-process test
/// directory and OHTTP relay (<c>TestServices</c>), with the Wasabi wallet callbacks and
/// SQLite persistence — including a kill/resume in the middle of the chain.
/// </summary>
public class Bip77ReceiverTypestateWalkTests
{
	/// <summary>ffi persister for the test's sender side; sender persistence is lane W1's concern.</summary>
	private class InMemorySenderPersister : JsonSenderSessionPersister
	{
		private readonly List<string> _events = new();

		public void Save(string @event) => _events.Add(@event);

		public string[] Load() => _events.ToArray();

		public void Close()
		{
		}
	}

	[Fact]
	public async Task ReceiverTypestateWalk_KillMidChain_ResumesAndFinalizesProposal()
	{
		string workDir = await Common.GetEmptyWorkDirAsync();
		string dbPath = Path.Combine(workDir, "Sessions.sqlite");

		// Receiver wallet: hot (empty password), one payment address, one coin to contribute.
		KeyManager keyManager = ServiceFactory.CreateKeyManager(password: "");
		HdPubKey receiverHdPubKey = BitcoinFactory.CreateHdPubKey(keyManager);
		BitcoinAddress receiverAddress = receiverHdPubKey.GetAddress(Network.Main);
		SmartCoin contributionCoin = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), 0.4m);

		// Sender wallet: a bare NBitcoin key with a fake funding coin, enough to craft a
		// finalized BIP 78 original PSBT paying the receiver.
		using Key senderKey = new();
		Script senderScript = senderKey.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit);
		Transaction fundingTx = Transaction.Create(Network.Main);
		fundingTx.Outputs.Add(Money.Coins(1m), senderScript);
		Coin fundingCoin = new(fundingTx, 0);

		Transaction originalTx = Transaction.Create(Network.Main);
		originalTx.Inputs.Add(new TxIn(fundingCoin.Outpoint));
		originalTx.Outputs.Add(Money.Coins(0.3m), receiverAddress.ScriptPubKey);
		originalTx.Outputs.Add(Money.Coins(0.6999m), senderScript);
		PSBT originalPsbt = PSBT.FromTransaction(originalTx, Network.Main);
		originalPsbt.AddCoins(fundingCoin);
		originalPsbt.SignWithKeys(senderKey);
		originalPsbt.Finalize();

		using TestServices services = TestServices.Initialize();
		services.WaitForServicesReady();
		string directory = services.DirectoryUrl();
		string relay = services.OhttpRelayUrl();
		using OhttpKeys ohttpKeys = services.FetchOhttpKeys();

		// UseProxy=false: the relay/directory are in-process loopback listeners; on hosts with
		// HTTP(S)_PROXY set (e.g. a sandbox egress proxy) the default handler would send the
		// loopback request to the proxy and get its block page back (see snags.md W2).
#pragma warning disable CA2000 // Ownership of the handler is transferred to HttpClient.
		using HttpClient httpClient = new(new HttpClientHandler { UseProxy = false }, disposeHandler: true);
#pragma warning restore CA2000

		// Receiver session init, persisted.
		string sessionId;
		using (var store = PayjoinSessionStore.FromFile(dbPath))
		{
			sessionId = store.CreateSession("test-wallet", receiverAddress.ToString());
			var persister = new SqliteReceiverSessionPersister(store, sessionId);
			using var builder = new ReceiverBuilder(receiverAddress.ToString(), directory, ohttpKeys);
			using InitialReceiveTransition initTransition = builder.Build();
			using Initialized initialized = initTransition.Save(persister);
			using PjUri pjUri = initialized.PjUri();

			// Sender posts the original proposal through the OHTTP relay. The PjUri object is
			// used directly: round-tripping through the BIP 21 string uppercases the pj= URL
			// (QR convention) and the test relay's gateway check rejects it (see snags.md W2).
			var senderPersister = new InMemorySenderPersister();
			using var senderBuilder = new SenderBuilder(originalPsbt.ToBase64(), pjUri);
			using InitialSendTransition senderTransition = senderBuilder.BuildRecommended(1000);
			using WithReplyKey sender = senderTransition.Save(senderPersister);
			using RequestOhttpContext senderRequest = sender.CreateV2PostRequest(relay);
			byte[] senderResponse = await PostAsync(httpClient, senderRequest.Request);
			using WithReplyKeyTransition postedTransition = sender.ProcessResponse(senderResponse, senderRequest.OhttpCtx);
			using PollingForProposal polling = postedTransition.Save(senderPersister);

			// Receiver polls until the original payload arrives, then walks the checks up to
			// OutputsUnknown: broadcast sanity, inputs-not-owned, inputs-not-seen.
			UncheckedOriginalPayload? payload = null;
			for (int attempt = 0; attempt < 5 && payload is null; attempt++)
			{
				using RequestResponse pollRequest = initialized.CreatePollRequest(relay);
				byte[] pollResponse = await PostAsync(httpClient, pollRequest.Request);
				using InitializedTransition pollTransition = initialized.ProcessResponse(pollResponse, pollRequest.ClientResponse);

				// Disposing the outcome would dispose Inner with it; keep the payload alive
				// and dispose only the stasis wrapper.
				InitializedTransitionOutcome outcome = pollTransition.Save(persister);
				if (outcome is InitializedTransitionOutcome.Progress progress)
				{
					payload = progress.Inner;
				}
				else
				{
					outcome.Dispose();
				}
			}

			Assert.NotNull(payload);
			using UncheckedOriginalPayloadTransition checkedTransition = payload.CheckBroadcastSuitability(250, new PayjoinWalletCallbacks.TransactionSanityChecker(Network.Main));
			using MaybeInputsOwned maybeOwned = checkedTransition.Save(persister);
			using MaybeInputsOwnedTransition notOwnedTransition = maybeOwned.CheckInputsNotOwned(new PayjoinWalletCallbacks.ScriptOwnershipChecker(keyManager));
			using MaybeInputsSeen maybeSeen = notOwnedTransition.Save(persister);
			using MaybeInputsSeenTransition notSeenTransition = maybeSeen.CheckNoInputsSeenBefore(new PayjoinWalletCallbacks.InputsSeenChecker(store));
			using OutputsUnknown outputsUnknown = notSeenTransition.Save(persister);
			payload.Dispose();

			// The sender's input is now recorded in the persistent inputs-seen store.
			Assert.False(store.TryInsertInputSeen(fundingCoin.Outpoint));
		}

		// "Kill" the app mid-chain: everything above is dropped; a fresh store over the same
		// database must replay to exactly OutputsUnknown, and the chain continues from there.
		using (var store = PayjoinSessionStore.FromFile(dbPath))
		{
			var persister = new SqliteReceiverSessionPersister(store, sessionId);
			using ReplayResult replay = PayjoinMethods.ReplayReceiverEventLog(persister);
			using ReceiveSession state = replay.State();
			var resumed = Assert.IsType<ReceiveSession.OutputsUnknown>(state);

			using OutputsUnknownTransition identifyTransition = resumed.Inner.IdentifyReceiverOutputs(new PayjoinWalletCallbacks.ScriptOwnershipChecker(keyManager));
			using WantsOutputs wantsOutputs = identifyTransition.Save(persister);
			using WantsOutputsTransition commitOutputsTransition = wantsOutputs.CommitOutputs();
			using WantsInputs wantsInputs = commitOutputsTransition.Save(persister);

			using InputPair selected = wantsInputs.TryPreservingPrivacy([PayjoinWalletCallbacks.ToInputPair(contributionCoin)]);
			Assert.Equal(contributionCoin.TransactionId.ToString(), selected.Outpoint().Txid);
			using WantsInputs contributed = wantsInputs.ContributeInputs([selected]);
			using WantsInputsTransition commitInputsTransition = contributed.CommitInputs();
			using WantsFeeRange wantsFeeRange = commitInputsTransition.Save(persister);

			using WantsFeeRangeTransition feeTransition = wantsFeeRange.ApplyFeeRange(null, 1000);
			using ProvisionalProposal provisional = feeTransition.Save(persister);

			var signer = new PayjoinWalletCallbacks.ContributedInputSigner(Network.Main, keyManager, password: "", [contributionCoin]);
			using ProvisionalProposalTransition finalizeTransition = provisional.FinalizeProposal(signer);
			using PayjoinProposal proposal = finalizeTransition.Save(persister);

			// The proposal must contain the contributed input, finalized (receiver-signed),
			// and still pay the receiver.
			PSBT proposalPsbt = PSBT.Parse(proposal.Psbt(), Network.Main);
			PSBTInput contributedInput = Assert.Single(proposalPsbt.Inputs, x => x.PrevOut == contributionCoin.Outpoint);
			Assert.NotNull(contributedInput.FinalScriptWitness);
			Assert.Contains(proposalPsbt.Outputs, x => x.ScriptPubKey == receiverAddress.ScriptPubKey);

			// And the replayed session is now at PayjoinProposal.
			using ReplayResult finalReplay = PayjoinMethods.ReplayReceiverEventLog(persister);
			using ReceiveSession finalState = finalReplay.State();
			Assert.IsType<ReceiveSession.PayjoinProposal>(finalState);
		}
	}

	/// <summary>
	/// A coin reserved for a payjoin contribution must be unavailable to every other flow
	/// (send, coinjoin, other payjoin sessions all select through <see cref="CoinsView.Available"/>).
	/// </summary>
	[Fact]
	public void PayjoinReservation_MakesCoinUnavailable()
	{
		SmartCoin coin = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(ServiceFactory.CreateKeyManager()), 0.1m);
		Assert.True(coin.IsAvailable());

		coin.PayjoinInProgress = true;

		Assert.False(coin.IsAvailable());
		Assert.Empty(new CoinsView([coin]).Available());

		coin.PayjoinInProgress = false;
		Assert.True(coin.IsAvailable());
	}

	private static async Task<byte[]> PostAsync(HttpClient client, Request request)
	{
		using var content = new ByteArrayContent(request.Body);
		content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(request.ContentType);
		using HttpResponseMessage response = await client.PostAsync(request.Url, content);
		return await response.Content.ReadAsByteArrayAsync();
	}
}
