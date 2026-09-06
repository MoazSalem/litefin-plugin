// <copyright file="SeerrQuickConnectInitiateRequest.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

namespace Litefin.Plugin.Models;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// Request payload to initiate a Jellyfin Quick Connect session with Seerr.
/// </summary>
public class SeerrQuickConnectInitiateRequest
{
    /// <summary>
    /// Gets or sets the target Seerr base URL.
    /// </summary>
    [Required]
#pragma warning disable CA1056 // String preserves the administrator's unvalidated form input
    public string SeerrUrl { get; set; } = string.Empty;
#pragma warning restore CA1056
}
