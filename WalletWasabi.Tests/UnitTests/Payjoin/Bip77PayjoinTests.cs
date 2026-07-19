using NBitcoin;
using WalletWasabi.Userfacing;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Payjoin;

/// <summary>
/// Tests for the BIP 77 (async payjoin) integration.
/// Skipped tests are stubs for behavior that lands with the payjoin-ffi-driven sender/receiver;
/// active tests pin down current behavior the integration builds on.
/// </summary>
public class Bip77PayjoinTests
{
	/// <summary>
	/// A BIP 77 <c>pj=</c> value is a directory URL whose fragment carries the receiver's
	/// ephemeral parameters (<c>ohttp=</c> OHTTP keys, <c>ex=</c> expiry, <c>rk=</c> reply key).
	/// Inside a BIP 21 URI the whole value is percent-encoded; <see cref="AddressParser"/> must
	/// hand back the decoded URL, fragment intact, so the sender can feed it to payjoin-ffi.
	/// This pins the current parser behavior (HttpUtility.ParseQueryString percent-decodes).
	/// </summary>
	[Fact]
	public void AddressParser_PreservesBip77PjUrlFragmentParameters()
	{
		// BIP 77-shaped pj URL: directory + fragment params, percent-encoded as a BIP 21 query value.
		string pjUrl = "https://payjo.in/AbCd1234#ex=1747053654&ohttp=AQAg3c6WmZgWmVvhQXYFDYt6DzX9zSoDcyQdY3-Ln6BTgxkABAABAAM&rk=CtWViVSFmJDnzqhKAywgBpiaTIfrvQrCiUn6P6nGeHA";
		string encodedPjUrl = Uri.EscapeDataString(pjUrl);
		string bip21 = $"bitcoin:tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx?amount=0.00010727&pj={encodedPjUrl}";

		var result = AddressParser.Parse(bip21, Network.TestNet).Value;

		var uri = Assert.IsType<Address.Bip21Uri>(result);
		Assert.Equal(pjUrl, uri.PayjoinEndpoint);
		Assert.Equal(0.00010727m, uri.Amount);
	}

	/// <summary>BIP 21 without a pj parameter yields no payjoin endpoint.</summary>
	[Fact]
	public void AddressParser_NoPjParameter_YieldsNullEndpoint()
	{
		var result = AddressParser.Parse("bitcoin:tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx?amount=1", Network.TestNet).Value;

		var uri = Assert.IsType<Address.Bip21Uri>(result);
		Assert.Null(uri.PayjoinEndpoint);
	}

	[Fact(Skip = "Stub: lands with the payjoin-ffi BIP 77 sender. Persist sender session events via JsonSenderSessionPersister, replay with ReplaySenderEventLog, and assert the session resumes in the same state (WithReplyKey / PollingForProposal) with the same fallback tx.")]
	public void SenderSession_PersistAndReplay_ResumesState()
	{
	}

	[Fact(Skip = "Stub: lands with the PayjoinManager receiver. Persist receiver session events via JsonReceiverSessionPersister, replay with ReplayReceiverEventLog, and assert the session resumes (Initialized/typestate) and pj_uri/fallback_tx round-trip.")]
	public void ReceiverSession_PersistAndReplay_ResumesState()
	{
	}

	[Fact(Skip = "Stub: lands with BIP 77 error surfacing. Map payjoin-ffi errors (ResponseError well-known codes: unavailable, not-enough-money, version-unsupported, original-psbt-rejected; replay/persisted errors incl. expiry) to user-friendly strings via ToUserFriendlyString-style mapping; transient vs fatal must not read the same.")]
	public void PayjoinErrors_MapToUserFriendlyStrings()
	{
	}
}
