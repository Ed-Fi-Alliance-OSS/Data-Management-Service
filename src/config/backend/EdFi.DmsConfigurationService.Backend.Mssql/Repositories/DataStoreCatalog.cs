// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Repositories;

/// <summary>
/// Transaction-scoped CMS catalog coordination. The order is job fence, tenant gate, sorted target
/// reservations, managed aggregate, ordinary row. No physical database work belongs under these locks.
/// Every supplied transaction remains owned by the caller; failures must reach its rollback boundary.
/// </summary>
internal static class DataStoreCatalog
{
    internal sealed class NameReservedException()
        : InvalidOperationException("The data store name is reserved within the selected tenant.");

    internal static Task AcquireTenantGateAsync(
        DbTransaction transaction,
        long? tenantId,
        CancellationToken cancellationToken
    ) => AcquireAsync(transaction, TenantGateResource(tenantId), cancellationToken);

    internal static Task AcquireTenantGatesAsync(
        DbTransaction transaction,
        IEnumerable<long?> tenantIds,
        CancellationToken cancellationToken
    ) =>
        AcquireResourcesAsync(
            transaction,
            tenantIds.Distinct().Order().Select(TenantGateResource).ToArray(),
            cancellationToken
        );

    private static string TenantGateResource(long? tenantId) =>
        tenantId is long id
            ? "dmscs:datastore-catalog:tenant:" + id.ToString(CultureInfo.InvariantCulture)
            : "dmscs:datastore-catalog:null";

    /// <summary>
    /// Ordinary rename may exclude its own linked reservation only after verifying the ordinary row
    /// exists in this tenant under the gate. Insert never supplies an ordinary row ID.
    /// </summary>
    internal static async Task<bool> HasManagedNameReservationAsync(
        DbTransaction transaction,
        long? tenantId,
        string name,
        int? ordinaryDataStoreId,
        CancellationToken cancellationToken
    )
    {
        await AcquireTenantGateAsync(transaction, tenantId, cancellationToken);
        return await transaction.Connection!.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dmscs."DataStoreManage"
                    WHERE ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL)) AND NameComparisonKey = CONVERT(binary(4), DATALENGTH(@Name)) + CONVERT(varbinary(max), @Name) AND "Status" <> 'Deleted'
                      AND (@OrdinaryDataStoreId IS NULL OR "DataStoreId" IS NULL OR "DataStoreId" <> @OrdinaryDataStoreId)) THEN 1 ELSE 0 END AS bit);
                """,
                NameParameters(tenantId, name, ordinaryDataStoreId),
                transaction,
                cancellationToken: cancellationToken
            )
        );
    }

    /// <summary>
    /// Managed acceptance checks both namespaces with no exclusion. Publication uses its own verified
    /// reservation/current-link path together with the job fence and aggregate identity.
    /// </summary>
    internal static async Task<bool> HasNameReservationAsync(
        DbTransaction transaction,
        long? tenantId,
        string name,
        CancellationToken cancellationToken
    )
    {
        await AcquireTenantGateAsync(transaction, tenantId, cancellationToken);
        return await transaction.Connection!.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dmscs."DataStore" WHERE ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL)) AND NameComparisonKey = CONVERT(binary(4), DATALENGTH(@Name)) + CONVERT(varbinary(max), @Name)
                    UNION ALL
                    SELECT 1 FROM dmscs."DataStoreManage"
                    WHERE ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL)) AND NameComparisonKey = CONVERT(binary(4), DATALENGTH(@Name)) + CONVERT(varbinary(max), @Name) AND "Status" <> 'Deleted') THEN 1 ELSE 0 END AS bit);
                """,
                NameParameters(tenantId, name, null),
                transaction,
                cancellationToken: cancellationToken
            )
        );
    }

    private static DynamicParameters NameParameters(long? tenantId, string name, int? ordinaryDataStoreId)
    {
        DynamicParameters parameters = new(
            new { TenantId = tenantId, OrdinaryDataStoreId = ordinaryDataStoreId }
        );
        // Never pass unchecked candidates through the SQL Server nvarchar(256) schema function.
        parameters.Add("Name", CatalogName.Canonicalize(name), DbType.String, size: -1);
        return parameters;
    }

    internal static async Task AcquireTargetReservationsAsync(
        DbTransaction transaction,
        long? tenantId,
        ManagedDataStoreEndpoint endpoint,
        IEnumerable<DmsTargetDatabaseName> targets,
        CancellationToken cancellationToken
    )
    {
        await AcquireTenantGateAsync(transaction, tenantId, cancellationToken);
        // Target names have the provisioner's ASCII grammar, so the newline is an unambiguous separator.
        string[] resources = targets
            .Select(target =>
                "dmscs:datastore-target:"
                + Convert.ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(
                            endpoint.CanonicalEncoding + "\n" + CatalogName.FoldAscii(target.Value)
                        )
                    )
                )
            )
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (string resource in resources)
        {
            await AcquireAsync(transaction, resource, cancellationToken);
        }
    }

    internal static async Task<bool> HasTargetReservationAsync(
        DbTransaction transaction,
        long? tenantId,
        ManagedDataStoreEndpoint endpoint,
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    )
    {
        await AcquireTargetReservationsAsync(transaction, tenantId, endpoint, [target], cancellationToken);
        DynamicParameters parameters = new();
        parameters.Add("Endpoint", endpoint.CanonicalEncoding, DbType.String, size: -1);
        parameters.Add("Target", CatalogName.FoldAscii(target.Value), DbType.String, size: -1);
        return await transaction.Connection!.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dmscs."DataStoreManage"
                    WHERE "Status" <> 'Deleted'
                      AND "EndpointDigest" = HASHBYTES('SHA2_256', @Endpoint)
                      AND "TargetDatabaseNameKey" = @Target COLLATE Latin1_General_100_BIN2) THEN 1 ELSE 0 END AS bit);
                """,
                parameters,
                transaction,
                cancellationToken: cancellationToken
            )
        );
    }

    private static Task AcquireAsync(
        DbTransaction transaction,
        string resource,
        CancellationToken cancellationToken
    ) => AcquireResourcesAsync(transaction, [resource], cancellationToken);

    private static async Task AcquireResourcesAsync(
        DbTransaction transaction,
        string[] resources,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not SqlTransaction)
        {
            throw new ArgumentException(
                "The supplied transaction must be a SQL Server transaction.",
                nameof(transaction)
            );
        }
        _ = transaction.IsolationLevel;
        if (transaction.Connection is not SqlConnection { State: ConnectionState.Open } connection)
        {
            throw new InvalidOperationException("The supplied transaction must have an open connection.");
        }

        int result;
        try
        {
            result = await connection.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    """
                    DECLARE @Wait int = @@LOCK_TIMEOUT;
                    IF @Wait < 0 OR @Wait > 5000 SET @Wait = 5000;
                    DECLARE @Result int = 0, @Resource nvarchar(255);
                    DECLARE gates CURSOR LOCAL FAST_FORWARD FOR
                        SELECT value FROM OPENJSON(@Resources) ORDER BY CONVERT(int, [key]);
                    OPEN gates;
                    FETCH NEXT FROM gates INTO @Resource;
                    WHILE @@FETCH_STATUS = 0
                    BEGIN
                        EXEC @Result = sys.sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive',
                            @LockOwner = 'Transaction', @LockTimeout = @Wait;
                        IF @Result < 0 BREAK;
                        FETCH NEXT FROM gates INTO @Resource;
                    END;
                    CLOSE gates;
                    DEALLOCATE gates;
                    SELECT @Result;
                    """,
                    new { Resources = JsonSerializer.Serialize(resources) },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
        }
        catch (SqlException exception)
            when (cancellationToken.IsCancellationRequested
                && exception.Number == 0
                && exception.Errors.Cast<SqlError>().All(error => error.Number == 0)
            )
        {
            // SqlClient can report cancellation of an in-flight batch as error 0 instead of OCE.
            throw new OperationCanceledException(
                "The catalog reservation lock wait was cancelled.",
                exception,
                cancellationToken
            );
        }
        switch (result)
        {
            case 0:
            case 1:
                return;
            case -1:
                throw new TimeoutException("The catalog reservation lock wait expired.");
            case -2:
                throw new OperationCanceledException(
                    "The catalog reservation lock wait was cancelled.",
                    cancellationToken
                );
            case -3:
                throw new DBConcurrencyException(
                    "The catalog reservation lock was selected as a deadlock victim."
                );
            default:
                throw new InvalidOperationException("The catalog reservation lock could not be acquired.");
        }
    }
}
