// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace EdFi.DmsConfigurationService.Tests.DmsProjectionE2E.Live;

/// <summary>A tenant's education organization refresh schedule, as <c>dmscs.JobSchedule</c> holds it.</summary>
public sealed record RefreshScheduleRow(long Id, bool Enabled, int IntervalMinutes);

/// <summary>One education organization refresh job of a tenant, as <c>dmscs.Job</c> holds it.</summary>
public sealed record RefreshJobRow(
    string JobId,
    string Status,
    long? SourceScheduleId,
    DateTime? ScheduledOccurrence,
    DateTime? FinishedAt,
    string? ErrorMessage
)
{
    public override string ToString() =>
        $"{JobId} {Status} schedule={SourceScheduleId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} "
        + $"occurrence={ScheduledOccurrence:O} finished={FinishedAt:O} error={ErrorMessage ?? "none"}";
}

/// <summary>
/// Read-only queries of the Configuration Service tables no API lists: a tenant's refresh schedule and the refresh
/// jobs it enqueued. The connection's login can only read <c>dmscs.Tenant</c>, <c>dmscs.JobSchedule</c> and
/// <c>dmscs.Job</c>.
/// </summary>
public sealed class CmsJobDatabase(string databaseEngine, string connectionString)
{
    private const string PostgresScheduleSql = """
        SELECT s."Id", s."Enabled", s."IntervalMinutes"
        FROM "dmscs"."JobSchedule" AS s
        JOIN "dmscs"."Tenant" AS t ON t."Id" = s."TenantId"
        WHERE t."Name" = @Tenant AND s."ScheduleType" = @ScheduleType;
        """;

    private const string PostgresJobsSql = """
        SELECT j."JobId", j."Status", j."SourceScheduleId", j."ScheduledOccurrence", j."FinishedAt", j."ErrorMessage"
        FROM "dmscs"."Job" AS j
        JOIN "dmscs"."Tenant" AS t ON t."Id" = j."TenantId"
        WHERE t."Name" = @Tenant AND j."JobType" = @JobType
        ORDER BY j."Id";
        """;

    private const string MssqlScheduleSql = """
        SELECT s.Id, s.Enabled, s.IntervalMinutes
        FROM dmscs.JobSchedule AS s
        JOIN dmscs.Tenant AS t ON t.Id = s.TenantId
        WHERE t.Name = @Tenant AND s.ScheduleType = @ScheduleType;
        """;

    private const string MssqlJobsSql = """
        SELECT j.JobId, j.Status, j.SourceScheduleId, j.ScheduledOccurrence, j.FinishedAt, j.ErrorMessage
        FROM dmscs.Job AS j
        JOIN dmscs.Tenant AS t ON t.Id = j.TenantId
        WHERE t.Name = @Tenant AND j.JobType = @JobType
        ORDER BY j.Id;
        """;

    private bool IsMssql => databaseEngine == "mssql";

    /// <summary>The tenant's scheduled-refresh schedule, or null while the reconciler has not created it.</summary>
    public async Task<RefreshScheduleRow?> GetRefreshScheduleAsync(string tenant)
    {
        await using DbConnection connection = await OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = IsMssql ? MssqlScheduleSql : PostgresScheduleSql;
        AddParameter(command, "@Tenant", tenant);
        AddParameter(command, "@ScheduleType", EducationOrganizationRefreshJobTypes.ScheduledRefresh);
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new RefreshScheduleRow(reader.GetInt64(0), reader.GetBoolean(1), reader.GetInt32(2));
    }

    /// <summary>Every refresh job of the tenant, scheduled or not, oldest first.</summary>
    public async Task<IReadOnlyList<RefreshJobRow>> GetRefreshJobsAsync(string tenant)
    {
        await using DbConnection connection = await OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = IsMssql ? MssqlJobsSql : PostgresJobsSql;
        AddParameter(command, "@Tenant", tenant);
        AddParameter(command, "@JobType", EducationOrganizationRefreshJobTypes.Refresh);
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        List<RefreshJobRow> jobs = [];
        while (await reader.ReadAsync())
        {
            jobs.Add(
                new RefreshJobRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    await reader.IsDBNullAsync(2) ? null : reader.GetInt64(2),
                    await reader.IsDBNullAsync(3) ? null : reader.GetDateTime(3),
                    await reader.IsDBNullAsync(4) ? null : reader.GetDateTime(4),
                    await reader.IsDBNullAsync(5) ? null : reader.GetString(5)
                )
            );
        }

        return jobs;
    }

    private async Task<DbConnection> OpenAsync()
    {
        DbConnection connection = IsMssql
            ? new SqlConnection(connectionString)
            : new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
