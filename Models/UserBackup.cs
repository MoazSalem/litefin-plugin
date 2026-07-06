// <copyright file="UserBackup.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

namespace Litefin.Plugin.Models;

using System;

/// <summary>
/// Represents a user-specific settings backup snapshot stored on the server.
/// </summary>
public class UserBackup
{
    /// <summary>
    /// Gets or sets the unique identifier (GUID string) of the Jellyfin user.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin username at the time the backup was created.
    /// This is stored primarily for display purposes on the client UI.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp (UTC) when this backup was saved.
    /// Used by the client to determine if a newer local configuration exists.
    /// </summary>
    public DateTime DateCreated { get; set; }

    /// <summary>
    /// Gets or sets the serialized JSON payload containing the client settings key-value pairs.
    /// </summary>
    public string Settings { get; set; } = string.Empty;
}
