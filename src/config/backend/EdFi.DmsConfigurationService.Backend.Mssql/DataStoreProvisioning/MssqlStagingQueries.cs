// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// The catalog reads and the guarded drop of the SQL Server staging stage (6.4 steps 6 and 7, 6.7): the state of
/// the database under the staging name with its primary file, the session's own id, the server's default
/// directories, and a drop that runs only while the name still holds the instance the caller verified.
/// </summary>
internal static class MssqlStagingQueries
{
    public const string RestoringState = "RESTORING";

    public const string OnlineState = "ONLINE";

    private const string StateQuery =
        "SELECT d.database_id, d.state_desc, d.create_date, (SELECT f.physical_name FROM sys.master_files f WHERE f.database_id = d.database_id AND f.file_id = 1) FROM sys.databases d WHERE d.name = @name;";

    private const string OwnSessionQuery =
        "SELECT CONVERT(int, @@SPID), (SELECT s.login_time FROM sys.dm_exec_sessions s WHERE s.session_id = @@SPID);";

    private const string DefaultPathsQuery =
        "SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultDataPath')), CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultLogPath'));";

    /// <summary>What the guarded drop did.</summary>
    public enum DropVerdict
    {
        /// <summary>The name no longer held the verified instance; nothing was dropped.</summary>
        GuardFailed = 0,

        Dropped = 1,

        /// <summary>The drop raised an error, returned as its number and severity.</summary>
        Refused = 2,
    }

    /// <summary>
    /// One batch: the name, the <c>database_id</c>, and the primary file are checked together, and only then is the
    /// database dropped; an error of the drop is caught and returned as a number, so the guard and the drop are one
    /// statement apart on the server. The name is quoted, never a parameter (<c>DROP DATABASE</c> takes none).
    /// </summary>
    public static string GuardedDrop(DmsDatabaseName staging) =>
        $"""
            IF EXISTS (SELECT 1 FROM sys.databases d WHERE d.name = @name AND d.database_id = @id AND (SELECT f.physical_name FROM sys.master_files f WHERE f.database_id = d.database_id AND f.file_id = 1) = @file)
            BEGIN
                BEGIN TRY
                    DROP DATABASE {MssqlIdentifiers.Quote(staging.Value)};
                    SELECT CONVERT(int, 1), CONVERT(int, NULL), CONVERT(int, NULL);
                END TRY
                BEGIN CATCH
                    SELECT CONVERT(int, 2), ERROR_NUMBER(), ERROR_SEVERITY();
                END CATCH
            END
            ELSE
                SELECT CONVERT(int, 0), CONVERT(int, NULL), CONVERT(int, NULL);
            """;

    /// <summary>The database under <paramref name="name"/> with its state and primary file, or null when the name is free.</summary>
    public static async Task<MssqlStagingState?> ReadStateAsync(
        SqlConnection connection,
        DmsDatabaseName name,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = Command(StateQuery, connection, commandTimeoutSeconds);
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = name.Value });
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        int databaseId = reader.GetInt32(0);
        string state = reader.GetString(1);
        DateTime createDate = await reader.GetFieldValueAsync<DateTime>(2, cancellationToken);
        string primaryFile = await reader.IsDBNullAsync(3, cancellationToken)
            ? string.Empty
            : reader.GetString(3);
        return new MssqlStagingState(databaseId, state, AsUtc(createDate), primaryFile);
    }

    /// <summary>The <c>session_id</c> and <c>login_time</c> of the connection's own session (D-7b).</summary>
    public static async Task<DmsServerSessionId> ReadOwnSessionAsync(
        SqlConnection connection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = Command(OwnSessionQuery, connection, commandTimeoutSeconds);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The own-session query returned no row.");
        }

        int spid = reader.GetInt32(0);
        DateTime loginTime = await reader.GetFieldValueAsync<DateTime>(1, cancellationToken);
        return new DmsServerSessionId(spid, AsUtc(loginTime));
    }

    /// <summary><c>InstanceDefaultDataPath</c> and <c>InstanceDefaultLogPath</c>, each null when the server reports none.</summary>
    public static async Task<(string? DataPath, string? LogPath)> ReadDefaultPathsAsync(
        SqlConnection connection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = Command(DefaultPathsQuery, connection, commandTimeoutSeconds);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The default-paths query returned no row.");
        }

        string? dataPath = await reader.IsDBNullAsync(0, cancellationToken) ? null : reader.GetString(0);
        string? logPath = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1);
        return (dataPath, logPath);
    }

    /// <summary>Runs <see cref="GuardedDrop"/> for the verified <paramref name="state"/> under <paramref name="staging"/>.</summary>
    public static async Task<(DropVerdict Verdict, int? ErrorNumber, byte ErrorSeverity)> DropAsync(
        SqlConnection connection,
        DmsDatabaseName staging,
        MssqlStagingState state,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = Command(GuardedDrop(staging), connection, commandTimeoutSeconds);
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = staging.Value });
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = state.DatabaseId });
        command.Parameters.Add(
            new SqlParameter("@file", SqlDbType.NVarChar, 260) { Value = state.PrimaryFile }
        );
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The guarded drop returned no row.");
        }

        DropVerdict verdict = (DropVerdict)reader.GetInt32(0);
        int? number = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetInt32(1);
        byte severity = await reader.IsDBNullAsync(2, cancellationToken)
            ? (byte)0
            : Convert.ToByte(reader.GetInt32(2), CultureInfo.InvariantCulture);
        return (verdict, number, severity);
    }

    private static SqlCommand Command(string sql, SqlConnection connection, int commandTimeoutSeconds) =>
        new(sql, connection) { CommandTimeout = commandTimeoutSeconds };

    /// <summary>A catalog <c>datetime</c> as an offset value without conversion, as the engine's inspection reads it.</summary>
    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);
}

/// <summary>
/// The database under a staging name: its <c>database_id</c>, <c>state_desc</c>, <c>create_date</c>, and the
/// primary file's <c>physical_name</c> (empty when the caller cannot see it).
/// </summary>
internal sealed record MssqlStagingState(
    int DatabaseId,
    string State,
    DateTimeOffset CreateDate,
    string PrimaryFile
)
{
    /// <summary>The instance identity this state describes (D-13).</summary>
    public DmsInstanceIdentity.Mssql Instance => new(DatabaseId, PrimaryFile, CreateDate);
}
