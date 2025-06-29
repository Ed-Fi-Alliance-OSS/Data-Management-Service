// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories
{
    public class VendorRepository(IOptions<DatabaseOptions> databaseOptions, ILogger<VendorRepository> logger)
        : IVendorRepository
    {
        public async Task<VendorInsertResult> InsertVendor(VendorInsertCommand command)
        {
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                long id = 0;
                bool isNewVendor = false;
                var sql = "SELECT Id FROM dmscs.Vendor WHERE Company = @Company";
                var existingVendorId = await connection.ExecuteScalarAsync<long?>(sql, new { command.Company });

                if (existingVendorId.HasValue)
                {
                    sql = """
                        UPDATE dmscs.Vendor
                        SET ContactName=@ContactName, ContactEmailAddress=@ContactEmailAddress
                        WHERE Id = @Id;
                        """;
                    await connection.ExecuteAsync(sql, new
                    {
                        command.ContactName,
                        command.ContactEmailAddress,
                        Id = existingVendorId.Value
                    });

                    sql = "DELETE FROM dmscs.VendorNamespacePrefix WHERE VendorId = @VendorId";
                    await connection.ExecuteAsync(sql, new { VendorId = existingVendorId.Value });
                    id = existingVendorId.Value;
                }
                else
                {
                    sql = """
                    INSERT INTO dmscs.Vendor (Company, ContactName, ContactEmailAddress)
                    VALUES (@Company, @ContactName, @ContactEmailAddress)
                    RETURNING Id;
                    """;

                    id = await connection.ExecuteScalarAsync<long>(sql, command);
                    isNewVendor = true;
                }

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

                return new VendorInsertResult.Success(id, isNewVendor);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505" && ex.Message.Contains("uq_company"))
            {
                logger.LogWarning(ex, "Company Name must be unique");
                await transaction.RollbackAsync();
                return new VendorInsertResult.FailureDuplicateCompanyName();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Insert vendor failure");
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
                    FROM (SELECT * FROM dmscs.Vendor ORDER BY Id LIMIT @Limit OFFSET @Offset) AS v 
                    LEFT OUTER JOIN dmscs.VendorNamespacePrefix p ON v.Id = p.VendorId;
                    """;
                var vendors = await connection.QueryAsync<VendorResponse, string, VendorResponse>(
                    sql,
                    (vendor, namespacePrefix) =>
                    {
                        vendor.NamespacePrefixes = namespacePrefix;
                        return vendor;
                    },
                    param: query,
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
                logger.LogError(ex, "Query vendor failure");
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

                if (!vendors.Any())
                {
                    return new VendorGetResult.FailureNotFound();
                }

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
            catch (Exception ex)
            {
                logger.LogError(ex, "Get vendor failure");
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

                if (affectedRows == 0)
                {
                    return new VendorUpdateResult.FailureNotExists();
                }

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

                var apiClientSql = """
                    SELECT c.clientUuid
                    FROM dmscs.apiclient c
                    	INNER JOIN dmscs.application a ON a.id = c.applicationId
                    	INNER JOIN dmscs.vendor v on v.id = a.vendorid
                    WHERE v.id = @VendorId
                    """;

                var apiClientUuids = await connection.QueryAsync<Guid>(
                    apiClientSql,
                    param: new { VendorId = command.Id }
                );
                return new VendorUpdateResult.Success(apiClientUuids.ToList());
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Update vendor failure");
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
                logger.LogError(ex, "Delete vendor failure");
                return new VendorDeleteResult.FailureUnknown(ex.Message);
            }
        }

        public async Task<VendorApplicationsResult> GetVendorApplications(long vendorId)
        {
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            try
            {
                string sql = """
                        SELECT
                            v.Id as VendorId, a.Id, a.ApplicationName, a.ClaimSetName,
                            eo.EducationOrganizationId
                        FROM dmscs.vendor v
                        LEFT OUTER JOIN dmscs.Application a ON v.Id = a.VendorId
                        LEFT OUTER JOIN dmscs.ApplicationEducationOrganization eo ON a.Id = eo.ApplicationId
                        WHERE v.Id = @VendorId;
                    """;

                bool vendorExists = false;
                Dictionary<long, ApplicationResponse> response = [];

                await connection.QueryAsync<ApplicationResponse, long?, ApplicationResponse>(
                    sql,
                    (application, educationOrganizationId) =>
                    {
                        vendorExists = application.VendorId == vendorId;
                        if (application.Id == 0)
                        {
                            // vendor exists without applications
                            return application;
                        }

                        if (
                            response.TryGetValue(application.Id, out ApplicationResponse? thisApplication)
                            && educationOrganizationId != null
                        )
                        {
                            thisApplication.EducationOrganizationIds.Add(educationOrganizationId.Value);
                        }
                        else
                        {
                            response.Add(application.Id, application);
                        }

                        if (educationOrganizationId != null)
                        {
                            application.EducationOrganizationIds.Add(educationOrganizationId.Value);
                        }

                        return application;
                    },
                    param: new { VendorId = vendorId },
                    splitOn: "EducationOrganizationId"
                );

                if (!vendorExists)
                {
                    return new VendorApplicationsResult.FailureNotExists();
                }

                return new VendorApplicationsResult.Success(response.Values.OrderBy(a => a.ApplicationName));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Get vendor applications failure");
                return new VendorApplicationsResult.FailureUnknown(ex.Message);
            }
        }
    }
}
