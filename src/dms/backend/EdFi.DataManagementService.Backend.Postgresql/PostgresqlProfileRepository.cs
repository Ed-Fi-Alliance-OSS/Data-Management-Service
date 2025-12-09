// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Interface;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// PostgreSQL implementation of the Profile repository
/// </summary>
public class PostgresqlProfileRepository(
    NpgsqlDataSourceProvider _dataSourceProvider,
    ILogger<PostgresqlProfileRepository> _logger
) : IProfileRepository
{
    public async Task<ProfileInfo?> GetProfileByNameAsync(string profileName)
    {
        _logger.LogDebug("Retrieving profile by name: {ProfileName}", profileName);

        const string sql = @"
            SELECT Id, ProfileName, Description, ProfileDefinition::text, CreatedAt, UpdatedAt
            FROM dms.Profile
            WHERE ProfileName = @profileName";

        try
        {
            await using var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@profileName", profileName);

            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new ProfileInfo(
                    Id: reader.GetInt64(0),
                    ProfileName: reader.GetString(1),
                    Description: await reader.IsDBNullAsync(2) ? null : reader.GetString(2),
                    ProfileDefinition: reader.GetString(3),
                    CreatedAt: reader.GetDateTime(4),
                    UpdatedAt: reader.GetDateTime(5)
                );
            }

            return null;
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            _logger.LogError(ex, "Profile table does not exist. Please ensure database is deployed.");
            throw new InvalidOperationException("Profile table does not exist. Please ensure database is deployed.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error retrieving profile: {ProfileName}", profileName);
            throw new InvalidOperationException($"Failed to retrieve profile: {profileName}", ex);
        }
    }

    public async Task<IReadOnlyList<ProfileInfo>> GetAllProfilesAsync()
    {
        _logger.LogDebug("Retrieving all profiles");

        const string sql = @"
            SELECT Id, ProfileName, Description, ProfileDefinition::text, CreatedAt, UpdatedAt
            FROM dms.Profile
            ORDER BY ProfileName";

        try
        {
            await using var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            var profiles = new List<ProfileInfo>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                profiles.Add(new ProfileInfo(
                    Id: reader.GetInt64(0),
                    ProfileName: reader.GetString(1),
                    Description: await reader.IsDBNullAsync(2) ? null : reader.GetString(2),
                    ProfileDefinition: reader.GetString(3),
                    CreatedAt: reader.GetDateTime(4),
                    UpdatedAt: reader.GetDateTime(5)
                ));
            }

            return profiles;
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            _logger.LogError(ex, "Profile table does not exist. Please ensure database is deployed.");
            throw new InvalidOperationException("Profile table does not exist. Please ensure database is deployed.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error retrieving all profiles");
            throw new InvalidOperationException("Failed to retrieve all profiles", ex);
        }
    }

    public async Task<long> CreateProfileAsync(string profileName, string? description, string profileDefinition)
    {
        _logger.LogDebug("Creating profile: {ProfileName}", profileName);

        const string sql = @"
            INSERT INTO dms.Profile (ProfileName, Description, ProfileDefinition)
            VALUES (@profileName, @description, XMLPARSE(DOCUMENT @profileDefinition))
            RETURNING Id";

        try
        {
            await using var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@profileName", profileName);
            command.Parameters.AddWithValue("@description", description ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@profileDefinition", profileDefinition);

            var id = await command.ExecuteScalarAsync();
            return Convert.ToInt64(id);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            _logger.LogError(ex, "Profile with name '{ProfileName}' already exists", profileName);
            throw new InvalidOperationException($"Profile with name '{profileName}' already exists", ex);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            _logger.LogError(ex, "Profile table does not exist. Please ensure database is deployed.");
            throw new InvalidOperationException("Profile table does not exist. Please ensure database is deployed.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating profile: {ProfileName}", profileName);
            throw new InvalidOperationException($"Failed to create profile: {profileName}", ex);
        }
    }

    public async Task<bool> UpdateProfileAsync(string profileName, string? description, string profileDefinition)
    {
        _logger.LogDebug("Updating profile: {ProfileName}", profileName);

        const string sql = @"
            UPDATE dms.Profile
            SET Description = @description,
                ProfileDefinition = XMLPARSE(DOCUMENT @profileDefinition),
                UpdatedAt = NOW()
            WHERE ProfileName = @profileName";

        try
        {
            await using var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@profileName", profileName);
            command.Parameters.AddWithValue("@description", description ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@profileDefinition", profileDefinition);

            var rowsAffected = await command.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            _logger.LogError(ex, "Profile table does not exist. Please ensure database is deployed.");
            throw new InvalidOperationException("Profile table does not exist. Please ensure database is deployed.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating profile: {ProfileName}", profileName);
            throw new InvalidOperationException($"Failed to update profile: {profileName}", ex);
        }
    }

    public async Task<bool> DeleteProfileAsync(string profileName)
    {
        _logger.LogDebug("Deleting profile: {ProfileName}", profileName);

        const string sql = @"
            DELETE FROM dms.Profile
            WHERE ProfileName = @profileName";

        try
        {
            await using var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@profileName", profileName);

            var rowsAffected = await command.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            _logger.LogError(ex, "Profile table does not exist. Please ensure database is deployed.");
            throw new InvalidOperationException("Profile table does not exist. Please ensure database is deployed.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting profile: {ProfileName}", profileName);
            throw new InvalidOperationException($"Failed to delete profile: {profileName}", ex);
        }
    }

    public async Task<DateTime?> GetLatestUpdateTimestampAsync()
    {
        _logger.LogDebug("Retrieving latest profile update timestamp");

        const string sql = @"
            SELECT MAX(UpdatedAt)
            FROM dms.Profile";

        try
        {
            await using var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            var result = await command.ExecuteScalarAsync();
            return result == DBNull.Value ? null : (DateTime?)result;
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            _logger.LogError(ex, "Profile table does not exist. Please ensure database is deployed.");
            throw new InvalidOperationException("Profile table does not exist. Please ensure database is deployed.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error retrieving latest profile update timestamp");
            throw new InvalidOperationException("Failed to retrieve latest profile update timestamp", ex);
        }
    }
}
