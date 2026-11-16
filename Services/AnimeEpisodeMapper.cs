// <copyright file="AnimeEpisodeMapper.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

#pragma warning disable CA1308 // Normalize strings to uppercase - standard language codes are lowercase

namespace Litefin.Plugin.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Litefin.Plugin.Models;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

/// <summary>
/// Maps Jellyfin library episodes to canonical anime markers and resolves audio stream categories.
/// Handles continuous absolute numbering as well as season-offset library configurations.
/// </summary>
public static class AnimeEpisodeMapper
{
    /// <summary>
    /// Processes a collection of Jellyfin episodes against parsed show markers.
    /// Determines canon kind, recap flag, and audio classification for each episode and season.
    /// </summary>
    /// <param name="episodes">Collection of episodes belonging to the series.</param>
    /// <param name="showData">Parsed canonical markers from the reference provider, or null.</param>
    /// <param name="getStreams">Optional provider delegate to resolve media streams from the media source manager.</param>
    /// <returns>Pair containing mapped episode verdicts and season verdicts.</returns>
    public static (Dictionary<string, EpisodeVerdict> Episodes, Dictionary<string, SeasonVerdict> Seasons) MapEpisodes(
        IReadOnlyList<Episode> episodes,
        CachedShowData? showData,
        Func<Episode, IReadOnlyList<MediaStream>?>? getStreams = null)
    {
        var episodeResults = new Dictionary<string, EpisodeVerdict>(StringComparer.OrdinalIgnoreCase);
        var seasonResults = new Dictionary<string, SeasonVerdict>(StringComparer.OrdinalIgnoreCase);

        // Guard against empty episode lists
        if (episodes == null || episodes.Count == 0)
        {
            return (episodeResults, seasonResults);
        }

        // Build season offset table for seasonal libraries (e.g. S01E01..61, S02E01..16)
        var seasonOffsets = BuildSeasonOffsets(episodes);

        // Track audio verdicts per season to compute aggregated season classifications
        var seasonAudioTracking = new Dictionary<Guid, List<string>>();

        // Process each episode sequentially
        foreach (var episode in episodes)
        {
            var epKey = episode.Id.ToString("N", CultureInfo.InvariantCulture);

            // -----------------------------------------------------------------
            // Resolve Media Streams for Audio Classification
            // -----------------------------------------------------------------
            // Prefer streams supplied via the media source manager delegate to guarantee
            // relational database rows are queried even when GetItemList bypassed them.
            // -----------------------------------------------------------------
            var streams = getStreams != null ? getStreams(episode) : null;
            var audioVerdict = ClassifyAudioStreams(episode, streams);

            // Record audio in season tracking map
            if (episode.SeasonId != Guid.Empty && !string.IsNullOrEmpty(audioVerdict))
            {
                if (!seasonAudioTracking.TryGetValue(episode.SeasonId, out var audioList))
                {
                    audioList = new List<string>();
                    seasonAudioTracking[episode.SeasonId] = audioList;
                }

                audioList.Add(audioVerdict);
            }

            // -----------------------------------------------------------------
            // Resolve Canonical Episode Classification (Kind & Recap)
            // -----------------------------------------------------------------
            // When showData is populated, the series was successfully matched
            // to a recognized anime entry in the AnimeFillerList directory.
            // -----------------------------------------------------------------
            string? kind = null;
            var isRecap = false;

            if (showData != null)
            {
                // Compute the absolute episode sequence number candidate
                var candidateNumber = ResolveAbsoluteEpisodeNumber(episode, seasonOffsets, showData.Episodes);

                // Check if candidate number exists in the parsed marker dictionary
                if (candidateNumber.HasValue && showData.Episodes.TryGetValue(candidateNumber.Value, out var marker))
                {
                    // Map provider enum to standardized client contract strings
                    kind = marker.Kind switch
                    {
                        AnimeEpisodeKind.Filler => "Filler",
                        AnimeEpisodeKind.Mixed => "Mixed",
                        AnimeEpisodeKind.AnimeCanon => "AnimeCanon",
                        _ => "MangaCanon",
                    };

                    isRecap = marker.Recap;
                }
                else
                {
                    // ---------------------------------------------------------
                    // Default Canon Classification for Unindexed Episodes
                    // ---------------------------------------------------------
                    // In verified anime series, any standard episode that is not
                    // explicitly identified as filler or anime-original is canon
                    // (MangaCanon) by definition. Defaulting to MangaCanon ensures
                    // the kind property is never omitted or null for confirmed anime.
                    // ---------------------------------------------------------
                    kind = "MangaCanon";
                }
            }

            // Store episode verdict
            episodeResults[epKey] = new EpisodeVerdict
            {
                Kind = kind,
                Recap = isRecap,
                Audio = audioVerdict,
            };
        }

        // Aggregate season audio classifications
        foreach (var (seasonId, audioList) in seasonAudioTracking)
        {
            var seasonKey = seasonId.ToString("N", CultureInfo.InvariantCulture);
            var aggregated = AggregateSeasonAudio(audioList);

            if (!string.IsNullOrEmpty(aggregated))
            {
                seasonResults[seasonKey] = new SeasonVerdict
                {
                    Audio = aggregated,
                };
            }
        }

        return (episodeResults, seasonResults);
    }

    /// <summary>
    /// Analyzes audio streams on an episode item to categorize it as Subbed, Dubbed, or Subbed/Dubbed.
    /// </summary>
    /// <param name="episode">The Jellyfin episode entity.</param>
    /// <param name="mediaStreams">Optional explicit collection of media streams from the media source manager.</param>
    /// <returns>Audio classification string or null if unresolvable.</returns>
    public static string? ClassifyAudioStreams(Episode episode, IReadOnlyList<MediaStream>? mediaStreams = null)
    {
        // Guard against null episode
        if (episode == null)
        {
            return null;
        }

        // ---------------------------------------------------------------------
        // Media Stream Candidate Selection
        // ---------------------------------------------------------------------
        // First check explicit streams passed from IMediaSourceManager;
        // fall back to episode entity property if streams were already populated.
        // ---------------------------------------------------------------------
        var streams = mediaStreams ?? episode.GetMediaStreams();
        if (streams == null || streams.Count == 0)
        {
            return null;
        }

        var hasJapaneseAudio = false;
        var hasDubbedAudio = false;

        // Iterate through all media streams inspecting audio tracks
        foreach (var stream in streams)
        {
            if (stream.Type != MediaStreamType.Audio)
            {
                continue;
            }

            var lang = (stream.Language ?? string.Empty).Trim().ToLowerInvariant();
            var title = (stream.Title ?? string.Empty).Trim().ToLowerInvariant();

            // Detect Japanese language tracks
            if (lang == "ja" || lang == "jpn" || lang == "japanese" || title.Contains("japanese", StringComparison.Ordinal))
            {
                hasJapaneseAudio = true;
            }
            else if (lang == "en" || lang == "eng" || lang == "english" || title.Contains("english", StringComparison.Ordinal) || title.Contains("dub", StringComparison.Ordinal))
            {
                // Detect English or designated dub audio tracks
                hasDubbedAudio = true;
            }
            else if (!string.IsNullOrEmpty(lang) && lang != "und")
            {
                // Any other explicit non-Japanese language track indicates a dubbed release
                hasDubbedAudio = true;
            }
        }

        // Formulate final audio verdict
        if (hasJapaneseAudio && hasDubbedAudio)
        {
            return "Subbed/Dubbed";
        }

        if (hasJapaneseAudio)
        {
            return "Subbed";
        }

        if (hasDubbedAudio)
        {
            return "Dubbed";
        }

        return null;
    }

    /// <summary>
    /// Builds a lookup of cumulative episode count prior to each season index.
    /// Used to convert season-relative episode numbers to absolute episode numbers.
    /// </summary>
    /// <param name="episodes">All episodes in the series.</param>
    /// <returns>Map of season number to cumulative previous episode count.</returns>
    private static Dictionary<int, int> BuildSeasonOffsets(IReadOnlyList<Episode> episodes)
    {
        var offsets = new Dictionary<int, int>();

        // Group regular season episodes (ignoring specials in season 0)
        var seasonGroups = episodes
            .Where(e => (e.ParentIndexNumber ?? 1) > 0 && e.IndexNumber.HasValue)
            .GroupBy(e => e.ParentIndexNumber ?? 1)
            .OrderBy(g => g.Key)
            .ToList();

        var cumulative = 0;
        foreach (var group in seasonGroups)
        {
            offsets[group.Key] = cumulative;
            cumulative += group.Count();
        }

        return offsets;
    }

    /// <summary>
    /// Resolves the absolute sequence episode number for an episode item.
    /// Evaluates direct index numbers and cumulative season offset calculations.
    /// </summary>
    /// <param name="episode">The episode item.</param>
    /// <param name="seasonOffsets">Map of cumulative season offsets.</param>
    /// <param name="knownMarkers">Parsed reference marker dictionary.</param>
    /// <returns>Absolute episode number candidate, or null.</returns>
    private static int? ResolveAbsoluteEpisodeNumber(
        Episode episode,
        Dictionary<int, int> seasonOffsets,
        Dictionary<int, AnimeMarkerEpisode> knownMarkers)
    {
        // Require at least an episode index number
        if (!episode.IndexNumber.HasValue)
        {
            return null;
        }

        var epIndex = episode.IndexNumber.Value;
        var seasonNum = episode.ParentIndexNumber ?? 1;

        // Candidate 1: For Season 1 or absolute numbering (> 100), check index directly
        if (seasonNum <= 1 || epIndex > 100)
        {
            if (knownMarkers.ContainsKey(epIndex))
            {
                return epIndex;
            }
        }

        // Candidate 2: Try cumulative season offset for seasonal numbering schemes
        if (seasonNum > 1 && seasonOffsets.TryGetValue(seasonNum, out var offset))
        {
            var calculatedAbsolute = offset + epIndex;
            if (knownMarkers.ContainsKey(calculatedAbsolute))
            {
                return calculatedAbsolute;
            }
        }

        // Candidate 3: Check if index directly exists in known markers
        if (knownMarkers.ContainsKey(epIndex))
        {
            return epIndex;
        }

        // Default fallback to index number
        return epIndex;
    }

    /// <summary>
    /// Aggregates multiple episode audio verdicts into a coherent season-level verdict.
    /// </summary>
    /// <param name="audioVerdicts">List of episode audio classifications.</param>
    /// <returns>Aggregated classification string.</returns>
    private static string? AggregateSeasonAudio(List<string> audioVerdicts)
    {
        if (audioVerdicts.Count == 0)
        {
            return null;
        }

        // If any episode offers both sub and dub, or if both exist in the season, return Subbed/Dubbed
        var hasSub = audioVerdicts.Any(v => v.Contains("Subbed", StringComparison.Ordinal));
        var hasDub = audioVerdicts.Any(v => v.Contains("Dubbed", StringComparison.Ordinal));

        if (hasSub && hasDub)
        {
            return "Subbed/Dubbed";
        }

        if (hasSub)
        {
            return "Subbed";
        }

        if (hasDub)
        {
            return "Dubbed";
        }

        return null;
    }
}
