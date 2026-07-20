# Deferred tests — BIP 77 async payjoin integration

Unit-test stubs live in `WalletWasabi.Tests/UnitTests/Payjoin/Bip77PayjoinTests.cs`.
Status: **receiver persistence replay is un-skipped and real** (W2); sender replay and
error mapping remain Skip'd for W1. W2 also added `PayjoinSessionStoreTests` (inputs-
seen probe defense, event-log semantics) and `Bip77ReceiverTypestateWalkTests` (full
receiver chain against in-process TestServices with kill/resume + coin reservation).
The tests below cannot be written as unit tests at all and are deferred to the
integration harness lane (W3).

## Deferred UI test (W2)

The receive-flow payjoin opt-in (toggle -> session start -> QR/copy swap to pj URI ->
status line) has no headless Avalonia test; the VM logic is thin over PayjoinManager
(which is covered), but an Avalonia.Headless.XUnit test of ReceiveAddressViewModel
with a mocked IWalletModel.Payjoin would pin the degrade-to-plain-address path.
Deferred: needs a mockable seam for WalletPayjoinModel (currently a concrete class
over a live manager).

## Integration tests (blocked on harness lane W3)

| Test | Description | Blocked by |
|---|---|---|
| Sender round trip | Wasabi sender pays a `payjoin-cli` receiver through payjoin-mailroom (directory+relay) on regtest bitcoind; assert payjoin tx (sender+receiver inputs) confirms and wallet balance is correct. | W3 harness: spawn payjoin-cli + payjoin-mailroom + regtest bitcoind from `WalletWasabi.IntegrationTests` (follow `BitcoindRpcProcessBridge` process-spawn pattern). |
| Receiver round trip | Wasabi `PayjoinManager` produces a BIP 21 + `pj=` URI; `payjoin-cli` sender pays it; assert receiver input contribution and settlement detection (`Monitor`/`check_for_transaction`). | Same W3 harness; also requires PayjoinManager to exist. |
| Sender kill/resume | Kill the app between POST and proposal poll; restart; assert `ReplaySenderEventLog` resumes polling and completes (or falls back to original tx on expiry). | W3 harness + sender session persistence. |
| Receiver kill/resume | Kill the app after session init (URI already shown); restart; assert `ReplayReceiverEventLog` resumes the directory poll and the payment still completes. | W3 harness + receiver session persistence. |
| Expiry/fallback | Let a sender session expire with the receiver offline; assert fallback tx broadcast policy and user-visible outcome. | W3 harness + fallback policy decision. |
| Sender happy-path proposal + poll-window degrade (W1 addendum) | The states past `WithReplyKey` need valid OHTTP-encapsulated directory responses, which cannot be fabricated offline: proposal receipt (`Progress` → payjoin tx signed/broadcast), stasis long-poll behavior, and the 60 s window-end typed-cancel degrade (`Bip77PayjoinClient`) are only unit-tested up to the first response. Assert them end-to-end. | W3 harness (mock-HTTP unit tests cover relay failover, garbage response, dedup only). |
| Well-known receiver error mapping (W1 addendum) | BIP 78 well-known error codes (unavailable / not-enough-money / version-unsupported / original-psbt-rejected) reach the sender only inside a live encrypted response; assert each maps to its `FriendlyFfiMessage` string and shows in the downgrade dialog. | W3 harness (error objects are pointer-backed, not constructible from C#). |

Notes for W3:
- `nix build .#all` runs `WalletWasabi.IntegrationTests` in the nix sandbox (no network);
  any harness needing payjoin-cli/mailroom binaries must either bundle them like
  bitcoind (`BundledApps/Binaries/`, provisioned in flake `preBuild`) or the new tests
  must be excluded from the sandboxed run.
- payjoin-ffi's in-process `TestServices` (OHTTP relay + directory + test cert, behind
  `_test-utils` feature) is a lighter-weight alternative to spawning payjoin-mailroom.

## W3 resolution notes (2026-07-19)

The harness landed in `WalletWasabi.IntegrationTests/Payjoin/`
(`[Trait("Category", "PayjoinHarness")]`, excluded from the sandboxed `nix build .#all`
checkPhase via `--filter "Category!=PayjoinHarness"` in flake.nix; a sandbox canary test
keeps the exclusion honest). Binaries build from the pinned rust-payjoin worktree — see
worktree CLAUDE.md "payjoin harness" section for the exact commands.

Valley run (verified 2026-07-19, ~31 s wall after binaries built):

```bash
nix develop /home/claude-agent/payjoin/rust-payjoin#csharp --command bash -c \
  'cd <worktree> && dotnet test WalletWasabi.IntegrationTests/WalletWasabi.IntegrationTests.csproj --filter "Category=PayjoinHarness"'
```

Per-table status:

| Test | Status |
|---|---|
| Sender round trip (Wasabi sends) | **GREEN (WI integration)** — `WasabiSendsToCliReceiver_RoundTrip`: real coin via `WasabiWalletHarness`, production parse (`AddressParser`) + dispatch (`Bip77UriParams.IsBip77`), real `Bip77PayjoinClient` through the real `TransactionFactory` seam; negotiated payjoin broadcasts, cli receiver accepts and completes after confirmation. cli↔cli equivalent also GREEN. |
| Receiver round trip (Wasabi receives) | **GREEN (WI integration)** — `CliSendsToWasabiReceiver_AsyncCompletion`: real `PayjoinManager` opens the session, the manager instance is disposed (receiver offline), payjoin-cli pays into the void, a fresh manager over the same SQLite store replays and completes (contribution, signing, proposal post, settlement detection, reservation release). This IS the receiver kill/resume story end-to-end. |
| Sender kill/resume + Receiver kill/resume | cli↔cli both-sides version GREEN (`CliToCli_KilledMidSessionOnBothSides_ResumesFromPersistedStateAndCompletes`, mirrors payjoin-cli e2e choreography). Wasabi receiver-side restart-resume covered by `CliSendsToWasabiReceiver_AsyncCompletion` (manager disposed and re-created mid-session). Wasabi sender-side kill/resume (kill between POST and proposal poll, restart, `PayjoinSenderManager` sweep) deferred to lane W4's pending-send work — the W1 baseline cancels at window end rather than resuming. |
| Expiry/fallback | Infra-down variant GREEN (`CliSender_InfraUnreachable_SessionFailsResumableAndCancelBroadcastsFallback`: session fails with reason, cancel broadcasts fallback, invoice still paid). Timed-expiry variant deferred until the Wasabi fallback policy exists (payjoin-cli expiry markers exist: "Session expired"). |

TLS addendum (meta-approved shim, same date): `CliToCli_OverTls_RoundTripWithRelayKeyBootstrap`
(https directory, receiver bootstraps OHTTP keys through the relay CONNECT tunnel) and
`CSharpHttpClient_PinnedFixtureCert_BootstrapsOhttpKeysDirectlyAndViaRelayConnectTunnel`
(C# DER-pinned cert-trust + RFC 9540 bootstrap shape for Wasabi's transport) — both GREEN;
suite 6 passed / 2 W-blocked skips, ~37 s.

## WI integration notes (2026-07-20)

Both cross-lane stubs are implemented and GREEN (suite: 8 passed / 0 skipped, ~35 s).
`WasabiWalletHarness` builds a real Wallet stack (KeyManager/Wallet/stores) against the
harness bitcoind; coins enter via `TransactionProcessor.Process` (the mempool-arrival
path) instead of filter sync, which has its own integration tests. The W1-addendum rows
above (happy-path proposal receipt through the real client, window-end degrade against a
live-but-silent receiver) are partially discharged: `WasabiSendsToCliReceiver_RoundTrip`
covers the live happy path (Progress → signed payjoin broadcast); the live well-known
error-code mapping and timed window-end degrade remain deferred.
