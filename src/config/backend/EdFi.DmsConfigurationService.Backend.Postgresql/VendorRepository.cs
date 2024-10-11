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
        public Task<IReadOnlyList<Vendor>> GetAllAsync()
        {

            throw new NotImplementedException();
        }

        public Task<Vendor> GetByIdAsync(long id)
        {
            throw new NotImplementedException();
        }

        public async Task<bool> AddAsync(Vendor vendor)
        {
            var sql = "INSERT INTO dmscs.Vendor (Company, ContactName, ContactEmailAddress) values (@Company, @ContactName, @ContactEmailAddress)";
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            var affectedRows = await connection.ExecuteAsync(sql, vendor);
            return affectedRows == 1;
        }

        public Task<bool> UpdateAsync(Vendor entity)
        {
            throw new NotImplementedException();
        }

        public Task<bool> DeleteAsync(long id)
        {
            throw new NotImplementedException();
        }
    }
}
