// <copyright file="SeerrRequest.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

namespace Litefin.Plugin.Models;

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

/// <summary>
/// A media request submitted by an authenticated Litefin user.
/// </summary>
public class SeerrRequest
{
    /// <summary>
    /// Gets or sets the media type. Supported values are movie and tv.
    /// </summary>
    [Required]
    public string MediaType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the TMDB media identifier.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MediaId { get; set; }

    /// <summary>
    /// Gets or sets the requested season numbers for a television series.
    /// </summary>
    public IReadOnlyList<int>? Seasons { get; set; }
}
