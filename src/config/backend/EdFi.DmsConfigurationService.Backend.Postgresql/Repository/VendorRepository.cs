// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Vendor;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repository
{
    public class VendorRepository(IOptions<DatabaseOptions> databaseOptions) : IVendorRepository
    {
        public async Task<VendorInsertResult> InsertVendor(VendorInsertCommand command)
        {
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                var sql = """
                    INSERT INTO dmscs.Vendor (Company, ContactName, ContactEmailAddress)
                    VALUES (@Company, @ContactName, @ContactEmailAddress)
                    RETURNING Id;
                    """;

                var id = await connection.ExecuteScalarAsync<long>(sql, command);

                sql = """
                    INSERT INTO dmscs.VendorNamespacePrefix (VendorId, NamespacePrefix)
                    VALUES (@VendorId, @NamespacePrefix);
                    """;

                var namespacePrefixes = command
                    .NamespacePrefixes.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
                    .Select(p => new { VendorId = id, NamespacePrefix = p.Trim() });

                await connection.ExecuteAsync(sql, namespacePrefixes);
                await transaction.CommitAsync();

                return new VendorInsertResult.Success(id);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return new VendorInsertResult.FailureUnknown(ex.Message);
            }
        }

        public async Task<VendorQueryResult> QueryVendor(PagingQuery query)
        {
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            try
            {
                var sql = """
                    SELECT Id, Company, ContactName, ContactEmailAddress, NamespacePrefix
                    FROM dmscs.Vendor v LEFT OUTER JOIN dmscs.VendorNamespacePrefix p ON v.Id = p.VendorId;
                    """;
                var vendors = await connection.QueryAsync<VendorResponse, string, VendorResponse>(
                    sql,
                    (vendor, namespacePrefix) =>
                    {
                        vendor.NamespacePrefixes = namespacePrefix;
                        return vendor;
                    },
                    splitOn: "NamespacePrefix"
                );

                var returnVendors = vendors
                    .GroupBy(v => v.Id)
                    .Select(g =>
                    {
                        var grouped = g.First();
                        grouped.NamespacePrefixes = string.Join(',', g.Select(x => x.NamespacePrefixes));
                        return grouped;
                    })
                    .ToList();
                return new VendorQueryResult.Success(returnVendors);
            }
            catch (Exception ex)
            {
                return new VendorQueryResult.FailureUnknown(ex.Message);
            }
        }

        public async Task<VendorGetResult> GetVendor(long id)
        {
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            try
            {
                var sql = """
                    SELECT Id, Company, ContactName, ContactEmailAddress, NamespacePrefix
                    FROM dmscs.Vendor v LEFT OUTER JOIN dmscs.VendorNamespacePrefix p ON v.Id = p.VendorId
                    WHERE v.Id = @Id;
                    """;
                var vendors = await connection.QueryAsync<VendorResponse, string, VendorResponse>(
                    sql,
                    (vendor, namespacePrefix) =>
                    {
                        vendor.NamespacePrefixes = namespacePrefix;
                        return vendor;
                    },
                    param: new { Id = id },
                    splitOn: "NamespacePrefix"
                );

                var returnVendors = vendors
                    .GroupBy(v => v.Id)
                    .Select(g =>
                    {
                        var grouped = g.First();
                        grouped.NamespacePrefixes = string.Join(',', g.Select(x => x.NamespacePrefixes));
                        return grouped;
                    });

                return new VendorGetResult.Success(returnVendors.Single());
            }
            catch (InvalidOperationException ex) when (ex.Message == "Sequence contains no elements")
            {
                return new VendorGetResult.FailureNotFound();
            }
            catch (Exception ex)
            {
                return new VendorGetResult.FailureUnknown(ex.Message);
            }
        }

        public async Task<VendorUpdateResult> UpdateVendor(VendorUpdateCommand command)
        {
            var sql = """
                UPDATE dmscs.Vendor
                SET Company=@Company, ContactName=@ContactName, ContactEmailAddress=@ContactEmailAddress
                WHERE Id = @Id;
                """;
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                var affectedRows = await connection.ExecuteAsync(sql, command);

                sql = "DELETE FROM dmscs.VendorNamespacePrefix WHERE VendorId = @VendorId";
                await connection.ExecuteAsync(sql, new { VendorId = command.Id });

                sql = """
                    INSERT INTO dmscs.VendorNamespacePrefix (VendorId, NamespacePrefix)
                    VALUES (@VendorId, @NamespacePrefix);
                    """;

                var namespacePrefixes = command
                    .NamespacePrefixes.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
                    .Select(p => new { VendorId = command.Id, NamespacePrefix = p.Trim() });

                await connection.ExecuteAsync(sql, namespacePrefixes);
                await transaction.CommitAsync();

                return affectedRows > 0
                    ? new VendorUpdateResult.Success()
                    : new VendorUpdateResult.FailureNotExists();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return new VendorUpdateResult.FailureUnknown(ex.Message);
            }
        }

        public async Task<VendorDeleteResult> DeleteVendor(long id)
        {
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            try
            {
                var sql = """
                    DELETE FROM dmscs.Vendor where Id = @Id;
                    DELETE from dmscs.VendorNamespacePrefix WHERE VendorId = @Id;
                    """;

                var affectedRows = await connection.ExecuteAsync(sql, new { Id = id });
                return affectedRows > 0
                    ? new VendorDeleteResult.Success()
                    : new VendorDeleteResult.FailureNotExists();
            }
            catch (Exception ex)
            {
                return new VendorDeleteResult.FailureUnknown(ex.Message);
            }
        }
    }
}
