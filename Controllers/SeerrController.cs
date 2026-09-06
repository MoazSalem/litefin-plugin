// <copyright file="SeerrController.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

#pragma warning disable CA1848 // Use LoggerMessage delegates for performance

namespace Litefin.Plugin.Controllers;

using System;
using System.Collections.Concurrent;
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

    // In-memory cache holding non-admin blocklist visibility permissions
    // Keyed by Jellyfin User ID to avoid repeated upstream network roundtrips
    private static readonly ConcurrentDictionary<Guid, (bool CanView, DateTime ExpiresAt)> BlocklistPermissionCache = new();

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

        // Validate that a syntactically valid base URL has been supplied
        if (!TryNormalizeUrl(request.SeerrUrl, out var baseUrl))
        {
            return this.BadRequest(new { available = false, message = "A valid Seerr URL is required." });
        }

        var hasApiKey = !string.IsNullOrWhiteSpace(request.SeerrApiKey);

        try
        {
            // If an API key is present, verify full authentication against Seerr /auth/me
            if (hasApiKey)
            {
                var apiKey = request.SeerrApiKey!.Trim();
                using var response = await this.SendAsync(
                    HttpMethod.Get,
                    "/auth/me",
                    null,
                    baseUrl,
                    apiKey,
                    cancellationToken).ConfigureAwait(false);

                // Successfully verified both reachability and API key authentication
                if (response.IsSuccessStatusCode)
                {
                    return this.Ok(new { available = true, authenticated = true, message = "Connected and authenticated successfully." });
                }

                // Handle authorization rejection specifically
                if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                {
                    return this.Ok(new { available = false, authenticated = false, message = "Seerr reached, but the API key was rejected." });
                }

                return this.Ok(new { available = false, authenticated = false, message = $"Seerr responded with HTTP {(int)response.StatusCode}." });
            }

            // If no API key was supplied, verify server reachability via public /api/v1/status
            using var client = this.httpClientFactory.CreateClient();
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/status");
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

            // Bound timeout for quick feedback
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(10));

            using var pingResponse = await client.SendAsync(httpRequest, timeoutSource.Token).ConfigureAwait(false);
            if (pingResponse.IsSuccessStatusCode)
            {
                return this.Ok(new
                {
                    available = true,
                    authenticated = false,
                    message = "Seerr server is online and reachable (no API key configured yet).",
                });
            }

            return this.Ok(new
            {
                available = false,
                authenticated = false,
                message = $"Seerr server returned HTTP {(int)pingResponse.StatusCode} ({pingResponse.ReasonPhrase}).",
            });
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogWarning(ex, "Unable to reach Seerr while testing settings at {BaseUrl}", baseUrl);
            return this.Ok(new { available = false, message = $"Cannot reach server ({ex.Message}). Check IP, port, and network route." });
        }
        catch (TaskCanceledException ex)
        {
            this.logger.LogWarning(ex, "Timed out while testing Seerr settings at {BaseUrl}", baseUrl);
            return this.Ok(new { available = false, message = "Connection timed out. Check IP/port and firewall settings." });
        }
    }

    /// <summary>
    /// Tests whether the specified Seerr server is reachable without requiring an API key.
    /// </summary>
    /// <param name="request">The server reachability test parameters.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A status object indicating whether the server responded and details.</returns>
    [HttpPost("Status/Ping")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<object>> PingServer(
        [FromBody] SeerrServerTestRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Normalize and validate the provided Seerr base URL
        if (!TryNormalizeUrl(request.SeerrUrl, out var baseUrl))
        {
            return this.BadRequest(new { reachable = false, message = "A valid Seerr URL is required." });
        }

        try
        {
            // Contact Seerr's public unauthenticated status endpoint
            using var client = this.httpClientFactory.CreateClient();
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/status");
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

            // Apply a 10-second timeout to prevent UI hangs
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await client.SendAsync(httpRequest, timeoutSource.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return this.Ok(new
                {
                    reachable = true,
                    statusCode = (int)response.StatusCode,
                    message = "Seerr server is online and reachable.",
                });
            }

            return this.Ok(new
            {
                reachable = false,
                statusCode = (int)response.StatusCode,
                message = $"Server responded with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
            });
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogWarning(ex, "Unable to reach Seerr server at {BaseUrl}", baseUrl);
            return this.Ok(new
            {
                reachable = false,
                message = $"Cannot reach server ({ex.Message}). Check IP, port, and network route.",
            });
        }
        catch (TaskCanceledException ex)
        {
            this.logger.LogWarning(ex, "Timed out pinging Seerr server at {BaseUrl}", baseUrl);
            return this.Ok(new
            {
                reachable = false,
                message = "Connection timed out. Check IP/port and firewall settings.",
            });
        }
    }

    /// <summary>
    /// Initiates a Quick Connect pairing session with Seerr.
    /// </summary>
    /// <param name="request">The Quick Connect initiation parameters containing SeerrUrl.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A response containing the 6-character user code and status secret.</returns>
    [HttpPost("Auth/QuickConnect/Initiate")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<SeerrQuickConnectInitiateResult>> InitiateQuickConnect(
        [FromBody] SeerrQuickConnectInitiateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Normalize Seerr base URL ensuring valid HTTP/HTTPS scheme
        if (!TryNormalizeUrl(request.SeerrUrl, out var baseUrl))
        {
            return this.BadRequest(new { message = "A valid Seerr URL is required." });
        }

        try
        {
            // Contact Seerr's Quick Connect initiation endpoint directly
            using var client = this.httpClientFactory.CreateClient();
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/auth/jellyfin/quickconnect/initiate");
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(RequestTimeout);

            using var response = await client.SendAsync(httpRequest, timeoutSource.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                this.logger.LogWarning("Seerr Quick Connect initiate failed with status {StatusCode}", response.StatusCode);
                return this.StatusCode(StatusCodes.Status502BadGateway, new { message = "Seerr rejected the Quick Connect initiate request. Ensure Quick Connect is enabled in Jellyfin and Seerr." });
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            var code = doc.RootElement.TryGetProperty("code", out var codeProp) ? codeProp.GetString() : string.Empty;
            var secret = doc.RootElement.TryGetProperty("secret", out var secretProp) ? secretProp.GetString() : string.Empty;

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(secret))
            {
                return this.StatusCode(StatusCodes.Status502BadGateway, new { message = "Seerr returned an incomplete Quick Connect response." });
            }

            return this.Ok(new SeerrQuickConnectInitiateResult
            {
                Code = code,
                Secret = secret,
            });
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogWarning(ex, "Failed to reach Seerr for Quick Connect initiate at {Url}", baseUrl);
            return this.StatusCode(StatusCodes.Status502BadGateway, new { message = "Unable to contact Seerr server." });
        }
        catch (TaskCanceledException ex)
        {
            this.logger.LogWarning(ex, "Timed out waiting for Seerr Quick Connect initiate at {Url}", baseUrl);
            return this.StatusCode(StatusCodes.Status504GatewayTimeout, new { message = "The request to Seerr timed out." });
        }
    }

    /// <summary>
    /// Checks Quick Connect authorization status with Seerr and automatically extracts and persists the API key once authorized.
    /// </summary>
    /// <param name="request">The check request containing SeerrUrl and the secret.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A status result indicating authorization status and save success.</returns>
    [HttpPost("Auth/QuickConnect/Check")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<SeerrQuickConnectCheckResult>> CheckQuickConnect(
        [FromBody] SeerrQuickConnectCheckRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Validate Seerr base URL and secret
        if (!TryNormalizeUrl(request.SeerrUrl, out var baseUrl) || string.IsNullOrWhiteSpace(request.Secret))
        {
            return this.BadRequest(new { message = "A valid Seerr URL and Secret are required." });
        }

        try
        {
            using var client = this.httpClientFactory.CreateClient();

            // Step 1: Query Seerr's Quick Connect check endpoint to see if administrator approved the code
            var checkUrl = $"{baseUrl}/api/v1/auth/jellyfin/quickconnect/check?secret={Uri.EscapeDataString(request.Secret)}";
            using var checkRequest = new HttpRequestMessage(HttpMethod.Get, checkUrl);
            checkRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(RequestTimeout);

            using var checkResponse = await client.SendAsync(checkRequest, timeoutSource.Token).ConfigureAwait(false);
            if (!checkResponse.IsSuccessStatusCode)
            {
                return this.Ok(new SeerrQuickConnectCheckResult
                {
                    Authenticated = false,
                    Success = false,
                    Message = "Pending approval or expired code.",
                });
            }

            using var checkStream = await checkResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var checkDoc = await JsonDocument.ParseAsync(checkStream, cancellationToken: cancellationToken).ConfigureAwait(false);

            var isAuthenticated = checkDoc.RootElement.TryGetProperty("authenticated", out var authProp) && authProp.GetBoolean();
            if (!isAuthenticated)
            {
                // Still waiting for administrator to authorize code
                return this.Ok(new SeerrQuickConnectCheckResult
                {
                    Authenticated = false,
                    Success = false,
                });
            }

            // Step 2: Exchange authorized secret for an authenticated session with Seerr
            var authPayload = JsonSerializer.Serialize(new { secret = request.Secret });
            using var exchangeRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/auth/jellyfin/quickconnect/authenticate")
            {
                Content = new StringContent(authPayload, Encoding.UTF8, MediaTypeNames.Application.Json),
            };
            exchangeRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

            using var exchangeResponse = await client.SendAsync(exchangeRequest, timeoutSource.Token).ConfigureAwait(false);
            if (!exchangeResponse.IsSuccessStatusCode)
            {
                return this.StatusCode(StatusCodes.Status502BadGateway, new SeerrQuickConnectCheckResult
                {
                    Authenticated = true,
                    Success = false,
                    Message = "Quick Connect authorization confirmed, but failed to establish session with Seerr.",
                });
            }

            // Extract session cookie from Seerr response (connect.sid)
            var cookieHeader = GetCookieHeader(exchangeResponse);
            if (string.IsNullOrWhiteSpace(cookieHeader))
            {
                return this.StatusCode(StatusCodes.Status502BadGateway, new SeerrQuickConnectCheckResult
                {
                    Authenticated = true,
                    Success = false,
                    Message = "Seerr session cookie missing after authentication.",
                });
            }

            // Step 3: Fetch Seerr's main settings to extract the server's API key
            var apiKey = await this.FetchSeerrApiKeyAsync(baseUrl, cookieHeader, timeoutSource.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return this.StatusCode(StatusCodes.Status502BadGateway, new SeerrQuickConnectCheckResult
                {
                    Authenticated = true,
                    Success = false,
                    Message = "Quick Connect authorized, but this Jellyfin account lacks administrator rights in Seerr to retrieve the API key.",
                });
            }

            // Step 4: Persist the configuration in the Litefin plugin
            SaveSeerrConfiguration(baseUrl, apiKey);

            this.logger.LogInformation("Successfully paired Litefin with Seerr via Quick Connect at {Url}", baseUrl);

            return this.Ok(new SeerrQuickConnectCheckResult
            {
                Authenticated = true,
                Success = true,
                Message = "Quick Connect pairing successful! Seerr API key acquired and saved.",
            });
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogWarning(ex, "HTTP error during Seerr Quick Connect check at {Url}", baseUrl);
            return this.StatusCode(StatusCodes.Status502BadGateway, new SeerrQuickConnectCheckResult
            {
                Authenticated = false,
                Success = false,
                Message = "Unable to contact Seerr server.",
            });
        }
        catch (TaskCanceledException ex)
        {
            this.logger.LogWarning(ex, "Timeout during Seerr Quick Connect check at {Url}", baseUrl);
            return this.StatusCode(StatusCodes.Status504GatewayTimeout, new SeerrQuickConnectCheckResult
            {
                Authenticated = false,
                Success = false,
                Message = "Request to Seerr timed out.",
            });
        }
    }

    /// <summary>
    /// Authenticates with Seerr using administrator credentials, retrieves the API key, and saves it.
    /// </summary>
    /// <param name="request">The administrator login credentials and Seerr URL.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A status result indicating whether the API key was acquired and saved.</returns>
    [HttpPost("Auth/Login")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<SeerrAdminLoginResult>> LoginWithCredentials(
        [FromBody] SeerrAdminLoginRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Validate basic inputs
        if (!TryNormalizeUrl(request.SeerrUrl, out var baseUrl)
            || string.IsNullOrWhiteSpace(request.Username))
        {
            return this.BadRequest(new { message = "Seerr URL and Username are required." });
        }

        var password = request.Password ?? string.Empty;

        try
        {
            using var client = this.httpClientFactory.CreateClient();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(RequestTimeout);

            // Attempt Jellyfin authentication endpoint on Seerr first
            var jellyfinLoginPayload = JsonSerializer.Serialize(new
            {
                username = request.Username,
                password = password,
            });

            using var jfRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/auth/jellyfin")
            {
                Content = new StringContent(jellyfinLoginPayload, Encoding.UTF8, MediaTypeNames.Application.Json),
            };
            jfRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

            using var jfResponse = await client.SendAsync(jfRequest, timeoutSource.Token).ConfigureAwait(false);

            string? cookieHeader = null;
            if (jfResponse.IsSuccessStatusCode)
            {
                cookieHeader = GetCookieHeader(jfResponse);
            }
            else
            {
                // Fallback: Attempt local Seerr login in case local authentication is used
                var localLoginPayload = JsonSerializer.Serialize(new
                {
                    email = request.Username,
                    password = password,
                });

                using var localRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/auth/local")
                {
                    Content = new StringContent(localLoginPayload, Encoding.UTF8, MediaTypeNames.Application.Json),
                };
                localRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

                using var localResponse = await client.SendAsync(localRequest, timeoutSource.Token).ConfigureAwait(false);
                if (localResponse.IsSuccessStatusCode)
                {
                    cookieHeader = GetCookieHeader(localResponse);
                }
                else
                {
                    this.logger.LogWarning("Seerr login failed with status {StatusCode} (JF) and {LocalStatusCode} (Local)", jfResponse.StatusCode, localResponse.StatusCode);
                    return this.StatusCode(StatusCodes.Status401Unauthorized, new SeerrAdminLoginResult
                    {
                        Success = false,
                        Message = "Invalid Seerr credentials or account not found.",
                    });
                }
            }

            if (string.IsNullOrWhiteSpace(cookieHeader))
            {
                return this.StatusCode(StatusCodes.Status502BadGateway, new SeerrAdminLoginResult
                {
                    Success = false,
                    Message = "Seerr session cookie missing after authentication.",
                });
            }

            // Query Seerr settings/main to obtain the administrator API key
            var apiKey = await this.FetchSeerrApiKeyAsync(baseUrl, cookieHeader, timeoutSource.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return this.StatusCode(StatusCodes.Status403Forbidden, new SeerrAdminLoginResult
                {
                    Success = false,
                    Message = "Authentication succeeded, but this user account lacks administrator privileges in Seerr.",
                });
            }

            // Persist the configuration in the Litefin plugin
            SaveSeerrConfiguration(baseUrl, apiKey);

            this.logger.LogInformation("Successfully configured Seerr via admin login at {Url}", baseUrl);

            return this.Ok(new SeerrAdminLoginResult
            {
                Success = true,
                Message = "Logged in successfully! Seerr API key acquired and saved.",
            });
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogWarning(ex, "HTTP error during Seerr admin login at {Url}", baseUrl);
            return this.StatusCode(StatusCodes.Status502BadGateway, new SeerrAdminLoginResult
            {
                Success = false,
                Message = "Unable to contact Seerr server.",
            });
        }
        catch (TaskCanceledException ex)
        {
            this.logger.LogWarning(ex, "Timeout during Seerr admin login at {Url}", baseUrl);
            return this.StatusCode(StatusCodes.Status504GatewayTimeout, new SeerrAdminLoginResult
            {
                Success = false,
                Message = "Request to Seerr timed out.",
            });
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
    /// Gets popular movies from Seerr, optionally filtered by genre, language, certification, keywords, or studio, merging 5 upstream pages.
    /// </summary>
    /// <param name="page">The page number.</param>
    /// <param name="genre">The optional comma-separated genre identifier(s).</param>
    /// <param name="language">The optional pipe-separated language identifier(s).</param>
    /// <param name="certification">The optional pipe-separated certification rating(s).</param>
    /// <param name="certificationCountry">The optional certification country code (defaults to US).</param>
    /// <param name="keywords">The optional keywords identifier.</param>
    /// <param name="studio">The optional studio identifier.</param>
    /// <param name="sortBy">The optional TMDB sort expression (e.g. popularity.desc, release_date.desc, vote_average.desc, original_title.asc).</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Movies")]
    public Task<IActionResult> GetMovies(
        [FromQuery] int page = 1,
        [FromQuery] string? genre = null,
        [FromQuery] string? language = null,
        [FromQuery] string? certification = null,
        [FromQuery] string? certificationCountry = null,
        [FromQuery] int? keywords = null,
        [FromQuery] int? studio = null,
        [FromQuery] string? sortBy = null,
        CancellationToken cancellationToken = default)
    {
        var queryParams = new List<string>();
        if (!string.IsNullOrWhiteSpace(genre))
        {
            queryParams.Add($"genre={Uri.EscapeDataString(genre)}");
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            queryParams.Add($"language={Uri.EscapeDataString(language)}");
        }

        if (!string.IsNullOrWhiteSpace(certification))
        {
            var country = !string.IsNullOrWhiteSpace(certificationCountry) ? certificationCountry : "US";
            queryParams.Add($"certificationCountry={Uri.EscapeDataString(country)}");
            queryParams.Add($"certification={Uri.EscapeDataString(certification)}");
        }

        if (keywords.HasValue)
        {
            queryParams.Add($"keywords={keywords.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (studio.HasValue)
        {
            queryParams.Add($"studio={studio.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            queryParams.Add($"sortBy={Uri.EscapeDataString(sortBy)}");
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
    /// <param name="sortBy">The optional sort expression.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Movies/Genre/{genreId:int}")]
    public Task<IActionResult> GetMoviesByGenre(
        [FromRoute] int genreId,
        [FromQuery] int page = 1,
        [FromQuery] string? sortBy = null,
        CancellationToken cancellationToken = default)
    {
        var sortParam = !string.IsNullOrWhiteSpace(sortBy) ? $"&sortBy={Uri.EscapeDataString(sortBy)}" : string.Empty;
        return this.ProxyGetMerged5PagesAsync($"/discover/movies?genre={genreId.ToString(CultureInfo.InvariantCulture)}{sortParam}", page, cancellationToken);
    }

    /// <summary>
    /// Gets movies by keyword from Seerr, merging 5 upstream pages.
    /// </summary>
    /// <param name="keywordId">The keyword identifier.</param>
    /// <param name="page">The page number.</param>
    /// <param name="sortBy">The optional sort expression.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Movies/Keyword/{keywordId:int}")]
    public Task<IActionResult> GetMoviesByKeyword(
        [FromRoute] int keywordId,
        [FromQuery] int page = 1,
        [FromQuery] string? sortBy = null,
        CancellationToken cancellationToken = default)
    {
        var sortParam = !string.IsNullOrWhiteSpace(sortBy) ? $"&sortBy={Uri.EscapeDataString(sortBy)}" : string.Empty;
        return this.ProxyGetMerged5PagesAsync($"/discover/movies?keywords={keywordId.ToString(CultureInfo.InvariantCulture)}{sortParam}", page, cancellationToken);
    }

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
    /// Gets popular television series from Seerr, optionally filtered by genre, language, certification, keywords, or network, merging 5 upstream pages.
    /// </summary>
    /// <param name="page">The page number.</param>
    /// <param name="genre">The optional comma-separated genre identifier(s).</param>
    /// <param name="language">The optional pipe-separated language identifier(s).</param>
    /// <param name="certification">The optional pipe-separated certification rating(s).</param>
    /// <param name="certificationCountry">The optional certification country code (defaults to US).</param>
    /// <param name="keywords">The optional keywords identifier.</param>
    /// <param name="network">The optional network identifier.</param>
    /// <param name="sortBy">The optional TMDB sort expression (e.g. popularity.desc, first_air_date.desc, vote_average.desc, original_title.asc).</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Tv")]
    public Task<IActionResult> GetTv(
        [FromQuery] int page = 1,
        [FromQuery] string? genre = null,
        [FromQuery] string? language = null,
        [FromQuery] string? certification = null,
        [FromQuery] string? certificationCountry = null,
        [FromQuery] int? keywords = null,
        [FromQuery] int? network = null,
        [FromQuery] string? sortBy = null,
        CancellationToken cancellationToken = default)
    {
        var queryParams = new List<string>();
        if (!string.IsNullOrWhiteSpace(genre))
        {
            queryParams.Add($"genre={Uri.EscapeDataString(genre)}");
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            queryParams.Add($"language={Uri.EscapeDataString(language)}");
        }

        if (!string.IsNullOrWhiteSpace(certification))
        {
            var country = !string.IsNullOrWhiteSpace(certificationCountry) ? certificationCountry : "US";
            queryParams.Add($"certificationCountry={Uri.EscapeDataString(country)}");
            queryParams.Add($"certification={Uri.EscapeDataString(certification)}");
        }

        if (keywords.HasValue)
        {
            queryParams.Add($"keywords={keywords.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (network.HasValue)
        {
            queryParams.Add($"network={network.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            queryParams.Add($"sortBy={Uri.EscapeDataString(sortBy)}");
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
    /// <param name="sortBy">The optional sort expression.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Tv/Genre/{genreId:int}")]
    public Task<IActionResult> GetTvByGenre(
        [FromRoute] int genreId,
        [FromQuery] int page = 1,
        [FromQuery] string? sortBy = null,
        CancellationToken cancellationToken = default)
    {
        var sortParam = !string.IsNullOrWhiteSpace(sortBy) ? $"&sortBy={Uri.EscapeDataString(sortBy)}" : string.Empty;
        return this.ProxyGetMerged5PagesAsync($"/discover/tv?genre={genreId.ToString(CultureInfo.InvariantCulture)}{sortParam}", page, cancellationToken);
    }

    /// <summary>
    /// Gets television series by keyword from Seerr, merging 5 upstream pages.
    /// </summary>
    /// <param name="keywordId">The keyword identifier.</param>
    /// <param name="page">The page number.</param>
    /// <param name="sortBy">The optional sort expression.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr response.</returns>
    [HttpGet("Discover/Tv/Keyword/{keywordId:int}")]
    public Task<IActionResult> GetTvByKeyword(
        [FromRoute] int keywordId,
        [FromQuery] int page = 1,
        [FromQuery] string? sortBy = null,
        CancellationToken cancellationToken = default)
    {
        var sortParam = !string.IsNullOrWhiteSpace(sortBy) ? $"&sortBy={Uri.EscapeDataString(sortBy)}" : string.Empty;
        return this.ProxyGetMerged5PagesAsync($"/discover/tv?keywords={keywordId.ToString(CultureInfo.InvariantCulture)}{sortParam}", page, cancellationToken);
    }

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
    /// Gets Seerr requests list scoped specifically to the authenticated Jellyfin user.
    /// </summary>
    /// <param name="take">Number of requests to return.</param>
    /// <param name="skip">Number of requests to skip.</param>
    /// <param name="filter">Request status filter.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr requests list payload for the current user.</returns>
    [HttpGet("Requests")]
    [HttpGet("request")]
    public async Task<IActionResult> GetRequests(
        [FromQuery] int take = 20,
        [FromQuery] int skip = 0,
        [FromQuery] string filter = "all",
        CancellationToken cancellationToken = default)
    {
        // Resolve the authenticated user's Seerr ID to scope requests
        var userId = await this.ResolveAuthenticatedSeerrUserIdAsync(cancellationToken).ConfigureAwait(false);
        if (!userId.HasValue)
        {
            // If the user is not linked to Seerr, return an empty payload
            return this.Ok(new
            {
                page = 1,
                totalPages = 1,
                totalResults = 0,
                results = Array.Empty<object>(),
            });
        }

        // Scope to the specific user via requestedBy parameter and proxy as that user
        var path = $"/request?take={Math.Max(1, take).ToString(CultureInfo.InvariantCulture)}&skip={Math.Max(0, skip).ToString(CultureInfo.InvariantCulture)}&filter={Uri.EscapeDataString(filter)}&requestedBy={userId.Value.ToString(CultureInfo.InvariantCulture)}";
        return await this.ProxyAsync(HttpMethod.Get, path, null, cancellationToken, userId.Value).ConfigureAwait(false);
    }

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

        if (request.UserId.HasValue)
        {
            payload["userId"] = request.UserId.Value;
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
    /// <param name="mediaType">The media type.</param>
    /// <param name="tmdbId">The TMDB media identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The ratings response.</returns>
    [HttpGet("Ratings/{mediaType}/{tmdbId:int}")]
    public async Task<IActionResult> GetRatingsCombined([FromRoute] string mediaType, [FromRoute] int tmdbId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        var routeType = mediaType.Equals("tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
        var userId = await this.ResolveAuthenticatedSeerrUserIdAsync(cancellationToken).ConfigureAwait(false);
        if (!userId.HasValue)
        {
            return this.StatusCode(StatusCodes.Status403Forbidden);
        }

        return await this.ProxyAsync(HttpMethod.Get, $"/{routeType}/{tmdbId.ToString(CultureInfo.InvariantCulture)}/ratingscombined", null, cancellationToken, userId).ConfigureAwait(false);
    }

    /// <summary>Gets services configured in Seerr.</summary>
    /// <param name="mediaType">The Seerr media type.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The services list.</returns>
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

    /// <summary>
    /// Gets the list of users from Seerr.
    /// </summary>
    /// <param name="take">The number of users to retrieve.</param>
    /// <param name="sort">The sort order.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The Seerr user list response.</returns>
    [HttpGet("Users")]
    public Task<IActionResult> GetUsers(
        [FromQuery] int take = 1000,
        [FromQuery] string sort = "displayname",
        CancellationToken cancellationToken = default)
        => this.ProxyGetAsync($"/user?take={Math.Max(1, take).ToString(CultureInfo.InvariantCulture)}&sort={Uri.EscapeDataString(sort)}", cancellationToken);

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

    private static bool TryNormalizeUrl(string? configuredUrl, out string baseUrl)
    {
        baseUrl = configuredUrl?.Trim().TrimEnd('/') ?? string.Empty;
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryNormalizeConfiguration(
        string? configuredUrl,
        string? configuredApiKey,
        out string baseUrl,
        out string apiKey)
    {
        apiKey = configuredApiKey?.Trim() ?? string.Empty;
        return TryNormalizeUrl(configuredUrl, out baseUrl) && !string.IsNullOrWhiteSpace(apiKey);
    }

    private static string? GetCookieHeader(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var cookieList = cookies.Select(c => c.Split(';')[0].Trim()).Where(c => !string.IsNullOrWhiteSpace(c));
            return string.Join("; ", cookieList);
        }

        return null;
    }

    private static void SaveSeerrConfiguration(string baseUrl, string apiKey)
    {
        var plugin = Plugin.Instance;
        if (plugin != null)
        {
            plugin.Configuration.SeerrUrl = baseUrl;
            plugin.Configuration.SeerrApiKey = apiKey;
            plugin.SaveConfiguration();
        }
    }

    /// <summary>
    /// Checks whether a media item status property equals MediaStatus.BLOCKLISTED (value 6).
    /// </summary>
    /// <param name="prop">The JSON element representing the status value.</param>
    /// <returns><c>true</c> if the status represents a blocklisted title; otherwise <c>false</c>.</returns>
    private static bool IsBlocklistedStatus(JsonElement prop)
    {
        // Check numeric representation
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var num))
        {
            return num == 6;
        }

        // Check string representation
        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var strNum))
        {
            return strNum == 6;
        }

        return false;
    }

    /// <summary>
    /// Inspects a JSON element representing a media item or mediaInfo object to determine if it is blocklisted.
    /// </summary>
    /// <param name="element">The JSON element to test.</param>
    /// <returns><c>true</c> if marked as blocklisted; otherwise <c>false</c>.</returns>
    private static bool IsBlocklisted(JsonElement element)
    {
        // Must be a valid JSON object
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // Check embedded mediaInfo object (standard format for discover, search, and detail responses)
        if (element.TryGetProperty("mediaInfo", out var mediaInfo) && mediaInfo.ValueKind == JsonValueKind.Object)
        {
            // Standard media status
            if (mediaInfo.TryGetProperty("status", out var statusProp) && IsBlocklistedStatus(statusProp))
            {
                return true;
            }

            // 4K media status
            if (mediaInfo.TryGetProperty("status4k", out var status4kProp) && IsBlocklistedStatus(status4kProp))
            {
                return true;
            }
        }

        // Check top-level status properties (common when querying /media or /recentlyadded directly)
        if (element.TryGetProperty("status", out var rootStatusProp) && IsBlocklistedStatus(rootStatusProp))
        {
            return true;
        }

        if (element.TryGetProperty("status4k", out var rootStatus4kProp) && IsBlocklistedStatus(rootStatus4kProp))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Filters out blocklisted items from an embedded container object containing a "results" array (e.g. similar or recommendations).
    /// </summary>
    /// <param name="container">The sub-container JSON element.</param>
    /// <param name="filteredResults">The resulting filtered items list.</param>
    /// <returns><c>true</c> if any blocklisted items were removed; otherwise <c>false</c>.</returns>
    private static bool TryFilterResultsArray(JsonElement container, out List<JsonElement> filteredResults)
    {
        filteredResults = new List<JsonElement>();

        // Ensure container is an object with an array property named "results"
        if (container.ValueKind != JsonValueKind.Object
            || !container.TryGetProperty("results", out var resProp)
            || resProp.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var modified = false;

        // Iterate through items and exclude blocklisted media
        foreach (var item in resProp.EnumerateArray())
        {
            if (IsBlocklisted(item))
            {
                modified = true;
            }
            else
            {
                filteredResults.Add(item.Clone());
            }
        }

        return modified;
    }

    /// <summary>
    /// Rebuilds a container object replacing its "results" array with the filtered item list.
    /// </summary>
    /// <param name="container">The original container element.</param>
    /// <param name="filteredResults">The filtered results array.</param>
    /// <returns>A dictionary representing the reconstructed container object.</returns>
    private static Dictionary<string, object> RebuildContainerWithResults(JsonElement container, List<JsonElement> filteredResults)
    {
        var dict = new Dictionary<string, object>();

        // Copy existing properties, substituting the filtered results array
        foreach (var prop in container.EnumerateObject())
        {
            if (prop.NameEquals("results"))
            {
                dict[prop.Name] = filteredResults;
            }
            else
            {
                dict[prop.Name] = prop.Value.Clone();
            }
        }

        return dict;
    }

    private async Task<string?> FetchSeerrApiKeyAsync(string baseUrl, string cookieHeader, CancellationToken cancellationToken)
    {
        using var client = this.httpClientFactory.CreateClient();
        using var settingsRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/settings/main");
        settingsRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        settingsRequest.Headers.Add("Cookie", cookieHeader);

        using var settingsResponse = await client.SendAsync(settingsRequest, cancellationToken).ConfigureAwait(false);
        if (!settingsResponse.IsSuccessStatusCode)
        {
            this.logger.LogWarning("Failed to query Seerr main settings with status {StatusCode}", settingsResponse.StatusCode);
            return null;
        }

        using var stream = await settingsResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (doc.RootElement.TryGetProperty("apiKey", out var apiKeyProp))
        {
            return apiKeyProp.GetString();
        }

        return null;
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

        // Determine if the current authenticated user has administrative blocklist visibility
        var canViewBlocklist = await this.CanUserViewBlocklistAsync(cancellationToken).ConfigureAwait(false);

        // Collect and merge results across upstream pages, filtering blocklisted titles if non-admin
        var mergedResults = new List<JsonElement>();
        foreach (var doc in validDocs)
        {
            // Inspect document for results array
            if (doc.TryGetProperty("results", out var resProp) && resProp.ValueKind == JsonValueKind.Array)
            {
                // Iterate through each candidate item
                foreach (var item in resProp.EnumerateArray())
                {
                    // Exclude blocklisted titles when browsing as a standard non-admin user
                    if (canViewBlocklist || !IsBlocklisted(item))
                    {
                        mergedResults.Add(item.Clone());
                    }
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

            // Only evaluate blocklist filtering on successful GET requests returning JSON payloads
            if (response.IsSuccessStatusCode && method == HttpMethod.Get && contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                // Verify whether the requesting user is allowed to view blocklisted entries
                var canViewBlocklist = await this.CanUserViewBlocklistAsync(cancellationToken).ConfigureAwait(false);
                if (!canViewBlocklist)
                {
                    // Apply filtering logic; if modified or rejected (e.g. 404 for direct blocklisted detail), return filtered result
                    var filteredResult = this.FilterBlocklistedContent(content);
                    if (filteredResult != null)
                    {
                        return filteredResult;
                    }
                }
            }

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

    /// <summary>
    /// Checks whether the currently authenticated user is permitted to view blocklisted media.
    /// Returns true for Jellyfin administrators and users with Seerr MANAGE_BLOCKLIST or VIEW_BLOCKLIST permissions.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> if blocklisted items should be visible; otherwise <c>false</c>.</returns>
    private async Task<bool> CanUserViewBlocklistAsync(CancellationToken cancellationToken)
    {
        // -------------------------------------------------------------------------
        // 1. Jellyfin Administrator Check
        // -------------------------------------------------------------------------
        // Fast synchronous check: Jellyfin administrators always have elevated privileges.
        if (this.User.IsInRole("Administrator")
            || this.User.Claims.Any(c => (c.Type == System.Security.Claims.ClaimTypes.Role || c.Type.Equals("Role", StringComparison.OrdinalIgnoreCase))
                && c.Value.Equals("Administrator", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // -------------------------------------------------------------------------
        // 2. Memory Cache Lookup for Non-Admin Accounts
        // -------------------------------------------------------------------------
        // Check if we already resolved permission for this Jellyfin user in the last 5 minutes.
        var jellyfinUserId = this.GetAuthenticatedUserId();
        if (jellyfinUserId.HasValue && BlocklistPermissionCache.TryGetValue(jellyfinUserId.Value, out var cached) && DateTime.UtcNow < cached.ExpiresAt)
        {
            return cached.CanView;
        }

        var canView = false;

        // -------------------------------------------------------------------------
        // 3. Upstream Seerr User Permission Evaluation
        // -------------------------------------------------------------------------
        // Look up the matching Seerr user account and inspect their permission bits.
        // Permission bits: MANAGE_BLOCKLIST = 268435456 (0x10000000), VIEW_BLOCKLIST = 1073741824 (0x40000000)
        var seerrUserId = await this.ResolveAuthenticatedSeerrUserIdAsync(cancellationToken).ConfigureAwait(false);
        if (seerrUserId.HasValue)
        {
            const int manageBlocklist = 268435456;
            const int viewBlocklist = 1073741824;
            canView = await this.HasSeerrPermissionAsync(seerrUserId.Value, manageBlocklist | viewBlocklist, cancellationToken).ConfigureAwait(false);
        }

        // -------------------------------------------------------------------------
        // 4. Cache Update
        // -------------------------------------------------------------------------
        // Cache the result for 5 minutes so subsequent pagination and slider requests are instant.
        if (jellyfinUserId.HasValue)
        {
            BlocklistPermissionCache[jellyfinUserId.Value] = (canView, DateTime.UtcNow.AddMinutes(5));
        }

        return canView;
    }

    /// <summary>
    /// Filters blocklisted media items from a raw JSON string returned by Seerr.
    /// Handles lists, search results, collections, cast credits, and individual media details.
    /// </summary>
    /// <param name="content">The raw JSON content string.</param>
    /// <returns>An <see cref="IActionResult"/> if modified or rejected; otherwise <c>null</c>.</returns>
    private IActionResult? FilterBlocklistedContent(string content)
    {
        // Guard against empty strings
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            // ---------------------------------------------------------------------
            // Handle Object Payloads
            // ---------------------------------------------------------------------
            if (root.ValueKind == JsonValueKind.Object)
            {
                // Case 1: Direct Movie or TV Details (e.g. /movie/{id} or /tv/{id})
                // If the title itself is blocklisted, return a 404 NotFound so normal users cannot see it
                if (IsBlocklisted(root))
                {
                    return this.NotFound(new { message = "Media item not found." });
                }

                // Case 2: Standard Paged Lists (e.g. /discover/trending, /search, /media)
                if (root.TryGetProperty("results", out var resultsProp) && resultsProp.ValueKind == JsonValueKind.Array)
                {
                    var filteredResults = new List<JsonElement>();
                    var modified = false;

                    foreach (var item in resultsProp.EnumerateArray())
                    {
                        if (IsBlocklisted(item))
                        {
                            modified = true;
                        }
                        else
                        {
                            filteredResults.Add(item.Clone());
                        }
                    }

                    // Return Ok with filtered payload if items were removed
                    if (modified)
                    {
                        var dict = new Dictionary<string, object>();
                        foreach (var prop in root.EnumerateObject())
                        {
                            if (prop.NameEquals("results"))
                            {
                                dict[prop.Name] = filteredResults;
                            }
                            else
                            {
                                dict[prop.Name] = prop.Value.Clone();
                            }
                        }

                        return this.Ok(dict);
                    }
                }

                // Case 3: Collections (e.g. /collection/{id}) containing a "parts" array
                if (root.TryGetProperty("parts", out var partsProp) && partsProp.ValueKind == JsonValueKind.Array)
                {
                    var filteredParts = new List<JsonElement>();
                    var modified = false;

                    foreach (var part in partsProp.EnumerateArray())
                    {
                        if (IsBlocklisted(part))
                        {
                            modified = true;
                        }
                        else
                        {
                            filteredParts.Add(part.Clone());
                        }
                    }

                    if (modified)
                    {
                        var dict = new Dictionary<string, object>();
                        foreach (var prop in root.EnumerateObject())
                        {
                            if (prop.NameEquals("parts"))
                            {
                                dict[prop.Name] = filteredParts;
                            }
                            else
                            {
                                dict[prop.Name] = prop.Value.Clone();
                            }
                        }

                        return this.Ok(dict);
                    }
                }

                // Case 4: Person Filmography Credits (/person/{id}/combined_credits)
                if (root.TryGetProperty("cast", out var castProp) && castProp.ValueKind == JsonValueKind.Array)
                {
                    var filteredCast = new List<JsonElement>();
                    var filteredCrew = new List<JsonElement>();
                    var modified = false;

                    // Filter cast array
                    foreach (var item in castProp.EnumerateArray())
                    {
                        if (IsBlocklisted(item))
                        {
                            modified = true;
                        }
                        else
                        {
                            filteredCast.Add(item.Clone());
                        }
                    }

                    // Filter crew array if present
                    if (root.TryGetProperty("crew", out var crewProp) && crewProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in crewProp.EnumerateArray())
                        {
                            if (IsBlocklisted(item))
                            {
                                modified = true;
                            }
                            else
                            {
                                filteredCrew.Add(item.Clone());
                            }
                        }
                    }

                    if (modified)
                    {
                        var dict = new Dictionary<string, object>();
                        foreach (var prop in root.EnumerateObject())
                        {
                            if (prop.NameEquals("cast"))
                            {
                                dict[prop.Name] = filteredCast;
                            }
                            else if (prop.NameEquals("crew"))
                            {
                                dict[prop.Name] = filteredCrew;
                            }
                            else
                            {
                                dict[prop.Name] = prop.Value.Clone();
                            }
                        }

                        return this.Ok(dict);
                    }
                }

                // Case 5: Single Item Detail with embedded Similar or Recommendations
                var hasSimilar = false;
                List<JsonElement>? filteredSimilar = null;
                if (root.TryGetProperty("similar", out var similarProp))
                {
                    hasSimilar = TryFilterResultsArray(similarProp, out filteredSimilar);
                }

                var hasRecs = false;
                List<JsonElement>? filteredRecs = null;
                if (root.TryGetProperty("recommendations", out var recsProp))
                {
                    hasRecs = TryFilterResultsArray(recsProp, out filteredRecs);
                }

                if (hasSimilar || hasRecs)
                {
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.NameEquals("similar") && hasSimilar && filteredSimilar != null)
                        {
                            dict[prop.Name] = RebuildContainerWithResults(similarProp, filteredSimilar);
                        }
                        else if (prop.NameEquals("recommendations") && hasRecs && filteredRecs != null)
                        {
                            dict[prop.Name] = RebuildContainerWithResults(recsProp, filteredRecs);
                        }
                        else
                        {
                            dict[prop.Name] = prop.Value.Clone();
                        }
                    }

                    return this.Ok(dict);
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                // -----------------------------------------------------------------
                // Handle Direct Array Payloads
                // -----------------------------------------------------------------
                var filteredArray = new List<JsonElement>();
                var modified = false;

                foreach (var item in root.EnumerateArray())
                {
                    if (IsBlocklisted(item))
                    {
                        modified = true;
                    }
                    else
                    {
                        filteredArray.Add(item.Clone());
                    }
                }

                if (modified)
                {
                    return this.Ok(filteredArray);
                }
            }
        }
        catch (JsonException ex)
        {
            this.logger.LogWarning(ex, "Failed to parse JSON while evaluating blocklist filtering");
        }

        return null;
    }
}
