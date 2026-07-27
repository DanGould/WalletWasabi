# WalletWasabi — BIP 77 async payjoin integration (branch `agent/bip77-payjoin`)

Agent-authored subproject rules for this worktree. Base: WalletWasabi master `37285eeb8`.
Recon: `/home/claude-agent/payjoin/briefs/2026-07-19/wasabi-integration/` (read both recon
files before touching sender/receiver code). Deferred tests: `TODO-TESTS.md` (worktree root).

## Verification commands (verified on valley 2026-07-19)

Use the rust-payjoin `#csharp` devShell for dotnet (SDK 10.0.x; satisfies `global.json`
10.0.100 + rollForward). Do NOT use Wasabi's own devShell (`nix develop .`) from agent
sessions — its shellHook copies tor/hwi/bitcoind over checked-in `BundledApps/Binaries/`
files and dirties the worktree.

```bash
# Build (core lib + tests project; ~48 s wall incl. shell entry, warm restore):
nix develop /home/claude-agent/payjoin/rust-payjoin#csharp --command bash -c \
  'cd /home/claude-agent/payjoin/external-integrations/WalletWasabi-wt-bip77 && dotnet build WalletWasabi.Tests/WalletWasabi.Tests.csproj'

# Fast payjoin-scoped test loop (~10 s wall; covers UnitTests/Payjoin/* + legacy BIP 78 tests):
... dotnet test WalletWasabi.Tests/WalletWasabi.Tests.csproj --filter "FullyQualifiedName~UnitTests.Payjoin|FullyQualifiedName~UnitTests.Transactions.PayjoinTests"

# Unit-test gate (what CI runs inside nix; ~7 min 20 s wall on valley):
... dotnet test WalletWasabi.Tests/WalletWasabi.Tests.csproj --filter "FullyQualifiedName~UnitTests"
# KNOWN valley-only failures at base 37285eeb8 (975 passed / 11 failed, verified 2026-07-19):
#  - 10x UnitTests.Hwi.*: bundled hwi is a generic-linux dynamic binary, cannot exec on
#    NixOS ("NixOS cannot run dynamically linked executables"); nix CI swaps in nix hwi.
#  - 1x RegisterCoinIdempotencyAsync (WabiSabi stutterer): fails deterministically here.
# Valley dev-loop filter that excludes exactly the known-bad set (syntax verified):
... --filter "FullyQualifiedName~UnitTests&FullyQualifiedName!~UnitTests.Hwi&FullyQualifiedName!~RegisterCoinIdempotencyAsync"
# Any OTHER failure is yours. Do not let new failures hide behind the known 11.

# CI parity (the ONLY CI gate, .github/workflows/build.yml):
nix build --print-build-logs .#all
# VERIFIED GREEN on valley at base 37285eeb8 (2026-07-19, exit 0; ~11.5 min with warm
# nix cache — first cold run is substantially longer). All unit + integration tests
# pass inside the sandbox, including the 11 valley-host-only failures above.
# NOTE: .#all = build + UNIT **and** INTEGRATION tests (flake buildWithAllTests).
# .#default/.#unit-tests = unit only; .#integration-tests = integration only.
# Runs sandboxed (no network) — anything needing external binaries must be provisioned
# in the flake preBuild like bitcoind is; new integration tests that can't run there
# must be trait-excluded to match the flake's checkPhase filter (meta decision E3).
```

## payjoin-cli integration harness (lane W3; verified on valley 2026-07-19)

Lives in `WalletWasabi.IntegrationTests/Payjoin/`, all tests
`[Trait("Category", "PayjoinHarness")]`. EXCLUDED from the sandboxed
`nix build .#all` checkPhase (`--filter "Category!=PayjoinHarness"` in flake.nix — a
canary test fails the build if that filter is ever dropped). Valley-only, hermetic,
loopback-only; spawns regtest bitcoind + 2 payjoin-mailroom processes (directory and
relay MUST be separate instances: shared sentinel tag 403s same-instance self-loops) +
payjoin-cli per role.

```bash
# One-time: build fixture binaries from the PINNED worktree (~25 s warm):
nix develop /home/claude-agent/payjoin/rust-payjoin#csharp --command bash -c \
  'cd /home/claude-agent/payjoin/rust-payjoin-wt-wasabi-ffi && cargo build -p payjoin-cli --features _manual-tls,v1 -p payjoin-mailroom'

# One-time: build the TLS TestServices shim (in-repo, ~27 s warm; see contrib/payjoin-fixture/README.md):
nix develop /home/claude-agent/payjoin/rust-payjoin#csharp --command bash -c \
  'cd /home/claude-agent/payjoin/external-integrations/WalletWasabi-wt-bip77 && cargo build --manifest-path contrib/payjoin-fixture/Cargo.toml'

# Run the harness (~31 s wall; needs the #csharp shell for BITCOIND_EXE — the bundled
# generic-linux bitcoind cannot exec on NixOS; overrides: PAYJOIN_CLI_BIN, PAYJOIN_MAILROOM_BIN):
nix develop /home/claude-agent/payjoin/rust-payjoin#csharp --command bash -c \
  'cd /home/claude-agent/payjoin/external-integrations/WalletWasabi-wt-bip77 && dotnet test WalletWasabi.IntegrationTests/WalletWasabi.IntegrationTests.csproj --filter "Category=PayjoinHarness"'
```

Environment traps the harness already handles (do not "simplify" them away):
- Host proxy env (`http_proxy=127.0.0.1:3128`) intercepts loopback HTTP and 403s it.
  The fixture nulls `HttpClient.DefaultProxy` process-wide and scrubs proxy vars from
  every child process env.
- OHTTP-keys are pre-fetched from the directory `/ohttp-keys` into a file passed to the
  receiver (`--ohttp-keys`-equivalent config): the relay-proxied bootstrap only works via
  CONNECT/WS tunneling to https gateways; over plain HTTP the combined directory+relay
  binary answers proxy-form GETs with its OWN keys ("key identifier unknown" errors).
- Two topologies: plain-HTTP (stock mailroom binaries — degradation path) AND TLS via
  `contrib/payjoin-fixture` (TestServices shim; approved meta answer in
  `briefs/2026-07-19/wasabi-integration/questions-W3.md`). The standalone mailroom
  binary cannot serve manual TLS (`serve_manual_tls` is a `_manual-tls` library fn
  main() never calls) and its relay outgoing client trusts webpki roots only.
- payjoin-cli's config-file `root_certificate` key is silently ignored at the pin —
  pass `--root-certificate` as a CLI flag (the driver does).
- Out-of-workspace cargo builds do NOT inherit rust-payjoin's `[patch.crates-io]`:
  any crate depending on payjoin-test-utils/payjoin-mailroom by version silently pulls
  crates.io releases instead of the pin. The shim's Cargo.toml mirrors the patch.

Pre-commit hook: `.githooks/pre-commit` (junk-blocker + reminders). Install once per clone:
`cp .githooks/pre-commit "$(git rev-parse --git-path hooks)/pre-commit" && chmod +x $_`
Real gates run pre-handoff, not per-commit (no fast repo-native format gate exists).

## payjoin-ffi artifacts (META DECISION E1 — pinned source)

Build ONLY from the pinned worktree `/home/claude-agent/payjoin/rust-payjoin-wt-wasabi-ffi`
(detached at upstream master `d27b7b137c9e8696ad6bf542ba0c1bf93665df72`, 2026-07-19).
Do NOT build from `/home/claude-agent/payjoin/rust-payjoin` — that checkout is on the
unrelated feature branch `mailroom-caps-bridge`.

Generate (~3 min):
`nix develop /home/claude-agent/payjoin/rust-payjoin#csharp --command bash -c 'cd /home/claude-agent/payjoin/rust-payjoin-wt-wasabi-ffi/payjoin-ffi/csharp && bash ./scripts/generate_bindings.sh'`
→ `payjoin-ffi/csharp/src/payjoin.cs` (namespace `Payjoin`) + `payjoin-ffi/csharp/lib/libpayjoin_ffi.so`.
ffi suite at the pin: 26/27 pass (15 s). KNOWN failure `IntegrationTests.TestIntegrationV2ToV2`
("Unexpected response size 3107, expected 8192 bytes") — valley-environment artifact or
flake, NOT an upstream regression (upstream CSharp CI green 2026-07-17); logged in
meta snags.md. If your work hits 8192-byte padding errors, start there.
Never commit anything in rust-payjoin or the pinned worktree from this lane.

## Architecture constraints (from recon; keep unless Dan overrules)

- **Sender seam**: implement BIP 77 behind `IPayjoinClient`
  (`WalletWasabi/WebClients/PayJoin/`). Constructed in `SendViewModel.GetPayjoinClient`;
  invoked synchronously inside `TransactionFactory.TryNegotiatePayjoin` — bounded poll
  window there; long-lived sender sessions belong in the background manager.
- **Receiver**: net-new `PayjoinManager : BackgroundService` following the
  `CoinJoinManager` pattern (`WalletWasabi/WabiSabi/Client/CoinJoin/Manager/`), registered
  in `WalletWasabi.Client/Global.cs` `InitializeAsync` via `HostedServices.Register<>`;
  exposed to UI through `Services`/`IServices`. `PeriodicRunner` is the poll-loop base.
- **HTTP**: ALL bytes flow through Wasabi's `WasabiHttpClientFactory` /
  `UiContext.Services.CreateHttpClient(name)` (Tor stream isolation per client name).
  payjoin-ffi is a pure state machine — never let it own transport.
- **JSON**: System.Text.Json (in-box) for new code. Newtonsoft is only transitive via
  NBitcoin; do not add it as a direct dependency.
- **PSBT boundary**: NBitcoin `PSBT.Parse`/`.ToBase64()` ↔ ffi base64 strings.
- **Session persistence**: event-sourced append-only log (Json*SessionPersister contract);
  Microsoft.Data.Sqlite already in-tree for storage.
- **Errors/UI**: inline `IValidationErrors.Add` for pre-flight; `ShowErrorAsync` +
  `ToUserFriendlyString()` mapping for send-blocking failures; log-and-fallback for
  negotiation failures (existing silent-degradation precedent). Keep the hardware-wallet
  payjoin block in `SendViewModel.ValidateToField`.

## Dependency/packaging rules (break these and CI fails in non-obvious ways)

- **Central package management**: versions ONLY in `Directory.Packages.props`.
- **NuGet locked mode**: app projects (`WalletWasabi`, `.Client`, `.Fluent`, `.Daemon`,
  `.Fluent.Desktop`, `.Fluent.Generators`) have `RestorePackagesWithLockFile` +
  `packages.lock.json` — adding/changing a package there requires
  `dotnet restore --force-evaluate` to regenerate locks. `WalletWasabi.Tests` has NO lock file.
- **nix deps.json**: root `deps.json` is the nix NuGet lockfile
  (`nugetDeps = ./deps.json`). Any NuGet dep change must regenerate it:
  `nix build .#packages.x86_64-linux.all.passthru.fetch-deps` (needs network). Plain source
  files (e.g. vendored `payjoin.cs` + native .so as content) do NOT touch deps.json.
- **NuGetAudit** `all`/`low` + `TreatWarningsAsErrors=true`: a new package with any known
  advisory fails the build. Preferred payjoin-ffi consumption: source inclusion
  (`<Compile Include>` of generated payjoin.cs + `<None CopyToOutputDirectory>` for the
  .so, BTCPay-plugin style) — zero NuGet surface. payjoin.cs needs `AllowUnsafeBlocks`.

## Do not break

- `TreatWarningsAsErrors=true`, `Nullable=enable`, `LangVersion 14`, `AnalysisLevel latest`
  (Directory.Build.props) — leave as-is; new code must be warning-clean.
- `.editorconfig` conventions (tabs for indentation in .cs; file-scoped namespaces).
  No repo-wide reformatting; no CodeMaid churn in diffs.
- `BannedSymbols.txt` (BannedApiAnalyzers) — banned APIs fail the build.
- Existing BIP 78 behavior stays green: `WalletWasabi.Tests/UnitTests/Transactions/PayjoinTests.cs`.

## Process

- Tests: new tests in `WalletWasabi.Tests/UnitTests/Payjoin/` (stubs in
  `Bip77PayjoinTests.cs` — un-skip as features land). Behavior-changing commits need a
  test or a `TODO-TESTS.md` entry. Integration harness = lane W3 (see TODO-TESTS.md).
- No pushes, no PRs from agent sessions. End with /handoff.
