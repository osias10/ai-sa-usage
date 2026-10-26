# AI SA

**Your AI subscription usage, right on your desktop.**

**English** · [한국어](README.ko.md)

AI SA is a small Windows widget for keeping an eye on your AI subscription limits. See how much you've used, when your limits reset, and which account needs attention without opening a usage page each time.

Windows 11 x64 · Desktop widget & system tray · 0.4.11 (stable)

**Stable release with usage charts and fixes for empty widget space after sleep:** [Download 0.4.11](https://github.com/osias10/ai-sa-usage/releases/tag/v0.4.11), or check for updates in **Settings → Info**. This release includes the changes from 0.4.11-beta.5 and is available with pre-release updates turned off.

**Preview: account credits in [0.4.12-beta.1](https://github.com/osias10/ai-sa-usage/releases/tag/v0.4.12-beta.1).** See Codex credit balances and reset credits, Antigravity remaining credits, and Claude extra-usage spending and monthly limits when provided. Credits appear on the account page; enable **위젯에 크레딧 표시** in each account's settings to show them in the widget too (off by default). To update in the app, enable **프리릴리스 업데이트 받기** in **Settings → Info**, then check for updates. Real account credit values have not yet been compared with the providers' usage pages.

## Screenshots

Screenshots use demo data in the Korean UI. No real account information is shown.

**Desktop widget · Dark theme**

![AI SA compact widget with usage gauges and reset countdowns for Claude, Codex, and Antigravity](docs/images/widget-dark.png)

**Settings · Light theme**

![AI SA account settings showing Claude session, weekly, and Fable usage with demo accounts](docs/images/settings-light.png)

## At a glance

- **Usage and reset times.** Percentages, progress bars, and a countdown to each reset. Switch between used and remaining capacity.
- **Choose what matters.** Show multiple limits per account, including Claude's session, overall weekly, and Fable limits when available.
- **A separate tray choice.** Keep session and weekly usage in the widget while showing Fable—or another limit—as the tray number.
- **Stay up to date.** Refresh an account or all accounts manually, or set an automatic interval from 1 minute to 24 hours.
- **Alerts at your chosen threshold.** Enable notifications for individual limits and choose the usage percentage that triggers them.
- **Reset notifications.** In **Settings → Accounts → Account settings → Limit alerts (한도별 알림)**, enable **초기화 알림** for each limit you want to track. A fresh usage check confirms the reset before notifying you once. Reset alerts are off by default and independent of usage threshold alerts.
- **Optional message after a reset.** Enable **세션 초기화 후 자동 메시지** separately for each Claude or Codex account to send a short message after the session limit resets. It is off by default, consumes subscription usage, and works while AI SA is running. AI SA checks the account and limits first; it does not resend when a result is uncertain. Sending with real subscription accounts has not been tested.
- **Time-to-limit estimates.** Enable **소진 예상** in **Settings → Widget** to estimate when each limit will run out from recent usage. **위젯에 예상 표시** controls the widget line separately; results remain available in account settings. Forecasting is off by default.
- **Save usage statistics.** Enable **사용량 통계 저장** in **Settings → Widget** to record every successful refresh. Saving, forecasting, and widget display are independent. Turning saving off keeps existing records; **기록 삭제** clears them explicitly.
- **Explore saved usage.** In **Settings → Statistics (통계)**, choose an account, limit, and the last 1, 6, or 24 hours. View the usage chart, last and highest readings, observed increase in percentage points, and individual records. Hover over the chart or use arrow keys to inspect a reading.
- **Fit your desktop.** Adjust the size and transparency of the whole widget, use light or dark themes, keep it on top, or lock its position.
- **Show or hide the widget.** Use **Settings → Widget → 위젯 표시** to keep the desktop widget visible or use AI SA from the tray. Your choice is remembered after restart; hiding the widget leaves account refreshes running.
- **Remember your monitor.** Keep the widget's placement across monitor reconnections, desktop rearrangements, and scaling changes.
- **Click through the widget.** Keep usage visible while mouse input goes to the apps underneath. Toggle it from settings, the tray menu, or **Ctrl+Alt+W**.

Forecasts use saved statistics and need at least three checks spanning ten minutes with a measurable usage increase. They assume your current pace continues and distinguish an earlier reset or insufficient data. Records stay on your PC for up to 24 hours. Turning saving off stops new records; turning forecasting off stops calculation.

The widget stays out of Alt+Tab and the taskbar. It fits its height to the contents when it opens, while keeping your saved width and position. Version 0.4.11 fixes unnecessary scrolling after fitting three or more accounts; scrolling remains when the contents exceed the screen's available height. Settings open in a separate window, and optional startup brings AI SA back when you sign in to Windows.

Version 0.4.11 also fixes excess widget height after moving the widget and resuming with a monitor temporarily unavailable. It removes leftover space when an error banner clears and handles scaling notifications received during window restoration.

## Services

| Service | What you can track | Accounts |
| --- | --- | --- |
| **Claude** | Session, overall weekly, and additional limits such as Fable, when reported by the service | Multiple |
| **Codex** | Codex limits included with a ChatGPT subscription | Multiple |
| **Antigravity** | Model-specific limits reported by its official tool | Currently signed-in account |
| **Gemini web** | A shortcut to the official website; no automatic usage tracking | — |

AI SA tracks **subscription limits**, rather than API billing or token costs. Antigravity usage is separate from Gemini web usage.

This project is still in early development. Claude has been checked with a connected Max account. Codex and Antigravity integrations still need validation with signed-in subscription accounts.

## Get started

**Requires Windows 11 on an x64 PC.** The app interface is currently in Korean. [Download the latest published release](https://github.com/osias10/ai-sa-usage/releases/latest).

1. Download and run `AISA-{version}-win-x64-setup.exe` from the release assets. It installs for your Windows user without administrator privileges.
2. Open **Settings (설정) → Accounts (계정) → Add account (계정 추가)** and select a service.
3. Complete the provider's sign-in flow. AI SA prepares the required official tools automatically—no API key, terminal commands, or separate developer tools to set up.
4. Choose the limits you want in **Account settings (계정 설정) → Widget items (위젯 표시 항목)**. Set your tray account and limit separately in the **Widget (위젯)** tab.

The first connection needs internet access and may download several hundred megabytes of tools per service.

## Everyday controls

| To… | Use… |
| --- | --- |
| Open settings | Double-click the widget, or use the tray menu |
| Refresh usage | Press **F5** with the widget focused, or use its right-click menu |
| Adjust transparency | **설정 → 위젯 → 위젯 투명도**; applies to text, icons, gauges, and background |
| Toggle click-through | **Settings → Widget → 클릭 통과**, the tray's **클릭 통과** checkbox, or **Ctrl+Alt+W** from any app |
| Choose the tray number | **설정 → 위젯 → 트레이 표시 계정 / 트레이 표시 항목** |
| Show the last check time | **설정 → 위젯 → 마지막 확인 시간** |
| Show or hide the widget | **설정 → 위젯 → 위젯 표시**, or the tray menu; the choice survives restart |
| Bring back a hidden widget | Choose **위젯 열기** from the tray menu |
| Quit AI SA | Choose **종료** from the tray menu |

Changes are saved automatically. At 100% transparency, the whole widget is invisible; the tray menu and settings stay available so you can adjust it again.

Click-through is off by default and stays as you set it after restarting. When enabled, the whole widget passes mouse input through, including clicks used to drag, resize, open settings, or show its right-click menu. Turn it off from the tray or with **Ctrl+Alt+W** to interact with the widget again. If another app has claimed the shortcut, use the tray menu. Settings and tray controls remain available; transparency and position lock are separate options.

AI SA remembers the monitor and the widget's position within its available desktop area, accounting for display scaling. If that monitor disconnects, the widget temporarily appears on an available screen and returns when the original monitor reconnects. Moving or resizing it yourself makes that placement the new preference. This works automatically; position lock remains a separate option.

## Updates

Starting with **0.4.6**, AI SA checks GitHub for a new stable release once every 24 hours and notifies you once per version. You can turn automatic checks off or check manually in **Settings → Info (정보)**. The tray's **업데이트** menu opens the same screen.

Choose **Download (다운로드)** to download and verify the installer. You can cancel the download and keep using the widget. When ready, choose **Run installer (설치 실행)**: AI SA saves your settings, opens the installation wizard, and exits. Complete the wizard and use its **AI SA 실행** option to reopen the app. Your accounts and widget settings are preserved.

From **0.4.10**, you can enable **Receive pre-release updates (프리릴리스 업데이트 받기)** in **Settings → Info**. It is off by default and independent of automatic checks. When enabled, AI SA offers newer stable releases and previews, with the release type shown next to the version.

Turn it off while using a preview to return to the latest stable release—even when that version number is lower. Choose **정식 버전 다운로드**, then **정식 버전 설치** to run the usual wizard. Switching the option alone does not install anything or close the app. Changing it during a download cancels the old selection and checks the selected channel again.

Accounts, widget positions, startup preferences, and usage statistics stay in place. Before a preview transition, AI SA backs up its own settings and data without copying sign-in credentials. These local backups expire after 24 hours and are cleaned on the next app launch or transition; clearing statistics also removes backed-up statistics. A preview must remain compatible with the latest stable data format before it can be published.

**If you use 0.4.5, install the latest stable release manually first.** Its update address was not configured, so it cannot discover this first upgrade itself. Versions before 0.4.10 receive stable releases only; update to the latest stable release before opting into previews.

## Clear status, local settings

A refresh icon means a check is in progress. An exclamation mark means the account needs attention after the check. If a refresh fails, the widget keeps the last successful values and their timestamp. Missing limits show `—`, and a passed reset time does not turn usage into a guessed 0%.

AI SA runs on your PC without its own backend. Settings and saved usage data stay on your device; sign-in and usage requests use the providers' official tools and services. Checking usage does not send prompts or run model-generation tasks. The separate, optional reset-message feature does send a short prompt and consume subscription usage when you enable it.

For build instructions, integration details, and verification notes, see the [development documentation](DEVELOPMENT.md) (Korean).
