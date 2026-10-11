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

The dedicated server configuration must contain at least one valid Steam administrator. Those current Steam accounts can sign into se-servers.net to edit the community page. Player accounts are never uploaded. Changing configuration cancels pending transfers; disabling sharing sends an ordered withdrawal. An unreachable backoffice hides the listing within its last 90-second heartbeat lease.

For **Nexus V3**, install and enable this publisher on **one server per cluster**, normally the lobby. Use the public lobby join address. The adapter automatically detects the Nexus plugin and counts visible configured sectors on servers in the current Nexus cluster. Several sectors on the same server count separately. Other clusters, hidden sectors and servers without a configured visible sector are excluded. Missing/incompatible Nexus API data shows unknown counts, never a fabricated zero. Nexus V2 is not supported.

Nexus cluster player totals and simulation speed remain unknown: this plugin does not present the lobby's local metrics as cluster totals. Standalone Torch publishes its own server metrics. The publisher sends every 30 seconds and captures game-thread telemetry every 15 seconds. Live observations expire separately after 60 seconds.

## Data and identity

Sharing defaults to disabled. Until valid sharing settings and an admin roster exist, the plugin makes no backoffice requests.

Publication uses automatic machine-key enrollment with `https://log.se-servers.net`. The backoffice authenticates an installation-specific credential, persists source ordering and relays signed snapshots to the website. Torch uses metadata opt-in and short heartbeats; Quasar's existing diagnostic-log eligibility remains separate. No diagnostic logs, dumps, player identifiers, private topology, names or endpoints are collected by this plugin.

`ServerList.cfg` stores public settings. `ServerListIdentity` stores the installation/listing IDs, private machine key and durable epoch; protect this directory with the same filesystem access restrictions as Torch's configuration. Back up the entire identity directory. Never copy it to another running server: a separate installation needs its own identity. Restart reserves a higher epoch; replayed updates and earlier withdrawals cannot renew visibility. The process lock fences duplicate local publishers.

Deploy the matching website and logging-backend changes before enabling sharing. Existing Quasar listings, pages and votes require no database migration.

## Build and verify

Requires .NET 10 SDK, .NET Framework 4.8 reference assemblies (restored by NuGet), Torch binaries and the Space Engineers dedicated server binaries:

```sh
dotnet build Plugin/ServerList.Torch.csproj -c Release -p:Torch=/path/to/torch
# If game binaries live elsewhere, add -p:Game=/path/to/DedicatedServer64
dotnet run --project Tests/Tests.csproj
pwsh -File Tests/PublishChecks.ps1
```

BouncyCastle.Cryptography supplies portable RSA-PSS, OAEP-SHA256 and PEM/SPKI encoding on .NET Framework 4.8. Newtonsoft.Json and protobuf-net are provided by Torch. The focused tests exercise real publisher source, interoperable cryptography, durable identity/order, process fencing and Nexus protobuf sector counting.

## Automatic releases

GitHub Actions tests and builds on `main`, pull requests and manual runs. It downloads Torch and the dedicated server through SteamCMD, caches references weekly, and uploads a ZIP artifact. Push a tag such as `v0.1.0` to publish that version automatically to both GitHub Releases and torchapi.com.

The repository secret **`TORCH_API_KEY`** is OwendB's publishing secret from the authenticated Torch upload/profile page. It is passed as the multipart `secret` field to `POST https://torchapi.com/api/plugins/upload`, alongside `username=OwendB`, `action=upload` or `update` and `uploadZip` with MIME type `application/zip`. First publication also supplies name, description and public visibility; updates supply a release note. The manifest name/GUID are stable. No browser session or Discord credential is needed by CI.

Publication jobs run serially. The script checks the public plugin API first, treats an already published version as an idempotent retry, refuses older versions and fails on HTTP or API-level errors. Never paste the publishing secret into source or workflow files.
