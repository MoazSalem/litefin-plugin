// <copyright file="SeerrQuickConnectCheckResult.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

namespace Litefin.Plugin.Models;

/// <summary>
/// Result of checking Quick Connect status and retrieving the Seerr API key.
/// </summary>
public class SeerrQuickConnectCheckResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the code was authorized by the user.
    /// </summary>
    public bool Authenticated { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the API key was successfully retrieved and saved.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets an optional user-facing message or error explanation.
    /// </summary>
    public string? Message { get; set; }
}
