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
    /// Gets or sets the serialized settings JSON payload.
    /// </summary>
    public string Settings { get; set; } = string.Empty;
}
