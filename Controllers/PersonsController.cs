// <copyright file="PersonsController.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

#pragma warning disable CA1848 // Use LoggerMessage delegates for performance

namespace Litefin.Plugin.Controllers;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
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
/// REST API controller that exposes endpoints to query person filmography items with role names attached.
/// </summary>
[ApiController]
[Authorize]
[Route("Litefin/[controller]")]
[Produces(MediaTypeNames.Application.Json)]
public class PersonsController : ControllerBase
{
    private readonly IUserManager userManager;
    private readonly ILibraryManager libraryManager;
    private readonly IDtoService dtoService;
    private readonly ILogger<PersonsController> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersonsController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public PersonsController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IDtoService dtoService,
        ILoggerFactory loggerFactory)
    {
        this.userManager = userManager;
        this.libraryManager = libraryManager;
        this.dtoService = dtoService;
        this.logger = loggerFactory.CreateLogger<PersonsController>();
    }

    /// <summary>
    /// Gets items for a person with their character role attached directly to each item DTO.
    /// </summary>
    /// <param name="personId">The person id.</param>
    /// <param name="userId">Optional. Filter by user id.</param>
    /// <param name="limit">Optional. Maximum number of records to return.</param>
    /// <response code="200">Person items with character roles returned.</response>
    /// <response code="401">User context missing.</response>
    /// <response code="404">Person or user not found.</response>
    /// <returns>A QueryResult of BaseItemDto with character role populated.</returns>
    [HttpGet("{personId}/Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<QueryResult<BaseItemDto>> GetPersonItems(
        [FromRoute, Required] Guid personId,
        [FromQuery] Guid? userId,
        [FromQuery] int? limit)
    {
        this.logger.LogInformation("Processing GetPersonItems for person ID: {PersonId}", personId);

        // Resolve user security claim ID from identity if query parameter is omitted
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

        // Resolve target person entity or name for matching role entries
        var targetPerson = this.libraryManager.GetItemById(personId);
        var targetPersonName = targetPerson?.Name;

        // Query Movies (limit to 12 - UI displays 10 cards + 2 to trigger See All button)
        var movies = this.libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            PersonIds = [personId],
            IncludeItemTypes = [BaseItemKind.Movie],
            OrderBy = [(ItemSortBy.PremiereDate, SortOrder.Descending)],
            Limit = 12,
            Recursive = true,
        });

        // Query Series (limit to 12 - UI displays 10 cards + 2 to trigger See All button)
        var series = this.libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            PersonIds = [personId],
            IncludeItemTypes = [BaseItemKind.Series],
            OrderBy = [(ItemSortBy.PremiereDate, SortOrder.Descending)],
            Limit = 12,
            Recursive = true,
        });

        // Query Episodes (limit to 10 - UI displays 9 cards + 1 to trigger See All button)
        var episodes = this.libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            PersonIds = [personId],
            IncludeItemTypes = [BaseItemKind.Episode],
            OrderBy = [(ItemSortBy.PremiereDate, SortOrder.Descending)],
            Limit = 10,
            Recursive = true,
        });

        // Query Music Albums (limit to 12 - UI displays 10 cards + 2 to trigger See All button)
        var albums = this.libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            ArtistIds = [personId],
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            OrderBy = [(ItemSortBy.ProductionYear, SortOrder.Descending), (ItemSortBy.SortName, SortOrder.Ascending)],
            Limit = 12,
            Recursive = true,
        });

        if (albums.Count == 0)
        {
            albums = this.libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                PersonIds = [personId],
                IncludeItemTypes = [BaseItemKind.MusicAlbum],
                OrderBy = [(ItemSortBy.ProductionYear, SortOrder.Descending), (ItemSortBy.SortName, SortOrder.Ascending)],
                Limit = 12,
                Recursive = true,
            });
        }

        // Query Audio Songs (limit to 12 - UI displays 10 cards + 2 to trigger See All button)
        var songs = this.libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            ArtistIds = [personId],
            IncludeItemTypes = [BaseItemKind.Audio],
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
            Limit = 12,
            Recursive = true,
        });

        if (songs.Count == 0)
        {
            songs = this.libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                PersonIds = [personId],
                IncludeItemTypes = [BaseItemKind.Audio],
                OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
                Limit = 12,
                Recursive = true,
            });
        }

        // Combine into a single categorized list for DTO serialization
        var items = new List<BaseItem>(movies.Count + series.Count + episodes.Count + albums.Count + songs.Count);
        items.AddRange(movies);
        items.AddRange(series);
        items.AddRange(episodes);
        items.AddRange(albums);
        items.AddRange(songs);

        // Configure lightweight DTO serialization options
        var dtoOptions = new DtoOptions(allFields: false)
        {
            Fields = [
                ItemFields.PrimaryImageAspectRatio,
                ItemFields.SeriesStudio,
                ItemFields.MediaSourceCount,
                ItemFields.MediaSources
            ],
            EnableImages = true,
            EnableUserData = true,
            ImageTypeLimit = 1,
        };

        var dtos = this.dtoService.GetBaseItemDtos(items, dtoOptions, user);

        // For each item, attach ONLY the specific person's character role
        for (int i = 0; i < items.Count && i < dtos.Count; i++)
        {
            var item = items[i];
            var itemDto = dtos[i];

            var people = this.libraryManager.GetPeople(item);
            PersonInfo? person = null;
            if (targetPersonName != null)
            {
                person = people.FirstOrDefault(p => string.Equals(p.Name, targetPersonName, StringComparison.OrdinalIgnoreCase));
            }
            else if (people.Count > 0)
            {
                person = people[0];
            }

            if (person != null)
            {
                itemDto.People = [
                    new BaseItemPerson
                    {
                        Id = personId,
                        Name = person.Name,
                        Role = person.Role ?? string.Empty,
                        Type = person.Type,
                    }
                ];
            }
        }

        return this.Ok(new QueryResult<BaseItemDto>(
            0,
            dtos.Count,
            dtos));
    }
}
