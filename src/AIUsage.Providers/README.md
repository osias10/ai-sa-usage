# Provider adapters

The usage adapters invoke the original vendor runtimes provisioned by `AIUsage.Infrastructure`. They do not read browser cookies, copy tokens between accounts, implement private HTTP quota endpoints, or create model turns for usage/credit queries. The widget reads explicit subscription quota and account credit fields separately. Missing values stay unknown; token counts, costs, credits and response prose are never converted into a subscription percentage.

## Claude

Each widget account has its own `CLAUDE_CONFIG_DIR`. `auth login`, `auth status` and `auth logout` remain owned by Claude. `auth status` must identify a Claude subscription login and confirm the expected configuration directory before polling.

Polling runs `--safe-mode -p /usage --output-format stream-json --verbose --no-session-persistence`. The parser reads `assistant.usage_report.rate_limits.limits[]`, including scoped rows, and optional `extra_usage`. The latter contains enabled state, minor-unit overage spend (`used_credits`), monthly spending cap (`monthly_limit`), and optional currency. It is not a purchased balance: no balance is derived from cap minus spend, and session `total_cost_usd` is ignored. Explicit USD amounts are divided by 100; other or missing currencies retain explicitly labelled minor units. The adapter requires at least version 2.1.277; runtime provisioning currently pins 2.1.288. The fixture shape comes from the published SDK declaration, not from a captured customer session.

Child processes explicitly clear both inherited nonessential-traffic umbrella variables and disable auto-updates, telemetry and error reporting individually (`DISABLE_AUTOUPDATER`, `DISABLE_TELEMETRY`, `DISABLE_ERROR_REPORTING`). They also clear `CLAUDE_CODE_ENABLE_TELEMETRY`. The 2.1.288 shared OAuth transport rejects this quota request with `essential-traffic-only` when the umbrella setting is enabled. Its `fetchUtilization: GET` diagnostic is emitted before that transport check, so that line alone does not establish an actual network request. The restriction is unrelated to safe mode, login validity or subscription tier.

Scoped checks with a connected Max account reproduced the failure after the native 60-second cache expired: the original settings returned a null usage report; the individual settings returned HTTP 200 and three quota rows. Further actual endpoint reads with the individual settings also succeeded, with no model turns or cost. Cached successes alone do not establish a fresh endpoint result.

The CLI can return a successful command result with `rate_limits: null` after a failed quota fetch. The adapter keeps that state unavailable and asks the user to retry; it does not infer a plan or login problem, start reauthentication, generate a model response, or report zero usage.

References: [CLI authentication](https://code.claude.com/docs/en/authentication), [CLI reference](https://code.claude.com/docs/en/cli-reference), [SDK 0.3.273 structured usage release](https://github.com/anthropics/claude-agent-sdk-typescript/releases/tag/v0.3.273), [SDK 0.3.277 live usage rows](https://github.com/anthropics/claude-agent-sdk-typescript/releases/tag/v0.3.277), [published 0.3.277 declarations](https://unpkg.com/@anthropic-ai/claude-agent-sdk@0.3.277/sdk.d.ts).

## Codex

Optional credit data is parsed separately from quota windows: bucket `credits` exposes a string `balance`, boolean `hasCredits`, and boolean `unlimited`. Values are credits, not currency. The multi-bucket view is authoritative when present; legacy balances are not duplicated or summed. The account-level `rateLimitResetCredits.availableCount` is shown separately as reset credits; no redemption method is invoked. The pinned 0.157.0 generated schema and [official OpenAI app-server documentation](https://learn.chatgpt.com/docs/app-server) establish these fields.

Each account gets a separate `CODEX_HOME`. The private stdio app-server receives `initialize`, `initialized`, `account/login/start`, `account/read`, `account/rateLimits/read` and, when explicitly disconnected, `account/logout`. Browser authorization uses the URL returned by the official server. No thread or turn RPC is sent. API-key accounts are rejected.

`cli_auth_credentials_store="keyring"` is explicit. In the pinned 0.157.0 source, the direct keyring entry is scoped to a SHA-256 hash of the canonical `CODEX_HOME` path, so separate account directories do not share the default CLI's entry. Keyring errors do not fall back to plaintext auth in this mode. The app never opens those entries itself.

References: [official app-server API](https://developers.openai.com/codex/app-server), [pinned credential storage implementation](https://github.com/openai/codex/blob/rust-v0.157.0/codex-rs/login/src/auth/storage.rs).

## Antigravity

Credit refresh additionally uses the native `-p /credits --output-format json` command inside the same before/after identity check as `/usage`. The pinned 1.3.1 `printmode.creditsJSON` struct contains required `remaining_credits:int64`, optional `plan_name:string` and `upgrade_uri:string`. Only an explicit successful zero-turn `credits` envelope with a nonnegative integer balance is accepted. No currency or total credit cap is inferred, and the returned upgrade URL is not followed. A credit-only error preserves quotas, clears the unavailable current balance and reports a separate message. Credit rate limits defer further credit calls for the supplied retry period. These fixtures come from checksum-verified static metadata, not authenticated customer output; real balances have not been compared with the provider UI.

Antigravity uses the current account in its own shared native credential store. The adapter serializes its operations and validates identity immediately before and after `/usage`; an account change invalidates the widget's previous snapshot. Removing the widget card does not sign the official Antigravity applications out.

Polling runs `-p /usage --output-format json`. The parser requires `status: "SUCCESS"`, `num_turns: 0`, `command.name: "usage"`, and reads `command.data.groups[].buckets[]`. Quota buckets expose a remaining fraction and optional reset timestamp; disabled and amount-only buckets do not become a percentage. This native command shape differs from the statusline schema. It contains no account email or subscription plan.

The 1.3.1 runtime has an internal native auth entry point, `AGY_CLI_CDE_AUTH_ACTION=check|login`, before normal agent startup. This is **version-specific, not a documented stable public interface**. The adapter requires exactly 1.3.1 and disables that runtime's automatic updater. `check` uses the vendor's read-only auth path and returns `Already authenticated as <email>.`; static control-flow inspection verified that it skips the interactive token acquirer. `login` uses the vendor OAuth path in an invisible Windows pseudoconsole, because a redirected process cannot perform first-time Antigravity login. URLs are restricted to official sign-in hosts, with an optional GUI code-entry dialog. Terminal output is never logged. `AGY_ADC_AUTH` is removed from the child environment to prevent an unrelated cloud-credential path.

The auth output and quota fixtures were reconstructed from the SHA-512-verified official 1.3.1 Windows runtime's native command code and JSON struct tags. They are not authenticated user captures. Any runtime update must revalidate these contracts before changing the pinned version; unrecognized output fails closed.

References: [native headless command release, 1.1.11](https://antigravity.google/docs/changelog), [usage command](https://antigravity.google/docs/cli/commands/usage), [headless operation](https://antigravity.google/docs/cli/headless), [original inspected Windows runtime](https://storage.googleapis.com/antigravity-public/antigravity-cli/1.3.1-4582356770750464/windows-x64/cli_windows_x64.exe).

## Validation status

Provider tests exercise the documented/pinned response contracts, malformed data, missing values, native account identity, account switching and server retry hints. A fresh isolated ephemeral Codex profile passed the real stdio initialization and unauthenticated `account/read` handshake without touching the user's credentials. Infrastructure separately tests the hidden pseudoconsole with a local non-authenticating child.

Initial implementation used no existing user credentials, live usage or model requests. A subsequent scoped Claude Max check through the widget's own connected profile confirmed successful native quota reads without model turns; application code did not read raw credentials. Other providers' browser OAuth and live subscriber quotas still need acceptance checks with deliberately connected test accounts. Synthetic contract tests are not a substitute for those checks.
