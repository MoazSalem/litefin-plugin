// <copyright file="HeroController.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

#pragma warning disable CA1848 // Use LoggerMessage delegates for performance
#pragma warning disable CA1031 // Do not catch general exception types

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
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

/// <summary>
/// REST API controller that exposes optimized endpoints for the Home Hero Carousel.
/// </summary>
[ApiController]
[Authorize]
[Route("Litefin/[controller]")]
[Produces(MediaTypeNames.Application.Json)]
public class HeroController : ControllerBase
{
    private readonly IUserManager userManager;
    private readonly ILibraryManager libraryManager;
    private readonly IUserDataManager userDataManager;
    private readonly IDtoService dtoService;
    private readonly ILogger<HeroController> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeroController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public HeroController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IDtoService dtoService,
        ILoggerFactory loggerFactory)
    {
        this.userManager = userManager;
        this.libraryManager = libraryManager;
        this.userDataManager = userDataManager;
        this.dtoService = dtoService;
        this.logger = loggerFactory.CreateLogger<HeroController>();
    }

    /// <summary>
    /// Retrieves a curated list of featured Hero Carousel items (Movies &amp; Series with Backdrops) in a single request.
    /// </summary>
    /// <param name="userId">Optional. User ID filter.</param>
    /// <param name="limit">Optional. Maximum number of hero items to return (default: 5).</param>
    /// <param name="ignoreWatched">Optional. If true, filters out played movies and completed series.</param>
    /// <response code="200">List of BaseItemDto items optimized for the Home Hero Carousel.</response>
    /// <response code="401">User context missing.</response>
    /// <returns>A QueryResult list of BaseItemDto hero items.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<QueryResult<BaseItemDto>> GetHeroItems(
        [FromQuery] Guid? userId,
        [FromQuery] int? limit,
        [FromQuery] bool? ignoreWatched)
    {
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

        var itemLimit = limit ?? 5;
        var filterWatched = ignoreWatched.GetValueOrDefault(false);

        this.logger.LogInformation("Processing GetHeroItems request for user: {UserId}, limit: {Limit}, ignoreWatched: {IgnoreWatched}", user.Id, itemLimit, filterWatched);

        // Configure DTO options for Hero items
        var dtoOptions = new DtoOptions(allFields: false)
        {
            Fields =
            [
                ItemFields.Overview,
                ItemFields.PrimaryImageAspectRatio,
                ItemFields.ProviderIds
            ],
            EnableImages = true,
            EnableUserData = true,
            ImageTypeLimit = 1,
        };

        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsVirtualItem = false,
            OrderBy = [(ItemSortBy.Random, SortOrder.Ascending)],
            Limit = itemLimit * 4, // Fetch a candidate pool to filter backdrops and watched status
            Recursive = true,
            DtoOptions = dtoOptions,
        };

        if (filterWatched)
        {
            query.IsPlayed = false;
        }

        var candidatesResult = this.libraryManager.GetItemsResult(query);
        var candidateItems = candidatesResult.Items
            .Where(item => item.HasImage(ImageType.Backdrop))
            .ToList();

        if (filterWatched)
        {
            candidateItems = candidateItems
                .Where(item => !item.IsPlayed(user, null))
                .ToList();
        }

        // Shuffle candidates and pick requested limit
        var selectedItems = candidateItems
            .OrderBy(_ => Guid.NewGuid())
            .Take(itemLimit)
            .ToList();

        if (selectedItems.Count == 0)
        {
            return this.Ok(new QueryResult<BaseItemDto>(Array.Empty<BaseItemDto>()));
        }

        var dtos = this.dtoService.GetBaseItemDtos(selectedItems, dtoOptions, user);

        for (int i = 0; i < dtos.Count; i++)
        {
            var dto = dtos[i];

            // Strip unneeded heavy fields
            dto.PremiereDate = null;
            dto.EndDate = null;
            dto.Status = null;
            dto.AirDays = null;
            dto.ChildCount = null;
            dto.Genres = null;
            dto.Taglines = null;
            dto.ExternalUrls = null;
            dto.People = null;
            dto.Studios = null;
        }

        return this.Ok(new QueryResult<BaseItemDto>(dtos));
    }
}
