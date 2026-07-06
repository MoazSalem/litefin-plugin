# Litefin Plugin

A lightweight Jellyfin Server plugin that acts as a companion server extension for the Litefin client application. It introduces dedicated server-side APIs, state persistence, and integrations to enhance the client experience.

---

## Feature Modules

### Settings Backup & Restore (Current)
* **Multi-User Sync**: Securely stores settings backups partitioned by Jellyfin User ID.
* **Privacy Focused**: Safely filters out sensitive authentication tokens, connection URLs, and device identifiers from the backup payload before saving.
* **Admin Dashboard**: Adds a management page to the Jellyfin dashboard under *Plugins*, allowing administrators to view active user backups, check backup timestamps, and prune storage.

*More companion features and server-side helper modules will be added in future releases.*

---


## Installation

### Option 1: Via Repository Manifest (Recommended)
1. Copy the raw manifest repository URL:
   ```text
   https://raw.githubusercontent.com/MoazSalem/litefin-plugin/release/manifest.json
   ```
2. In your Jellyfin Server, navigate to **Dashboard** > **Plugins** > **Repositories**.
3. Click **Add** (`+`), enter a name (e.g. `Litefin Plugins`), and paste the copied URL.
4. Go to the **Catalog** tab, locate **Litefin Plugin** under the **General** category, and click **Install**.
5. Restart your Jellyfin Server to load the plugin.

### Option 2: Manual Installation
1. Compile the plugin or download the `.zip` archive from the Releases page.
2. Extract the contents (including `Litefin.Plugin.dll`) into your Jellyfin server's `plugins/Litefin` directory:
   * **Windows (Service)**: `C:\ProgramData\Jellyfin\Server\plugins\`
   * **Windows (Portable)**: `<Jellyfin-Directory>\data\plugins\`
   * **Linux/Docker**: `/config/plugins/` (or mapped path)
3. Restart the Jellyfin server.

---

## Build and Development

### Prerequisites
* [.NET 9.0 SDK](https://dotnet.microsoft.com/download)
* A local Jellyfin server development setup (optional, for testing)

### Compilation
Build the plugin in Release mode using the .NET CLI:
```bash
dotnet build -c Release
```
This produces the plugin DLL inside `bin/Release/net9.0/`.

---

## Configuration

No initial configuration is needed on the server side. 
* **Clients**: Connects automatically to the client's companion API. User settings can be backed up from the **Backup & Restore** tab in Litefin client settings.
* **Administrators**: Navigate to **Dashboard** > **Plugins** > **Litefin** to view a summary of stored user configurations and delete records when necessary.
