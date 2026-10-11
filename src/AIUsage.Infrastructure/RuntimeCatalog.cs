using AIUsage.Core;

namespace AIUsage.Infrastructure;

/// <summary>
/// Exact upstream Windows x64 binaries reviewed with this application release.
/// Hashes are copied from the official manifests/release metadata, not learned from a download.
/// Upgrading a provider requires updating this catalog and checking its wire protocol fixtures.
/// </summary>
public sealed record RuntimeRelease(
    ProviderKind Provider, string Version, string FileName, Uri DownloadUri,
    string HashAlgorithm, string Digest, long? Size, Uri ManifestUri);

public static class RuntimeCatalog
{
    public static RuntimeRelease Get(ProviderKind provider) => provider switch
    {
        ProviderKind.Claude => new(provider, "2.1.288", "claude.exe",
            new("https://downloads.claude.ai/claude-code-releases/2.1.288/win32-x64/claude.exe"),
            "SHA256", "84304f7d4b0cd0ebcbe8318695a260151b48991a6659c3366fdd5da290c0ab91", 249149600,
            new("https://downloads.claude.ai/claude-code-releases/2.1.288/manifest.json")),
        ProviderKind.Codex => new(provider, "0.157.0", "codex.exe",
            new("https://github.com/openai/codex/releases/download/rust-v0.157.0/codex-x86_64-pc-windows-msvc.exe"),
            "SHA256", "ed1c7b36e44536809c868864c833af8a857f56599a7a7fe23b908a1ba1093b1f", 322525488,
            new("https://api.github.com/repos/openai/codex/releases/tags/rust-v0.157.0")),
        ProviderKind.Antigravity => new(provider, "1.3.1", "agy.exe",
            new("https://storage.googleapis.com/antigravity-public/antigravity-cli/1.3.1-4582356770750464/windows-x64/cli_windows_x64.exe"),
            "SHA512", "f346d2cd9d68e7e5395cb94ff95e7e8ab2e0c65cf2ad835d0eb671fb41f12f5fc713d7a26ceb4510678eb9baa62c6cd9ab8205211e3661c6cd1e23ca7bc69781", null,
            new("https://antigravity-cli-auto-updater-974169037036.us-central1.run.app/manifests/windows_amd64.json")),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
}
