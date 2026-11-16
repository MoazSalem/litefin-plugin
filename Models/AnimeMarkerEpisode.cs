// <copyright file="AnimeMarkerEpisode.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

namespace Litefin.Plugin.Models;

/// <summary>
/// Represents individual episode classification metadata parsed from the canon provider.
/// </summary>
public class AnimeMarkerEpisode
{
    /// <summary>
    /// Gets or sets the absolute sequence number of the anime episode.
    /// </summary>
    public int Number { get; set; }

    /// <summary>
    /// Gets or sets the original canonical episode title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the canon classification kind.
    /// </summary>
    public AnimeEpisodeKind Kind { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this episode serves primarily as a recap installment.
    /// </summary>
    public bool Recap { get; set; }

    /// <summary>
    /// Gets or sets an optional descriptive summary or arc designation.
    /// </summary>
    public string? Description { get; set; }
}
