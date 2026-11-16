// <copyright file="AnimeMarkersResponse.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

namespace Litefin.Plugin.Models;

using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// Response payload delivered to the web client for an entire series query.
/// </summary>
public class AnimeMarkersResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether anime marker processing is enabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the series was positively identified as an anime title.
    /// </summary>
    [JsonPropertyName("matched")]
    public bool Matched { get; set; }

    /// <summary>
    /// Gets or sets the canonical provider slug resolved for this series.
    /// </summary>
    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    /// <summary>
    /// Gets the episode classification entries keyed by Jellyfin episode GUID.
    /// </summary>
    [JsonPropertyName("episodes")]
    public Dictionary<string, EpisodeVerdict> Episodes { get; } = new();

    /// <summary>
    /// Gets the aggregated season entries keyed by Jellyfin season GUID.
    /// </summary>
    [JsonPropertyName("seasons")]
    public Dictionary<string, SeasonVerdict> Seasons { get; } = new();
}
