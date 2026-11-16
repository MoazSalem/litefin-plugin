// <copyright file="CachedShowData.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

namespace Litefin.Plugin.Models;

using System;
using System.Collections.Generic;

/// <summary>
/// In-memory and on-disk payload representing cached metadata for a specific anime series slug.
/// </summary>
public class CachedShowData
{
    /// <summary>
    /// Gets or sets the normalized show slug identifier used by the canon provider.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC timestamp when this data was fetched from the remote provider.
    /// </summary>
    public DateTime FetchedAt { get; set; }

    /// <summary>
    /// Gets the map of absolute episode numbers to their parsed classification metadata.
    /// </summary>
    public Dictionary<int, AnimeMarkerEpisode> Episodes { get; } = new();
}
