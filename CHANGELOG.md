# Changelog

All notable changes to Gluj Drive are documented here.

## [0.1.0] - Unreleased

Initial self-hosted preview release for Windows.

### Included

- Recursive local media folders, subfolder albums, exclusions, uploads, downloads, and recoverable deletion.
- Timeline and album views with progressive image previews, GIF playback, and buffered video streaming.
- Local TinyCLIP semantic search, resumable indexing, similarity search, and CPU/Vulkan selection.
- Owner-account authentication for remote clients, host-PC administration, same-origin request protection, and configurable IP allow/deny lists.
- Light and dark themes, scoped media search, infinite scrolling, a responsive media viewer, responsive top/side library controls, and card or resolution-aware justified gallery layouts.

### Deployment notes

- The React/Vite development client listens on port `5173` on all IPv4 interfaces; the ASP.NET server remains loopback-only by default.
- Windows Firewall and network/router access remain the responsibility of the host owner.
- HTTPS or a private VPN such as Tailscale is recommended for remote access.
- This is preview software supplied as is; back up important media and configuration before upgrading.
