# Calldown

Oilspill calls down thumpers, Calldown calls down a Firefall server.

**tl;dr:** Check out PIN, RIN.WebAPI and SDBrowser next to this repo, start Docker, then run `aspire run` in this folder.
Once everything is green in the dashboard, run "Create account" on `rin-webapi`, "Configure client" on `firefall`, and start `firefall`.

<img width="1606" height="670" alt="image" src="https://github.com/user-attachments/assets/7821b842-7847-4a90-8df2-489479b5e617" />

Calldown is an [Aspire](https://aspire.dev) AppHost that runs the whole stack with one command:

- `postgres` with pgweb, running in Docker with a persistent volume
- `db-setup`, which applies RIN's `webapi_schema.sql` and seed data, then imports the client's `clientdb.sd2` into the `sdb` schema
- `rin-internal` and `rin-webapi`
- `collision-cache`, which pre-generates the collision data with PIN's `CollisionGenerator`
- `pin-gameserver` and `pin-matrix`
- `firefall`, the game client itself, which only starts when you tell it to

The resources wait on each other in that order. So the Matrix server only hands out clients once the game server says, that it's ready.

## Prerequisites

- .NET 10 SDK (the AppHost), plus the .NET 8 and 9 runtimes for RIN and PIN
- An ASP.NET Core development certificate, i.e. `dotnet dev-certs https` once
- Docker or a compatible runtime for Postgres. On Windows, Rancher Desktop works well, while Podman 6 has issues with published ports on WSL
- A Firefall 1962 client install - Calldown finds it through the `firefall://` handler or your Steam libraries
- Write access to the install folder, "Configure client" edits `firefall.ini` and patches `FirefallClient.exe` to bypass RedHanded
- The Aspire CLI is optional but handy: `dotnet tool install -g Aspire.Cli`

The repos are expected as siblings:

```
TMW/
  Calldown/
  PIN/
  RIN.WebAPI/
  SDBrowser/
```

If yours live elsewhere, set the MSBuild properties `PinRoot`, `RinRoot` and `SDBrowserRoot`, e.g. with `dotnet build -p:PinRoot=D:\code\PIN\`.

## Running It

```
aspire run
```

`dotnet run --project src/Calldown.AppHost` or F5 in Rider/Visual Studio work just as well.

The first start takes a bit longer:
- The SDB import takes about 20s
- The collision cache for zone 448 about a minute and 5.6 GB in `%LocalAppData%\Calldown\collision`

Every later start skips both, as long as the client files haven't changed.

## Parameters

Set them in the AppHost's user secrets, e.g. `aspire secret set Parameters:zone-id 1030` or `dotnet user-secrets set Parameters:zone-id 1030 --project src/Calldown.AppHost`.

| Parameter               | Default                                    |                                                                                                                                                                 |
|-------------------------|--------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `firefall-path`         | detected install                           | Folder that contains `system\` and `firefall.ini`                                                                                                               |
| `zone-id`               | `448`                                      | Default zone for characters without a last zone; the collision cache is built for it                                                                            |
| `preload-zones`         | `1030`                                     | Comma separated zones hosted from startup. Other zones load on first use, and their collision cache is built then                                               |
| `collision`             | `zone+assets`                              | `off`, `zone` or `zone+assets`. Changing it needs an AppHost restart                                                                                            |
| `pg-user`               | `tmwadmin`                                 | Has to stay `tmwadmin`, RIN's schema assigns everything to that role                                                                                            |
| `streaming-assets-path` | `%LocalAppData%\Calldown\streaming-assets` | Drop folder RIN serves in place of `dl.firefall.com`, see [Streaming Assets](#streaming-assets)                                                                 |
| `public-host`           | `localhost`                                | Address the clients reach the stack at, see [Playing From Another PC](#playing-from-another-pc). Changing it needs an AppHost restart                           |
| `tls-cert-path`         | none                                       | PFX certificate for `public-host` that RIN serves in place of the development certificate. Only used with a `public-host`, changing it needs an AppHost restart |
| `tls-cert-password`     | none                                       | Password of that PFX                                                                                                                                            |

## Dashboard Commands

| Resource          | Command                 |                                                                                                                                                                                                                                     |
|-------------------|-------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `firefall`        | Configure client        | Sets `OperatorHost` in `firefall.ini` to `https://<public-host>:5001`, streams assets from RIN instead of `dl.firefall.com` and patches `FirefallClient.exe` to bypass RedHanded, keeps the originals with a `.calldown.bak` suffix |
| `firefall`        | Client options          | Screen mode and a key to leave a vehicle                                                                                                                                                                                            |
| `firefall`        | Restore client          | Puts the backups back, the unpatched client included                                                                                                                                                                                |
| `rin-webapi`      | Create account          | Registers an account with email, password and country                                                                                                                                                                               |
| `db-setup`        | Re-import SDB           | Imports `clientdb.sd2` again                                                                                                                                                                                                        |
| `db-setup`        | Reset database          | Drops everything, accounts and characters included, and sets it up from scratch                                                                                                                                                     |
| `collision-cache` | Rebuild collision cache | Regenerates the cache even if it looks current                                                                                                                                                                                      |

All of them also work from the terminal, e.g. `aspire resource rin-webapi create-account --email me@example.com --password hunter22 --country US`.

## Streaming Assets

The installer didn't ship everything, the client streamed the rest from `dl.firefall.com` with HTTP range requests:

- the three finest levels of the virtual texture (`system\vt\static.vtex0` to `2`, about 25 GB), streamed only where the install lacks them
- the dialogue media banks listed in `dbsounddata::SoundStreamingAsset`, the client has no other source for those voice lines

RIN serves the `streaming-assets-path` folder at `https://<public-host>:5001/dl`, and "Configure client" points `VTRemotePath` and `AssetStreamPath` in `firefall.ini` at it. Drop the files in with this layout:

```
streaming-assets\
  vtex\static.vtex0                    any level the install lacks, the files as they are in system\vt
  AssetStream\00180000\00180091.bnk    media banks, <id rounded down to 1000, 8 digits>\<id, 8 digits>.bnk
  AssetStream\00180000\00180091.bnk_ac for a partial bank, the ranges it holds
  ingame\panelmanager.html             the in-game web UI page, served at /ingame/panelmanager
  ingame\assets\...                    its scripts, styles, fonts and the item icons, served at /ingame/assets
```

The client reads the offsets from its own `static.vtex_idx` and from the `_Event` banks, so the files have to be the ones of build 1962. A missing file is a 404.

No complete copy of the dialogue banks is known. The banks merged from the player caches in the library hold about a third of 1962's voice lines, each with a `.bnk_ac` in the client cache's format. RIN answers requests outside those ranges with a 404, so the client never gets the gaps as audio. `Firefall-Client-Cache.md` in the workspace root describes the caches and the merge.

The client also loads its web UI, the panel manager, from RIN's `/ingame` host. The `ingame` folder holds the 1962 page from the library's browser caches, with the New Relic tracking removed and its urls moved under `/ingame`, and the assets of the library's 2016 crawl of the in-game host. The page's panels call `/ingame/api/...`, which RIN mostly doesn't implement yet.

## Playing From Another PC

By default everything is handed out as `localhost`, so only the PC running the stack can play. With `public-host` set to an address the other PCs can reach, e.g. `aspire secret set Parameters:public-host 192.168.1.20`, RIN listens on all interfaces and hands out that address as its base URL and as the Matrix server. The game and Matrix server listen on all interfaces anyway.

1. Get a certificate for that address. The client talks to RIN through WinHTTP, which rejects the development certificate (issued for `localhost`) on any other PC. The certificate's subject alternative names have to contain `public-host`, as a DNS name or an IP address, whichever you set. Export it with its private key as a PFX and set `tls-cert-path` and `tls-cert-password`.
2. Trust that certificate, or the CA that issued it, under "Trusted Root Certification Authorities" on every PC that plays, the one running the stack included.
3. Allow TCP 5001 and UDP 25000 and 25001 through the firewall of the PC running the stack.
4. Restart the AppHost and run "Configure client", which now points the local client at `public-host` too.
5. On the other PCs, take `firefall.ini` and the patched `FirefallClient.exe` from the configured install, or set `OperatorHost`, `VTRemotePath` and `AssetStreamPath` by hand. The other PCs need the same 1962 client.

Keep in mind, that anyone who reaches RIN can sign up from the client and play. Don't use `public-host` on a network you don't trust.

## Ports

| Port          |                                                                                                                                         |
|---------------|-----------------------------------------------------------------------------------------------------------------------------------------|
| 5000 / 5001   | RIN.WebAPI (http / https), the client's `OperatorHost`. With a `public-host`, the unused http endpoint gets a free port instead of 5000 |
| 5201          | RIN.InternalAPI, gRPC over cleartext HTTP/2                                                                                             |
| 25000 / 25001 | Matrix and game server, UDP                                                                                                             |
| 25080 / 25081 | Health endpoints of the Matrix and game server                                                                                          |
