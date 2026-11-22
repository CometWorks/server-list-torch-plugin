# CometWorks Server List for Torch

MIT-licensed Torch plugin for [se-servers.net](https://se-servers.net), maintained by CometWorks and published on Torch under **OwendB**.

Standalone Torch servers publish player counts, capacity and simulation speed. Nexus V3 clusters publish **online / total sectors** through Nexus's public Mod API. Nexus is optional; its DLL is neither referenced nor bundled. CometWorks cluster node summaries are published separately by Quasar.

## Install and configure

Install the release ZIP through Torch's plugin manager, or extract it into a dedicated folder under Torch `Plugins`. Restart Torch. The package contains the plugin, its manifest, Bouncy Castle and license notices; game and Torch assemblies are provided by the host.

In the plugin tab, set:

1. **Public join host** to the public IP or DNS name players use.
2. **Public join port** to the public game/lobby port (default 27016).
3. Optional **Community name**; an empty name uses the dedicated server name.
4. Enable **Share server listing** to opt into publication.

The dedicated server configuration must contain at least one valid Steam administrator. Those current Steam accounts can sign into se-servers.net to edit the community page. Changing configuration cancels pending publication. Disabling sharing sends a signed withdrawal for a listing the running plugin may have published. If the directory cannot receive the update, the listing expires three minutes after its last accepted update was issued.

For **Nexus V3**, install and enable this publisher on **one server per cluster**, normally the lobby. Use the public lobby join address. The adapter automatically detects the Nexus plugin and counts visible configured sectors on servers in the current Nexus cluster. Several sectors on the same server count separately. Other clusters, hidden sectors and servers without a configured visible sector are excluded. Missing/incompatible Nexus API data shows unknown counts, never a fabricated zero. Nexus V2 is not supported.

Nexus cluster player totals and simulation speed remain unknown: this plugin does not present the lobby's local metrics as cluster totals. Standalone Torch publishes its own server metrics. The publisher sends every 30 seconds and captures game-thread telemetry every 15 seconds. Live observations expire separately after 60 seconds.

## Data and identity

Sharing defaults to disabled. The plugin first contacts the directory when sharing is enabled, the server is running and the publication settings and Steam admin roster are valid.

The plugin signs each update with its installation key and sends it directly to `https://se-servers.net/v2/listings`. The directory verifies the signature and enforces publisher ownership, update ordering and listing expiry. The update contains the public listing, telemetry and Steam admin IDs used to grant editor access. The plugin sends no diagnostic logs, dumps, player identities or private topology.

`ServerList.cfg` stores public settings. `ServerListIdentity` stores the installation and listing IDs, private key and durable epoch. Back up the entire directory and protect it like Torch's configuration. Keep each server's identity on its own installation. Each restart reserves a higher epoch, and a process lock prevents two local publishers from using the same identity concurrently.

### Upgrade an existing installation

Keep the existing `ServerListIdentity` directory, including `machine-key.pem` if present. The plugin reuses the original key and IDs so the directory can retain the existing listing, page edits and votes. Before the first direct update from a legacy installation, the directory operator must import its trusted public-key fingerprint using the website's migration procedure. An unimported legacy identity receives HTTP 403. Fresh installations derive their IDs from their keys and publish without this step. [Directory protocol and key import](https://github.com/CometWorks/server-list/blob/main/docs/Protocol.md#existing-installation-ownership)

## Build and verify

Requires .NET 10 SDK, .NET Framework 4.8 reference assemblies (restored by NuGet), Torch binaries and the Space Engineers dedicated server binaries:

```sh
dotnet build Plugin/ServerList.Torch.csproj -c Release -p:Torch=/path/to/torch
# If game binaries live elsewhere, add -p:Game=/path/to/DedicatedServer64
dotnet run --project Tests/Tests.csproj
pwsh -File Tests/PublishChecks.ps1
```

BouncyCastle.Cryptography supplies RSA-PSS and PEM/SPKI encoding on .NET Framework 4.8. Torch provides Newtonsoft.Json and protobuf-net. The tests verify v2 signatures with .NET RSA and cover publication, withdrawal, identity migration, restart ordering, process locking and Nexus sector counting.

## Automatic releases

GitHub Actions tests and builds on `main`, pull requests and manual runs. It downloads Torch and the dedicated server through SteamCMD, caches references weekly, and uploads a ZIP artifact. Push a tag such as `v0.1.0` to publish that version automatically to both GitHub Releases and torchapi.com.

The repository secret **`TORCH_API_KEY`** is OwendB's publishing secret from the authenticated Torch upload/profile page. It is passed as the multipart `secret` field to `POST https://torchapi.com/api/plugins/upload`, alongside `username=OwendB`, `action=upload` or `update` and `uploadZip` with MIME type `application/zip`. First publication also supplies name, description and public visibility; updates supply a release note. The manifest name/GUID are stable. No browser session or Discord credential is needed by CI.

Publication jobs run serially. The script checks the public plugin API first, treats an already published version as an idempotent retry, refuses older versions and fails on HTTP or API-level errors. Never paste the publishing secret into source or workflow files.
