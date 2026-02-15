SteamDepotDownloaderService
==========================

Steam depot downloader utilizing the SteamKit2 library. Supports .NET 8.0

This repository is a fork of SteamRE/DepotDownloader with additional features and modifications by QEStudio.
See NOTICE and AUTHORS for attribution and modification notes.

In this fork, the build output assembly name is `SteamDepotDownloaderService` (see `-V` output). CLI arguments remain compatible with DepotDownloader unless stated otherwise.

Description: A GPLv2 Steam depot downloader fork that adds a self-hosted HTTP service mode and a lightweight web UI to manage install/download jobs with progress, logs, cancel, and retry.

## License

This project (including the service mode and frontend in this repository) is distributed under the GNU General Public License v2.0 (GPL-2.0-only). See [LICENSE](LICENSE).

When distributing binaries, provide recipients the corresponding source code under GPLv2. If you use GitHub Releases, the tag source archive provided by GitHub satisfies this when it matches the released binary.

## Installation

### Download from GitHub Releases

Download a binary from [the releases page](https://github.com/QEStudio/SteamDepotDownloaderService/releases/latest).

### Build from source

Requirements: .NET SDK 8.x

```bash
dotnet build DepotDownloader/DepotDownloader.csproj -c Release
dotnet run --project DepotDownloader/DepotDownloader.csproj -c Release -- -V
```

### Build modes (static / non-static)

Non-static (framework-dependent, smaller output):
```bash
dotnet publish DepotDownloader/DepotDownloader.csproj -c Release -r linux-x64 --self-contained false -p:PublishSingleFile=false
```

Static (self-contained, single file):
```bash
dotnet publish DepotDownloader/DepotDownloader.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
```

## Usage

### Downloading one or all depots for an app
```powershell
./SteamDepotDownloaderService -app <id> [-depot <id> [-manifest <id>]]
                             [-username <username> [-password <password>]] [other options]
```

For example: `./SteamDepotDownloaderService -app 730 -depot 731 -manifest 7617088375292372759`

By default it will use anonymous account ([view which apps are available on it here](https://steamdb.info/sub/17906/)).

To use your account, specify the `-username <username>` parameter. Password will be asked interactively if you do
not use specify the `-password` parameter.

### CLI examples

Download all depots for an app into a custom directory:
```bash
dotnet run --project DepotDownloader/DepotDownloader.csproj -c Release -- \
  -app 730 -dir /data/depots
```

Download a specific branch:
```bash
dotnet run --project DepotDownloader/DepotDownloader.csproj -c Release -- \
  -app 730 -branch public -dir /data/depots
```

Manifest-only (prints a human readable manifest without downloading depot content):
```bash
dotnet run --project DepotDownloader/DepotDownloader.csproj -c Release -- \
  -app 730 -manifest-only
```

Validate already downloaded files:
```bash
dotnet run --project DepotDownloader/DepotDownloader.csproj -c Release -- \
  -app 730 -dir /data/depots -validate
```

### Service mode (SteamDepotDownloaderService, QEStudio fork)

This fork adds a service mode that exposes an HTTP API for installing/downloading apps and querying job status, with optional WebSocket streaming.

Service mode is enabled when either:
- `--service` is present in the arguments, or
- environment variable `STEAMDDS_SERVICE` is set to `1|true|yes`

#### Environment variables

Variable | Description | Default
---|---|---
`STEAMDDS_SERVICE` | enable service mode (`1|true|yes`) | empty (disabled)
`STEAMDDS_API_KEY` | API key for requests (optional). Use `X-Api-Key` or `Authorization: Bearer ...` | empty (no auth)
`STEAMDDS_LISTEN_MODE` | `tcp` or `unix` | `tcp`
`STEAMDDS_LISTEN_URL` | listen URL for TCP mode | `http://127.0.0.1:8080`
`STEAMDDS_UNIX_SOCKET_PATH` | unix domain socket path for `unix` mode | `/tmp/steamdds.sock`
`STEAMDDS_CORS_ORIGINS` | allowed CORS origins for browser clients (comma-separated, or `*`) | `http://localhost:5173,http://127.0.0.1:5173`

#### Start examples

TCP listener:
```bash
STEAMDDS_SERVICE=1 \
STEAMDDS_LISTEN_MODE=tcp \
STEAMDDS_LISTEN_URL=http://127.0.0.1:18080 \
STEAMDDS_API_KEY=test \
dotnet run --project DepotDownloader/DepotDownloader.csproj -c Release
```

Unix domain socket listener:
```bash
STEAMDDS_SERVICE=1 \
STEAMDDS_LISTEN_MODE=unix \
STEAMDDS_UNIX_SOCKET_PATH=/tmp/steamdds.sock \
STEAMDDS_API_KEY=test \
dotnet run --project DepotDownloader/DepotDownloader.csproj -c Release
```

#### HTTP API

Endpoint | Method | Description | Auth
---|---|---|---
`/health` | GET | health check | no
`/api/install` | POST | create an install/download job | yes (if `STEAMDDS_API_KEY` set)
`/api/jobs` | GET | list jobs | yes (if `STEAMDDS_API_KEY` set)
`/api/jobs/{id}` | GET | job details + log tail | yes (if `STEAMDDS_API_KEY` set)
`/api/jobs/{id}` | DELETE | cancel a queued/running job | yes (if `STEAMDDS_API_KEY` set)
`/api/jobs/{id}/retry` | POST | retry a failed/canceled job (creates a new job) | yes (if `STEAMDDS_API_KEY` set)

Install request body fields (`POST /api/install`):

Field | Type | Required | Description
---|---|---|---
`appId` | number | yes | Steam AppID
`depotId` | number | no | download a specific depot only
`manifestId` | number | no | specify a specific manifest id (requires `depotId`)
`branch` | string | no | branch name (default: `public`)
`branchPassword` | string | no | branch password
`dir` | string | no | install directory (same as `-dir`)
`os` | string | no | `windows|macos|linux`
`arch` | string | no | `32|64`
`language` | string | no | language name (default: `english`)
`lowViolence` | boolean | no | low violence depots
`validate` | boolean | no | validate existing files
`maxDownloads` | number | no | max concurrent chunk downloads
`username` | string | no | Steam account username (required for restricted apps)
`password` | string | no | Steam account password
`rememberPassword` | boolean | no | persist login key for future sessions
`skipAppConfirmation` | boolean | no | skip app confirmation prompt

Example: list jobs (TCP mode)
```bash
curl -H 'X-Api-Key: test' http://127.0.0.1:18080/api/jobs
```

Example: create install job (anonymous, may fail for restricted apps)
```bash
curl -H 'X-Api-Key: test' -H 'Content-Type: application/json' \
  -d '{"appId":10,"dir":"/tmp/steamdds-test","maxDownloads":1,"validate":false}' \
  http://127.0.0.1:18080/api/install
```

Example: create install job (restricted apps require Steam account)
```bash
curl -H 'X-Api-Key: test' -H 'Content-Type: application/json' \
  -d '{"appId":730,"dir":"/data/steamdds","username":"YOUR_USER","password":"YOUR_PASS","rememberPassword":true}' \
  http://127.0.0.1:18080/api/install
```

Example: unix domain socket request
```bash
curl --unix-socket /tmp/steamdds.sock -H 'X-Api-Key: test' http://localhost/health
```

#### WebSocket

`GET /ws` upgrades to WebSocket and streams job events:
- `?jobId=<guid>`: only stream events for the given job
- no `jobId`: stream events for all jobs
If `STEAMDDS_API_KEY` is set, browsers cannot send custom headers in WebSocket handshakes. This service also accepts `?apiKey=<key>` for `/ws`.

Event payload format (JSON):
```json
{"jobId":"...","timestamp":"...","type":"log|state|error|progress","message":"..."}
```

Progress events (`type: "progress"`) use `message` as a JSON string:
```json
{"phase":"Downloading Files","percent":0.42,"detail":"42% ..."}
```

#### Built-in retry behavior

The service will retry transient install failures up to 3 times within the same job. For permanent failures (or after retries are exhausted), the job becomes `Failed`. You can then call `POST /api/jobs/{id}/retry` (or use the frontend Retry button) to create a new job with the same request payload.

### Frontend UI (this fork)

This repository includes a small web UI under [frontend](frontend) for:
- creating install jobs
- viewing job list + progress/phase
- streaming logs (WebSocket)
- canceling jobs
- retrying failed/canceled jobs
- exporting logs

Development:
```bash
cd frontend
npm ci
VITE_PROXY_TARGET=http://127.0.0.1:18080 npm run dev -- --host 127.0.0.1 --port 5173
```

Production build:
```bash
cd frontend
npm ci
npm run build
```

### Downloading a workshop item using pubfile id
```powershell
./SteamDepotDownloaderService -app <id> -pubfile <id> [-username <username> [-password <password>]]
```

For example: `./SteamDepotDownloaderService -app 730 -pubfile 1885082371`

### Downloading a workshop item using ugc id
```powershell
./SteamDepotDownloaderService -app <id> -ugc <id> [-username <username> [-password <password>]]
```

For example: `./SteamDepotDownloaderService -app 730 -ugc 770604181014286929`

## Parameters

#### Authentication

Parameter               | Description
----------------------- | -----------
`-username <user>`      | the username of the account to login to for restricted content.
`-password <pass>`      | the password of the account to login to for restricted content.
`-remember-password`    | if set, remember the password for subsequent logins of this user. (Use `-username <username> -remember-password` as login credentials)
`-qr`                   | display a login QR code to be scanned with the Steam mobile app
`-no-mobile`            | prefer entering a 2FA code instead of prompting to accept in the Steam mobile app.
`-loginid <#>`          | a unique 32-bit integer Steam LogonID in decimal, required if running multiple instances of DepotDownloader concurrently.

#### Downloading

Parameter                | Description
------------------------ | -----------
`-app <#>`               | the AppID to download.
`-depot <#>`             | the DepotID to download.
`-manifest <id>`         | manifest id of content to download (requires `-depot`, default: current for branch).
`-ugc <#>`               | the UGC ID to download.
`-pubfile <#>`           | the PublishedFileId to download. (Will automatically resolve to UGC id)
`-branch <branchname>`   | download from specified branch if available (default: Public).
`-branchpassword <pass>` | branch password if applicable.

#### Download configuration

Parameter               | Description
----------------------- | -----------
`-all-platforms`        | downloads all platform-specific depots when `-app` is used.
`-os <os>`              | the operating system for which to download the game (windows, macos or linux, default: OS the program is currently running on)
`-osarch <arch>`        | the architecture for which to download the game (32 or 64, default: the host's architecture)
`-all-archs`            | download all architecture-specific depots when `-app` is used.
`-all-languages`        | download all language-specific depots when `-app` is used.
`-language <lang>`      | the language for which to download the game (default: english)
`-lowviolence`          | download low violence depots when `-app` is used.
`-dir <installdir>`     | the directory in which to place downloaded files.
`-filelist <file.txt>`  | the name of a local file that contains a list of files to download (from the manifest). prefix file path with `regex:` if you want to match with regex. each file path should be on their own line.
`-validate`             | include checksum verification of files already downloaded.
`-manifest-only`        | downloads a human readable manifest for any depots that would be downloaded.
`-cellid <#>`           | the overridden CellID of the content server to download from.
`-max-downloads <#>`    | maximum number of chunks to download concurrently. (default: 8).
`-use-lancache`         | forces downloads over the local network via a Lancache instance.

#### Other

Parameter               | Description
----------------------- | -----------
`-debug`                | enable verbose debug logging.
`-V` or `--version`     | print version and runtime.

## Frequently Asked Questions

### Why am I prompted to enter a 2-factor code every time I run the app?
Your 2-factor code authenticates a Steam session. You need to "remember" your session with `-remember-password` which persists the login key for your Steam session.

### Can I run DepotDownloader while an account is already connected to Steam?
Any connection to Steam will be closed if they share a LoginID. You can specify a different LoginID with `-loginid`.

### Why doesn't my password containing special characters work? Do I have to specify the password on the command line?
If you pass the `-password` parameter with a password that contains special characters, you will need to escape the command appropriately for the shell you are using. You do not have to include the `-password` parameter on the command line as long as you include a `-username`. You will be prompted to enter your password interactively.

### I am getting error 401 or no manifest code returned for old manifest ids
Try logging in with a Steam account, this may happen when using anonymous account.

Steam allows developers to block downloading old manifests, in which case no manifest code is returned even when parameters appear correct.

### Why am I getting slow download speeds and frequent connection timeouts?
When downloading old builds, cache server may not have the chunks readily available which makes downloading slower.
Try increasing `-max-downloads` to saturate the network more.
