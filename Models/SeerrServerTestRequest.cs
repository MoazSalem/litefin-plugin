// <copyright file="SeerrServerTestRequest.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

namespace Litefin.Plugin.Models;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// Request payload to test whether a Seerr server is reachable without requiring an API key.
/// </summary>
public class SeerrServerTestRequest
{
    /// <summary>
    /// Gets or sets the target Seerr base URL.
    /// </summary>
    [Required]
#pragma warning disable CA1056 // String preserves the administrator's unvalidated form input
    public string SeerrUrl { get; set; } = string.Empty;
#pragma warning restore CA1056
}
