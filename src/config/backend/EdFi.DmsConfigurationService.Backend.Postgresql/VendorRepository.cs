// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.DataModel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql
{
    public class VendorRepository(IOptions<DatabaseOptions> databaseOptions) : IRepository<Vendor>
    {
        public async Task<GetResult<Vendor>> GetAllAsync()
        {
            var sql = """
                SELECT Id, Company, ContactName, ContactEmailAddress, NamespacePrefix
                FROM dmscs.Vendor v LEFT OUTER JOIN dmscs.VendorNamespacePrefix p ON v.Id = p.VendorId;
                """;
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            try
            {
                var vendors = await connection.QueryAsync<Vendor, string, Vendor>(
                    sql,
                    (vendor, namespacePrefix) =>
                    {
                        vendor.NamespacePrefixes.Add(namespacePrefix);
                        return vendor;
                    },
                    splitOn: "NamespacePrefix"
                );

                var returnVendors = vendors
                    .GroupBy(v => v.Id)
                    .Select(g =>
                    {
                        var grouped = g.First();
                        grouped.NamespacePrefixes = g.Select(p => p.NamespacePrefixes.Single()).ToList();
                        return grouped;
                    })
                    .ToList();
                return new GetResult<Vendor>.GetSuccess(returnVendors);
            }
            catch (Exception ex)
            {
                return new GetResult<Vendor>.UnknownFailure(ex.Message);
            }
        }

        public async Task<GetResult<Vendor>> GetByIdAsync(long id)
        {
            var sql = """
                SELECT Id, Company, ContactName, ContactEmailAddress, NamespacePrefix
                FROM dmscs.Vendor v LEFT OUTER JOIN dmscs.VendorNamespacePrefix p ON v.Id = p.VendorId
                WHERE v.Id = @Id;
                """;
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            try
            {
                var vendors = await connection.QueryAsync<Vendor, string, Vendor>(
                    sql,
                    (vendor, namespacePrefix) =>
                    {
                        vendor.NamespacePrefixes.Add(namespacePrefix);
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
                        grouped.NamespacePrefixes = g.Select(p => p.NamespacePrefixes.Single()).ToList();
                        return grouped;
                    });

                return new GetResult<Vendor>.GetByIdSuccess(returnVendors.Single());
            }
            catch (InvalidOperationException ex) when (ex.Message == "Sequence contains no elements")
            {
                return new GetResult<Vendor>.GetByIdFailureNotExists();
            }
            catch (Exception ex)
            {
                return new GetResult<Vendor>.UnknownFailure(ex.Message);
            }
        }

        public async Task<InsertResult> AddAsync(Vendor vendor)
        {
            var sql = """
                INSERT INTO dmscs.Vendor (Company, ContactName, ContactEmailAddress)
                VALUES (@Company, @ContactName, @ContactEmailAddress)
                RETURNING Id;
                """;
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                var id = await connection.ExecuteScalarAsync<long>(sql, vendor);

                sql = """
                    INSERT INTO dmscs.VendorNamespacePrefix (VendorId, NamespacePrefix)
                    VALUES (@VendorId, @NamespacePrefix);
                    """;

                var namespacePrefixes = vendor.NamespacePrefixes.Select(p => new
                {
                    VendorId = id,
                    NamespacePrefix = p,
                });

                await connection.ExecuteAsync(sql, namespacePrefixes);
                await transaction.CommitAsync();

                return new InsertResult.InsertSuccess(id);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return new InsertResult.UnknownFailure(ex.Message);
            }
        }

        public async Task<UpdateResult> UpdateAsync(Vendor vendor)
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
                var affectedRows = await connection.ExecuteAsync(sql, vendor);

                sql = "DELETE FROM dmscs.VendorNamespacePrefix WHERE VendorId = @VendorId";
                await connection.ExecuteAsync(sql, new { VendorId = vendor.Id });

                sql = """
                    INSERT INTO dmscs.VendorNamespacePrefix (VendorId, NamespacePrefix)
                    VALUES (@VendorId, @NamespacePrefix);
                    """;

                var namespacePrefixes = vendor.NamespacePrefixes.Select(p => new
                {
                    VendorId = vendor.Id,
                    NamespacePrefix = p,
                });

                await connection.ExecuteAsync(sql, namespacePrefixes);
                await transaction.CommitAsync();

                return affectedRows > 0
                    ? new UpdateResult.UpdateSuccess(affectedRows)
                    : new UpdateResult.UpdateFailureNotExists();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return new UpdateResult.UnknownFailure(ex.Message);
            }
        }

        public async Task<DeleteResult> DeleteAsync(long id)
        {
            var sql = """
                DELETE FROM dmscs.Vendor where Id = @Id;
                DELETE from dmscs.VendorNamespacePrefix WHERE VendorId = @Id;
                """;
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            try
            {
                var affectedRows = await connection.ExecuteAsync(sql, new { Id = id });
                return affectedRows > 0
                    ? new DeleteResult.DeleteSuccess(affectedRows)
                    : new DeleteResult.DeleteFailureNotExists();
            }
            catch (Exception ex)
            {
                return new DeleteResult.UnknownFailure(ex.Message);
            }
        }
    }
}
