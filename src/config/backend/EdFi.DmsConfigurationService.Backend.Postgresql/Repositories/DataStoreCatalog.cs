// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

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
    ) =>
        AcquireAsync(
            transaction,
            tenantId is long id
                ? "dmscs:datastore-catalog:tenant:" + id.ToString(CultureInfo.InvariantCulture)
                : "dmscs:datastore-catalog:null",
            cancellationToken
        );

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
                SELECT EXISTS (SELECT 1 FROM dmscs."DataStoreManage"
                    WHERE ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL)) AND "NameComparisonKey" = @Name COLLATE "C" AND "Status" <> 'Deleted'
                      AND (@OrdinaryDataStoreId IS NULL OR "DataStoreId" IS NULL OR "DataStoreId" <> @OrdinaryDataStoreId));
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
                SELECT EXISTS (SELECT 1 FROM dmscs."DataStore" WHERE ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL)) AND "NameComparisonKey" = @Name COLLATE "C"
                    UNION ALL
                    SELECT 1 FROM dmscs."DataStoreManage"
                    WHERE ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL)) AND "NameComparisonKey" = @Name COLLATE "C" AND "Status" <> 'Deleted');
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
                SELECT EXISTS (SELECT 1 FROM dmscs."DataStoreManage"
                    WHERE "Status" <> 'Deleted'
                      AND "EndpointDigest" = public.digest(@Endpoint, 'sha256')
                      AND "TargetDatabaseNameKey" = @Target COLLATE "C");
                """,
                parameters,
                transaction,
                cancellationToken: cancellationToken
            )
        );
    }

    private static async Task AcquireAsync(
        DbTransaction transaction,
        string resource,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not NpgsqlTransaction)
        {
            throw new ArgumentException(
                "The supplied transaction must be a PostgreSQL transaction.",
                nameof(transaction)
            );
        }
        _ = transaction.IsolationLevel;
        if (transaction.Connection is not NpgsqlConnection { State: ConnectionState.Open } connection)
        {
            throw new InvalidOperationException("The supplied transaction must have an open connection.");
        }

        // pg_settings exposes lock_timeout numerically in milliseconds. Do not lengthen the
        // shorter budget already installed by a job fence, and restore it after acquisition.
        long previous = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "SELECT setting::bigint FROM pg_settings WHERE name = 'lock_timeout';",
                transaction: transaction,
                cancellationToken: cancellationToken
            )
        );
        long bounded = previous == 0 ? 5000 : Math.Min(previous, 5000);
        long key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(resource)));
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                SELECT set_config('lock_timeout', @Timeout, true);
                SELECT pg_advisory_xact_lock(@Key);
                SELECT set_config('lock_timeout', @Previous, true);
                """,
                new
                {
                    Timeout = bounded.ToString(CultureInfo.InvariantCulture) + "ms",
                    Previous = previous.ToString(CultureInfo.InvariantCulture) + "ms",
                    Key = key,
                },
                transaction,
                cancellationToken: cancellationToken
            )
        );
    }
}
