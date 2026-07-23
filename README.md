# Gluj Drive

Gluj Drive is a lightweight personal photo server designed to run directly on a Windows home PC. The backend uses ASP.NET Core and the browser interface uses React with TypeScript.

See [PROJECT_PLAN.md](PROJECT_PLAN.md) for the product scope and delivery plan.

## Development prerequisites

- .NET SDK 10.0.302 or a compatible .NET 10 patch
- Node.js 24 or newer
- npm 11 or newer

## Development

Start the ASP.NET Core API in one terminal:

```powershell
dotnet run --project src/GlujDrive.Server
```

Start the React development server in another terminal:

```powershell
npm.cmd run dev --prefix src/GlujDrive.Web
```

Open `http://localhost:5173` on the host PC or `http://HOST-PC-IP:5173` from another device on the LAN. The Vite React development client listens on all IPv4 interfaces and proxies `/api` requests to the loopback-only ASP.NET Core endpoint. The proxy preserves the original client address and browser host so remote development clients still require authentication and cannot use host-only controls.

### Visual Studio Code

Open the Run and Debug view and select **Gluj Drive: Full stack** to debug the ASP.NET Core server and React application together in Microsoft Edge. The individual **Gluj Drive: Server** and **Gluj Drive: Web (Edge)** profiles are also available. The web profile runs Vite as a managed debug process and starts the Edge debugger after Vite reports that it is ready, so it does not rely on a never-ending pre-launch task.

The default VS Code build task runs both backend and frontend builds. Additional tasks are available for dependency installation, frontend linting, the Vite development server, and Windows publishing.

## Build

Build the backend solution:

```powershell
dotnet build GlujDrive.slnx
```

Build the frontend:

```powershell
npm.cmd run build --prefix src/GlujDrive.Web
```

Publishing the server runs `npm ci` and the frontend production build, then includes the generated assets in the server's `wwwroot` output:

```powershell
dotnet publish src/GlujDrive.Server -c Release
```

## Local media storage

The React application never accesses Windows files directly. It calls the ASP.NET Core API, whose controllers use the `IAssetStorage` contract from `GlujDrive.Application`. `LocalAssetStorage` in `GlujDrive.Infrastructure` performs the actual filesystem operations.

During development, folder registrations are stored in `src/GlujDrive.Server/data/catalog/folders.json`, and the initial default source folder is `src/GlujDrive.Server/data/photos`. Development overrides in `appsettings.Development.json` keep this state inside the repository.

Published builds store the catalog, owner account, server settings, authentication keys, previews, downloaded semantic model, and semantic index under `%LOCALAPPDATA%\Gluj Drive\data`. The initial upload folder is `%USERPROFILE%\Pictures\Gluj Drive`. Program files and mutable user state are deliberately separate, so installing an update or uninstalling the executable does not remove the catalog. Existing media registered from other directories remains in those directories.

On the host PC, use the site's **Folders** panel to register additional existing Windows directories, choose a default upload destination, or stop scanning a directory. **Browse...** opens a native Windows folder dialog so paths do not need to be pasted manually. Registered folders are scanned recursively, and the panel displays their derived subfolder hierarchy. The upload selector includes the source root and every existing subfolder, allowing uploads to be written directly into an album. Images remain in their source directories and are not copied into the catalog. Removing a registration never deletes its files.

Folder paths and folder-management operations are host-only. Connections from other LAN or VPN devices can choose among folder names when uploading, but cannot see local paths, open the native picker, add or remove folders, empty folders, or change the default. The server enforces this using the connection's loopback address; it is not merely a hidden frontend control.

The default library timeline searches by media name, folder, or relative path and progressively adds items in batches of 24 as the user scrolls. On wide screens, search, folder/media filters, gallery layout, and Timeline/Albums switching remain in a fixed left-side panel. Narrower screens use a translucent toolbar that follows scrolling; on phones, detailed filters open in an anchored panel so they do not permanently cover the gallery. **Photos** layout is the default and forms compact, justified rows using each loaded preview's aspect ratio and available pixel dimensions. Low-resolution media is capped near its native display size instead of being stretched across a sparse row. **Cards** layout restores equal-sized cards with persistent metadata and actions; the preference is saved in the browser. Items are grouped newest-first by month across the library, with one unified date-rail entry per month. **Albums** switches to a collapsible tree where each registered source is a root and nested filesystem directories become nested albums instead of being flattened into the source. Cards and the viewer show the privacy-safe relative location (`Source / subfolder / file`) without exposing absolute Windows paths remotely. Selecting an image opens the zoomable viewer; animated GIFs play in that viewer, and videos use the browser's native controls.

Gallery cards use a staged derivative pipeline. The initial library response reads only filesystem metadata plus any cached average RGB color and pixel dimensions; an unprocessed image uses neutral size/color fallbacks without opening its original. A card that remains near the viewport requests a 64-pixel WebP after 150 milliseconds, which also creates its true visual-metadata cache, and promotes to a 640-pixel WebP after 500 milliseconds total. Legacy preview caches learn dimensions opportunistically from their largest available derivative. Cards far outside the viewport remove their image source while retaining inexpensive text, color, and dimension metadata. The original file is requested only when the viewer opens. Generated metadata and previews live under the application catalog's `previews` directory and can be rebuilt from originals; generation is limited to two concurrent jobs to protect the host PC during fast scrolling.

The catalog admits JPG/JPEG, PNG, WebP, GIF, HEIC, HEIF, MP4, M4V, MOV, WebM, and OGV. This is intentionally not a claim to support every image or video codec. The derivative pipeline uses SixLabors.ImageSharp 3.1.12 under its community/non-commercial license terms. JPEG, PNG, GIF, and WebP derivatives are supported. HEIC/HEIF files retain their color fallback if the decoder cannot process them; originals remain viewable only when the browser supports the format.

GIF card previews, average color, and semantic embeddings use only the first frame. Video first-frame derivatives use FFmpeg. For a bundled Windows release, place `ffmpeg.exe`, its adjacent DLLs when using a shared build, and its license files in `src/GlujDrive.Server/runtime/ffmpeg/win-x64/`; build and publish output includes that directory automatically. `ffprobe.exe` and `ffplay.exe` are not used. If the bundled executable is absent, the server falls back to `ffmpeg` on `PATH`. `Media:FfmpegPath` can select another location. If FFmpeg is unavailable, supported videos still play and stream in the modal, and cards fall back to the browser's first-frame rendering, but server-side color and semantic analysis cannot process them.

Video playback uses ASP.NET Core HTTP range responses and `<video preload="metadata">`. Browsers request and buffer portions of a file and can seek without first downloading the whole video. This first version streams the original file; it does not transcode codecs or provide adaptive multi-resolution HLS/DASH renditions, so actual playback support depends on the codecs installed in the viewing browser.

Initial media routes:

- `POST /api/uploads?folderId={folderId}` with one or more multipart fields named `files`
- `GET /api/assets` to list the current library
- `GET /api/assets/{assetId}` for inline viewing
- `GET /api/assets/{assetId}/preview?size=low|medium` for cached WebP derivatives
- `GET /api/assets/{assetId}/download` for attachment download
- `DELETE /api/assets/{assetId}` to move an asset into `.gluj-trash` inside its source folder
- `GET /api/folders` to list registered source folders
- `POST /api/folders` to register an existing folder
- `PUT /api/folders/{folderId}/default` to change the default upload folder
- `DELETE /api/folders/{folderId}` to unregister a folder without deleting files
- `DELETE /api/folders/{folderId}/assets` to move every scanned image in a folder to its `.gluj-trash` directory (host only)

Asset routes never accept physical paths. Assets are addressed by opaque IDs derived from their registered folder and relative path. Folder-management routes accept local paths as owner configuration, while browser clients never use those paths to retrieve individual files.

## Swagger

When the server runs in the Development environment, interactive API documentation is available at `http://localhost:5199/swagger`. Use **Try it out** on `POST /api/uploads` to select multiple images, then use the returned asset IDs with the view, download, and delete operations.

Swagger is disabled outside Development so the interactive API surface is not exposed by a normal home-server deployment.

## Owner account and remote access

Gluj Drive is provided **as is** as self-hosted software. It does not include a hosted relay, tunnelling service, automatic router configuration, or a guarantee that the server will be reachable outside your home network. The Windows PC running Gluj Drive must remain powered on, connected to the network, and permitted through Windows Firewall for other devices to connect.

During development, the React/Vite client listens on port `5173` across all IPv4 interfaces (`0.0.0.0`), while the ASP.NET server remains on `http://localhost:5199`. Devices on the same LAN can therefore open `http://HOST-PC-IP:5173` once Windows Firewall permits the Vite/Node process.

Vite is a development tool, not the production server. A published build places the compiled React files in ASP.NET's `wwwroot`, so one ASP.NET process serves both the site and API. Published builds listen on TCP port `5199` on all IPv4 interfaces by default. The host opens `http://localhost:5199`; another LAN or Tailscale device opens `http://HOST-PC-IP:5199`. Listening on `0.0.0.0:5199` means one port across the PC's network interfaces, not every port. Gluj Drive's IP access rules and the Windows Firewall rule still determine which clients can connect.

The host-only **Settings** panel provides IP allow and deny lists. Each line accepts one IPv4 address, IPv6 address, or CIDR range. An empty allow list permits every address, while a populated allow list accepts only matching addresses; deny rules always take priority. Direct loopback connections remain available even if a rule is entered incorrectly, preventing the owner from locking themselves out. Rules use the peer address seen directly by the server and apply immediately to the site and API.

Direct access from the wider internet will usually require router port forwarding, a stable public address or dynamic DNS, HTTPS, and careful firewall configuration. Port forwarding cannot bypass carrier-grade NAT (CGNAT), double NAT, a restrictive ISP, or another firewall that you do not control. Do not expose an unencrypted HTTP endpoint directly to the public internet.

For most personal installations, the simplest option is to use Gluj Drive with [Tailscale](https://tailscale.com/). Install Tailscale on the host PC and the devices that need access, then connect to Gluj Drive through the host's private Tailscale address or name. This normally avoids public port forwarding and works across many NAT configurations while keeping access inside your private tailnet.

On first launch, open Gluj Drive directly on the Windows host. The site requires creation of one owner account before any remote client can access the library. Loopback requests from the host PC deliberately bypass sign-in so the owner cannot lock themselves out; folder management, semantic-search management, account changes, and server settings remain unavailable to remote clients even after they authenticate.

Remote authentication uses an HTTP-only, SameSite `Strict` ASP.NET Core cookie. The default persistent session is 365 days and can be shortened from **Settings** on the host PC. Cookie encryption keys are persisted beneath `data/catalog/auth/keys` and protected with Windows DPAPI, so sessions survive restarts but remain tied to the Windows account running Gluj Drive. Passwords are never stored directly: the account file contains a unique salt and a PBKDF2-HMAC-SHA256 hash using 600,000 iterations. Changing the owner account rotates its security stamp and invalidates existing remote sessions.

API requests carrying a foreign `Origin` are rejected instead of merely omitting CORS response headers. Unsafe remote requests without a browser origin are also rejected. This same-origin policy is intentionally stricter than ordinary CORS and still permits the loopback Vite development proxy. Authentication is rate-limited to five attempts per remote address per minute.

HTTP authentication does not encrypt traffic. Configure HTTPS or use a trusted private VPN before signing in across an untrusted network; the remote login screen warns when its connection is not HTTPS.

The **Settings** panel on the host PC manages IP access rules, session lifetime, upload limits, semantic-similarity thresholds, result limits, and owner-account changes. These overrides are stored in `data/catalog/server-settings.json`. Increased upload limits require a server restart because Kestrel's request-body ceiling is established during startup; other exposed settings apply immediately.

## Version and release

The current preview version is **0.1.0**. Backend assembly versions are defined in `Directory.Build.props`, while the frontend version is recorded in its `package.json`. See [CHANGELOG.md](CHANGELOG.md) for release notes.

Windows releases contain two artifacts:

- `GlujDrive-Setup-0.1.0-win-x64.exe` is an Inno Setup installer with a self-contained .NET runtime. The user does not install .NET, Node.js, npm, or FFmpeg.
- `GlujDrive-Portable-0.1.0-win-x64.zip` is a smaller framework-dependent build. It requires the **ASP.NET Core Runtime 10 x64**, but it does not require Node.js because React is compiled into the archive before release.

Both editions include `Start-GlujDrive.cmd`. Launching it opens a visible terminal, starts the server, waits for the health endpoint, and opens the default browser. Keep the terminal open; press `Ctrl+C` or close it to stop Gluj Drive and release its resources. The installer creates a Start Menu shortcut to this launcher and can optionally create a desktop shortcut.

Install [Inno Setup 6](https://jrsoftware.org/isinfo.php) on the release machine, then build both artifacts and their SHA-256 checksum file:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/release/build-windows-release.ps1 `
  -Version 0.1.0
```

Output is written to `artifacts/release`. Use `-SkipInstaller` to build and validate only the portable archive when Inno Setup is unavailable. Node.js/npm and the .NET SDK are build-machine dependencies only.

Before publishing a GitHub release, verify the production build on a clean Windows machine, include the required FFmpeg license files and optional semantic-search package licenses, and tag the commit as `v0.1.0`.

## Optional semantic search

Semantic image search is opt-in. The server does not load a model or inspect image pixels until the host explicitly starts **Analyze library**. Embeddings are stored in `data/catalog/semantic/semantic.db`; the USearch HNSW file is a rebuildable cache.

Published builds can include a verified semantic-search package. To enable it, open **Semantic search** on the host PC and click **Install semantic search**. The browser starts a background task that copies or downloads the package, verifies its archive and per-file SHA-256 hashes, extracts the TinyCLIP search model and Windows search engine, activates them atomically, and reports each phase in the UI. It does not require administrator access or development tools.

To publish a verified TinyCLIP package, build the native runtime described in [`native/GlujDrive.Inference.Native/README.md`](native/GlujDrive.Inference.Native/README.md), then run `tools/semantic/package-ai-release.ps1`. Its default output is copied into published builds automatically. A smaller build may omit the bundled ZIP and use a release download instead:

```json
"SemanticSearch": {
  "ModelPackageUrl": "https://your-release-host/TinyCLIP-ncnn.zip",
  "ModelPackageSha256": "FULL_ARCHIVE_SHA256"
}
```

The installer prefers the bundled package and falls back to the configured download. If neither is available, the install control explains that the build has no semantic-search package; ordinary filename/path search continues to work.

TinyCLIP search values are normalized cosine similarities, not probabilities. Text search averages embeddings for the raw query and `a photo of {query}`, then rejects semantic results below `SemanticSearch:MinimumTextSimilarity` or farther than `SemanticSearch:MaximumTextSimilarityDrop` below the strongest in-scope result. The `MaximumSemanticCandidates` configuration value limits how many accepted semantic results can participate in rank fusion. Exact and ordinary filename/path matches are not removed by these semantic thresholds.
