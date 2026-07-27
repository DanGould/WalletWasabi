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

	/// <summary>Default upper bound (sat/vB) on the effective fee rate the receiver will
	/// accept for a proposed payjoin — a safety cap, well above any realistic fee
	/// environment, not a working limit. Overridable via the PayjoinMaxFeeRateSatPerVb
	/// config knob. Deliberately more conservative than the BTCPay plugin's 1000 sat/vB
	/// parity value; a dynamic estimator-based cap is a tracked follow-up.</summary>
	public const ulong DefaultMaxFeeRateSatPerVb = 250;
}
