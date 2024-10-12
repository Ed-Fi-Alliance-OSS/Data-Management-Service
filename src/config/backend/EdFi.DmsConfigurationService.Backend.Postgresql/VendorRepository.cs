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
        public async Task<IReadOnlyList<Vendor>> GetAllAsync()
        {
            var sql = "SELECT Id, Company, ContactName, ContactEmailAddress FROM dmscs.Vendor;";
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            var vendors = await connection.QueryAsync<Vendor>(sql);
            return (IReadOnlyList<Vendor>)vendors;
        }

        public async Task<Vendor?> GetByIdAsync(long id)
        {
            var sql = "SELECT Id, Company, ContactName, ContactEmailAddress  FROM dmscs.Vendor where Id = @Id;";
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            var vendor = await connection.QuerySingleOrDefaultAsync<Vendor>(sql, new { Id = id });
            return vendor;
        }

        public async Task<long> AddAsync(Vendor vendor)
        {
            var sql = "INSERT INTO dmscs.Vendor (Company, ContactName, ContactEmailAddress) VALUES (@Company, @ContactName, @ContactEmailAddress) RETURNING Id";
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            return await connection.ExecuteScalarAsync<long>(sql, vendor);
        }

        public async Task<bool> UpdateAsync(Vendor vendor)
        {
            var sql = @"UPDATE dmscs.vendor
	                    SET Company=@Company, ContactName=@ContactName, ContactEmailAddress=@ContactEmailAddress
	                    WHERE Id = @Id;";
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            var affectedRows = await connection.ExecuteAsync(sql, vendor);
            return affectedRows == 1;
        }

        public async Task<bool> DeleteAsync(long id)
        {
            var sql = "DELETE FROM dmscs.Vendor where Id = @Id;";
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            var affectedRows = await connection.ExecuteAsync(sql, new { Id = id });
            return affectedRows == 1;
        }
    }
}
