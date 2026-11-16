// <copyright file="AnimeMarkersController.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

#pragma warning disable CA1848 // Use LoggerMessage delegates for performance
#pragma warning disable CA1031 // Do not catch general exception types

namespace Litefin.Plugin.Controllers;

using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Litefin.Plugin.Models;
using Litefin.Plugin.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

/// <summary>
/// REST API controller that exposes anime episode classification markers (Filler, Mixed, Manga/Anime Canon)
/// and audio stream verdicts (Subbed, Dubbed) for television series.
/// </summary>
[ApiController]
[Authorize]
[Route("Litefin/[controller]")]
[Produces(MediaTypeNames.Application.Json)]
public class AnimeMarkersController : ControllerBase
{
    private readonly ILibraryManager libraryManager;
    private readonly IMediaSourceManager mediaSourceManager;
    private readonly AnimeFillerListService fillerListService;
    private readonly ILogger<AnimeMarkersController> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeMarkersController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public AnimeMarkersController(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IHttpClientFactory httpClientFactory,
        IApplicationPaths applicationPaths,
        ILoggerFactory loggerFactory)
    {
        this.libraryManager = libraryManager;
        this.mediaSourceManager = mediaSourceManager;
        this.logger = loggerFactory.CreateLogger<AnimeMarkersController>();

        // Instantiate the marker resolution and cache service
        var serviceLogger = loggerFactory.CreateLogger<AnimeFillerListService>();
        this.fillerListService = new AnimeFillerListService(httpClientFactory, applicationPaths, serviceLogger);
    }

    /// <summary>
    /// Queries episode and season markers for an anime series.
    /// Returns filler/canon classifications and audio stream verdicts.
    /// </summary>
    /// <param name="seriesId">The unique identifier of the television series.</param>
    /// <param name="cancellationToken">Cancellation token for async operations.</param>
    /// <response code="200">Returns the full marker dataset for the requested series.</response>
    /// <response code="400">If the series identifier is missing or malformed.</response>
    /// <response code="404">If the requested series cannot be found in the library.</response>
    /// <returns>The classification response payload.</returns>
    [HttpGet("Series")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AnimeMarkersResponse>> GetSeriesMarkers(
        [FromQuery, Required] string seriesId,
        CancellationToken cancellationToken)
    {
        // Validate and parse the incoming series GUID
        if (!Guid.TryParse(seriesId, out var seriesGuid) || seriesGuid == Guid.Empty)
        {
            return this.BadRequest("A valid series identifier GUID is required.");
        }

        // Fetch the target series item from the Jellyfin library manager
        var targetItem = this.libraryManager.GetItemById(seriesGuid);
        if (targetItem is not Series series)
        {
            this.logger.LogWarning("Requested item {Id} is not a television series", seriesId);
            return this.NotFound($"Series with id {seriesId} was not found.");
        }

        // Check if the series represents an anime candidate
        var isAnime = AnimeTitleMatcher.IsAnimeCandidate(series);

        // If the series has no anime indicators and is not in our known catalog, return early
        if (!isAnime)
        {
            return this.Ok(new AnimeMarkersResponse
            {
                Enabled = true,
                Matched = false,
                Slug = null,
            });
        }

        var resolvedSlug = AnimeTitleMatcher.ResolveSlug(series.Name);

        // Retrieve scraped or cached marker definitions if a slug was resolved
        CachedShowData? showData = null;
        if (!string.IsNullOrEmpty(resolvedSlug))
        {
            showData = await this.fillerListService.GetShowDataAsync(resolvedSlug, cancellationToken).ConfigureAwait(false);
        }

        // Query all episodes belonging to this series across all seasons
        var episodeQuery = new InternalItemsQuery
        {
            AncestorIds = [series.Id],
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
        };

        var episodes = this.libraryManager.GetItemList(episodeQuery).OfType<Episode>().ToList();

        // ---------------------------------------------------------------------
        // Episode Media Stream & Canon Marker Association
        // ---------------------------------------------------------------------
        // Standard Jellyfin GetItemList queries deliberately omit media stream
        // relational records to keep database query latency minimal.
        // We supply an explicit stream resolution delegate through IMediaSourceManager
        // so audio tracks (Subbed, Dubbed, Subbed/Dubbed) are populated accurately
        // for all episodes without failing back to unclassified states.
        // ---------------------------------------------------------------------
        var (episodeVerdicts, seasonVerdicts) = AnimeEpisodeMapper.MapEpisodes(
            episodes,
            showData,
            ep =>
            {
                try
                {
                    // Query stream descriptors from the media source manager
                    return this.mediaSourceManager.GetMediaStreams(ep.Id);
                }
                catch (Exception ex)
                {
                    this.logger.LogTrace(ex, "Failed to resolve media streams for episode {Id}", ep.Id);
                    return null;
                }
            });

        // Assemble the client response payload
        var response = new AnimeMarkersResponse
        {
            Enabled = true,
            Matched = true,
            Slug = resolvedSlug,
        };

        foreach (var (epKey, verdict) in episodeVerdicts)
        {
            response.Episodes[epKey] = verdict;
        }

        foreach (var (seasonKey, verdict) in seasonVerdicts)
        {
            response.Seasons[seasonKey] = verdict;
        }

        return this.Ok(response);
    }
}
