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

namespace EdFi.DmsConfigurationService.Backend.Postgresql
{
    public class VendorRepository(IOptions<DatabaseOptions> databaseOptions) : IVendorRepository
    {
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

        public async Task<GetResult<Vendor>> GetVendorByIdWithApplicationsAsync(long vendorId)
        {
            string sql = """
                    SELECT v.Company, v.ContactName, v.ContactEmailAddress,
                        a.Id, a.ApplicationName, a.VendorId, a.ClaimSetName, 
                        eo.EducationOrganizationId
                    FROM dmscs.vendor v
                    LEFT OUTER JOIN dmscs.Application a ON v.Id = a.VendorId
                    LEFT OUTER JOIN dmscs.ApplicationEducationOrganization eo ON a.Id = eo.ApplicationId
                    WHERE v.Id = @VendorId;
                """;

            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);

            try
            {
                var vendors = await connection.QueryAsync<Vendor, Application?, long?, Vendor>(
                    sql,
                    (vendor, application, educationOrganizationId) =>
                    {
                        vendor.Id = vendorId;
                        var existingApplication = vendor.Applications.FirstOrDefault(app =>
                            app.Id == application?.Id
                        );

                        if (existingApplication == null && application != null)
                        {
                            application.EducationOrganizationIds = new List<long>();
                            vendor.Applications.Add(application);
                            existingApplication = application;
                        }

                        if (educationOrganizationId.HasValue)
                        {
                            existingApplication?.EducationOrganizationIds.Add(educationOrganizationId.Value);
                        }

                        return vendor;
                    },
                    param: new { VendorId = vendorId },
                    splitOn: "Id, EducationOrganizationId"
                );

                var result = vendors
                    .GroupBy(v => v.Id)
                    .Select(g =>
                    {
                        var groupedVendor = g.First();
                        groupedVendor.Applications = g.SelectMany(v => v.Applications)
                            .GroupBy(a => a.Id)
                            .Select(a =>
                            {
                                var app = a.First();
                                app.EducationOrganizationIds = a.SelectMany(x => x.EducationOrganizationIds)
                                    .Distinct()
                                    .ToList();
                                return app;
                            })
                            .ToList();
                        return groupedVendor;
                    })
                    .ToList();

                return result.Any()
                    ? new GetResult<Vendor>.GetByIdSuccess(result.Single())
                    : new GetResult<Vendor>.GetByIdFailureNotExists();
            }
            catch (Exception ex)
            {
                return new GetResult<Vendor>.UnknownFailure(ex.Message);
            }
        }
    }
}
