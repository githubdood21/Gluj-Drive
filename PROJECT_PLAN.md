# Gluj Drive — Project Plan

## 1. Product vision

Gluj Drive is a free, personal photo server that runs directly on a Windows home PC. The owner selects where photos are stored, and other devices can upload and browse the library through a responsive web interface.

The application is self-hosted software, not a hosted cloud service. It must remain useful on the local network without an internet connection and must not require a subscription, rented server, Docker, WSL, or an externally managed database.

### Product promise

> Install one lightweight Windows application, choose a photo folder, and privately browse or upload photos from any device.

## 2. Core principles

- **User-owned data:** Original files remain ordinary files on storage selected by the user.
- **Local-first:** Core functionality works entirely on the home network.
- **Simple installation:** Distribute a normal Windows installer with no separate runtime or database setup.
- **Low maintenance:** The server starts automatically and recovers safely from reboots and interrupted work.
- **No lock-in:** The catalog can be rebuilt by scanning registered source folders; files do not depend on sidecar metadata.
- **Secure defaults:** Listen locally by default; LAN and remote access require explicit configuration.
- **Lightweight by default:** AI, video transcoding, and other expensive features must not burden the base installation.
- **Originals are immutable:** Importing, browsing, and thumbnail generation never modify original media.

## 3. Intended users and deployment

The initial target is one owner or household running Windows 10 or Windows 11 on a home PC with an internal drive, external drive, or NAS share.

The application consists of:

1. An ASP.NET Core server running in the background, eventually as a Windows Service.
2. A React/TypeScript single-page application served by that backend.
3. An optional small Windows control or tray application for setup, status, and diagnostics.

While the server runs interactively, the web setup screen may invoke a native Windows folder picker through a host-only API. Once the server runs as a Windows Service, this responsibility moves to the tray/control application because Windows services cannot display UI in the signed-in user's desktop session.

Clients use a modern browser on a phone, tablet, or computer. Local-network access is the first target. Access from outside the home should initially use a private VPN such as Tailscale rather than automatic port forwarding or infrastructure operated by this project.

## 4. Technology decisions

### Backend

- **Runtime/framework:** ASP.NET Core on .NET 10 LTS
- **Language:** C#
- **API style:** Versioned JSON REST API with OpenAPI documentation
- **Database:** SQLite using WAL mode
- **Data access:** Entity Framework Core; targeted SQL where profiling justifies it
- **Background work:** Hosted `BackgroundService` workers with bounded channels
- **Authentication:** ASP.NET Core secure cookie authentication
- **Logging:** Built-in structured logging with rolling local log files
- **Image processing:** Magick.NET for the first implementation
- **Metadata extraction:** MetadataExtractor, supplemented where format support requires it
- **File identity:** SHA-256 content hash plus file size

### Frontend

- **Framework:** React
- **Language:** TypeScript with strict checking enabled
- **Build tool:** Vite
- **Server-state management:** TanStack Query
- **Routing:** React Router
- **Styling:** CSS modules or a small token-based CSS system; avoid a heavy component suite initially
- **Large timeline rendering:** A virtualized grid selected after a small performance prototype
- **End-to-end tests:** Playwright

### Packaging and operation

- Publish the backend as a self-contained `win-x64` application.
- Serve the compiled frontend from ASP.NET Core in production.
- Run interactively during development and as a Windows Service in production.
- Use a signed Windows installer when release infrastructure is available.
- Keep server configuration and the catalog separate from the photo library.

## 5. High-level architecture

```text
Browser clients
      |
 HTTP/HTTPS
      |
ASP.NET Core host
  |-- React static application
  |-- REST API and authentication
  |-- Upload and download streaming
  |-- Library/indexing services
  |-- Background job workers
  |
  |-- SQLite catalog
  |-- Preview/cache directory
  `-- User-selected original-media directories
```

The API, background processing, and web host begin as modules in one deployable process. We should not introduce microservices for the personal-server use case.

## 6. Storage model

Original media, generated previews, and application data have separate roles:

- **Originals:** User-owned source files. Never silently changed or deleted.
- **Catalog:** Searchable metadata, album membership, favorites, accounts, jobs, and hashes in SQLite.
- **Previews:** Disposable thumbnails and screen-sized images that can be regenerated.
- **Temporary files:** In-progress uploads and atomic-processing intermediates.
- **Backups:** Exported catalog and configuration snapshots; original-media backup remains the owner's responsibility.

The application uses registered source folders. Existing images are indexed in place, and uploads are written directly into a selected registered folder. One folder is designated as the default upload destination. The application catalog stores folder registrations and derived metadata, but never duplicates originals.

### Upload pipeline

1. Stream an upload to a temporary file without buffering the whole file in memory.
2. Enforce configured size limits and available-disk checks.
3. Calculate SHA-256 while streaming.
4. Verify the file signature and supported format independently of its extension.
5. Detect duplicates using the content hash and file size.
6. Atomically move the completed upload into the selected source folder.
7. Commit its catalog record.
8. Queue metadata extraction and preview generation.
9. Make processing state visible to the client.

Interrupted work must be retryable and must not create phantom catalog records.

## 7. Initial domain model

- **User:** Owner account and future household accounts.
- **Asset:** An original image and its core file properties.
- **AssetMetadata:** Capture time, dimensions, orientation, camera, GPS, and other extracted information.
- **StorageRoot:** Configured managed or indexed directory.
- **Preview:** Generated rendition identified by asset, size, and processing version.
- **Album:** User-created logical collection.
- **AlbumAsset:** Ordered album membership.
- **Favorite:** Per-user favorite state.
- **Upload:** Progress and result of an upload session.
- **BackgroundJob:** Durable work and retry state for indexing and preview generation.
- **TrashEntry:** Recoverable deletion state and expiry information.

Schema details will be recorded in migrations and architecture decision records as implementation begins.

## 8. MVP scope

The first usable release should provide:

- First-run owner setup.
- Registration, removal, and default selection of source folders.
- Authenticated browser access.
- Drag-and-drop and mobile-browser uploads.
- JPEG, PNG, WebP, GIF, and HEIC ingestion where codec support is dependable.
- Streaming uploads with progress reporting.
- Duplicate detection.
- EXIF capture-date and orientation extraction.
- Asynchronous thumbnail generation.
- Chronological, virtualized photo timeline.
- Month and year navigation.
- Full-screen viewer and original download.
- Favorites and albums.
- A recoverable trash workflow.
- Clear storage, processing, and error status.
- Optional local semantic search and visually similar-media lookup.
- LAN-only operation with secure defaults.
- Self-contained Windows build.
- Catalog/configuration backup and restore.

## 9. Explicit non-goals for the MVP

- Commercial hosting, subscriptions, or project-operated cloud infrastructure
- Native mobile applications
- Facial recognition
- Video transcoding
- RAW photo development
- Photo editing
- Public social features
- Automatic router port forwarding
- A custom VPN, relay, or dynamic-DNS service
- Microservices or distributed job infrastructure
- S3/object storage as the primary implementation

These may be considered later without weakening the lightweight base product.

## 10. Security baseline

- Bind to loopback by default; LAN listening is an explicit owner action.
- Require authentication even on the LAN once setup is complete.
- Use secure, HTTP-only, same-site cookies and anti-forgery protection.
- Rate-limit login and upload endpoints.
- Validate file signatures and decode media in isolated, failure-tolerant processing paths.
- Apply upload-size, request-time, and decompression limits.
- Never construct storage paths directly from user-supplied filenames.
- Protect against traversal, symlink/reparse-point escape, and unsafe archive formats.
- Avoid writing secrets to logs.
- Prefer a private VPN for access outside the home.
- Do not claim that self-hosting is a backup; provide visible backup guidance.

## 11. Proposed repository layout

```text
Gluj-Drive/
|-- src/
|   |-- GlujDrive.Server/          # ASP.NET Core host and API
|   |-- GlujDrive.Application/     # Use cases and application services
|   |-- GlujDrive.Domain/          # Domain entities and rules
|   |-- GlujDrive.Infrastructure/  # SQLite, filesystem, codecs, background work
|   `-- GlujDrive.Web/             # React and TypeScript application
|-- tests/
|   |-- GlujDrive.UnitTests/
|   |-- GlujDrive.IntegrationTests/
|   `-- GlujDrive.E2E/
|-- docs/
|   `-- decisions/                 # Architecture decision records
|-- installer/
|-- GlujDrive.slnx
`-- PROJECT_PLAN.md
```

Projects may be consolidated if the boundaries add ceremony without value. The deployable result remains one server process plus static frontend files.

## 12. Delivery phases

### Phase 0 — Foundation

- Create the .NET solution and React/Vite workspace.
- Establish development commands, formatting, and tests.
- Serve the production frontend through ASP.NET Core.
- Add SQLite migrations, health checks, structured logging, and configuration validation.
- Document local development and data-directory conventions.

**Exit condition:** One command starts both development environments, and a production build launches as one self-contained server.

### Phase 1 — Reliable ingestion

- Implement library configuration and storage-root validation.
- Stream uploads through temporary files.
- Add validation, hashing, deduplication, atomic placement, and durable job state.
- Extract essential metadata and generate thumbnails.
- Provide upload progress and actionable errors.

**Exit condition:** A large batch can be interrupted and retried without lost originals, duplicate assets, or invalid catalog state.

### Phase 2 — Photo browsing

- Implement paginated timeline endpoints.
- Build the virtualized responsive gallery.
- Add date navigation, asset details, full-screen viewing, and downloads.
- Handle orientation and supported browser formats correctly.

**Exit condition:** A representative large library remains responsive on desktop and mobile browsers.

### Phase 3 — Personal organization

- Add favorites, albums, selection tools, and recoverable trash.
- Add background indexing of existing folders.
- Define behavior for offline or removed external drives.

**Exit condition:** Users can manage both uploaded and pre-existing collections without changing originals unexpectedly.

### Phase 4 — Windows productization

- Run reliably as a Windows Service.
- Add a setup/status control application if testing validates the need.
- Build installer, upgrade, uninstall, and recovery flows.
- Add backup/restore and diagnostic export.
- Test reboot, sleep, disk-full, drive-removal, and interrupted-update scenarios.

**Exit condition:** A non-developer can install, configure, use, update, and remove the application without command-line work.

### Phase 5 — Safe remote and household access

- Add household accounts and authorization rules.
- Document and optionally detect private-VPN access.
- Add HTTPS/reverse-proxy guidance for advanced users.
- Perform focused threat modeling and security testing.

**Exit condition:** Remote use does not require exposing an unaudited server directly to the public internet.

## 13. Quality and performance targets

Initial targets, to be validated with prototypes:

- Idle memory below 200 MB excluding operating-system file cache.
- First useful server response within five seconds on a typical home PC.
- No full image buffered in server memory during upload or download.
- Smooth timeline browsing with at least 100,000 cataloged assets.
- Staged color, low-resolution, and medium-resolution gallery rendering with off-screen image-source eviction.
- Bounded CPU and worker concurrency during indexing.
- Generated data can be deleted and rebuilt without affecting originals.
- Database and filesystem changes remain consistent across forced termination.

Performance claims must be backed by a repeatable sample-library benchmark.

## 14. Testing strategy

- Unit tests for path safety, hashing, metadata normalization, and domain rules.
- Integration tests using temporary real directories and SQLite databases.
- Contract tests for upload, timeline, and authentication APIs.
- Codec fixtures containing malformed, unusually large, rotated, and metadata-heavy images.
- Playwright tests for setup, upload, browsing, albums, and trash recovery.
- Failure-injection tests for cancellation, disk-full conditions, missing drives, and process termination.
- Windows release smoke tests on clean supported installations.

## 15. Early decisions still to validate

- Exact minimum supported Windows release.
- Whether the MVP needs a tray/control application or can open a setup page from the installer.
- Upload filename collision and optional subfolder policies.
- HEIC decoding and browser-display strategy.
- Whether resumable uploads are required in the MVP or immediately afterward.
- Local hostname discovery approach.
- Installer and code-signing strategy.
- Backup format and schedule.

These should be resolved through small technical prototypes or architecture decision records, not assumptions embedded deeply in the implementation.

## 16. Immediate next steps

1. Scaffold the solution and frontend according to the proposed layout.
2. Add a development orchestration command and production static-file hosting.
3. Write architecture decisions for storage ownership, file identity, and local network exposure.
4. Build an ingestion spike that streams, validates, hashes, stores, and thumbnails one image.
5. Benchmark thumbnail generation and timeline queries before expanding the feature set.
