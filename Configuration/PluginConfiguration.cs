// <copyright file="PluginConfiguration.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

namespace Litefin.Plugin.Configuration;

using System.Collections.ObjectModel;
using Litefin.Plugin.Models;
using MediaBrowser.Model.Plugins;

/// <summary>
/// Plugin configuration for Litefin.
/// Holds system-wide settings as well as user-specific backups.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// Sets default values for settings and initializes lists.
    /// </summary>
    public PluginConfiguration()
    {
        this.Backups = new Collection<UserBackup>();
        this.SeerrUrl = string.Empty;
        this.SeerrApiKey = string.Empty;
    }

    /// <summary>
    /// Gets or sets the collection of user-specific client settings backups.
    /// This collection is serialized automatically by Jellyfin as part of the plugin configuration.
    /// </summary>
#pragma warning disable CA2227 // Collection properties should be read-only - setter required for JSON deserialization
    public Collection<UserBackup> Backups { get; set; }
#pragma warning restore CA2227

    /// <summary>
    /// Gets or sets the base URL of the server-wide Seerr instance.
    /// </summary>
#pragma warning disable CA1056 // String is required for Jellyfin's editable plugin configuration field
    public string SeerrUrl { get; set; }
#pragma warning restore CA1056

    /// <summary>
    /// Gets or sets the Seerr API key. This value is only managed through the
    /// administrator-only Jellyfin plugin configuration page.
    /// </summary>
    public string SeerrApiKey { get; set; }
}
