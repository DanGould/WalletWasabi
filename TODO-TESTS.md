# Deferred tests — BIP 77 async payjoin integration

Unit-test stubs live in `WalletWasabi.Tests/UnitTests/Payjoin/Bip77PayjoinTests.cs`
(three `Skip`'d facts: sender persistence replay, receiver persistence replay, error
mapping). They un-skip as the corresponding code lands. The tests below cannot be
written as unit tests at all and are deferred to the integration harness lane (W3).

## Integration tests (blocked on harness lane W3)

| Test | Description | Blocked by |
|---|---|---|
| Sender round trip | Wasabi sender pays a `payjoin-cli` receiver through payjoin-mailroom (directory+relay) on regtest bitcoind; assert payjoin tx (sender+receiver inputs) confirms and wallet balance is correct. | W3 harness: spawn payjoin-cli + payjoin-mailroom + regtest bitcoind from `WalletWasabi.IntegrationTests` (follow `BitcoindRpcProcessBridge` process-spawn pattern). |
| Receiver round trip | Wasabi `PayjoinManager` produces a BIP 21 + `pj=` URI; `payjoin-cli` sender pays it; assert receiver input contribution and settlement detection (`Monitor`/`check_for_transaction`). | Same W3 harness; also requires PayjoinManager to exist. |
| Sender kill/resume | Kill the app between POST and proposal poll; restart; assert `ReplaySenderEventLog` resumes polling and completes (or falls back to original tx on expiry). | W3 harness + sender session persistence. |
| Receiver kill/resume | Kill the app after session init (URI already shown); restart; assert `ReplayReceiverEventLog` resumes the directory poll and the payment still completes. | W3 harness + receiver session persistence. |
| Expiry/fallback | Let a sender session expire with the receiver offline; assert fallback tx broadcast policy and user-visible outcome. | W3 harness + fallback policy decision. |

Notes for W3:
- `nix build .#all` runs `WalletWasabi.IntegrationTests` in the nix sandbox (no network);
  any harness needing payjoin-cli/mailroom binaries must either bundle them like
  bitcoind (`BundledApps/Binaries/`, provisioned in flake `preBuild`) or the new tests
  must be excluded from the sandboxed run.
- payjoin-ffi's in-process `TestServices` (OHTTP relay + directory + test cert, behind
  `_test-utils` feature) is a lighter-weight alternative to spawning payjoin-mailroom.
