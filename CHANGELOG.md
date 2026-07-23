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

- Published builds serve React and the API from port `5199` on all IPv4 interfaces; development keeps ASP.NET loopback-only behind the LAN-accessible Vite proxy.
- Mutable catalog and configuration state is stored beneath `%LOCALAPPDATA%\Gluj Drive\data`, separate from installed program files.
- Windows releases provide a self-contained Inno Setup installer and a framework-dependent portable ZIP with the same visible-console launcher and Ctrl+C shutdown behavior.
- The installer can add a private-network, local-subnet Windows Firewall rule; router and wider internet access remain the host owner's responsibility.
- HTTPS or a private VPN such as Tailscale is recommended for remote access.
- This is preview software supplied as is; back up important media and configuration before upgrading.
