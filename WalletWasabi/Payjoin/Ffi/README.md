# Vendored payjoin-ffi C# bindings

Generated artifacts from [payjoin/rust-payjoin](https://github.com/payjoin/rust-payjoin),
vendored BTCPay-plugin style (source inclusion, no NuGet):

- `payjoin.cs` — uniffi-bindgen-cs output (namespace `Payjoin`), generated at
  rust-payjoin master `d27b7b137c9e8696ad6bf542ba0c1bf93665df72` (payjoin-ffi 0.24.0,
  payjoin 1.0.0-rc.5) with cargo features `csharp,_test-utils`. Local patch: a
  `#pragma warning disable CS0659, CS0108` header, because the generated exception
  types fail Wasabi's `TreatWarningsAsErrors` build otherwise. Re-apply on
  regeneration (upstream fix candidate).
- `Payjoin.Http.cs` — hand-written OHTTP-keys bootstrap helper copied verbatim from
  `payjoin-ffi/csharp/Payjoin.Http.cs` at the same commit.
- `libpayjoin_ffi.so` — linux-x64 cdylib built from the same commit with
  `cargo build --features csharp,_test-utils --release`, stripped. Copied to the
  build output root so `DllImport("payjoin_ffi")` resolves.

Only the linux-x64 native library is vendored: this branch is dev-reproducible on
Linux only; per-RID packaging is a release concern.

Regenerate:

```sh
cd rust-payjoin && git checkout d27b7b137c9e8696ad6bf542ba0c1bf93665df72
nix develop .#csharp --command bash -c 'cd payjoin-ffi/csharp && bash ./scripts/generate_bindings.sh'
# release native lib:
nix develop .#csharp --command bash -c 'cd payjoin-ffi && cargo build --features csharp,_test-utils --release'
strip rust-payjoin/target/release/libpayjoin_ffi.so
```
