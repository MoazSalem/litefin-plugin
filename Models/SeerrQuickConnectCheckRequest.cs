// <copyright file="SeerrQuickConnectCheckRequest.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

namespace Litefin.Plugin.Models;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// Request payload to check Quick Connect status and complete API key acquisition.
/// </summary>
public class SeerrQuickConnectCheckRequest
{
    /// <summary>
    /// Gets or sets the target Seerr base URL.
    /// </summary>
    [Required]
#pragma warning disable CA1056
    public string SeerrUrl { get; set; } = string.Empty;
#pragma warning restore CA1056

    /// <summary>
    /// Gets or sets the secret token from initiation.
    /// </summary>
    [Required]
    public string Secret { get; set; } = string.Empty;
}
