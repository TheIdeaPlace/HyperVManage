# Hyper-V Manage bug-report relay

A Cloudflare Worker that turns a report from Help, Report a Bug into a GitHub issue on this
repository, so nobody needs a GitHub account to report a bug and nothing that can write to the
repository ships in the app. It's QuickMail's relay (kellylford/QuickMail, `relay/`) with this
repository's name and its own key header, `X-HyperVManage-Key`.

The relay holds a GitHub App's private key. The app sends a report with a relay key compiled into
release builds; that key can only file an issue here, it is assumed extractable from the exe, and
it can be changed without touching any GitHub account.

**Until the relay is set up, bug reporting still works**: Report a Bug shows only Send with
GitHub, which opens GitHub's new issue form filled in with the report and copies the whole report
to the clipboard. That needs a GitHub account. Builds made on your own PC always work this way.

Nothing runs until a report arrives. The free tier is 100,000 requests a day.

## Deploying runs in CI

wrangler can't run on Windows on Arm (its `workerd` dependency has no build for it), so the
**Deploy bug-report relay** workflow (`.github/workflows/deploy-relay.yml`) runs it on Linux. Every
value the Worker needs is a repository secret. The tests need only Node:

```bash
node relay/test/jwt.test.js
```

## One-time setup

1. **Create a GitHub App** at <https://github.com/settings/apps/new>:
   - Name: `Hyper-V Manage Bug Reporter` (issues show as `hyper-v-manage-bug-reporter[bot]`)
   - Homepage URL: `https://github.com/kellylford/HyperVManage`
   - Webhook: uncheck **Active**
   - Repository permissions, **Issues**: Read and write. Nothing else.
   - Where can it be installed: only on this account

   Then note the **App ID**, **generate a private key** (a `.pem` downloads; it's the only copy),
   and **Install App** on `HyperVManage` only. The number at the end of the address you land on
   is the **installation ID**.

2. **A Cloudflare API token** at <https://dash.cloudflare.com/profile/api-tokens>, Create Custom
   Token, with `Account / Workers Scripts / Edit` and `Account / Account Settings / Read`, your
   account, no zones, no expiry. QuickMail's relay token has the same permissions and can be
   reused if you kept it. The account's workers.dev subdomain is already registered (QuickMail's
   relay uses it), so the Worker will be at
   `https://hyperv-manage-bug-relay.<subdomain>.workers.dev`.

3. **Repository secrets** at <https://github.com/kellylford/HyperVManage/settings/secrets/actions>:

   | Secret | Value |
   |---|---|
   | `CLOUDFLARE_API_TOKEN` | the token from step 2 |
   | `RELAY_GITHUB_APP_ID` | the App ID |
   | `RELAY_GITHUB_INSTALLATION_ID` | the installation ID |
   | `RELAY_GITHUB_PRIVATE_KEY` | the whole `.pem` file, BEGIN and END lines included |
   | `BUG_REPORT_RELAY_KEY` | a long random string: `node -e "console.log(require('crypto').randomBytes(32).toString('base64url'))"` |

4. **Deploy**: Actions, **Deploy bug-report relay**, Run workflow, with **Also push the Worker's
   secrets** ticked. The Deploy Worker step's log ends with the Worker's address. If it says more
   than one account is available, add a repository variable `CLOUDFLARE_ACCOUNT_ID`.

5. **Point the app at it**: add a repository **variable** (not a secret) `BUG_REPORT_RELAY_URL`,
   the Worker's address plus `/report`. The next release compiles it and the key in.

6. **Check it**:

   ```bash
   curl -X POST https://hyperv-manage-bug-relay.<subdomain>.workers.dev/report -H "X-HyperVManage-Key: <relay key>" -H "Content-Type: application/json" -d "{\"title\":\"Relay smoke test\",\"body\":\"Ignore and close.\"}"
   ```

   It answers `{"issueUrl":"...","number":N}`, and the issue's author is the App's bot. Close it.
   A 401 means the key header doesn't match; a 502 means the App's details are wrong or it isn't
   installed on the repository (the Worker's logs in the Cloudflare dashboard say which); a 429
   is the limit of 5 reports a minute from one address.

## If the relay key leaks

It ships in a public exe, so assume it will. The most it allows is junk issues here. Change
`BUG_REPORT_RELAY_KEY`, run the deploy workflow with the secrets box ticked, and release. Copies
with the old key fall back to Send with GitHub.
