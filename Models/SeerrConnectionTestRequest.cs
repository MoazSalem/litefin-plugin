// <copyright file="SeerrConnectionTestRequest.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

namespace Litefin.Plugin.Models;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// Temporary Seerr credentials supplied by an administrator for a connection test.
/// </summary>
public class SeerrConnectionTestRequest
{
    /// <summary>
    /// Gets or sets the Seerr base URL.
    /// </summary>
    [Required]
#pragma warning disable CA1056 // String preserves the administrator's unvalidated form input
    public string SeerrUrl { get; set; } = string.Empty;
#pragma warning restore CA1056

    /// <summary>
    /// Gets or sets the Seerr API key.
    /// </summary>
    [Required]
    public string SeerrApiKey { get; set; } = string.Empty;
}
