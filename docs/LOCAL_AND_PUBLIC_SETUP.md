# Local development and public-server switching

## What this launcher currently does

The WPF launcher talks to the independent 1.61ONLINE community API. It supports account registration and sign-in, protected refresh-token storage, a community profile, player search and friend requests, and discovery/creation/joining of community session records.

Session records are **not** live GTA V multiplayer. A compatible client adapter and a gameplay transport still need to be designed and implemented separately. This launcher does not reproduce Rockstar Social Club or claim compatibility with its private protocols.

## Run locally on Windows

1. Install the .NET 10 SDK for Windows.
2. Start the PostgreSQL and API backend using the backend repository's development instructions. The API should answer at `http://127.0.0.1:3000/health/ready`.
3. From the launcher repository root, run:

   ```powershell
   dotnet run --project src/OnlineLauncher/OnlineLauncher.csproj
   ```

The first run defaults to **Local Development** at `http://127.0.0.1:3000/`. The local backend and its database must already be running for account and social features to work.

The launcher saves its non-secret preferences under:

`%LOCALAPPDATA%\1.61ONLINE\launcher-settings.json`

The saved game folder is just a preference at this stage; the launcher does not modify, patch, or launch GTA V in this foundation.

## Switch to a public server

1. Deploy the community backend and PostgreSQL to infrastructure you control. Configure production secrets on the server; do not use development secrets publicly.
2. Put the backend behind HTTPS and confirm these return successful responses:
   - `https://YOUR-HOST/health/ready`
   - `https://YOUR-HOST/v1/client/bootstrap`
3. Open **Settings** in the launcher, select **Public Server**, enter the deployed API base URL (for example, `https://community.example.com/`), then select **Save & Apply**.
4. Create/sign in to an account in the public environment. Public and local sessions are stored separately, so switching environments will not silently reuse the other server's token.

The URL is a placeholder example, not a real deployment. Switching the setting does not deploy or expose the API; you must deploy it yourself. Public mode refuses plain HTTP so credentials and tokens are not sent unencrypted. Do not put a database password, JWT signing secret, Discord bot token, or any other server secret into the launcher.

## Build a Release

Run this from a Windows terminal in the repository root:

```powershell
dotnet build src/OnlineLauncher/OnlineLauncher.csproj -c Release
dotnet publish src/OnlineLauncher/OnlineLauncher.csproj -c Release -r win-x64 --self-contained false -o artifacts/launcher
```

The publish command expects the .NET desktop runtime matching the target framework on the user's PC. Signing, an installer, an automatic update channel, and a verified game-client adapter are later milestones.

## Security notes

- Access and refresh tokens are encrypted using Windows DPAPI for the current Windows user before being written to local app data.
- Local and public environments use different encrypted session files.
- Passwords are never written to launcher settings.
- Keep the backend's `.env` file private and out of Git.
- Use HTTPS for any public API endpoint and rotate any credentials that were ever pasted into chat, source control, or public logs.
