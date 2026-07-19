using System;
using System.IO;
using WalletWasabi.BundledApps;

namespace WalletWasabi.IntegrationTests.Payjoin;

/// <summary>
/// Resolves the external binaries the payjoin harness spawns. These are NOT bundled with the
/// repository: payjoin-cli and payjoin-mailroom are built from the pinned rust-payjoin worktree
/// (upstream master d27b7b137c9e8696ad6bf542ba0c1bf93665df72, meta decision E1), and bitcoind
/// comes from the BITCOIND_EXE environment variable (set by the rust-payjoin nix devShells)
/// because the bundled generic-linux bitcoind cannot exec on NixOS hosts.
/// Tests using these binaries carry <c>[Trait("Category", "PayjoinHarness")]</c> and are excluded
/// from the sandboxed <c>nix build .#all</c> checkPhase, which has no network and none of these
/// binaries provisioned.
/// </summary>
public static class HarnessBinaries
{
	private const string PinnedWorktree = "/home/claude-agent/payjoin/rust-payjoin-wt-wasabi-ffi";

	private const string BuildInstruction =
		"Build it from the pinned rust-payjoin worktree: nix develop /home/claude-agent/payjoin/rust-payjoin#csharp " +
		"--command bash -c 'cd " + PinnedWorktree + " && cargo build -p payjoin-cli --features _manual-tls,v1 -p payjoin-mailroom'";

	public static string PayjoinCliPath => Resolve("PAYJOIN_CLI_BIN", Path.Combine(PinnedWorktree, "target", "debug", "payjoin-cli"));

	public static string MailroomPath => Resolve("PAYJOIN_MAILROOM_BIN", Path.Combine(PinnedWorktree, "target", "debug", "payjoin-mailroom"));

	/// <summary>The bundled bitcoind cannot exec on NixOS, so prefer BITCOIND_EXE when set.</summary>
	public static string BitcoindPath
	{
		get
		{
			string? fromEnv = Environment.GetEnvironmentVariable("BITCOIND_EXE");
			if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
			{
				return fromEnv;
			}

			return BundledAppHelpers.GetBinaryPath("bitcoind");
		}
	}

	private static string Resolve(string envVar, string defaultPath)
	{
		string path = Environment.GetEnvironmentVariable(envVar) is { Length: > 0 } fromEnv ? fromEnv : defaultPath;
		if (!File.Exists(path))
		{
			throw new FileNotFoundException($"'{Path.GetFileName(defaultPath)}' not found at '{path}' (override with {envVar}). {BuildInstruction}", path);
		}

		return path;
	}
}
