// <copyright file="SeerrAdminLoginResult.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

namespace Litefin.Plugin.Models;

/// <summary>
/// Result of authenticating via admin credentials and saving the Seerr API key.
/// </summary>
public class SeerrAdminLoginResult
{
    /// <summary>
    /// Gets or sets a value indicating whether login succeeded and the API key was saved.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets an optional user-facing message or error explanation.
    /// </summary>
    public string? Message { get; set; }
}
