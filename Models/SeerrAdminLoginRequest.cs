// <copyright file="SeerrAdminLoginRequest.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

namespace Litefin.Plugin.Models;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// Request payload to authenticate directly using Seerr admin credentials.
/// </summary>
public class SeerrAdminLoginRequest
{
    /// <summary>
    /// Gets or sets the target Seerr base URL.
    /// </summary>
    [Required]
#pragma warning disable CA1056
    public string SeerrUrl { get; set; } = string.Empty;
#pragma warning restore CA1056

    /// <summary>
    /// Gets or sets the Seerr admin username or email.
    /// </summary>
    [Required]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Seerr admin password.
    /// </summary>
    public string Password { get; set; } = string.Empty;
}
