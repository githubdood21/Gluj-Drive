# Gluj Drive

Gluj Drive is a lightweight personal photo server designed to run directly on a Windows home PC. The backend uses ASP.NET Core and the browser interface uses React with TypeScript.

See [PROJECT_PLAN.md](PROJECT_PLAN.md) for the product scope and delivery plan.

## Prerequisites

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

Open `http://localhost:5173`. Vite proxies `/api` requests to the loopback-only ASP.NET Core development endpoint.

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

During development, folder registrations are stored in `src/GlujDrive.Server/data/catalog/folders.json`, and the initial default source folder is `src/GlujDrive.Server/data/photos`. These bootstrap locations are controlled by `Storage:CatalogPath` and `Storage:DefaultFolderPath` in `appsettings.json`.

On the host PC, use the site's **Folders** panel to register additional existing Windows directories, choose a default upload destination, or stop scanning a directory. **Browse...** opens a native Windows folder dialog so paths do not need to be pasted manually. Registered folders are scanned recursively, and the panel displays their derived subfolder hierarchy. The upload selector includes the source root and every existing subfolder, allowing uploads to be written directly into an album. Images remain in their source directories and are not copied into the catalog. Removing a registration never deletes its files.

Folder paths and folder-management operations are host-only. Connections from other LAN or VPN devices can choose among folder names when uploading, but cannot see local paths, open the native picker, add or remove folders, empty folders, or change the default. The server enforces this using the connection's loopback address; it is not merely a hidden frontend control.

The default library timeline searches by media name, folder, or relative path and progressively adds metadata cards in batches of 24 as the user scrolls. Items are grouped newest-first by month across the library, with one unified date-rail entry per month. **View albums** switches to a collapsible tree where each registered source is a root and nested filesystem directories become nested albums instead of being flattened into the source. Cards and the viewer show the privacy-safe relative location (`Source / subfolder / file`) without exposing absolute Windows paths remotely. Selecting an image opens the zoomable viewer; animated GIFs play in that viewer, and videos use the browser's native controls.

Gallery cards use a staged derivative pipeline. The initial library response reads only filesystem metadata and any already-cached average RGB color; an unprocessed image uses a neutral fallback without opening its original. A card that remains near the viewport requests a 64-pixel WebP after 150 milliseconds, which also creates its true average-color cache, and promotes to a 640-pixel WebP after 500 milliseconds total. Cards far outside the viewport remove their image source while retaining inexpensive text and color metadata. The original file is requested only when the viewer opens. Generated colors and previews live under the application catalog's `previews` directory and can be rebuilt from originals; generation is limited to two concurrent jobs to protect the host PC during fast scrolling.

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

## Root account and remote access

On first launch, open Gluj Drive directly on the Windows host. The site requires creation of one root account before any remote client can access library APIs. Loopback requests from the host PC deliberately bypass sign-in so the owner cannot lock themselves out; host-only folder, AI, account, and server settings remain unavailable to remote clients even after they authenticate.

Remote authentication uses an HTTP-only, SameSite `Strict` ASP.NET Core cookie. The default persistent session is 365 days and can be shortened from the host-only **Settings** panel. Cookie encryption keys are persisted beneath `data/catalog/auth/keys` and protected with Windows DPAPI, so sessions survive restarts but remain tied to the Windows account running Gluj Drive. Passwords are never stored directly: the account file contains a unique salt and a PBKDF2-HMAC-SHA256 hash using 600,000 iterations. Changing the root account rotates its security stamp and invalidates existing remote sessions.

API requests carrying a foreign `Origin` are rejected instead of merely omitting CORS response headers. Unsafe remote requests without a browser origin are also rejected. This same-origin policy is intentionally stricter than ordinary CORS and still permits the loopback Vite development proxy. Authentication is rate-limited to five attempts per remote address per minute.

HTTP authentication does not encrypt traffic. Configure HTTPS or use a trusted private VPN before signing in across an untrusted network; the remote login screen warns when its connection is not HTTPS.

The host-only **Settings** panel manages session lifetime, upload limits, TinyCLIP similarity thresholds, semantic candidate limits, and root-account changes. These overrides are stored in `data/catalog/server-settings.json`. Increased upload limits require a server restart because Kestrel's request-body ceiling is established during startup; other exposed settings apply immediately.

## Optional semantic search

Semantic image search is opt-in. The server does not load a model or inspect image pixels until the host explicitly starts **Analyze library**. Embeddings are stored in `data/catalog/semantic/semantic.db`; the USearch HNSW file is a rebuildable cache.

Published builds can include a verified combined AI package. A non-technical user only needs to open the host-only **AI search** panel and click **Install AI search**. The browser queues a background server worker which copies or downloads the package, verifies its archive and per-file SHA-256 hashes, extracts the TinyCLIP model and Windows native runtime, activates them atomically, and reports each phase in the UI. It does not require administrator access or development tools.

To publish a verified TinyCLIP package, build the native runtime described in [`native/GlujDrive.Inference.Native/README.md`](native/GlujDrive.Inference.Native/README.md), then run `tools/semantic/package-ai-release.ps1`. Its default output is copied into published builds automatically. A smaller build may omit the bundled ZIP and use a release download instead:

```json
"SemanticSearch": {
  "ModelPackageUrl": "https://your-release-host/TinyCLIP-ncnn.zip",
  "ModelPackageSha256": "FULL_ARCHIVE_SHA256"
}
```

The installer prefers the bundled package and falls back to the configured download. If neither is available, the install control explains that the build has no AI package; ordinary filename/path search continues to work.

TinyCLIP search values are normalized cosine similarities, not probabilities. Text search averages embeddings for the raw query and `a photo of {query}`, then rejects semantic candidates below `SemanticSearch:MinimumTextSimilarity` or farther than `SemanticSearch:MaximumTextSimilarityDrop` below the strongest in-scope result. `MaximumSemanticCandidates` limits how many accepted semantic neighbors can participate in rank fusion. Exact and ordinary filename/path matches are not removed by these semantic thresholds.
