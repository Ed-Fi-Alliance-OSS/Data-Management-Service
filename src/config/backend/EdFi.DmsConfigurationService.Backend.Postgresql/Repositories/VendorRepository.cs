// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories
{
    public class VendorRepository(
        IOptions<DatabaseOptions> databaseOptions,
        ILogger<VendorRepository> logger,
        IAuditContext auditContext,
        ITenantContextProvider tenantContextProvider
    ) : IVendorRepository
    {
        private TenantContext TenantContext => tenantContextProvider.Context;

        private long? TenantId => TenantContext is TenantContext.Multitenant mt ? mt.TenantId : null;

        public async Task<VendorInsertResult> InsertVendor(VendorInsertCommand command)
        {
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                long id = 0;
                bool isNewVendor = false;
                // Check for existing vendor by Company (and TenantId if multi-tenancy is enabled)
                var sql =
                    $"SELECT \"Id\" FROM \"dmscs\".\"Vendor\" WHERE \"Company\" = @Company AND {TenantContext.TenantWhereClause()}";

                var existingVendorId = await connection.ExecuteScalarAsync<long?>(
                    sql,
                    new { command.Company, TenantId }
                );

                if (existingVendorId.HasValue)
                {
                    sql = $"""
                        UPDATE "dmscs"."Vendor"
                        SET "ContactName"=@ContactName, "ContactEmailAddress"=@ContactEmailAddress,
                            "LastModifiedAt"=@LastModifiedAt, "ModifiedBy"=@ModifiedBy
                        WHERE "Id" = @Id AND {TenantContext.TenantWhereClause()};
                        """;

                    await connection.ExecuteAsync(
                        sql,
                        new
                        {
                            command.ContactName,
                            command.ContactEmailAddress,
                            Id = existingVendorId.Value,
                            LastModifiedAt = auditContext.GetCurrentTimestamp(),
                            ModifiedBy = auditContext.GetCurrentUser(),
                            TenantId,
                        }
                    );

                    sql = "DELETE FROM \"dmscs\".\"VendorNamespacePrefix\" WHERE \"VendorId\" = @VendorId";
                    await connection.ExecuteAsync(sql, new { VendorId = existingVendorId.Value });
                    id = existingVendorId.Value;
                }
                else
                {
                    sql = """
                        INSERT INTO "dmscs"."Vendor" ("Company", "ContactName", "ContactEmailAddress", "CreatedBy", "TenantId")
                        VALUES (@Company, @ContactName, @ContactEmailAddress, @CreatedBy, @TenantId)
                        RETURNING "Id";
                        """;

                    id = await connection.ExecuteScalarAsync<long>(
                        sql,
                        new
                        {
                            command.Company,
                            command.ContactName,
                            command.ContactEmailAddress,
                            CreatedBy = auditContext.GetCurrentUser(),
                            TenantId,
                        }
                    );
                    isNewVendor = true;
                }

                sql = """
                    INSERT INTO "dmscs"."VendorNamespacePrefix" ("VendorId", "NamespacePrefix", "CreatedBy")
                    VALUES (@VendorId, @NamespacePrefix, @CreatedBy);
                    """;

                var currentUser = auditContext.GetCurrentUser();
                var namespacePrefixes = command
                    .NamespacePrefixes.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
                    .Select(p => new
                    {
                        VendorId = id,
                        NamespacePrefix = p.Trim(),
                        CreatedBy = currentUser,
                    });

                await connection.ExecuteAsync(sql, namespacePrefixes);
                await transaction.CommitAsync();

                return new VendorInsertResult.Success(id, isNewVendor);
            }
            catch (PostgresException ex)
                when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                    && ex.ConstraintName == "UX_Vendor_Company"
                )
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

        private static readonly IReadOnlyDictionary<string, string> OrderByColumns = new Dictionary<
            string,
            string
        >(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = "Id",
            ["company"] = "Company",
            ["contactName"] = "ContactName",
            ["contactEmailAddress"] = "ContactEmailAddress",
        };

        private static string BuildOrderByClause(VendorQuery query, string? tableAlias = null)
        {
            if (query.OrderBy is not null && OrderByColumns.TryGetValue(query.OrderBy, out var col))
            {
                return PostgresqlIdentifier.OrderBy(col, query.IsDescending, tableAlias);
            }

            return PostgresqlIdentifier.OrderBy(
                "Id",
                isDescending: tableAlias is not null && query.IsDescending,
                tableAlias
            );
        }

        private static string BuildFilterClause(VendorQuery query)
        {
            var conditions = new List<string>();
            if (query.Id.HasValue)
            {
                conditions.Add("\"Id\" = @Id");
            }
            if (query.Company is not null)
            {
                conditions.Add("\"Company\" = @Company");
            }
            if (query.ContactName is not null)
            {
                conditions.Add("\"ContactName\" = @ContactName");
            }
            if (query.ContactEmailAddress is not null)
            {
                conditions.Add("\"ContactEmailAddress\" = @ContactEmailAddress");
            }
            if (query.NamespacePrefixes is not null)
            {
                conditions.Add(
                    "EXISTS (SELECT 1 FROM \"dmscs\".\"VendorNamespacePrefix\" np WHERE np.\"VendorId\" = \"Id\" AND np.\"NamespacePrefix\" = @NamespacePrefixes)"
                );
            }
            return conditions.Count > 0 ? " AND " + string.Join(" AND ", conditions) : string.Empty;
        }

        public async Task<VendorQueryResult> QueryVendor(VendorQuery query)
        {
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            try
            {
                string orderByClause = BuildOrderByClause(query);
                string filterClause = BuildFilterClause(query);
                string outerOrderByClause = BuildOrderByClause(query, "v");
                var sql = $"""
                    SELECT v."Id", v."Company", v."ContactName", v."ContactEmailAddress", v."TenantId", p."NamespacePrefix"
                    FROM (SELECT * FROM "dmscs"."Vendor" WHERE {TenantContext.TenantWhereClause()}{filterClause} {orderByClause} {query.BuildPagingClause()}) AS v
                    LEFT OUTER JOIN "dmscs"."VendorNamespacePrefix" p ON v."Id" = p."VendorId"
                    {outerOrderByClause};
                    """;
                var vendors = await connection.QueryAsync<VendorResponse, string, VendorResponse>(
                    sql,
                    (vendor, namespacePrefix) =>
                    {
                        vendor.NamespacePrefixes = namespacePrefix;
                        return vendor;
                    },
                    param: new
                    {
                        query.Limit,
                        query.Offset,
                        TenantId,
                        query.Id,
                        query.Company,
                        query.ContactName,
                        query.ContactEmailAddress,
                        query.NamespacePrefixes,
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
                logger.LogError(ex, "Query vendor failure");
                return new VendorQueryResult.FailureUnknown(ex.Message);
            }
        }

        public async Task<VendorGetResult> GetVendor(long id)
        {
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            try
            {
                var sql = $"""
                    SELECT v."Id", v."Company", v."ContactName", v."ContactEmailAddress", v."TenantId", p."NamespacePrefix"
                    FROM "dmscs"."Vendor" v LEFT OUTER JOIN "dmscs"."VendorNamespacePrefix" p ON v."Id" = p."VendorId"
                    WHERE v."Id" = @Id AND {TenantContext.TenantWhereClause("v")};
                    """;
                var vendors = await connection.QueryAsync<VendorResponse, string, VendorResponse>(
                    sql,
                    (vendor, namespacePrefix) =>
                    {
                        vendor.NamespacePrefixes = namespacePrefix;
                        return vendor;
                    },
                    param: new { Id = id, TenantId },
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
            var sql = $"""
                UPDATE "dmscs"."Vendor"
                SET "Company"=@Company, "ContactName"=@ContactName, "ContactEmailAddress"=@ContactEmailAddress,
                    "LastModifiedAt"=@LastModifiedAt, "ModifiedBy"=@ModifiedBy
                WHERE "Id" = @Id AND {TenantContext.TenantWhereClause()};
                """;

            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                var affectedRows = await connection.ExecuteAsync(
                    sql,
                    new
                    {
                        command.Id,
                        command.Company,
                        command.ContactName,
                        command.ContactEmailAddress,
                        LastModifiedAt = auditContext.GetCurrentTimestamp(),
                        ModifiedBy = auditContext.GetCurrentUser(),
                        TenantId,
                    }
                );

                if (affectedRows == 0)
                {
                    return new VendorUpdateResult.FailureNotExists();
                }

                sql = "DELETE FROM \"dmscs\".\"VendorNamespacePrefix\" WHERE \"VendorId\" = @VendorId";
                await connection.ExecuteAsync(sql, new { VendorId = command.Id });

                sql = """
                    INSERT INTO "dmscs"."VendorNamespacePrefix" ("VendorId", "NamespacePrefix", "CreatedBy")
                    VALUES (@VendorId, @NamespacePrefix, @CreatedBy);
                    """;

                var currentUser = auditContext.GetCurrentUser();
                var namespacePrefixes = command
                    .NamespacePrefixes.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
                    .Select(p => new
                    {
                        VendorId = command.Id,
                        NamespacePrefix = p.Trim(),
                        CreatedBy = currentUser,
                    });

                await connection.ExecuteAsync(sql, namespacePrefixes);
                await transaction.CommitAsync();

                var apiClientSql = """
                    SELECT c."ClientUuid"
                    FROM "dmscs"."ApiClient" c
                        INNER JOIN "dmscs"."Application" a ON a."Id" = c."ApplicationId"
                        INNER JOIN "dmscs"."Vendor" v ON v."Id" = a."VendorId"
                    WHERE v."Id" = @VendorId
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
                var sql = $"""
                    DELETE FROM "dmscs"."VendorNamespacePrefix" WHERE "VendorId" IN (
                        SELECT "Id" FROM "dmscs"."Vendor" WHERE "Id" = @Id AND {TenantContext.TenantWhereClause()}
                    );
                    DELETE FROM "dmscs"."Vendor" WHERE "Id" = @Id AND {TenantContext.TenantWhereClause()};
                    """;

                var affectedRows = await connection.ExecuteAsync(sql, new { Id = id, TenantId });
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
                // First get applications with edOrgs
                string sqlEdOrgs = $"""
                    SELECT
                        v."Id" AS "VendorId", a."Id", a."ApplicationName", a."ClaimSetName",
                        -- Enabled: application is enabled only if ALL its ApiClients are approved (application-wide)
                        (SELECT COALESCE(BOOL_AND(ac."IsApproved"), true) FROM "dmscs"."ApiClient" ac WHERE ac."ApplicationId" = a."Id") AS "Enabled",
                        eo."EducationOrganizationId"
                    FROM "dmscs"."Vendor" v
                    LEFT OUTER JOIN "dmscs"."Application" a ON v."Id" = a."VendorId"
                    LEFT OUTER JOIN "dmscs"."ApplicationEducationOrganization" eo ON a."Id" = eo."ApplicationId"
                    WHERE v."Id" = @VendorId AND {TenantContext.TenantWhereClause("v")};
                    """;

                bool vendorExists = false;
                Dictionary<long, ApplicationResponse> response = [];

                await connection.QueryAsync<ApplicationResponse, long?, ApplicationResponse>(
                    sqlEdOrgs,
                    (application, educationOrganizationId) =>
                    {
                        vendorExists = application.VendorId == vendorId;
                        if (application.Id == 0)
                        {
                            // vendor exists without applications
                            return application;
                        }

                        if (response.TryGetValue(application.Id, out ApplicationResponse? thisApplication))
                        {
                            if (educationOrganizationId is not null)
                            {
                                thisApplication.EducationOrganizationIds.Add(educationOrganizationId.Value);
                            }
                        }
                        else
                        {
                            if (educationOrganizationId is not null)
                            {
                                application.EducationOrganizationIds.Add(educationOrganizationId.Value);
                            }
                            response.Add(application.Id, application);
                        }

                        return application;
                    },
                    param: new { VendorId = vendorId, TenantId },
                    splitOn: "EducationOrganizationId"
                );

                // Now get data store IDs for each application through ApiClient
                if (response.Any())
                {
                    string sqlDataStores = """
                            SELECT
                                a."Id" AS "ApplicationId",
                                acdi."DataStoreId"
                            FROM "dmscs"."Application" a
                            INNER JOIN "dmscs"."ApiClient" ac ON a."Id" = ac."ApplicationId"
                            INNER JOIN "dmscs"."ApiClientDataStore" acdi ON ac."Id" = acdi."ApiClientId"
                            WHERE a."VendorId" = @VendorId;
                        """;

                    await connection.QueryAsync<long, long, long>(
                        sqlDataStores,
                        (applicationId, dataStoreId) =>
                        {
                            if (
                                response.TryGetValue(applicationId, out ApplicationResponse? application)
                                && !application.DataStoreIds.Contains(dataStoreId)
                            )
                            {
                                application.DataStoreIds.Add(dataStoreId);
                            }
                            return applicationId;
                        },
                        param: new { VendorId = vendorId },
                        splitOn: "DataStoreId"
                    );

                    // Get Profile IDs for each application
                    string sqlProfiles = """
                            SELECT
                                ap."ApplicationId",
                                ap."ProfileId"
                            FROM "dmscs"."ApplicationProfile" ap
                            INNER JOIN "dmscs"."Application" a ON ap."ApplicationId" = a."Id"
                            WHERE a."VendorId" = @VendorId;
                        """;

                    await connection.QueryAsync<long, long, long>(
                        sqlProfiles,
                        (applicationId, profileId) =>
                        {
                            if (
                                response.TryGetValue(applicationId, out ApplicationResponse? application)
                                && !application.ProfileIds.Contains(profileId)
                            )
                            {
                                application.ProfileIds.Add(profileId);
                            }
                            return applicationId;
                        },
                        param: new { VendorId = vendorId },
                        splitOn: "ProfileId"
                    );
                }

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
