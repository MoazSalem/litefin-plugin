// <copyright file="SeasonVerdict.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

namespace Litefin.Plugin.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Transport DTO representing the audio evaluation verdict for an entire season.
/// </summary>
public class SeasonVerdict
{
    /// <summary>
    /// Gets or sets the aggregated audio classification across all episodes in this season.
    /// </summary>
    [JsonPropertyName("audio")]
    public string? Audio { get; set; }
}
