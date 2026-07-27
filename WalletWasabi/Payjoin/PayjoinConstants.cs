using System.Collections.Generic;

namespace WalletWasabi.Payjoin;

/// <summary>BIP 77 payjoin defaults. Kept in one file: the relay set is a trust/privacy
/// statement that gets an explicit sign-off before any upstream PR (questions-W1 Q3),
/// so the sign-off diff stays one hunk.</summary>
public static class PayjoinConstants
{
	/// <summary>
	/// OHTTP relays used to reach the payjoin directory (payjo.in-ecosystem set, matching
	/// what Bull Bitcoin Mobile and Cake Wallet ship). Order is randomized per session;
	/// a relay that fails transport is quarantined for the session and the next is tried.
	/// </summary>
	public static readonly IReadOnlyList<string> DefaultOhttpRelays =
	[
		"https://pj.bobspacebkk.com",
		"https://ohttp.achow101.com",
		"https://ohttp.cakewallet.com",
	];

	/// <summary>How long the send confirm flow polls for a proposal before degrading to a
	/// plain send (questions-W1 Q1, option A).</summary>
	public static readonly TimeSpan DefaultPollWindow = TimeSpan.FromSeconds(60);
}
