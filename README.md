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

The default library timeline searches by image, folder, or relative path and progressively adds metadata cards in batches of 24 as the user scrolls. Pictures are grouped newest-first by month across the library, with one unified date-rail entry per month. **View albums** switches to a collapsible tree where each registered source is a root and nested filesystem directories become nested albums instead of being flattened into the source. Cards and the viewer show the privacy-safe relative location (`Source / subfolder / file`) without exposing absolute Windows paths remotely. Selecting a picture opens the built-in viewer, which supports mouse-wheel zoom, click-and-drag panning, phone pinch zoom, keyboard navigation, downloading, and moving an individual image to trash.

Gallery cards use a staged derivative pipeline. The initial library response reads only filesystem metadata and any already-cached average RGB color; an unprocessed image uses a neutral fallback without opening its original. A card that remains near the viewport requests a 64-pixel WebP after 150 milliseconds, which also creates its true average-color cache, and promotes to a 640-pixel WebP after 500 milliseconds total. Cards far outside the viewport remove their image source while retaining inexpensive text and color metadata. The original file is requested only when the viewer opens. Generated colors and previews live under the application catalog's `previews` directory and can be rebuilt from originals; generation is limited to two concurrent jobs to protect the host PC during fast scrolling.

The derivative pipeline uses SixLabors.ImageSharp 3.1.12 under its community/non-commercial license terms. JPEG, PNG, GIF, and WebP derivatives are supported. HEIC/HEIF files currently retain their color fallback if the decoder cannot process them; originals remain viewable when the browser supports the format.

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

## Optional semantic search

Semantic image search is opt-in. The server does not load a model or inspect image pixels until the host explicitly starts **Analyze library**. Embeddings are stored in `data/catalog/semantic/semantic.db`; the USearch HNSW file is a rebuildable cache.

To publish a verified TinyCLIP package, build the native runtime described in [`native/GlujDrive.Inference.Native/README.md`](native/GlujDrive.Inference.Native/README.md), host the converted model ZIP, and set:

```json
"SemanticSearch": {
  "ModelPackageUrl": "https://your-release-host/TinyCLIP-ncnn.zip",
  "ModelPackageSha256": "FULL_ARCHIVE_SHA256"
}
```

Until both a valid package URL and hash are configured, the download control stays disabled and ordinary filename/path search continues to work.
