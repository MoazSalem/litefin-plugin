// <copyright file="BackupRequest.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

namespace Litefin.Plugin.Models;

/// <summary>
/// Data transfer object holding backup creation parameters.
/// </summary>
public class BackupRequest
{
    /// <summary>
    /// Gets or sets the unique identifier of the backup snapshot.
    /// If null or empty, this request represents a new backup creation.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the unique identifier of the client device.
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the friendly model/display name of the client device.
    /// </summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the custom name given to the backup by the user.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the serialized settings JSON payload.
    /// </summary>
    public string Settings { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Litefin application version.
    /// </summary>
    public string? AppVersion { get; set; }

    /// <summary>
    /// Gets or sets the platform/OS code.
    /// </summary>
    public string? Platform { get; set; }
}
