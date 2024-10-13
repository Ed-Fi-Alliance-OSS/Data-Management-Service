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
            var sql = "SELECT Id, Company, ContactName, ContactEmailAddress FROM dmscs.Vendor;";
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            var vendors = await connection.QueryAsync<Vendor>(sql);
            return new GetResult<Vendor>.GetSuccess((IReadOnlyList<Vendor>)vendors);
        }

        public async Task<GetResult<Vendor>> GetByIdAsync(long id)
        {
            var sql = "SELECT Id, Company, ContactName, ContactEmailAddress  FROM dmscs.Vendor where Id = @Id;";
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            try
            {
                var vendor = await connection.QuerySingleAsync<Vendor>(sql, new { Id = id });
                return new GetResult<Vendor>.GetByIdSuccess(vendor);
            }
            catch (InvalidOperationException ex) when (ex.Message == "Sequence contains no elements")
            {
                return new GetResult<Vendor>.GetByIdFailureNotExists();
            }
            catch(Exception ex)
            {
                return new GetResult<Vendor>.UnknownFailure(ex.Message);
            }
            
        }

        public async Task<InsertResult> AddAsync(Vendor vendor)
        {
            var sql = "INSERT INTO dmscs.Vendor (Company, ContactName, ContactEmailAddress) VALUES (@Company, @ContactName, @ContactEmailAddress) RETURNING Id";
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            try
            {
                var id = await connection.ExecuteScalarAsync<long>(sql, vendor);
                return new InsertResult.InsertSuccess(id);
            }
            catch (Exception ex)
            {
                return new InsertResult.UnknownFailure(ex.Message);
            }
        }

        public async Task<UpdateResult> UpdateAsync(Vendor vendor)
        {
            var sql = @"UPDATE dmscs.vendor
	                    SET Company=@Company, ContactName=@ContactName, ContactEmailAddress=@ContactEmailAddress
	                    WHERE Id = @Id;";
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            try
            {
                var affectedRows = await connection.ExecuteAsync(sql, vendor);
                return affectedRows > 0
                    ? new UpdateResult.UpdateSuccess(affectedRows)
                    : new UpdateResult.UpdateFailureNotExists();
            }
            catch (Exception ex)
            {
                return new UpdateResult.UnknownFailure(ex.Message);
            }
        }

        public async Task<DeleteResult> DeleteAsync(long id)
        {
            var sql = "DELETE FROM dmscs.Vendor where Id = @Id;";
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
