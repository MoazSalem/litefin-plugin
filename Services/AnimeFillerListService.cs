// <copyright file="AnimeFillerListService.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

#pragma warning disable CA1031 // Do not catch general exception types
#pragma warning disable CA1308 // Normalize strings to uppercase - lowercase required for URLs and CSS classes
#pragma warning disable CA1848 // Use LoggerMessage delegates for performance

namespace Litefin.Plugin.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Litefin.Plugin.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

/// <summary>
/// Service responsible for scraping, caching, and querying episode canon classifications
/// from the AnimeFillerList reference provider.
/// </summary>
public class AnimeFillerListService
{
    // Maximum cache age before refreshing show listings from the remote provider (7 days)
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    // Negative cache duration for slugs that returned 404 or failed parsing (1 day)
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromDays(1);

    // Concurrency throttle to avoid overwhelming the upstream provider
    private static readonly SemaphoreSlim RequestThrottle = new(2, 2);

    // Cached JSON serializer options to avoid re-allocation on read and write operations
    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    // In-memory cache holding parsed show datasets keyed by normalized slug
    private static readonly ConcurrentDictionary<string, CachedShowData> MemoryCache = new(StringComparer.OrdinalIgnoreCase);

    // Negative cache storing timestamps for slugs that do not exist upstream
    private static readonly ConcurrentDictionary<string, DateTime> NegativeCache = new(StringComparer.OrdinalIgnoreCase);

    // Regex matching episode row elements with class and eps id
    private static readonly Regex EpisodeRowRegex = new(
        @"<tr\s+class=""(?<class>[^""]+)""[^>]*id=""eps-(?<num>\d+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Alternate regex matching episode row elements where id precedes class
    private static readonly Regex EpisodeRowAltRegex = new(
        @"<tr[^>]*id=""eps-(?<num>\d+)""[^>]*class=""(?<class>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IApplicationPaths applicationPaths;
    private readonly ILogger<AnimeFillerListService> logger;
    private readonly string cacheDirectory;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeFillerListService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{AnimeFillerListService}"/> interface.</param>
    public AnimeFillerListService(
        IHttpClientFactory httpClientFactory,
        IApplicationPaths applicationPaths,
        ILogger<AnimeFillerListService> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.applicationPaths = applicationPaths;
        this.logger = logger;

        // Establish the persistent cache folder inside the plugin configuration directory
        this.cacheDirectory = Path.Combine(this.applicationPaths.PluginConfigurationsPath, "litefin_anime_markers");

        // Ensure the directory exists on disk for immediate reading and writing
        try
        {
            if (!Directory.Exists(this.cacheDirectory))
            {
                Directory.CreateDirectory(this.cacheDirectory);
            }
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Failed to initialize anime markers cache directory at {Path}", this.cacheDirectory);
        }
    }

    /// <summary>
    /// Retrieves episode classification data for the given show slug.
    /// Checks in-memory cache, then local disk storage, and finally scrapes remote upstream.
    /// </summary>
    /// <param name="slug">Canonical show slug identifier.</param>
    /// <param name="cancellationToken">Cancellation token for async operations.</param>
    /// <returns>Cached show data if resolved; otherwise, null.</returns>
    public async Task<CachedShowData?> GetShowDataAsync(string slug, CancellationToken cancellationToken = default)
    {
        // Guard against empty or blank slugs
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        var normalizedSlug = slug.Trim().ToLowerInvariant();

        // Check if this slug is temporarily flagged in the negative cache
        if (NegativeCache.TryGetValue(normalizedSlug, out var negativeTimestamp))
        {
            if (DateTime.UtcNow - negativeTimestamp < NegativeCacheTtl)
            {
                return null;
            }

            NegativeCache.TryRemove(normalizedSlug, out _);
        }

        // Layer 1: Check fast in-memory concurrent dictionary
        if (MemoryCache.TryGetValue(normalizedSlug, out var cachedData))
        {
            if (DateTime.UtcNow - cachedData.FetchedAt < CacheTtl)
            {
                return cachedData;
            }
        }

        // Layer 2: Check persistent disk storage
        var diskData = await this.LoadFromDiskAsync(normalizedSlug, cancellationToken).ConfigureAwait(false);
        if (diskData != null)
        {
            MemoryCache[normalizedSlug] = diskData;

            if (DateTime.UtcNow - diskData.FetchedAt < CacheTtl)
            {
                return diskData;
            }
        }

        // Layer 3: Fetch on-demand from AnimeFillerList remote provider
        return await this.FetchFromRemoteAsync(normalizedSlug, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses table rows and extracts episode classification metadata from raw HTML.
    /// </summary>
    /// <param name="html">Raw HTML markup from AnimeFillerList.</param>
    /// <returns>Map of episode numbers to parsed classification instances.</returns>
    private static Dictionary<int, AnimeMarkerEpisode> ParseEpisodesFromHtml(string html)
    {
        var result = new Dictionary<int, AnimeMarkerEpisode>();

        // Try primary row pattern where class comes first
        var matches = EpisodeRowRegex.Matches(html);

        // Fall back to secondary pattern where id comes first
        if (matches.Count == 0)
        {
            matches = EpisodeRowAltRegex.Matches(html);
        }

        // Iterate through all matched episode elements
        foreach (Match match in matches)
        {
            if (!match.Success)
            {
                continue;
            }

            var numStr = match.Groups["num"].Value;
            var classStr = match.Groups["class"].Value.ToLowerInvariant();

            // Parse episode number integer
            if (!int.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var episodeNum))
            {
                continue;
            }

            // Determine episode kind from the class token
            var kind = AnimeEpisodeKind.MangaCanon;
            var isRecap = classStr.Contains("recap", StringComparison.Ordinal);

            if (classStr.Contains("filler", StringComparison.Ordinal) &&
                !classStr.Contains("canon", StringComparison.Ordinal))
            {
                // Pure filler episode
                kind = AnimeEpisodeKind.Filler;
            }
            else if (classStr.Contains("mixed", StringComparison.Ordinal))
            {
                // Mixed canon and filler content
                kind = AnimeEpisodeKind.Mixed;
            }
            else if (classStr.Contains("anime_canon", StringComparison.Ordinal) ||
                     classStr.Contains("anime-canon", StringComparison.Ordinal))
            {
                // TV anime canonical content
                kind = AnimeEpisodeKind.AnimeCanon;
            }
            else
            {
                // Manga canon content
                kind = AnimeEpisodeKind.MangaCanon;
            }

            // Record episode metadata
            result[episodeNum] = new AnimeMarkerEpisode
            {
                Number = episodeNum,
                Kind = kind,
                Recap = isRecap,
            };
        }

        return result;
    }

    /// <summary>
    /// Scrapes remote HTML from AnimeFillerList and parses episode tables.
    /// </summary>
    /// <param name="slug">Normalized show slug.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Parsed show data or null if request failed or was not found.</returns>
    private async Task<CachedShowData?> FetchFromRemoteAsync(string slug, CancellationToken cancellationToken)
    {
        // Wait on the concurrency throttle to prevent hammering upstream
        await RequestThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check memory cache after acquiring throttle lock
            if (MemoryCache.TryGetValue(slug, out var existing) && DateTime.UtcNow - existing.FetchedAt < CacheTtl)
            {
                return existing;
            }

            // Construct HTTP request with standard browser User-Agent
            using var client = this.httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Litefin/1.0");

            var url = $"https://www.animefillerlist.com/shows/{slug}";
            this.logger.LogInformation("Fetching anime filler list from {Url}", url);

            using var response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);

            // Handle not found status codes gracefully
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                this.logger.LogWarning("Anime show slug '{Slug}' was not found on AnimeFillerList (404)", slug);
                NegativeCache[slug] = DateTime.UtcNow;
                return null;
            }

            response.EnsureSuccessStatusCode();

            // Read raw HTML content
            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Parse episode rows from the HTML markup
            var episodes = ParseEpisodesFromHtml(html);

            if (episodes.Count == 0)
            {
                this.logger.LogWarning("Found no episode markers for slug '{Slug}' in remote HTML", slug);
                NegativeCache[slug] = DateTime.UtcNow;
                return null;
            }

            // Assemble cached show data container
            var showData = new CachedShowData
            {
                Slug = slug,
                FetchedAt = DateTime.UtcNow,
            };

            foreach (var ep in episodes)
            {
                showData.Episodes[ep.Key] = ep.Value;
            }

            // Update in-memory dictionary
            MemoryCache[slug] = showData;

            // Persist to local disk cache asynchronously
            await this.SaveToDiskAsync(showData, cancellationToken).ConfigureAwait(false);

            this.logger.LogInformation("Successfully parsed and cached {Count} episodes for slug '{Slug}'", episodes.Count, slug);
            return showData;
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Failed to fetch anime filler markers for slug '{Slug}'", slug);
            return null;
        }
        finally
        {
            // Always release the concurrency semaphore
            RequestThrottle.Release();
        }
    }

    /// <summary>
    /// Reads and deserializes cached show data from local disk storage.
    /// </summary>
    /// <param name="slug">Normalized show slug.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Deserialized show data or null if absent or corrupt.</returns>
    private async Task<CachedShowData?> LoadFromDiskAsync(string slug, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(this.cacheDirectory, $"{slug}.json");

        // Verify that the cached JSON file exists on disk
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            // Open read stream to deserialize cached show data
            using var stream = File.OpenRead(filePath);
            var data = await JsonSerializer.DeserializeAsync<CachedShowData>(stream, IndentedJsonOptions, cancellationToken).ConfigureAwait(false);

            // -----------------------------------------------------------------
            // Validate Cached Episode Dictionary
            // -----------------------------------------------------------------
            // If the cache file is empty, missing episodes, or was serialized
            // prior to property setter enablement, invalidate and purge the file
            // so an upstream fetch is triggered instead of serving stale/empty data.
            // -----------------------------------------------------------------
            if (data == null || data.Episodes == null || data.Episodes.Count == 0)
            {
                this.logger.LogWarning("Cached anime markers file at {Path} contains no episodes; purging corrupted cache file", filePath);

                try
                {
                    File.Delete(filePath);
                }
                catch (Exception deleteEx)
                {
                    this.logger.LogTrace(deleteEx, "Failed to delete corrupted anime cache file {Path}", filePath);
                }

                return null;
            }

            return data;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to load cached anime markers from file: {Path}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Serializes and writes show data to disk storage.
    /// </summary>
    /// <param name="data">The show data instance to serialize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task tracking async completion.</returns>
    private async Task SaveToDiskAsync(CachedShowData data, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(this.cacheDirectory, $"{data.Slug}.json");

        try
        {
            using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, data, IndentedJsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to write anime markers cache to file: {Path}", filePath);
        }
    }
}
