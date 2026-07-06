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
    /// Gets the settings backup for the currently authenticated user.
    /// </summary>
    /// <response code="200">Returns the backup details if found.</response>
    /// <response code="404">If no backup exists for the user.</response>
    /// <returns>A user backup representation.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<object> GetBackup()
    {
        // Retrieve the authenticated User ID claim from the security claims principal
        var userIdStr = this.User.Claims.FirstOrDefault(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrEmpty(userIdStr))
        {
            this.logger.LogWarning("GetBackup failed: User ID not found in security claims.");
            return this.Unauthorized("User ID not found in claims.");
        }

        // Fetch the backup from the configuration
        var backup = Plugin.Instance?.Configuration.Backups
            .FirstOrDefault(b => b.UserId.Equals(userIdStr, StringComparison.OrdinalIgnoreCase));

        if (backup == null)
        {
            this.logger.LogInformation("No backup found for User ID {UserId}", userIdStr);
            return this.NotFound("No backup found for this user.");
        }

        return this.Ok(new
        {
            UserId = backup.UserId,
            Username = backup.Username,
            DateCreated = backup.DateCreated,
            Settings = backup.Settings,
        });
    }

    /// <summary>
    /// Creates or updates the settings backup for the currently authenticated user.
    /// </summary>
    /// <param name="request">The backup request containing the serialized settings payload.</param>
    /// <response code="200">If the backup was successfully created or updated.</response>
    /// <returns>HTTP 200 OK.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
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

        // Resolve the username directly from the security identity claims (avoiding dependency on IUserManager and User entity class)
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
            // Find existing backup to overwrite or append a new entry
            var existing = backups.FirstOrDefault(b => b.UserId.Equals(userIdStr, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                this.logger.LogInformation("Updating existing settings backup for user {Username} ({UserId})", username, userIdStr);
                existing.Settings = request.Settings;
                existing.DateCreated = DateTime.UtcNow;
                existing.Username = username;
            }
            else
            {
                this.logger.LogInformation("Creating new settings backup for user {Username} ({UserId})", username, userIdStr);
                backups.Add(new UserBackup
                {
                    UserId = userIdStr,
                    Username = username,
                    DateCreated = DateTime.UtcNow,
                    Settings = request.Settings,
                });
            }

            // Write updated configuration snapshot back to the filesystem
            Plugin.Instance?.SaveConfiguration();
        }

        return this.Ok();
    }

    /// <summary>
    /// Deletes the settings backup for the currently authenticated user.
    /// </summary>
    /// <response code="200">If the backup was deleted successfully.</response>
    /// <response code="404">If no backup was found for this user.</response>
    /// <returns>HTTP 200 OK.</returns>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteBackup()
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
            var existing = backups.FirstOrDefault(b => b.UserId.Equals(userIdStr, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                this.logger.LogWarning("DeleteBackup failed: No backup found to delete for User ID {UserId}", userIdStr);
                return this.NotFound("No backup found for this user.");
            }

            this.logger.LogInformation("Deleting settings backup for user {Username} ({UserId})", existing.Username, userIdStr);
            backups.Remove(existing);
            Plugin.Instance?.SaveConfiguration();
        }

        return this.Ok();
    }
}
