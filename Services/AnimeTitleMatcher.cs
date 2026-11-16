// <copyright file="AnimeTitleMatcher.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

#pragma warning disable CA1308 // Normalize strings to uppercase - lowercase required for slugs

namespace Litefin.Plugin.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities.TV;

/// <summary>
/// Normalizes anime show titles and resolves canonical slugs for external marker providers.
/// Handles Romanized alternatives, English localizations, and common punctuation variations.
/// </summary>
public static class AnimeTitleMatcher
{
    // Dictionary mapping normalized titles and known aliases to canonical AnimeFillerList slugs
    private static readonly Dictionary<string, string> KnownTitleToSlugMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // One Piece and primary Shonen franchises
        { "one piece", "one-piece" },
        { "naruto", "naruto" },
        { "naruto shippuden", "naruto-shippuden" },
        { "naruto shippuuden", "naruto-shippuden" },
        { "bleach", "bleach" },
        { "bleach thousand year blood war", "bleach-thousand-year-blood-war" },
        { "bleach sennen kessen hen", "bleach-thousand-year-blood-war" },
        { "dragon ball", "dragon-ball" },
        { "dragon ball z", "dragon-ball-z" },
        { "dragon ball super", "dragon-ball-super" },
        { "dragon ball gt", "dragon-ball-gt" },
        { "boruto", "boruto-naruto-next-generations" },
        { "boruto naruto next generations", "boruto-naruto-next-generations" },

        // Popular modern and classic series
        { "black clover", "black-clover" },
        { "fairy tail", "fairy-tail" },
        { "detective conan", "detective-conan" },
        { "case closed", "detective-conan" },
        { "meitantei conan", "detective-conan" },
        { "gintama", "gintama" },
        { "jujutsu kaisen", "jujutsu-kaisen" },
        { "demon slayer", "demon-slayer-kimetsu-no-yaiba" },
        { "demon slayer kimetsu no yaiba", "demon-slayer-kimetsu-no-yaiba" },
        { "kimetsu no yaiba", "demon-slayer-kimetsu-no-yaiba" },
        { "attack on titan", "attack-titan" },
        { "shingeki no kyojin", "attack-titan" },
        { "my hero academia", "my-hero-academia" },
        { "boku no hero academia", "my-hero-academia" },
        { "hunter x hunter", "hunter-x-hunter-2011" },
        { "hunter x hunter 2011", "hunter-x-hunter-2011" },
        { "inuyasha", "inuyasha" },
        { "rurouni kenshin", "rurouni-kenshin" },
        { "yu gi oh", "yu-gi-oh" },
        { "yu gi oh duel monsters", "yu-gi-oh" },
        { "sailor moon", "sailor-moon" },
        { "pokemon", "pokemon" },
        { "death note", "death-note" },
        { "fullmetal alchemist", "fullmetal-alchemist" },
        { "fullmetal alchemist brotherhood", "fullmetal-alchemist-brotherhood" },
        { "hagane no renkinjutsushi", "fullmetal-alchemist-brotherhood" },
        { "cowboy bebop", "cowboy-bebop" },
        { "sword art online", "sword-art-online" },
        { "steins gate", "steinsgate" },
        { "code geass", "code-geass-lelouch-rebellion" },
        { "code geass lelouch of the rebellion", "code-geass-lelouch-rebellion" },
        { "tokyo ghoul", "tokyo-ghoul" },
        { "haikyuu", "haikyu" },
        { "haikyu", "haikyu" },
        { "mob psycho 100", "mob-psycho-100" },
        { "dr stone", "dr-stone" },
        { "vinland saga", "vinland-saga" },
        { "chainsaw man", "chainsaw-man" },
        { "spy x family", "spy-x-family" },
        { "solo leveling", "solo-leveling" },
        { "blue exorcist", "blue-exorcist" },
        { "ao no exorcist", "blue-exorcist" },
        { "soul eater", "soul-eater" },
        { "fate zero", "fatezero" },
        { "fate stay night", "fatestay-night" },
        { "overlord", "overlord" },
        { "re zero", "rezero-starting-life-another-world" },
        { "re zero starting life in another world", "rezero-starting-life-another-world" },
    };

    // Regex cleaning invalid characters for slug formatting
    private static readonly Regex SlugCleanRegex = new(@"[^a-z0-9\-]", RegexOptions.Compiled);

    // Regex collapsing multiple consecutive hyphens into a single hyphen
    private static readonly Regex MultipleHyphensRegex = new(@"-{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Resolves the canonical slug identifier for a given series title.
    /// Checks exact dictionary matches first, then falls back to normalized slug generation.
    /// </summary>
    /// <param name="seriesName">Original name of the television series.</param>
    /// <returns>Resolved slug string or null if unresolved.</returns>
    public static string? ResolveSlug(string? seriesName)
    {
        // Guard against empty or null title inputs
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        // Clean and normalize the title text
        var normalized = NormalizeTitle(seriesName);

        // Check if an explicit alias mapping exists for this title
        if (KnownTitleToSlugMap.TryGetValue(normalized, out var mappedSlug))
        {
            return mappedSlug;
        }

        // Also check if any known dictionary key matches as a prefix
        foreach (var entry in KnownTitleToSlugMap)
        {
            if (normalized.StartsWith(entry.Key, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        // Fall back to algorithmic slug generation from the normalized title
        return GenerateSlug(seriesName);
    }

    /// <summary>
    /// Determines whether the given series is likely an anime title based on metadata indicators.
    /// Examines provider IDs, genres, tags, title aliases, and media file paths.
    /// </summary>
    /// <param name="series">The Jellyfin series item instance.</param>
    /// <returns>True if the series matches anime identification criteria; otherwise, false.</returns>
    public static bool IsAnimeCandidate(Series series)
    {
        // Validate parameter presence
        if (series == null)
        {
            return false;
        }

        // First check: Positive provider ID matches from anime metadata agents
        if (series.ProviderIds != null &&
            (series.ProviderIds.ContainsKey("AniDB") ||
             series.ProviderIds.ContainsKey("AniList") ||
             series.ProviderIds.ContainsKey("MyAnimeList") ||
             series.ProviderIds.ContainsKey("Kitsu") ||
             series.ProviderIds.ContainsKey("AniSearch")))
        {
            return true;
        }

        // Second check: Direct inclusion in our curated anime dictionary
        var normalized = NormalizeTitle(series.Name);
        if (KnownTitleToSlugMap.ContainsKey(normalized))
        {
            return true;
        }

        // Third check: Tags explicitly marking anime content
        if (series.Tags != null)
        {
            foreach (var tag in series.Tags)
            {
                if (string.Equals(tag, "Anime", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        // Fourth check: Genres indicating animation or anime designation
        if (series.Genres != null)
        {
            foreach (var genre in series.Genres)
            {
                if (string.Equals(genre, "Anime", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        // Fifth check: Directory path containing dedicated anime library folders
        var path = series.Path;
        if (!string.IsNullOrEmpty(path))
        {
            var lowerPath = path.ToLowerInvariant();
            if (lowerPath.Contains("/anime/", StringComparison.Ordinal) ||
                lowerPath.Contains("\\anime\\", StringComparison.Ordinal) ||
                lowerPath.EndsWith("\\anime", StringComparison.Ordinal) ||
                lowerPath.EndsWith("/anime", StringComparison.Ordinal))
            {
                return true;
            }
        }

        // Return false when none of the anime indicators are met
        return false;
    }

    /// <summary>
    /// Strips non-alphanumeric punctuation and collapses whitespace for comparison.
    /// </summary>
    /// <param name="input">Raw string to normalize.</param>
    /// <returns>Normalized title string.</returns>
    public static string NormalizeTitle(string? input)
    {
        // Return empty string on null or blank input
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        // Normalize unicode characters and remove accents
        var normalizedString = input.Normalize(NormalizationForm.FormD);
        var stringBuilder = new StringBuilder(normalizedString.Length);

        // Keep alphanumeric characters and convert punctuation to spaces
        foreach (var c in normalizedString)
        {
            var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != UnicodeCategory.NonSpacingMark)
            {
                if (char.IsLetterOrDigit(c))
                {
                    stringBuilder.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    stringBuilder.Append(' ');
                }
            }
        }

        // Collapse duplicate whitespace tokens into single spaces
        var cleaned = stringBuilder.ToString();
        var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Formats a human title into a URL slug compatible with AnimeFillerList.
    /// </summary>
    /// <param name="title">Human readable show title.</param>
    /// <returns>Hyphenated slug string.</returns>
    public static string GenerateSlug(string? title)
    {
        // Fall back to empty string when title is absent
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        // Lowercase and replace spaces with hyphens
        var slug = title.ToLowerInvariant().Trim();
        slug = slug.Replace(' ', '-');
        slug = slug.Replace('_', '-');

        // Remove any characters that are not lowercase alphanumeric or hyphens
        slug = SlugCleanRegex.Replace(slug, string.Empty);

        // Collapse repeated hyphens and trim extremities
        slug = MultipleHyphensRegex.Replace(slug, "-");
        return slug.Trim('-');
    }
}
