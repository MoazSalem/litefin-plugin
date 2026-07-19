// <copyright file="MergedRowsController.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

#pragma warning disable CA1848 // Use LoggerMessage delegates for performance

namespace Litefin.Plugin.Controllers;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

/// <summary>
/// REST API controller that exposes endpoints to retrieve merged Continue Watching and Next Up items.
/// </summary>
[ApiController]
[Authorize]
[Route("Litefin/[controller]")]
[Produces(MediaTypeNames.Application.Json)]
public class MergedRowsController : ControllerBase
{
    // Define the core managers used for querying series information and items database
    private readonly IUserManager userManager;
    private readonly ILibraryManager libraryManager;
    private readonly ITVSeriesManager tvSeriesManager;
    private readonly IDtoService dtoService;
    private readonly IUserDataManager userDataManager;
    private readonly ILogger<MergedRowsController> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MergedRowsController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="tvSeriesManager">Instance of the <see cref="ITVSeriesManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public MergedRowsController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        ITVSeriesManager tvSeriesManager,
        IDtoService dtoService,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory)
    {
        // Bind the injected service interfaces to local fields
        this.userManager = userManager;
        this.libraryManager = libraryManager;
        this.tvSeriesManager = tvSeriesManager;
        this.dtoService = dtoService;
        this.userDataManager = userDataManager;
        this.logger = loggerFactory.CreateLogger<MergedRowsController>();
    }

    /// <summary>
    /// Gets a single merged and deduplicated list containing both Continue Watching and Next Up rows.
    /// </summary>
    /// <param name="userId">Optional. The user id to get the merged list for.</param>
    /// <param name="limit">Optional. The maximum number of records to return for each query.</param>
    /// <response code="200">Returns the merged list of base item DTOs.</response>
    /// <response code="404">If the user is not found on the server.</response>
    /// <returns>A merged query result list of BaseItemDto.</returns>
    [HttpGet("ContinueAndNextUp")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<QueryResult<BaseItemDto>> GetContinueAndNextUp(
        [FromQuery] Guid? userId,
        [FromQuery] int? limit)
    {
        // Log query details for debugging performance
        this.logger.LogInformation("Processing GetContinueAndNextUp API request for user: {UserId}", userId);

        // Retrieve user security claim ID from identity if the query parameter is not explicitly defined
        var targetUserId = userId;
        if (!targetUserId.HasValue)
        {
            var claimsUserId = this.User.Claims.FirstOrDefault(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
            if (Guid.TryParse(claimsUserId, out var parsedGuid))
            {
                targetUserId = parsedGuid;
            }
        }

        // If still no user id is resolvable, throw bad request
        if (!targetUserId.HasValue)
        {
            return this.BadRequest("User ID must be provided in request or claims.");
        }

        // Fetch matching user entity using the resolved Guid
        var user = this.userManager.GetUserById(targetUserId.Value);
        if (user == null)
        {
            this.logger.LogWarning("User entity lookup failed for UserID: {UserId}", targetUserId.Value);
            return this.NotFound("User not found.");
        }

        // Set default row limit size if none is requested by the client
        var rowLimit = limit ?? 12;

        // Configure default DTO serialization options to include metadata and layout elements
        var dtoOptions = new DtoOptions
        {
            EnableImages = true,
            EnableUserData = true,
            ImageTypeLimit = 1,
        };

        // Fetch resume items (Continue Watching list) sorted by play date order
        this.logger.LogDebug("Querying resume items from LibraryManager.");
        var resumeItemsResult = this.libraryManager.GetItemsResult(new InternalItemsQuery(user)
        {
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            IsResumable = true,
            StartIndex = 0,
            Limit = rowLimit,
            Recursive = true,
            DtoOptions = dtoOptions,
            IsVirtualItem = false,
            CollapseBoxSetItems = false,
        });

        // Query the next up series episodes (Next Up list) using series manager
        this.logger.LogDebug("Querying NextUp items from TVSeriesManager.");
        var nextUpResult = this.tvSeriesManager.GetNextUp(
            new NextUpQuery
            {
                User = user,
                Limit = rowLimit,
                EnableResumable = true,
                EnableRewatching = false,
            },
            dtoOptions);

        // Combine both lists, deduplicating elements by their unique base item ID and sorting chronologically
        var itemsWithActivity = new List<(BaseItem Item, DateTime ActivityDate)>();

        // Process Resume Items (Continue Watching)
        if (resumeItemsResult.Items != null)
        {
            foreach (var item in resumeItemsResult.Items)
            {
                if (item == null)
                {
                    continue;
                }

                var userData = this.userDataManager.GetUserData(user, item);
                var activityDate = userData?.LastPlayedDate ?? item.DateCreated;
                itemsWithActivity.Add((item, activityDate));
            }
        }

        // Process Next Up Items and interweave them based on parent series activity
        if (nextUpResult.Items != null)
        {
            foreach (var item in nextUpResult.Items)
            {
                if (item == null)
                {
                    continue;
                }

                // Avoid duplicate items if they are already present
                if (itemsWithActivity.Any(x => x.Item.Id == item.Id))
                {
                    continue;
                }

                DateTime? activityDate = null;

                // For next-up episodes, determine when the parent series was last active by querying its last played episode
                if (item is MediaBrowser.Controller.Entities.TV.Episode episode && episode.SeriesId != Guid.Empty)
                {
                    var lastPlayedEpisodes = this.libraryManager.GetItemList(new InternalItemsQuery(user)
                    {
                        AncestorIds = [episode.SeriesId],
                        IncludeItemTypes = [BaseItemKind.Episode],
                        OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
                        Limit = 1,
                        Recursive = true,
                    });

                    if (lastPlayedEpisodes.Count > 0)
                    {
                        var lastPlayedEpisode = lastPlayedEpisodes[0];
                        var epUserData = this.userDataManager.GetUserData(user, lastPlayedEpisode);
                        activityDate = epUserData?.LastPlayedDate;
                    }
                }

                // Fallback to the episode's own last played date or creation date
                if (!activityDate.HasValue)
                {
                    var userData = this.userDataManager.GetUserData(user, item);
                    activityDate = userData?.LastPlayedDate ?? item.DateCreated;
                }

                itemsWithActivity.Add((item, activityDate.Value));
            }
        }

        // Sort descending by activity date so the most recently active item appears first (Plex-style)
        var finalSlice = itemsWithActivity
            .OrderByDescending(x => x.ActivityDate)
            .Select(x => x.Item)
            .Take(rowLimit * 2)
            .ToList();

        // Convert raw server entities into presentation DTO format
        var dtos = this.dtoService.GetBaseItemDtos(finalSlice, dtoOptions, user);

        // Package the results in a query wrapper response
        return this.Ok(new QueryResult<BaseItemDto>(
            0,
            dtos.Count,
            dtos));
    }
}
