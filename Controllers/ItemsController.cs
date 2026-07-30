// <copyright file="ItemsController.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

#pragma warning disable CA1848 // Use LoggerMessage delegates for performance
#pragma warning disable CA1031 // Do not catch general exception types
#pragma warning disable CA5394 // Do not use insecure randomness - shuffling display thumbnails only

namespace Litefin.Plugin.Controllers;

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Litefin.Plugin.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

/// <summary>
/// REST API controller that exposes endpoints to batch query item DTOs across multiple libraries.
/// </summary>
[ApiController]
[Authorize]
[Route("Litefin/[controller]")]
[Produces(MediaTypeNames.Application.Json)]
public class ItemsController : ControllerBase
{
    private readonly IUserManager userManager;
    private readonly IUserViewManager userViewManager;
    private readonly ILibraryManager libraryManager;
    private readonly IPlaylistManager playlistManager;
    private readonly IDtoService dtoService;
    private readonly ILogger<ItemsController> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemsController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userViewManager">Instance of the <see cref="IUserViewManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="playlistManager">Instance of the <see cref="IPlaylistManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public ItemsController(
        IUserManager userManager,
        IUserViewManager userViewManager,
        ILibraryManager libraryManager,
        IPlaylistManager playlistManager,
        IDtoService dtoService,
        ILoggerFactory loggerFactory)
    {
        this.userManager = userManager;
        this.userViewManager = userViewManager;
        this.libraryManager = libraryManager;
        this.playlistManager = playlistManager;
        this.dtoService = dtoService;
        this.logger = loggerFactory.CreateLogger<ItemsController>();
    }

    /// <summary>
    /// Batch retrieves latest items for multiple library parent IDs in a single HTTP request.
    /// </summary>
    /// <param name="parentIds">Array of parent library IDs to fetch latest items for.</param>
    /// <param name="userId">Optional. User ID filter.</param>
    /// <param name="limit">Optional. Maximum number of items per library (default: 12).</param>
    /// <param name="isPlayed">Optional. Filter by played status.</param>
    /// <param name="fields">Optional. Additional DTO fields to include.</param>
    /// <response code="200">Dictionary mapping library parent ID to its list of latest BaseItemDto items.</response>
    /// <response code="401">User context missing.</response>
    /// <returns>A dictionary mapping parent ID to BaseItemDto list.</returns>
    [HttpGet("Latest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<Dictionary<Guid, IReadOnlyList<BaseItemDto>>> GetBatchLatest(
        [FromQuery] string? parentIds,
        [FromQuery] Guid? userId,
        [FromQuery] int? limit,
        [FromQuery] bool? isPlayed,
        [FromQuery] string? fields)
    {
        if (string.IsNullOrWhiteSpace(parentIds))
        {
            return this.Ok(new Dictionary<Guid, IReadOnlyList<BaseItemDto>>());
        }

        var parsedParentIds = parentIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToArray();

        if (parsedParentIds.Length == 0)
        {
            return this.Ok(new Dictionary<Guid, IReadOnlyList<BaseItemDto>>());
        }

        this.logger.LogInformation("Processing GetBatchLatest for {Count} parent libraries", parsedParentIds.Length);

        // Resolve user claim or query parameter
        var targetUserId = userId;
        if (!targetUserId.HasValue)
        {
            var claimsUserId = this.User.Claims.FirstOrDefault(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
            if (Guid.TryParse(claimsUserId, out var parsedGuid))
            {
                targetUserId = parsedGuid;
            }
        }

        if (!targetUserId.HasValue)
        {
            return this.Unauthorized("User context missing.");
        }

        var user = this.userManager.GetUserById(targetUserId.Value);
        if (user == null)
        {
            return this.Unauthorized("User not found.");
        }

        var itemLimit = limit ?? 12;

        ItemFields[]? parsedFields = null;
        if (!string.IsNullOrWhiteSpace(fields))
        {
            parsedFields = fields
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Enum.TryParse<ItemFields>(s, true, out var f) ? (ItemFields?)f : null)
                .Where(f => f.HasValue)
                .Select(f => f!.Value)
                .ToArray();
        }

        // Configure DTO options. Default to lightweight fields unless client explicitly requests more.
        var dtoOptions = new DtoOptions(allFields: false)
        {
            Fields = parsedFields is { Length: > 0 } ? parsedFields : Array.Empty<ItemFields>(),
            EnableImages = true,
            EnableUserData = true,
            ImageTypeLimit = 1,
        };

        var result = new Dictionary<Guid, IReadOnlyList<BaseItemDto>>();

        foreach (var parentId in parsedParentIds)
        {
            if (parentId == Guid.Empty)
            {
                continue;
            }

            var parentItem = this.libraryManager.GetItemById(parentId);
            if (parentItem == null)
            {
                result[parentId] = Array.Empty<BaseItemDto>();
                continue;
            }

            var collectionFolder = parentItem as ICollectionFolder;
            var collectionType = collectionFolder?.CollectionType;
            BaseItemKind[] includeItemTypes = Array.Empty<BaseItemKind>();

            if (collectionType == CollectionType.movies)
            {
                includeItemTypes = [BaseItemKind.Movie];
            }
            else if (collectionType == CollectionType.tvshows)
            {
                includeItemTypes = [BaseItemKind.Episode];
            }
            else if (collectionType == CollectionType.music)
            {
                includeItemTypes = Array.Empty<BaseItemKind>();
            }
            else if (collectionType == CollectionType.musicvideos)
            {
                includeItemTypes = [BaseItemKind.MusicVideo];
            }

            try
            {
                var list = this.userViewManager.GetLatestItems(
                    new LatestItemsQuery
                    {
                        GroupItems = true,
                        IncludeItemTypes = includeItemTypes,
                        IsPlayed = isPlayed ?? (user.HidePlayedInLatest ? false : null),
                        Limit = itemLimit,
                        ParentId = parentId,
                        User = user,
                    },
                    dtoOptions);

                if (list == null || list.Count == 0)
                {
                    result[parentId] = Array.Empty<BaseItemDto>();
                    continue;
                }

                var resolvedItems = new List<BaseItem>(list.Count);
                var childCounts = new List<int>(list.Count);

                foreach (var tuple in list)
                {
                    var children = tuple.Item2;
                    if ((children == null || children.Count == 0) && tuple.Item1 == null)
                    {
                        continue;
                    }

                    BaseItem item;
                    int childCount = 0;

                    if (children != null && children.Count > 0)
                    {
                        item = children[0];
                        if (tuple.Item1 is not null && (children.Count > 1 || tuple.Item1 is MusicAlbum || tuple.Item1 is Series))
                        {
                            item = tuple.Item1;
                            childCount = children.Count;
                        }
                    }
                    else
                    {
                        item = tuple.Item1!;
                    }

                    resolvedItems.Add(item);
                    childCounts.Add(childCount);
                }

                if (resolvedItems.Count == 0)
                {
                    result[parentId] = Array.Empty<BaseItemDto>();
                    continue;
                }

                var dtos = this.dtoService.GetBaseItemDtos(resolvedItems, dtoOptions, user);
                for (int i = 0; i < dtos.Count; i++)
                {
                    var dto = dtos[i];

                    // Strip unneeded metadata fields to produce a minimal home card DTO
                    dto.PremiereDate = null;
                    dto.EndDate = null;
                    dto.OfficialRating = null;
                    dto.CommunityRating = null;
                    dto.ChannelId = null;
                    dto.Status = null;
                    dto.AirDays = null;
                    dto.ChildCount = null;
                    dto.Overview = null;
                    dto.Genres = null;
                    dto.Taglines = null;
                    dto.ExternalUrls = null;
                    dto.People = null;
                    dto.Studios = null;
                }

                result[parentId] = dtos;
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Error processing latest items for parentId {ParentId}", parentId);
                result[parentId] = Array.Empty<BaseItemDto>();
            }
        }

        return this.Ok(result);
    }

    /// <summary>
    /// Retrieves candidate backdrop &amp; thumbnail items for multiple parent library IDs in a single request.
    /// Also resolves the best image URL server-side using the appropriate priority chain for each collection type.
    /// </summary>
    /// <param name="parentIds">Comma-separated list of parent library GUIDs.</param>
    /// <param name="userId">Optional. User ID filter.</param>
    /// <response code="200">Dictionary mapping parentId -> LibraryThumbnailResult with candidate items and pre-resolved image URL.</response>
    /// <returns>A dictionary mapping parentId to LibraryThumbnailResult.</returns>
    [HttpGet("Thumbnails")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyDictionary<string, LibraryThumbnailResult>> GetLibraryThumbnails(
        [FromQuery] string? parentIds,
        [FromQuery] Guid? userId)
    {
        var result = new Dictionary<string, LibraryThumbnailResult>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(parentIds))
        {
            return this.Ok(result);
        }

        var targetUserId = userId;
        if (!targetUserId.HasValue)
        {
            var claimsUserId = this.User.Claims.FirstOrDefault(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
            if (Guid.TryParse(claimsUserId, out var parsedGuid))
            {
                targetUserId = parsedGuid;
            }
        }

        if (!targetUserId.HasValue)
        {
            return this.Unauthorized("User context missing.");
        }

        var user = this.userManager.GetUserById(targetUserId.Value);
        if (user == null)
        {
            return this.Unauthorized("User not found.");
        }

        var ids = parentIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? (Guid?)g : null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .ToArray();

        var dtoOptions = new DtoOptions(allFields: false)
        {
            EnableImages = true,
            EnableUserData = false,
            ImageTypeLimit = 1,
        };

        var serverUrl = $"{this.Request.Scheme}://{this.Request.Host}{this.Request.PathBase}";

        foreach (var parentId in ids)
        {
            try
            {
                var parentItem = this.libraryManager.GetItemById(parentId);
                if (parentItem == null)
                {
                    result[parentId.ToString("N")] = new LibraryThumbnailResult();
                    continue;
                }

                var collectionFolder = parentItem as ICollectionFolder;
                var collectionType = collectionFolder?.CollectionType;

                BaseItemKind[] includeItemTypes = collectionType switch
                {
                    CollectionType.music => [BaseItemKind.MusicAlbum, BaseItemKind.Audio],
                    CollectionType.movies => [BaseItemKind.Movie],
                    CollectionType.tvshows => [BaseItemKind.Series],
                    CollectionType.boxsets => [BaseItemKind.BoxSet],
                    CollectionType.playlists => [BaseItemKind.Playlist],
                    _ => Array.Empty<BaseItemKind>(),
                };

                var query = new InternalItemsQuery(user)
                {
                    IncludeItemTypes = includeItemTypes,
                    IsVirtualItem = false,
                    OrderBy = [(ItemSortBy.Random, SortOrder.Ascending)],
                    Limit = 5,
                    Recursive = true,
                    DtoOptions = dtoOptions,
                };

                QueryResult<BaseItem> itemsResult;
                if (parentItem is Folder folder)
                {
                    itemsResult = folder.GetItems(query);
                }
                else
                {
                    itemsResult = this.libraryManager.GetItemsResult(query);
                }

                var rawItems = itemsResult.Items;

                // Build candidate list (children for boxsets/playlists, raw items otherwise).
                // NOTE: The Playlists virtual library folder may report CollectionType=null,
                // so we also check if the returned raw items are themselves containers
                // (Playlist / BoxSet) that need drilling into.
                IEnumerable<BaseItem> candidates;
                var needsChildTraversal = collectionType is CollectionType.boxsets or CollectionType.playlists
                    || rawItems.Any(i => i is Playlist or BoxSet);
                if (needsChildTraversal)
                {
                    candidates = [];
                    foreach (var container in rawItems)
                    {
                        if (container is not Folder childFolder)
                        {
                            continue;
                        }

                        // Try LinkedChildren first (works for BoxSets)
                        var linkedValid = childFolder.LinkedChildren
                            .Select(lc => lc.ItemId.HasValue
                                ? this.libraryManager.GetItemById(lc.ItemId.Value)
                                : (!string.IsNullOrEmpty(lc.LibraryItemId) && Guid.TryParse(lc.LibraryItemId, out var libGuid)
                                    ? this.libraryManager.GetItemById(libGuid)
                                    : null))
                            .Where(child => child != null)
                            .Cast<BaseItem>()
                            .ToList();

                        IEnumerable<BaseItem> children;
                        if (linkedValid.Count > 0)
                        {
                            children = linkedValid;
                        }
                        else
                        {
                            IEnumerable<BaseItem> playlistChildren;
                            try
                            {
                                if (childFolder is Playlist playlist)
                                {
                                    var manageableItems = playlist.GetManageableItems().ToArray();
                                    playlistChildren = manageableItems.Select(t => t.Item2).Where(item => item != null).ToList();
                                }
                                else
                                {
                                    playlistChildren = [];
                                }
                            }
                            catch (Exception ex)
                            {
                                this.logger.LogWarning(ex, "Failed to get playlist items via playlistManager for folder {FolderId}", childFolder.Id);
                                playlistChildren = [];
                            }

                            if (playlistChildren.Any())
                            {
                                children = playlistChildren;
                            }
                            else
                            {
                                var childResult = childFolder.GetItems(new InternalItemsQuery(user)
                                {
                                    Recursive = true,
                                    Limit = 10,
                                    DtoOptions = dtoOptions,
                                });
                                children = childResult.Items.Where(c => c.Id != childFolder.Id);
                            }
                        }

                        candidates = candidates.Concat(children);
                    }
                }
                else
                {
                    candidates = rawItems;
                }

                // Shuffle candidates so a different item can win on each call
                var shuffled = candidates.OrderBy(_ => Random.Shared.Next()).ToList();

                // Find single best item + matching image type
                var (bestItem, matchedType) = FindBestItem(shuffled, collectionType?.ToString());

                BaseItemDto? itemDto = null;
                string? resolvedUrl = null;
                if (bestItem != null && matchedType.HasValue)
                {
                    itemDto = this.dtoService.GetBaseItemDto(bestItem, dtoOptions, user);
                    itemDto.MediaSources = null;
                    itemDto.UserData = null;
                    itemDto.PremiereDate = null;
                    itemDto.EndDate = null;
                    itemDto.OfficialRating = null;
                    itemDto.CommunityRating = null;
                    itemDto.ChannelId = null;
                    itemDto.Status = null;
                    itemDto.AirDays = null;
                    itemDto.ChildCount = null;
                    itemDto.Overview = null;
                    itemDto.Genres = null;
                    itemDto.Taglines = null;
                    itemDto.ExternalUrls = null;
                    itemDto.People = null;
                    itemDto.Studios = null;

                    var tag = GetImageTag(itemDto, matchedType.Value);
                    if (tag != null)
                    {
                        resolvedUrl = $"{serverUrl}/Items/{bestItem.Id:N}/Images/{matchedType.Value}?tag={tag}&maxWidth=512&quality=80";
                    }
                }

                result[parentId.ToString("N")] = new LibraryThumbnailResult
                {
                    Item = itemDto,
                    ResolvedUrl = resolvedUrl,
                };
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Error processing library thumbnail candidates for parentId {ParentId}", parentId);
                result[parentId.ToString("N")] = new LibraryThumbnailResult();
            }
        }

        return this.Ok(result);
    }

    private static (BaseItem? Item, ImageType? MatchedType) FindBestItem(IEnumerable<BaseItem> items, string? collectionType)
    {
        ImageType[] priorityOrder = collectionType switch
        {
            "music"
                => [ImageType.Primary, ImageType.Thumb, ImageType.Backdrop],
            "photos" or "homevideos" or "musicvideos" or "livetv"
                => [ImageType.Primary, ImageType.Thumb],
            _ => [ImageType.Backdrop, ImageType.Thumb, ImageType.Primary],
        };

        foreach (var imageType in priorityOrder)
        {
            foreach (var item in items)
            {
                if (item.HasImage(imageType))
                {
                    return (item, imageType);
                }
            }
        }

        return (null, null);
    }

    private static string? GetImageTag(BaseItemDto item, ImageType imageType)
    {
        return imageType switch
        {
            ImageType.Backdrop => item.BackdropImageTags?.Length > 0 ? item.BackdropImageTags[0] : null,
            _ => item.ImageTags?.TryGetValue(imageType, out var tag) == true ? tag : null,
        };
    }
}
