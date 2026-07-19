// <copyright file="CollectionsController.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

#pragma warning disable CA1848 // Use LoggerMessage delegates for performance

namespace Litefin.Plugin.Controllers;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

/// <summary>
/// REST API controller that exposes endpoints to query item collections.
/// </summary>
[ApiController]
[Authorize]
[Route("Litefin")]
[Produces(MediaTypeNames.Application.Json)]
public class CollectionsController : ControllerBase
{
    private readonly IUserManager userManager;
    private readonly ILibraryManager libraryManager;
    private readonly IDtoService dtoService;
    private readonly ILogger<CollectionsController> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionsController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public CollectionsController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IDtoService dtoService,
        ILoggerFactory loggerFactory)
    {
        this.userManager = userManager;
        this.libraryManager = libraryManager;
        this.dtoService = dtoService;
        this.logger = loggerFactory.CreateLogger<CollectionsController>();
    }

    /// <summary>
    /// Gets the collections that include the specified item.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="startIndex">Optional. The index of the first record in the output.</param>
    /// <param name="limit">Optional. The maximum number of records to return.</param>
    /// <param name="fields">Optional. Specify additional fields of information to return in the output.</param>
    /// <response code="200">Collections returned.</response>
    /// <response code="401">User context missing.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>The collections that contain the requested item.</returns>
    [HttpGet("Items/{itemId}/Collections")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<QueryResult<BaseItemDto>> GetItemCollections(
        [FromRoute, Required] Guid itemId,
        [FromQuery] Guid? userId,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit,
        [FromQuery] ItemFields[]? fields)
    {
        this.logger.LogInformation("Querying collections containing item ID: {ItemId}", itemId);

        // Resolve user security claim ID from identity if the query parameter is not explicitly defined
        var targetUserId = userId;
        if (!targetUserId.HasValue)
        {
            var claimsUserId = this.User.Claims.FirstOrDefault(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
            if (Guid.TryParse(claimsUserId, out var parsedGuid))
            {
                targetUserId = parsedGuid;
            }
        }

        // Return unauthorized if user could not be resolved from claims
        if (!targetUserId.HasValue)
        {
            return this.Unauthorized("User context missing.");
        }

        var user = this.userManager.GetUserById(targetUserId.Value);
        if (user == null)
        {
            return this.Unauthorized("User not found.");
        }

        // Fetch matching library item using the resolved Guid
        var item = this.libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item == null)
        {
            return this.NotFound("Item not found.");
        }

        var dtoOptions = new DtoOptions { Fields = fields ?? Array.Empty<ItemFields>() };

        // Fetch all BoxSet collections the user can view, and filter by those containing the item ID
        var visibleCollections = this.libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.BoxSet],
            Recursive = true,
        })
        .OfType<BoxSet>()
        .Where(boxSet => boxSet.LinkedChildren.Any(child => child.ItemId == item.Id))
        .OrderBy(i => i.SortName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

        // Paginate collections
        IEnumerable<BaseItem> pagedCollections = visibleCollections;
        if (startIndex.HasValue)
        {
            pagedCollections = pagedCollections.Skip(startIndex.Value);
        }

        if (limit.HasValue)
        {
            pagedCollections = pagedCollections.Take(limit.Value);
        }

        // Map server entities to DTO format
        var dtos = this.dtoService.GetBaseItemDtos(pagedCollections.ToList(), dtoOptions, user);

        return this.Ok(new QueryResult<BaseItemDto>(
            startIndex,
            visibleCollections.Count,
            dtos));
    }
}
