// <copyright file="LibraryThumbnailResult.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

#pragma warning disable CA1056 // URI properties should not be strings - used for JSON transport

namespace Litefin.Plugin.Models;

using System.Collections.Generic;
using MediaBrowser.Model.Dto;

/// <summary>
/// DTO returned by the /Litefin/Items/Thumbnails endpoint, containing candidate items
/// and a pre-resolved best image URL for a single library.
/// </summary>
public class LibraryThumbnailResult
{
    /// <summary>
    /// Gets or sets the list of candidate BaseItemDto items that have relevant image types.
    /// </summary>
    public IReadOnlyList<BaseItemDto> Items { get; set; } = System.Array.Empty<BaseItemDto>();

    /// <summary>
    /// Gets or sets the pre-resolved best image URL for the library card thumbnail.
    /// Null when no suitable image could be resolved.
    /// </summary>
    public string? ResolvedUrl { get; set; }
}
