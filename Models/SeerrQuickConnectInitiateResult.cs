// <copyright file="SeerrQuickConnectInitiateResult.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

namespace Litefin.Plugin.Models;

/// <summary>
/// Result payload returned upon initiating Quick Connect.
/// </summary>
public class SeerrQuickConnectInitiateResult
{
    /// <summary>
    /// Gets or sets the user-facing verification code (e.g. 6 characters).
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the secret token used for status verification polling.
    /// </summary>
    public string Secret { get; set; } = string.Empty;
}
