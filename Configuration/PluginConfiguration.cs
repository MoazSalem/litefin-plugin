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
        // Use the recommended Collection type for StyleCop / CA compliance
        this.Backups = new Collection<UserBackup>();
    }

    /// <summary>
    /// Gets the collection of user-specific client settings backups.
    /// This collection is serialized automatically by Jellyfin as part of the plugin configuration.
    /// </summary>
    public Collection<UserBackup> Backups { get; }
}
