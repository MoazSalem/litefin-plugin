// <copyright file="BackupController.cs" company="Jellyfin Project">
// Copyright (c) Jellyfin Project. All rights reserved.
// </copyright>

#pragma warning disable CA1848 // Use LoggerMessage delegates for performance

namespace Litefin.Plugin.Controllers;

using System;
using System.Linq;
using System.Net.Mime;
using System.Security.Claims;
using Litefin.Plugin.Configuration;
using Litefin.Plugin.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

/// <summary>
/// REST API controller that exposes endpoints to back up and restore application settings.
/// </summary>
[ApiController]
[Authorize]
[Route("Litefin/[controller]")]
[Produces(MediaTypeNames.Application.Json)]
public class BackupController : ControllerBase
{
    private readonly ILogger<BackupController> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackupController"/> class.
    /// </summary>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public BackupController(ILoggerFactory loggerFactory)
    {
        this.logger = loggerFactory.CreateLogger<BackupController>();
    }

    /// <summary>
    /// Gets all settings backups stored on the server to allow sharing between users.
    /// </summary>
    /// <response code="200">Returns the full list of backups.</response>
    /// <returns>A collection of all user backups.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetBackups()
    {
        // Simply return all backups currently registered in the plugin configuration
        var backups = Plugin.Instance?.Configuration.Backups;

        if (backups == null)
        {
            return this.Ok(Array.Empty<object>());
        }

        // Return a projection to the client of all backups
        return this.Ok(backups.Select(b => new
        {
            Id = b.Id,
            UserId = b.UserId,
            Username = b.Username,
            DeviceId = b.DeviceId,
            DeviceName = b.DeviceName,
            Name = b.Name,
            DateCreated = b.DateCreated,
            Settings = b.Settings,
            AppVersion = b.AppVersion,
            Platform = b.Platform,
        }));
    }

    /// <summary>
    /// Creates a new settings backup or updates (overwrites) an existing backup snapshot.
    /// </summary>
    /// <param name="request">The backup request containing the settings payload and metadata.</param>
    /// <response code="200">If the backup was successfully created or updated.</response>
    /// <response code="403">If attempting to overwrite a backup owned by another user.</response>
    /// <returns>HTTP 200 OK.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult SaveBackup([FromBody] BackupRequest request)
    {
        // Retrieve the authenticated User ID claim from the security claims principal
        var userIdStr = this.User.Claims.FirstOrDefault(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrEmpty(userIdStr))
        {
            this.logger.LogWarning("SaveBackup failed: User ID not found in security claims.");
            return this.Unauthorized("User ID not found in claims.");
        }

        if (request == null || string.IsNullOrEmpty(request.Settings))
        {
            return this.BadRequest("Settings data is required.");
        }

        // Resolve the username directly from the security identity claims
        var username = this.User.Identity?.Name;
        if (string.IsNullOrEmpty(username))
        {
            username = this.User.FindFirst(ClaimTypes.Name)?.Value;
        }

        if (string.IsNullOrEmpty(username))
        {
            username = "Unknown";
        }

        var backups = Plugin.Instance?.Configuration.Backups;
        if (backups != null)
        {
            UserBackup? existing = null;
            if (!string.IsNullOrEmpty(request.Id))
            {
                existing = backups.FirstOrDefault(b => b.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase));
            }

            if (existing != null)
            {
                // Verify backup ownership: only the user who created it can overwrite it
                if (!existing.UserId.Equals(userIdStr, StringComparison.OrdinalIgnoreCase))
                {
                    this.logger.LogWarning("User {UserId} attempted to overwrite backup {BackupId} owned by {OwnerId}", userIdStr, existing.Id, existing.UserId);
                    return this.Forbid();
                }

                this.logger.LogInformation("Updating existing settings backup {BackupId} for user {Username} ({UserId})", existing.Id, username, userIdStr);
                existing.Settings = request.Settings;
                existing.DateCreated = DateTime.UtcNow;
                existing.Username = username;
                existing.DeviceId = request.DeviceId;
                existing.DeviceName = request.DeviceName;
                existing.AppVersion = request.AppVersion ?? string.Empty;
                existing.Platform = request.Platform ?? string.Empty;
                if (!string.IsNullOrEmpty(request.Name))
                {
                    existing.Name = request.Name;
                }
            }
            else
            {
                // Create a new settings backup snapshot
                var backupId = string.IsNullOrEmpty(request.Id) ? Guid.NewGuid().ToString() : request.Id;

                var backupName = request.Name ?? string.Empty;

                this.logger.LogInformation("Creating new settings backup {BackupId} named '{BackupName}' for user {Username} ({UserId})", backupId, backupName, username, userIdStr);
                backups.Add(new UserBackup
                {
                    Id = backupId,
                    UserId = userIdStr,
                    Username = username,
                    DeviceId = request.DeviceId,
                    DeviceName = request.DeviceName,
                    Name = backupName,
                    DateCreated = DateTime.UtcNow,
                    Settings = request.Settings,
                    AppVersion = request.AppVersion ?? string.Empty,
                    Platform = request.Platform ?? string.Empty,
                });
            }

            // Write updated configuration snapshot back to the filesystem
            Plugin.Instance?.SaveConfiguration();
        }

        return this.Ok();
    }

    /// <summary>
    /// Deletes a specific settings backup snapshot by its ID.
    /// </summary>
    /// <param name="id">The unique identifier of the backup snapshot to delete.</param>
    /// <response code="200">If the backup was deleted successfully.</response>
    /// <response code="403">If attempting to delete a backup owned by another user.</response>
    /// <response code="404">If no backup was found with the specified ID.</response>
    /// <returns>HTTP 200 OK.</returns>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteBackup([FromRoute] string id)
    {
        // Retrieve the authenticated User ID claim from the security claims principal
        var userIdStr = this.User.Claims.FirstOrDefault(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrEmpty(userIdStr))
        {
            this.logger.LogWarning("DeleteBackup failed: User ID not found in security claims.");
            return this.Unauthorized("User ID not found in claims.");
        }

        var backups = Plugin.Instance?.Configuration.Backups;
        if (backups != null)
        {
            var existing = backups.FirstOrDefault(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                this.logger.LogWarning("DeleteBackup failed: No backup found to delete with ID {BackupId}", id);
                return this.NotFound("No backup found with that ID.");
            }

            // Verify backup ownership: only the user who created it can delete it
            if (!existing.UserId.Equals(userIdStr, StringComparison.OrdinalIgnoreCase))
            {
                this.logger.LogWarning("User {UserId} attempted to delete backup {BackupId} owned by {OwnerId}", userIdStr, existing.Id, existing.UserId);
                return this.Forbid();
            }

            this.logger.LogInformation("Deleting settings backup {BackupId} for user {Username} ({UserId})", existing.Id, existing.Username, userIdStr);
            backups.Remove(existing);
            Plugin.Instance?.SaveConfiguration();
        }

        return this.Ok();
    }
}
