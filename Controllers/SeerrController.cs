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
    /// Gets popular movies from Seerr.
    /// </summary>
    /// <param name="page">The page number.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Movies")]
    public Task<IActionResult> GetMovies([FromQuery] int page = 1, CancellationToken cancellationToken = default)
        => this.ProxyGetAsync($"/discover/movies?page={Math.Max(1, page).ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    /// <summary>
    /// Gets popular television series from Seerr.
    /// </summary>
    /// <param name="page">The page number.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Tv")]
    public Task<IActionResult> GetTv([FromQuery] int page = 1, CancellationToken cancellationToken = default)
        => this.ProxyGetAsync($"/discover/tv?page={Math.Max(1, page).ToString(CultureInfo.InvariantCulture)}", cancellationToken);

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
    /// Gets details for a Seerr movie.
    /// </summary>
    /// <param name="tmdbId">The TMDB movie identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Movie/{tmdbId:int}")]
    public Task<IActionResult> GetMovieDetails([FromRoute] int tmdbId, CancellationToken cancellationToken)
        => this.ProxyGetAsync($"/movie/{tmdbId.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

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
            ["userId"] = seerrUserId.Value,
        };

        if (isTv)
        {
            payload["seasons"] = seasons!;
        }

        return await this.ProxyAsync(HttpMethod.Post, "/request", payload, cancellationToken).ConfigureAwait(false);
    }

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

    private async Task<IActionResult> ProxyGetAsync(string path, CancellationToken cancellationToken)
        => await this.ProxyAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);

    private async Task<IActionResult> ProxyAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        if (!TryGetConfiguration(out _, out _))
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Seerr is not configured." });
        }

        try
        {
            using var response = await this.SendAsync(method, path, body, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        if (!TryGetConfiguration(out var baseUrl, out var apiKey))
        {
            throw new InvalidOperationException("Seerr is not configured.");
        }

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

    private Guid? GetAuthenticatedUserId()
    {
        var claim = this.User.Claims.FirstOrDefault(
            candidate => candidate.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase));
        return Guid.TryParse(claim?.Value, out var userId) ? userId : null;
    }
}
