# Litefin Plugin

A lightweight Jellyfin Server companion extension for the Litefin client. It exposes dedicated server-side APIs, state persistence, and management tools to store and sync your app settings securely.

---

## Features

* **Multi-Snapshot Backups**: Save as many preference snapshots as you want. Give them custom labels or let the client auto-identify them using device metadata.
* **Shared Visibility, Secured Control**: All backups on the server are visible to any user for cross-profile restoring. However, overwrite and delete actions are strictly restricted to the user who created the backup.
* **Device & Platform Context**: Captures device name, device ID, Litefin app version, and platform (Web, Tizen, webOS) for every snapshot.
* **Admin Console**: Administrators can manage all stored backups from the Jellyfin Dashboard (under **Plugins** > **Litefin**). Features newest-first sorting, custom backup records deletion, export/downloading, and merging/importing backup JSON files.
* **Privacy Focused**: Sensitive parameters (e.g., access tokens, server connection URLs, local session states) are stripped client-side before backup payloads are uploaded.

---

## Installation

### Option 1: Via Repository Manifest (Recommended)
1. Copy this manifest repository URL:
   ```text
   https://raw.githubusercontent.com/MoazSalem/litefin-plugin/release/manifest.json
   ```
2. Navigate to **Dashboard** > **Plugins** > **Repositories** in your Jellyfin Server.
3. Click **Add** (`+`), enter a name (e.g. `Litefin Plugins`), and paste the URL.
4. Go to the **Catalog** tab, find **Litefin Plugin** under **General**, and click **Install**.
5. Restart your Jellyfin Server.

### Option 2: Manual Installation
1. Compile the plugin or download the `.zip` archive from the Releases page.
2. Extract the archive (specifically `Litefin.Plugin.dll` and `manifest.json`) into your server's plugin directory:
   * **Windows (Service)**: `C:\ProgramData\Jellyfin\Server\plugins\Litefin\`
   * **Windows (Portable)**: `<Jellyfin-Directory>\data\plugins\Litefin\`
   * **Linux/Docker**: `/config/plugins/Litefin/`
3. Restart your Jellyfin Server.

---

## Build and Development

### Prerequisites
* [.NET 9.0 SDK](https://dotnet.microsoft.com/download)

### Compilation
Build the plugin DLL using the .NET CLI:
```bash
dotnet build -c Release
```
This outputs the compiled DLL under `bin/Release/net9.0/Litefin.Plugin.dll`.

---

## Configuration

* **Client**: In the Litefin client, go to **Settings** > **Backup & Restore** to select, restore, overwrite, or delete backup snapshots.
* **Admin**: Go to **Dashboard** > **Plugins** > **Litefin** to monitor stored configs, export copies, or prune database records.

## Storage Location
Stored backups are saved inside the Jellyfin configuration folder:
* **Windows**: `C:\ProgramData\Jellyfin\Server\plugins\configurations\Litefin.Plugin.xml` (or `Litefin.xml`)
* **Linux/Docker**: `/config/plugins/configurations/Litefin.Plugin.xml`
