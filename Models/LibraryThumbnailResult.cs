// <copyright file="LibraryThumbnailResult.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

#pragma warning disable CA1056 // URI properties should not be strings - used for JSON transport

namespace Litefin.Plugin.Models;

using MediaBrowser.Model.Dto;

/// <summary>
/// DTO returned by the /Litefin/Items/Thumbnails endpoint, containing the single best
/// candidate item and a pre-resolved best image URL for a given library.
/// </summary>
public class LibraryThumbnailResult
{
    /// <summary>
    /// Gets or sets the single best candidate item whose image is used for the resolved URL.
    /// Null when no suitable item could be found.
    /// </summary>
    public BaseItemDto? Item { get; set; }

    /// <summary>
    /// Gets or sets the pre-resolved best image URL for the library card thumbnail.
    /// Null when no suitable image could be resolved.
    /// </summary>
    public string? ResolvedUrl { get; set; }
}
