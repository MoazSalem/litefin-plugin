// <copyright file="SeerrController.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

#pragma warning disable CA1848 // Use LoggerMessage delegates for performance

namespace Litefin.Plugin.Controllers;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Litefin.Plugin.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

/// <summary>
/// Provides a constrained authenticated proxy to the configured Seerr instance.
/// </summary>
[ApiController]
[Authorize]
[Route("Litefin/Seerr")]
[Produces(MediaTypeNames.Application.Json)]
public class SeerrController : ControllerBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<SeerrController> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrController"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public SeerrController(IHttpClientFactory httpClientFactory, ILogger<SeerrController> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;
    }

    /// <summary>
    /// Reports whether Seerr is configured and reachable.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The current integration status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetStatus(CancellationToken cancellationToken)
    {
        if (!TryGetConfiguration(out _, out _))
        {
            return this.Ok(new { configured = false, available = false });
        }

        try
        {
            using var response = await this.SendAsync(HttpMethod.Get, "/auth/me", null, cancellationToken).ConfigureAwait(false);
            return this.Ok(new { configured = true, available = response.IsSuccessStatusCode });
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogWarning(ex, "Unable to reach the configured Seerr instance");
            return this.Ok(new { configured = true, available = false });
        }
        catch (TaskCanceledException ex)
        {
            this.logger.LogWarning(ex, "Timed out while checking the configured Seerr instance");
            return this.Ok(new { configured = true, available = false });
        }
    }

    /// <summary>
    /// Tests temporary Seerr settings without persisting them.
    /// </summary>
    /// <param name="request">The temporary Seerr settings.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>Whether Seerr accepted the supplied settings.</returns>
    [HttpPost("Status/Test")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<object>> TestConnection(
        [FromBody] SeerrConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryNormalizeConfiguration(request.SeerrUrl, request.SeerrApiKey, out var baseUrl, out var apiKey))
        {
            return this.BadRequest(new { available = false, message = "A valid Seerr URL and API key are required." });
        }

        try
        {
            using var response = await this.SendAsync(
                HttpMethod.Get,
                "/auth/me",
                null,
                baseUrl,
                apiKey,
                cancellationToken).ConfigureAwait(false);
            return this.Ok(new { available = response.IsSuccessStatusCode });
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogWarning(ex, "Unable to reach Seerr while testing temporary administrator settings");
            return this.Ok(new { available = false });
        }
        catch (TaskCanceledException ex)
        {
            this.logger.LogWarning(ex, "Timed out while testing temporary administrator Seerr settings");
            return this.Ok(new { available = false });
        }
    }

    /// <summary>
    /// Gets trending media from Seerr.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Trending")]
    public Task<IActionResult> GetTrending(CancellationToken cancellationToken)
        => this.ProxyGetAsync("/discover/trending", cancellationToken);

    /// <summary>
    /// Gets popular movies from Seerr, optionally filtered by genre, keywords, or studio, merging 5 upstream pages.
    /// </summary>
    /// <param name="page">The page number.</param>
    /// <param name="genre">The optional genre identifier.</param>
    /// <param name="keywords">The optional keywords identifier.</param>
    /// <param name="studio">The optional studio identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Movies")]
    public Task<IActionResult> GetMovies(
        [FromQuery] int page = 1,
        [FromQuery] int? genre = null,
        [FromQuery] int? keywords = null,
        [FromQuery] int? studio = null,
        CancellationToken cancellationToken = default)
    {
        var queryParams = new List<string>();
        if (genre.HasValue)
        {
            queryParams.Add($"genre={genre.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (keywords.HasValue)
        {
            queryParams.Add($"keywords={keywords.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (studio.HasValue)
        {
            queryParams.Add($"studio={studio.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        var basePath = queryParams.Count > 0
            ? $"/discover/movies?{string.Join("&", queryParams)}"
            : "/discover/movies";

        return this.ProxyGetMerged5PagesAsync(basePath, page, cancellationToken);
    }

    /// <summary>
    /// Gets movies by genre from Seerr, merging 5 upstream pages.
    /// </summary>
    /// <param name="genreId">The genre identifier.</param>
    /// <param name="page">The page number.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Movies/Genre/{genreId:int}")]
    public Task<IActionResult> GetMoviesByGenre(
        [FromRoute] int genreId,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
        => this.ProxyGetMerged5PagesAsync($"/discover/movies?genre={genreId.ToString(CultureInfo.InvariantCulture)}", page, cancellationToken);

    /// <summary>
    /// Gets movies by keyword from Seerr, merging 5 upstream pages.
    /// </summary>
    /// <param name="keywordId">The keyword identifier.</param>
    /// <param name="page">The page number.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Movies/Keyword/{keywordId:int}")]
    public Task<IActionResult> GetMoviesByKeyword(
        [FromRoute] int keywordId,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
        => this.ProxyGetMerged5PagesAsync($"/discover/movies?keywords={keywordId.ToString(CultureInfo.InvariantCulture)}", page, cancellationToken);

    /// <summary>
    /// Gets movies by studio from Seerr, merging 5 upstream pages.
    /// </summary>
    /// <param name="studioId">The studio identifier.</param>
    /// <param name="page">The page number.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Movies/Studio/{studioId:int}")]
    public Task<IActionResult> GetMoviesByStudio(
        [FromRoute] int studioId,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
        => this.ProxyGetMerged5PagesAsync($"/discover/movies/studio/{studioId.ToString(CultureInfo.InvariantCulture)}", page, cancellationToken);

    /// <summary>
    /// Gets popular television series from Seerr, optionally filtered by genre, keywords, or network, merging 5 upstream pages.
    /// </summary>
    /// <param name="page">The page number.</param>
    /// <param name="genre">The optional genre identifier.</param>
    /// <param name="keywords">The optional keywords identifier.</param>
    /// <param name="network">The optional network identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Tv")]
    public Task<IActionResult> GetTv(
        [FromQuery] int page = 1,
        [FromQuery] int? genre = null,
        [FromQuery] int? keywords = null,
        [FromQuery] int? network = null,
        CancellationToken cancellationToken = default)
    {
        var queryParams = new List<string>();
        if (genre.HasValue)
        {
            queryParams.Add($"genre={genre.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (keywords.HasValue)
        {
            queryParams.Add($"keywords={keywords.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (network.HasValue)
        {
            queryParams.Add($"network={network.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        var basePath = queryParams.Count > 0
            ? $"/discover/tv?{string.Join("&", queryParams)}"
            : "/discover/tv";

        return this.ProxyGetMerged5PagesAsync(basePath, page, cancellationToken);
    }

    /// <summary>
    /// Gets television series by genre from Seerr, merging 5 upstream pages.
    /// </summary>
    /// <param name="genreId">The genre identifier.</param>
    /// <param name="page">The page number.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Tv/Genre/{genreId:int}")]
    public Task<IActionResult> GetTvByGenre(
        [FromRoute] int genreId,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
        => this.ProxyGetMerged5PagesAsync($"/discover/tv?genre={genreId.ToString(CultureInfo.InvariantCulture)}", page, cancellationToken);

    /// <summary>
    /// Gets television series by keyword from Seerr, merging 5 upstream pages.
    /// </summary>
    /// <param name="keywordId">The keyword identifier.</param>
    /// <param name="page">The page number.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Tv/Keyword/{keywordId:int}")]
    public Task<IActionResult> GetTvByKeyword(
        [FromRoute] int keywordId,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
        => this.ProxyGetMerged5PagesAsync($"/discover/tv?keywords={keywordId.ToString(CultureInfo.InvariantCulture)}", page, cancellationToken);

    /// <summary>
    /// Gets television series by network from Seerr, merging 5 upstream pages.
    /// </summary>
    /// <param name="networkId">The network identifier.</param>
    /// <param name="page">The page number.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Tv/Network/{networkId:int}")]
    public Task<IActionResult> GetTvByNetwork(
        [FromRoute] int networkId,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
        => this.ProxyGetMerged5PagesAsync($"/discover/tv/network/{networkId.ToString(CultureInfo.InvariantCulture)}", page, cancellationToken);

    /// <summary>
    /// Gets upcoming movies from Seerr, merging 5 upstream pages.
    /// </summary>
    /// <param name="page">The page number.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Movies/Upcoming")]
    public Task<IActionResult> GetUpcomingMovies([FromQuery] int page = 1, CancellationToken cancellationToken = default)
        => this.ProxyGetMerged5PagesAsync("/discover/movies/upcoming", page, cancellationToken);

    /// <summary>
    /// Gets upcoming television series from Seerr, merging 5 upstream pages.
    /// </summary>
    /// <param name="page">The page number.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Tv/Upcoming")]
    public Task<IActionResult> GetUpcomingTv([FromQuery] int page = 1, CancellationToken cancellationToken = default)
        => this.ProxyGetMerged5PagesAsync("/discover/tv/upcoming", page, cancellationToken);

    /// <summary>
    /// Gets movie genre slider items from Seerr.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/GenreSlider/Movie")]
    public Task<IActionResult> GetGenreSliderMovie(CancellationToken cancellationToken = default)
        => this.ProxyGetAsync("/discover/genreslider/movie", cancellationToken);

    /// <summary>
    /// Gets tv genre slider items from Seerr.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/GenreSlider/Tv")]
    public Task<IActionResult> GetGenreSliderTv(CancellationToken cancellationToken = default)
        => this.ProxyGetAsync("/discover/genreslider/tv", cancellationToken);

    /// <summary>
    /// Searches the Seerr catalogue.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="page">The page number.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Search")]
    public Task<IActionResult> Search(
        [FromQuery] string query,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
    {
        var path = $"/search?query={Uri.EscapeDataString(query ?? string.Empty)}&page={Math.Max(1, page).ToString(CultureInfo.InvariantCulture)}";
        return this.ProxyGetAsync(path, cancellationToken);
    }

    /// <summary>
    /// Gets details for a Seerr collection.
    /// </summary>
    /// <param name="collectionId">The collection identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Collection/{collectionId:int}")]
    public Task<IActionResult> GetCollectionDetails([FromRoute] int collectionId, CancellationToken cancellationToken)
        => this.ProxyGetAsync($"/collection/{collectionId.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    /// <summary>
    /// Gets details for a person from Seerr.
    /// </summary>
    /// <param name="personId">The TMDB person identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Person/{personId:int}")]
    public Task<IActionResult> GetPersonDetails([FromRoute] int personId, CancellationToken cancellationToken)
        => this.ProxyGetAsync($"/person/{personId.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    /// <summary>
    /// Gets combined credits (cast and crew filmography) for a person from Seerr.
    /// </summary>
    /// <param name="personId">The TMDB person identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Person/{personId:int}/CombinedCredits")]
    [HttpGet("Person/{personId:int}/combined_credits")]
    public Task<IActionResult> GetPersonCombinedCredits([FromRoute] int personId, CancellationToken cancellationToken)
        => this.ProxyGetAsync($"/person/{personId.ToString(CultureInfo.InvariantCulture)}/combined_credits", cancellationToken);

    /// <summary>
    /// Gets details for a Seerr movie.
    /// </summary>
    /// <param name="tmdbId">The TMDB movie identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Movie/{tmdbId:int}")]
    public Task<IActionResult> GetMovieDetails([FromRoute] int tmdbId, CancellationToken cancellationToken)
        => this.ProxyGetAsync($"/movie/{tmdbId.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    /// <summary>
    /// Gets similar movies from Seerr.
    /// </summary>
    /// <param name="tmdbId">The TMDB movie identifier.</param>
    /// <param name="page">The page number.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Movie/{tmdbId:int}/Similar")]
    public Task<IActionResult> GetMovieSimilar(
        [FromRoute] int tmdbId,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
        => this.ProxyGetAsync($"/movie/{tmdbId.ToString(CultureInfo.InvariantCulture)}/similar?page={Math.Max(1, page).ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    /// <summary>
    /// Gets recommended movies from Seerr.
    /// </summary>
    /// <param name="tmdbId">The TMDB movie identifier.</param>
    /// <param name="page">The page number.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Movie/{tmdbId:int}/Recommendations")]
    public Task<IActionResult> GetMovieRecommendations(
        [FromRoute] int tmdbId,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
        => this.ProxyGetAsync($"/movie/{tmdbId.ToString(CultureInfo.InvariantCulture)}/recommendations?page={Math.Max(1, page).ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    /// <summary>
    /// Gets details and seasons for a Seerr television series.
    /// </summary>
    /// <param name="tmdbId">The TMDB series identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Tv/{tmdbId:int}")]
    public Task<IActionResult> GetTvDetails([FromRoute] int tmdbId, CancellationToken cancellationToken)
        => this.ProxyGetAsync($"/tv/{tmdbId.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    /// <summary>
    /// Gets similar television series from Seerr.
    /// </summary>
    /// <param name="tmdbId">The TMDB series identifier.</param>
    /// <param name="page">The page number.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Tv/{tmdbId:int}/Similar")]
    public Task<IActionResult> GetTvSimilar(
        [FromRoute] int tmdbId,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
        => this.ProxyGetAsync($"/tv/{tmdbId.ToString(CultureInfo.InvariantCulture)}/similar?page={Math.Max(1, page).ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    /// <summary>
    /// Gets recommended television series from Seerr.
    /// </summary>
    /// <param name="tmdbId">The TMDB series identifier.</param>
    /// <param name="page">The page number.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Tv/{tmdbId:int}/Recommendations")]
    public Task<IActionResult> GetTvRecommendations(
        [FromRoute] int tmdbId,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
        => this.ProxyGetAsync($"/tv/{tmdbId.ToString(CultureInfo.InvariantCulture)}/recommendations?page={Math.Max(1, page).ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    /// <summary>
    /// Gets Seerr requests list for display on TV discovery screens.
    /// </summary>
    /// <param name="take">Number of requests to return.</param>
    /// <param name="skip">Number of requests to skip.</param>
    /// <param name="filter">Request status filter.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr requests list payload.</returns>
    [HttpGet("Requests")]
    [HttpGet("request")]
    public Task<IActionResult> GetRequests(
        [FromQuery] int take = 20,
        [FromQuery] int skip = 0,
        [FromQuery] string filter = "all",
        CancellationToken cancellationToken = default)
        => this.ProxyGetAsync($"/request?take={Math.Max(1, take).ToString(CultureInfo.InvariantCulture)}&skip={Math.Max(0, skip).ToString(CultureInfo.InvariantCulture)}&filter={Uri.EscapeDataString(filter)}", cancellationToken);

    /// <summary>
    /// Gets recently added media items from Seerr.
    /// </summary>
    /// <param name="take">Number of items to return.</param>
    /// <param name="skip">Number of items to skip.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The recently added media items payload.</returns>
    [HttpGet("Media")]
    [HttpGet("RecentlyAdded")]
    public Task<IActionResult> GetRecentlyAdded(
        [FromQuery] int take = 20,
        [FromQuery] int skip = 0,
        CancellationToken cancellationToken = default)
        => this.ProxyGetAsync($"/media?filter=allavailable&sort=mediaAdded&take={Math.Max(1, take).ToString(CultureInfo.InvariantCulture)}&skip={Math.Max(0, skip).ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    /// <summary>
    /// Creates a Seerr request for the authenticated Jellyfin user.
    /// </summary>
    /// <param name="request">The media request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpPost("Requests")]
    public async Task<IActionResult> CreateRequest([FromBody] SeerrRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var isTv = request.MediaType.Equals("tv", StringComparison.OrdinalIgnoreCase);
        if (!request.MediaType.Equals("movie", StringComparison.OrdinalIgnoreCase) && !isTv)
        {
            return this.BadRequest(new { message = "MediaType must be movie or tv." });
        }

        var seasons = request.Seasons?.Where(season => season > 0).Distinct().ToArray();
        if (isTv && (seasons == null || seasons.Length == 0))
        {
            return this.BadRequest(new { message = "At least one season is required for a television request." });
        }

        var jellyfinUserId = this.GetAuthenticatedUserId();
        if (jellyfinUserId == null)
        {
            return this.Unauthorized(new { message = "Authenticated Jellyfin user context is missing." });
        }

        int? seerrUserId;
        try
        {
            seerrUserId = await this.ResolveSeerrUserIdAsync(jellyfinUserId.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogWarning(ex, "Unable to resolve the authenticated user in Seerr");
            return this.StatusCode(StatusCodes.Status502BadGateway, new { message = "Unable to reach Seerr." });
        }
        catch (TaskCanceledException ex)
        {
            this.logger.LogWarning(ex, "Timed out while resolving the authenticated user in Seerr");
            return this.StatusCode(StatusCodes.Status504GatewayTimeout, new { message = "The Seerr request timed out." });
        }

        if (!seerrUserId.HasValue)
        {
            return this.StatusCode(StatusCodes.Status403Forbidden, new
            {
                message = "The authenticated Jellyfin user is not linked to a Seerr account.",
            });
        }

        var payload = new Dictionary<string, object>
        {
            ["mediaType"] = isTv ? "tv" : "movie",
            ["mediaId"] = request.MediaId,
            ["is4k"] = request.Is4K,
        };

        if (request.ServerId.HasValue)
        {
            payload["serverId"] = request.ServerId.Value;
        }

        var usesAdvancedOptions = request.ServerId.HasValue
            || request.ProfileId.HasValue
            || !string.IsNullOrWhiteSpace(request.RootFolder)
            || request.LanguageProfileId.HasValue;
        if (usesAdvancedOptions
            && !await this.HasSeerrPermissionAsync(seerrUserId.Value, 8192, cancellationToken).ConfigureAwait(false))
        {
            return this.StatusCode(StatusCodes.Status403Forbidden, new { message = "Advanced request permission is required." });
        }

        if (request.ProfileId.HasValue)
        {
            payload["profileId"] = request.ProfileId.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.RootFolder))
        {
            payload["rootFolder"] = request.RootFolder;
        }

        if (request.LanguageProfileId.HasValue)
        {
            payload["languageProfileId"] = request.LanguageProfileId.Value;
        }

        if (isTv)
        {
            payload["seasons"] = seasons!;
        }

        return await this.ProxyAsync(HttpMethod.Post, "/request", payload, cancellationToken, seerrUserId).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels a Seerr request for the authenticated Jellyfin user.
    /// </summary>
    /// <param name="requestId">The Seerr request identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>An empty successful response.</returns>
    [HttpDelete("Requests/{requestId:int}")]
    public async Task<IActionResult> CancelRequest([FromRoute] int requestId, CancellationToken cancellationToken)
    {
        var userId = await this.ResolveAuthenticatedSeerrUserIdAsync(cancellationToken).ConfigureAwait(false);
        if (!userId.HasValue)
        {
            return this.StatusCode(StatusCodes.Status403Forbidden);
        }

        return await this.ProxyAsync(HttpMethod.Delete, $"/request/{requestId.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken, userId).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets combined ratings for a media item.
    /// </summary>
    /// <param name="mediaType">The Seerr media type (movie or tv).</param>
    /// <param name="tmdbId">The TMDB identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The combined ratings payload.</returns>
    [HttpGet("Ratings/{mediaType}/{tmdbId:int}")]
    public async Task<IActionResult> GetRatingsCombined([FromRoute] string mediaType, [FromRoute] int tmdbId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        var isTv = mediaType.Equals("tv", StringComparison.OrdinalIgnoreCase);
        if (!mediaType.Equals("movie", StringComparison.OrdinalIgnoreCase) && !isTv)
        {
            return this.BadRequest(new { message = "MediaType must be movie or tv." });
        }

        var userId = await this.ResolveAuthenticatedSeerrUserIdAsync(cancellationToken).ConfigureAwait(false);
        if (!userId.HasValue)
        {
            return this.StatusCode(StatusCodes.Status403Forbidden);
        }

        var routeType = isTv ? "tv" : "movie";
        return await this.ProxyAsync(HttpMethod.Get, $"/{routeType}/{tmdbId.ToString(CultureInfo.InvariantCulture)}/ratingscombined", null, cancellationToken, userId).ConfigureAwait(false);
    }

    /// <summary>Gets request services for a media type.</summary>
    /// <param name="mediaType">The Seerr media type.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The configured services.</returns>
    [HttpGet("Services/{mediaType}")]
    public async Task<IActionResult> GetServices([FromRoute] string mediaType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        var service = GetServiceName(mediaType);
        if (service == null)
        {
            return this.BadRequest(new { message = "MediaType must be movie or tv." });
        }

        var userId = await this.ResolveAuthenticatedSeerrUserIdAsync(cancellationToken).ConfigureAwait(false);
        if (!userId.HasValue)
        {
            return this.StatusCode(StatusCodes.Status403Forbidden);
        }

        return await this.ProxyAsync(HttpMethod.Get, $"/service/{service}", null, cancellationToken, userId).ConfigureAwait(false);
    }

    /// <summary>Gets the authenticated user's public Seerr capabilities.</summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The permission bitmask used to shape the client UI.</returns>
    [HttpGet("User")]
    public async Task<IActionResult> GetUserCapabilities(CancellationToken cancellationToken)
    {
        var userId = await this.ResolveAuthenticatedSeerrUserIdAsync(cancellationToken).ConfigureAwait(false);
        if (!userId.HasValue)
        {
            return this.StatusCode(StatusCodes.Status403Forbidden);
        }

        using var response = await this.SendAsync(HttpMethod.Get, "/auth/me", null, cancellationToken, userId).ConfigureAwait(false);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var permissions = document.RootElement.TryGetProperty("permissions", out var value) && value.TryGetInt32(out var mask) ? mask : 0;
        return this.StatusCode((int)response.StatusCode, new { permissions });
    }

    /// <summary>Gets profiles and folders for a request service.</summary>
    /// <param name="mediaType">The Seerr media type.</param>
    /// <param name="serverId">The configured service identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The service profiles and folders.</returns>
    [HttpGet("Services/{mediaType}/{serverId:int}")]
    public async Task<IActionResult> GetServiceDetails([FromRoute] string mediaType, [FromRoute] int serverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        var service = GetServiceName(mediaType);
        if (service == null)
        {
            return this.BadRequest(new { message = "MediaType must be movie or tv." });
        }

        var userId = await this.ResolveAuthenticatedSeerrUserIdAsync(cancellationToken).ConfigureAwait(false);
        if (!userId.HasValue)
        {
            return this.StatusCode(StatusCodes.Status403Forbidden);
        }

        return await this.ProxyAsync(HttpMethod.Get, $"/service/{service}/{serverId.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken, userId).ConfigureAwait(false);
    }

    /// <summary>Adds a title to the authenticated user's Seerr watchlist.</summary>
    /// <param name="payload">The validated watchlist payload forwarded to Seerr.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The created watchlist item.</returns>
    [HttpPost("Watchlist")]
    public async Task<IActionResult> AddToWatchlist([FromBody] JsonElement payload, CancellationToken cancellationToken)
    {
        var userId = await this.ResolveAuthenticatedSeerrUserIdAsync(cancellationToken).ConfigureAwait(false);
        if (!userId.HasValue)
        {
            return this.StatusCode(StatusCodes.Status403Forbidden);
        }

        return await this.ProxyAsync(HttpMethod.Post, "/watchlist", payload, cancellationToken, userId).ConfigureAwait(false);
    }

    /// <summary>Gets the authenticated user's Seerr watchlist.</summary>
    /// <param name="page">The result page.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The user's watchlist.</returns>
    [HttpGet("Watchlist")]
    public async Task<IActionResult> GetWatchlist([FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        var userId = await this.ResolveAuthenticatedSeerrUserIdAsync(cancellationToken).ConfigureAwait(false);
        if (!userId.HasValue)
        {
            return this.StatusCode(StatusCodes.Status403Forbidden);
        }

        var path = $"/user/{userId.Value.ToString(CultureInfo.InvariantCulture)}/watchlist?page={Math.Max(1, page).ToString(CultureInfo.InvariantCulture)}";
        return await this.ProxyAsync(HttpMethod.Get, path, null, cancellationToken, userId).ConfigureAwait(false);
    }

    /// <summary>Removes a title from the authenticated user's Seerr watchlist.</summary>
    /// <param name="mediaType">The Seerr media type.</param>
    /// <param name="tmdbId">The TMDB media identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>An empty successful response.</returns>
    [HttpDelete("Watchlist/{mediaType}/{tmdbId:int}")]
    public async Task<IActionResult> RemoveFromWatchlist([FromRoute] string mediaType, [FromRoute] int tmdbId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        var normalizedMediaType = mediaType.Equals("tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
        if (GetServiceName(mediaType) == null)
        {
            return this.BadRequest();
        }

        var userId = await this.ResolveAuthenticatedSeerrUserIdAsync(cancellationToken).ConfigureAwait(false);
        if (!userId.HasValue)
        {
            return this.StatusCode(StatusCodes.Status403Forbidden);
        }

        var path = $"/watchlist/{tmdbId.ToString(CultureInfo.InvariantCulture)}?mediaType={normalizedMediaType}";
        return await this.ProxyAsync(HttpMethod.Delete, path, null, cancellationToken, userId).ConfigureAwait(false);
    }

    private static string? GetServiceName(string mediaType)
        => mediaType.Equals("tv", StringComparison.OrdinalIgnoreCase) ? "sonarr" :
            mediaType.Equals("movie", StringComparison.OrdinalIgnoreCase) ? "radarr" : null;

    private static bool TryGetConfiguration(out string baseUrl, out string apiKey)
    {
        var configuration = Plugin.Instance?.Configuration;
        return TryNormalizeConfiguration(configuration?.SeerrUrl, configuration?.SeerrApiKey, out baseUrl, out apiKey);
    }

    private static bool TryNormalizeConfiguration(
        string? configuredUrl,
        string? configuredApiKey,
        out string baseUrl,
        out string apiKey)
    {
        baseUrl = configuredUrl?.Trim().TrimEnd('/') ?? string.Empty;
        apiKey = configuredApiKey?.Trim() ?? string.Empty;

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(apiKey);
    }

    /// <summary>
    /// Proxies a GET request to Seerr by fetching 5 consecutive upstream pages concurrently and merging them into a single 100-item page payload.
    /// </summary>
    /// <param name="pathWithoutPage">The API path without the page query parameter.</param>
    /// <param name="litefinPage">The Litefin page number (where page 1 maps to Seerr pages 1-5, page 2 maps to 6-10, etc.).</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The aggregated Seerr response payload.</returns>
    private async Task<IActionResult> ProxyGetMerged5PagesAsync(
        string pathWithoutPage,
        int litefinPage,
        CancellationToken cancellationToken)
    {
        if (!TryGetConfiguration(out _, out _))
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Seerr is not configured." });
        }

        var normalizedPage = Math.Max(1, litefinPage);
        var startSeerrPage = ((normalizedPage - 1) * 5) + 1;
        var separator = pathWithoutPage.Contains('?', StringComparison.Ordinal) ? "&" : "?";

        var fetchTasks = Enumerable.Range(0, 5).Select(async offset =>
        {
            var targetSeerrPage = startSeerrPage + offset;
            var targetPath = $"{pathWithoutPage}{separator}page={targetSeerrPage.ToString(CultureInfo.InvariantCulture)}";
            try
            {
                using var response = await this.SendAsync(HttpMethod.Get, targetPath, null, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                return doc.RootElement.Clone();
            }
            catch (HttpRequestException ex)
            {
                this.logger.LogWarning(ex, "Failed to fetch Seerr page {TargetPage} for path {Path}", targetSeerrPage, pathWithoutPage);
                return (JsonElement?)null;
            }
            catch (TaskCanceledException ex)
            {
                this.logger.LogWarning(ex, "Timed out fetching Seerr page {TargetPage} for path {Path}", targetSeerrPage, pathWithoutPage);
                return (JsonElement?)null;
            }
        });

        var results = await Task.WhenAll(fetchTasks).ConfigureAwait(false);
        var validDocs = results.Where(d => d.HasValue && d.Value.ValueKind == JsonValueKind.Object).Select(d => d!.Value).ToList();

        if (validDocs.Count == 0)
        {
            return this.StatusCode(StatusCodes.Status502BadGateway, new { message = "Unable to reach Seerr." });
        }

        var firstDoc = validDocs[0];
        var upstreamTotalPages = firstDoc.TryGetProperty("totalPages", out var tpProp) && tpProp.TryGetInt32(out var tpVal) ? tpVal : 1;
        var upstreamTotalResults = firstDoc.TryGetProperty("totalResults", out var trProp) && trProp.TryGetInt32(out var trVal) ? trVal : 0;

        var mergedResults = new List<JsonElement>();
        foreach (var doc in validDocs)
        {
            if (doc.TryGetProperty("results", out var resProp) && resProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in resProp.EnumerateArray())
                {
                    mergedResults.Add(item.Clone());
                }
            }
        }

        var mergedTotalPages = (int)Math.Ceiling(upstreamTotalPages / 5.0);

        var mergedPayload = new Dictionary<string, object>
        {
            ["page"] = normalizedPage,
            ["totalPages"] = Math.Max(1, mergedTotalPages),
            ["totalResults"] = upstreamTotalResults,
            ["results"] = mergedResults,
        };

        return this.Ok(mergedPayload);
    }

    private async Task<IActionResult> ProxyGetAsync(string path, CancellationToken cancellationToken)
        => await this.ProxyAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);

    private async Task<IActionResult> ProxyAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken,
        int? seerrUserId = null)
    {
        if (!TryGetConfiguration(out _, out _))
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Seerr is not configured." });
        }

        try
        {
            using var response = await this.SendAsync(method, path, body, cancellationToken, seerrUserId).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? MediaTypeNames.Application.Json;
            return new ContentResult
            {
                Content = content,
                ContentType = contentType,
                StatusCode = (int)response.StatusCode,
            };
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogWarning(ex, "Seerr request failed for {Path}", path);
            return this.StatusCode(StatusCodes.Status502BadGateway, new { message = "Unable to reach Seerr." });
        }
        catch (TaskCanceledException ex)
        {
            this.logger.LogWarning(ex, "Seerr request timed out for {Path}", path);
            return this.StatusCode(StatusCodes.Status504GatewayTimeout, new { message = "The Seerr request timed out." });
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken,
        int? seerrUserId = null)
    {
        if (!TryGetConfiguration(out var baseUrl, out var apiKey))
        {
            throw new InvalidOperationException("Seerr is not configured.");
        }

        using var client = this.httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(method, $"{baseUrl}/api/v1{path}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        request.Headers.Add("X-Api-Key", apiKey);
        if (seerrUserId.HasValue)
        {
            request.Headers.Add("X-Api-User", seerrUserId.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (body != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, MediaTypeNames.Application.Json);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);
        return await client.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var client = this.httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(method, $"{baseUrl}/api/v1{path}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        request.Headers.Add("X-Api-Key", apiKey);

        if (body != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, MediaTypeNames.Application.Json);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);
        return await client.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
    }

    private async Task<int?> ResolveSeerrUserIdAsync(Guid jellyfinUserId, CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        var normalizedUserId = jellyfinUserId.ToString("N", CultureInfo.InvariantCulture);

        for (var skip = 0; skip < 10000; skip += pageSize)
        {
            var path = $"/user?take={pageSize.ToString(CultureInfo.InvariantCulture)}&skip={skip.ToString(CultureInfo.InvariantCulture)}";
            using var response = await this.SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                this.logger.LogWarning("Unable to resolve Seerr user: upstream returned {StatusCode}", response.StatusCode);
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var users = document.RootElement;
            if (users.ValueKind == JsonValueKind.Object && users.TryGetProperty("results", out var results))
            {
                users = results;
            }

            if (users.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var user in users.EnumerateArray())
            {
                if (!user.TryGetProperty("jellyfinUserId", out var jellyfinIdProperty)
                    || !user.TryGetProperty("id", out var idProperty))
                {
                    continue;
                }

                var candidate = jellyfinIdProperty.GetString()?.Replace("-", string.Empty, StringComparison.Ordinal);
                if (candidate != null
                    && candidate.Equals(normalizedUserId, StringComparison.OrdinalIgnoreCase)
                    && idProperty.TryGetInt32(out var seerrUserId))
                {
                    return seerrUserId;
                }
            }

            if (users.GetArrayLength() < pageSize)
            {
                break;
            }
        }

        return null;
    }

    private Task<int?> ResolveAuthenticatedSeerrUserIdAsync(CancellationToken cancellationToken)
    {
        var jellyfinUserId = this.GetAuthenticatedUserId();
        return jellyfinUserId.HasValue
            ? this.ResolveSeerrUserIdAsync(jellyfinUserId.Value, cancellationToken)
            : Task.FromResult<int?>(null);
    }

    private async Task<bool> HasSeerrPermissionAsync(int seerrUserId, int permission, CancellationToken cancellationToken)
    {
        using var response = await this.SendAsync(HttpMethod.Get, "/auth/me", null, cancellationToken, seerrUserId).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("permissions", out var value) || !value.TryGetInt32(out var permissions))
        {
            return false;
        }

        const int adminPermission = 2;
        return (permissions & adminPermission) != 0 || (permissions & permission) != 0;
    }

    private Guid? GetAuthenticatedUserId()
    {
        var claim = this.User.Claims.FirstOrDefault(
            candidate => candidate.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase));
        return Guid.TryParse(claim?.Value, out var userId) ? userId : null;
    }
}
