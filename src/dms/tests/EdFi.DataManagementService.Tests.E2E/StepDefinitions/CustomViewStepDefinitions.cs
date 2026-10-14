// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions;

/// <summary>
/// Provisions the <c>auth.{StrategyName}</c> custom authorization views a scenario configures. The DDL and
/// identifier quoting differ per engine, so each step builds its statements from
/// <see cref="AppSettings.DatabaseEngine"/> and connects with the host-side admin connection string the E2E
/// orchestration already resolved (<see cref="AppSettings.DataStoreAdminConnectionString"/>) rather than
/// re-deriving a host, port, or credentials here.
/// </summary>
/// <remarks>
/// Every view projects the basis resource's <c>DocumentId</c>, which is the DMS custom-view contract
/// (auth.md § "Custom view-based authorization strategy"). The steps mirror the views the ODS
/// integration test harness ships for its "Custom View-Based Authorization Test Suite" Postman collection:
/// a person basis driven by enrollments, an Assessment basis on a composite natural key, an abstract
/// EducationOrganization basis, and a descriptor basis.
/// </remarks>
[Binding]
public static class CustomViewStepDefinitions
{
    private static readonly (string Schema, string Table) StudentTable = ("edfi", "Student");
    private static readonly (string Schema, string Table) StudentSectionAssociationTable = (
        "edfi",
        "StudentSectionAssociation"
    );
    private static readonly (string Schema, string Table) AssessmentTable = ("edfi", "Assessment");

    [Given("descriptor document IDs are outside the compact ID range")]
    public static async Task GivenDescriptorDocumentIdsAreOutsideTheCompactIdRange()
    {
        await using DbConnection connection = CreateConnection();
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(
            connection,
            IsMssql
                ? CompactDescriptorSeedSupport.MssqlSeparateDocumentIdsSql
                : CompactDescriptorSeedSupport.PostgresqlSeparateDocumentIdsSql
        );
    }

    [Then("the custom auth view {string} contains exactly these {string} descriptor code values")]
    public static async Task ThenTheCustomAuthViewContainsExactlyTheseDescriptorCodeValues(
        string strategyName,
        string descriptorName,
        DataTable codeValues
    )
    {
        ValidateIdentifier(strategyName, nameof(strategyName));
        ValidateIdentifier(descriptorName, nameof(descriptorName));
        await using DbConnection connection = CreateConnection();
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT d.{Quote("DescriptorId")}, d.{Quote("DocumentId")}, d.{Quote("CodeValue")}
            FROM {Quote("dms")}.{Quote("Descriptor")} d
            INNER JOIN {Quote("dms")}.{Quote("ResourceKey")} rk
                ON rk.{Quote("ResourceKeyId")} = d.{Quote("ResourceKeyId")}
            WHERE rk.{Quote("ProjectName")} = {Literal("Ed-Fi")}
                AND rk.{Quote("ResourceName")} = {Literal(descriptorName)};
            """;
        List<long> expectedDocumentIds = [];
        HashSet<string> expectedCodeValues = codeValues.Rows.Select(row => row["codeValue"]).ToHashSet();
        HashSet<string> foundCodeValues = [];
        await using (DbDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                int descriptorId = reader.GetInt32(0);
                long documentId = reader.GetInt64(1);
                documentId.Should().BeGreaterThan(int.MaxValue).And.NotBe(descriptorId);
                string codeValue = reader.GetString(2);
                if (expectedCodeValues.Contains(codeValue))
                {
                    expectedDocumentIds.Add(documentId);
                    foundCodeValues.Add(codeValue);
                }
            }
        }
        foundCodeValues.Should().BeEquivalentTo(expectedCodeValues);
        (await ReadViewDocumentIdsAsync(connection, strategyName))
            .Should()
            .BeEquivalentTo(expectedDocumentIds);

        // A same-named type with identical components in another project must stay outside the view.
        // Roll back its temporary catalog/document/descriptor rows before subsequent API requests.
        await using DbTransaction transaction = await connection.BeginTransactionAsync();
        command.Transaction = transaction;
        command.CommandText = $"""
            {(
                IsMssql
                    ? "SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET ARITHABORT ON; SET CONCAT_NULL_YIELDS_NULL ON; SET QUOTED_IDENTIFIER ON; SET NUMERIC_ROUNDABORT OFF;"
                    : string.Empty
            )}
            INSERT INTO {Quote("dms")}.{Quote("ResourceKey")}
                ({Quote("ResourceKeyId")}, {Quote("ProjectName")}, {Quote("ResourceName")}, {Quote(
                "ResourceVersion"
            )})
            SELECT MAX({Quote("ResourceKeyId")}) + 1, 'E2E-OtherProject', {Literal(descriptorName)}, '1.0.0'
            FROM {Quote("dms")}.{Quote("ResourceKey")};
            INSERT INTO {Quote("dms")}.{Quote("Document")} ({Quote("DocumentUuid")}, {Quote("ResourceKeyId")})
            SELECT @documentUuid, {Quote("ResourceKeyId")}
            FROM {Quote("dms")}.{Quote("ResourceKey")}
            WHERE {Quote("ProjectName")} = 'E2E-OtherProject' AND {Quote("ResourceName")} = {Literal(
                descriptorName
            )};
            INSERT INTO {Quote("dms")}.{Quote("Descriptor")}
                ({Quote("DocumentId")}, {Quote("ResourceKeyId")}, {Quote("Namespace")}, {Quote(
                "CodeValue"
            )}, {Quote("ShortDescription")})
            SELECT doc.{Quote("DocumentId")}, doc.{Quote("ResourceKeyId")}, d.{Quote("Namespace")}, d.{Quote(
                "CodeValue"
            )}, d.{Quote("ShortDescription")}
            FROM {Quote("dms")}.{Quote("Document")} doc
            CROSS JOIN {Quote("dms")}.{Quote("Descriptor")} d
            WHERE doc.{Quote("DocumentUuid")} = @documentUuid AND d.{Quote("DocumentId")} = @sourceDocumentId;
            """;
        AddParameter(command, "@documentUuid", Guid.NewGuid());
        AddParameter(command, "@sourceDocumentId", expectedDocumentIds[0]);
        (await command.ExecuteNonQueryAsync()).Should().Be(3);
        (await ReadViewDocumentIdsAsync(connection, strategyName, transaction))
            .Should()
            .BeEquivalentTo(expectedDocumentIds);
        await transaction.RollbackAsync();
    }

    [Then(
        "the custom auth view {string} uses compact category keys for School {int} and LocalEducationAgency {int}"
    )]
    public static async Task ThenTheCustomAuthViewUsesCompactCategoryKeys(
        string strategyName,
        int schoolId,
        int localEducationAgencyId
    )
    {
        ValidateIdentifier(strategyName, nameof(strategyName));
        await using DbConnection connection = CreateConnection();
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        string SelectCategory(string resource, string table, string ownerColumn, string idParameter) =>
            $"""
                SELECT r.{Quote("DocumentId")}, d.{Quote("DescriptorId")}, d.{Quote("DocumentId")}
                FROM {Quote("edfi")}.{Quote(resource)} r
                INNER JOIN {Quote("edfi")}.{Quote(table)} c ON c.{Quote(ownerColumn)} = r.{Quote(
                    "DocumentId"
                )}
                INNER JOIN {Quote("dms")}.{Quote("Descriptor")} d
                    ON d.{Quote("DescriptorId")} = c.{Quote(
                    "EducationOrganizationCategoryDescriptor_DescriptorId"
                )}
                WHERE r.{Quote(resource + "Id")} = {idParameter}
                """;
        command.CommandText = $"""
            {SelectCategory(
                "School",
                "SchoolEducationOrganizationCategory",
                "School_DocumentId",
                "@schoolId"
            )};
            {SelectCategory(
                "LocalEducationAgency",
                "LocalEducationAgencyCategory",
                "LocalEducationAgency_DocumentId",
                "@agencyId"
            )};
            """;
        AddParameter(command, "@schoolId", schoolId);
        AddParameter(command, "@agencyId", localEducationAgencyId);
        List<long> ownerDocumentIds = [];
        List<int> descriptorIds = [];
        await using (DbDataReader reader = await command.ExecuteReaderAsync())
        {
            do
            {
                (await reader.ReadAsync()).Should().BeTrue();
                ownerDocumentIds.Add(reader.GetInt64(0));
                int descriptorId = reader.GetInt32(1);
                reader.GetInt64(2).Should().BeGreaterThan(int.MaxValue).And.NotBe(descriptorId);
                descriptorIds.Add(descriptorId);
                (await reader.ReadAsync()).Should().BeFalse();
            } while (await reader.NextResultAsync());
        }
        ownerDocumentIds.Should().HaveCount(2);
        (await ReadViewDocumentIdsAsync(connection, strategyName)).Should().Equal(ownerDocumentIds[0]);

        // Exercise the LEA half of the UNION with a qualifying category as well as the School half.
        await using DbTransaction transaction = await connection.BeginTransactionAsync();
        command.Transaction = transaction;
        command.Parameters.Clear();
        AddParameter(command, "@descriptorId", descriptorIds[1]);
        command.CommandText = $"""
            {(IsMssql ? "SET ARITHABORT ON;" : string.Empty)}
            UPDATE {Quote("dms")}.{Quote("Descriptor")} SET {Quote("CodeValue")} = 'State Agency'
            WHERE {Quote("DescriptorId")} = @descriptorId;
            """;
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
        (await ReadViewDocumentIdsAsync(connection, strategyName, transaction))
            .Should()
            .BeEquivalentTo(ownerDocumentIds);
        await transaction.RollbackAsync();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task<List<long>> ReadViewDocumentIdsAsync(
        DbConnection connection,
        string strategyName,
        DbTransaction? transaction = null
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Quote("DocumentId")} FROM {Quote("auth")}.{Quote(strategyName)};";
        List<long> documentIds = [];
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            documentIds.Add(reader.GetInt64(0));
        }
        return documentIds;
    }

    [Given("the custom auth view {string} authorizes Student {string}")]
    public static async Task GivenTheCustomAuthViewAuthorizesStudent(
        string strategyName,
        string studentUniqueId
    )
    {
        await CreateCustomAuthViewAsync(
            strategyName,
            selectList: Quote("DocumentId"),
            source: StudentTable,
            whereClause: $"{Quote("StudentUniqueId")} = {Literal(studentUniqueId)}"
        );
    }

    [Given("the custom auth view {string} authorizes no Students")]
    public static async Task GivenTheCustomAuthViewAuthorizesNoStudents(string strategyName)
    {
        await CreateCustomAuthViewAsync(
            strategyName,
            selectList: Quote("DocumentId"),
            source: StudentTable,
            whereClause: "1 = 0"
        );
    }

    [Given("the custom auth view {string} omits DocumentId")]
    public static async Task GivenTheCustomAuthViewOmitsDocumentId(string strategyName)
    {
        await CreateCustomAuthViewAsync(
            strategyName,
            selectList: Quote("StudentUniqueId"),
            source: StudentTable
        );
    }

    /// <summary>
    /// A data-driven person basis: the view authorizes every Student that currently has a
    /// StudentSectionAssociation, so creating or deleting an enrollment flips authorization without
    /// touching the view. This is the DMS analog of the ODS harness's
    /// <c>auth.StudentWithCTECourseEnrollments</c> view over <c>edfi.StudentSectionAssociation</c>.
    /// </summary>
    [Given("the custom auth view {string} authorizes Students with a section enrollment")]
    public static async Task GivenTheCustomAuthViewAuthorizesStudentsWithASectionEnrollment(
        string strategyName
    )
    {
        await CreateCustomAuthViewAsync(
            strategyName,
            selectList: $"DISTINCT {Quote("Student_DocumentId")} AS {Quote("DocumentId")}",
            source: StudentSectionAssociationTable
        );
    }

    /// <summary>
    /// A composite-natural-key basis (Assessment is identified by identifier + namespace). Mirrors the ODS
    /// harness's <c>auth.AssessmentWithAnACTIdentifier</c> view.
    /// </summary>
    [Given("the custom auth view {string} authorizes Assessments whose identifier starts with {string}")]
    public static async Task GivenTheCustomAuthViewAuthorizesAssessmentsWhoseIdentifierStartsWith(
        string strategyName,
        string identifierPrefix
    )
    {
        await CreateCustomAuthViewAsync(
            strategyName,
            selectList: Quote("DocumentId"),
            source: AssessmentTable,
            whereClause: $"{Quote("AssessmentIdentifier")} LIKE {Literal(EscapeLikePattern(identifierPrefix) + "%")}"
        );
    }

    /// <summary>
    /// An abstract EducationOrganization basis: every School or Local Education Agency whose
    /// <c>educationOrganizationCategories</c> contains a descriptor code value starting with an "S" word
    /// ("School" qualifies, "Local Education Agency" does not). The subject resource references
    /// <c>EducationOrganization</c>, so DMS resolves the basis through the education organization union view.
    /// Same predicate as the ODS harness's <c>auth.EducationOrganizationWithACategoryContainingAnSWord</c>
    /// view, projected as the member's <c>DocumentId</c>.
    /// </summary>
    [Given(
        "the custom auth view {string} authorizes education organizations with a category containing an S word"
    )]
    public static async Task GivenTheCustomAuthViewAuthorizesEducationOrganizationsWithACategoryContainingAnSWord(
        string strategyName
    )
    {
        string categoryPredicate =
            $"{Quote("CodeValue")} LIKE {Literal("S%")} OR {Quote("CodeValue")} LIKE {Literal("% S%")}";

        string SelectMembers(string categoryTable, string ownerColumn) =>
            $"""
                SELECT DISTINCT c.{Quote(ownerColumn)} AS {Quote("DocumentId")}
                FROM {Quote("edfi")}.{Quote(categoryTable)} c
                    INNER JOIN {Quote("dms")}.{Quote("Descriptor")} d
                        ON d.{Quote("DescriptorId")} = c.{Quote(
                    "EducationOrganizationCategoryDescriptor_DescriptorId"
                )}
                WHERE {categoryPredicate}
                """;

        await CreateCustomAuthViewFromQueryAsync(
            strategyName,
            $"""
            {SelectMembers("SchoolEducationOrganizationCategory", "School_DocumentId")}
            UNION
            {SelectMembers("LocalEducationAgencyCategory", "LocalEducationAgency_DocumentId")}
            """
        );
    }

    /// <summary>
    /// A descriptor basis over the shared <c>dms.Descriptor</c> table. Mirrors the ODS harness's
    /// <c>auth.TransportationTypeDescriptorWithABus</c> view.
    /// </summary>
    [Given(
        "the custom auth view {string} authorizes {string} descriptors whose code value contains {string}"
    )]
    public static async Task GivenTheCustomAuthViewAuthorizesDescriptorsWhoseCodeValueContains(
        string strategyName,
        string descriptorName,
        string codeValueFragment
    )
    {
        ValidateIdentifier(descriptorName, nameof(descriptorName));

        // Resource keys qualify the descriptor type; membership uses its owning document key.
        await CreateCustomAuthViewFromQueryAsync(
            strategyName,
            $"""
            SELECT d.{Quote("DocumentId")}
            FROM {Quote("dms")}.{Quote("Descriptor")} d
                INNER JOIN {Quote("dms")}.{Quote("ResourceKey")} rk
                    ON rk.{Quote("ResourceKeyId")} = d.{Quote("ResourceKeyId")}
            WHERE rk.{Quote("ProjectName")} = {Literal("Ed-Fi")}
                AND rk.{Quote("ResourceName")} = {Literal(descriptorName)}
                AND d.{Quote("CodeValue")} LIKE {Literal("%" + EscapeLikePattern(codeValueFragment) + "%")}
            """
        );
    }

    /// <summary>
    /// Drops any existing <c>auth.{strategyName}</c> object and creates the view over
    /// <paramref name="source"/>. SQL Server has no <c>CREATE OR REPLACE VIEW</c>, so both engines take the
    /// drop-then-create path.
    /// </summary>
    private static async Task CreateCustomAuthViewAsync(
        string strategyName,
        string selectList,
        (string Schema, string Table) source,
        string? whereClause = null
    )
    {
        var where = whereClause is null ? string.Empty : $"{Environment.NewLine}WHERE {whereClause}";

        await CreateCustomAuthViewFromQueryAsync(
            strategyName,
            $"""
            SELECT {selectList}
            FROM {Quote(source.Schema)}.{Quote(source.Table)}{where}
            """
        );
    }

    /// <summary>
    /// Drops any existing <c>auth.{strategyName}</c> object and creates the view as
    /// <paramref name="selectQuery"/>, an already engine-quoted SELECT (a UNION is fine).
    /// </summary>
    private static async Task CreateCustomAuthViewFromQueryAsync(string strategyName, string selectQuery)
    {
        ValidateIdentifier(strategyName, nameof(strategyName));

        await using DbConnection connection = CreateConnection();
        await connection.OpenAsync();

        foreach (var sql in BuildAuthObjectResetStatements(strategyName))
        {
            await ExecuteNonQueryAsync(connection, sql);
        }

        await ExecuteNonQueryAsync(
            connection,
            $"""
            CREATE VIEW {Quote("auth")}.{Quote(strategyName)} AS
            {selectQuery};
            """
        );
    }

    /// <summary>
    /// Statements that make <c>auth.{strategyName}</c> absent and the <c>auth</c> schema present. SQL
    /// Server rejects <c>CREATE SCHEMA</c> outside its own batch and has no <c>IF NOT EXISTS</c> form, so
    /// it is guarded with a catalog check instead of PostgreSQL's <c>CREATE SCHEMA IF NOT EXISTS</c>.
    /// </summary>
    private static IReadOnlyList<string> BuildAuthObjectResetStatements(string strategyName)
    {
        if (IsMssql)
        {
            return
            [
                "IF SCHEMA_ID('auth') IS NULL EXEC('CREATE SCHEMA [auth];');",
                $"DROP VIEW IF EXISTS {Quote("auth")}.{Quote(strategyName)};",
                $"DROP TABLE IF EXISTS {Quote("auth")}.{Quote(strategyName)};",
                // A synonym would also resolve as auth.{StrategyName} and shadow the created view.
                $"IF EXISTS (SELECT 1 FROM sys.synonyms WHERE name = {Literal(strategyName)} AND schema_id = SCHEMA_ID('auth')) DROP SYNONYM {Quote("auth")}.{Quote(strategyName)};",
            ];
        }

        return
        [
            "CREATE SCHEMA IF NOT EXISTS auth;",
            $"DROP VIEW IF EXISTS {Quote("auth")}.{Quote(strategyName)};",
            $"DROP TABLE IF EXISTS {Quote("auth")}.{Quote(strategyName)};",
        ];
    }

    private static async Task ExecuteNonQueryAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static DbConnection CreateConnection()
    {
        var connectionString = AppSettings.DataStoreAdminConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Custom auth view provisioning requires the host-side data-store admin connection string; "
                    + "run the E2E suite through the standard orchestration so AppSettings:DataStoreAdminConnectionString is set."
            );
        }

        return IsMssql ? new SqlConnection(connectionString) : new NpgsqlConnection(connectionString);
    }

    private static bool IsMssql =>
        string.Equals(AppSettings.DatabaseEngine, "mssql", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Quotes an identifier for the selected engine: brackets on SQL Server, double quotes on PostgreSQL.
    /// Identifiers reaching here are either literals in this file or already validated by
    /// <see cref="ValidateIdentifier"/>, so no embedded delimiter can appear; the doubling is defensive.
    /// </summary>
    private static string Quote(string identifier) =>
        IsMssql
            ? $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]"
            : $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>
    /// A single-quoted string literal with embedded quotes doubled; the same syntax is valid on both engines.
    /// </summary>
    private static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>
    /// Escapes the LIKE wildcards in a value that must match literally; both engines treat <c>\</c> as the
    /// escape character when the pattern is used without an ESCAPE clause on PostgreSQL, so the bracket form
    /// SQL Server accepts is used there instead.
    /// </summary>
    private static string EscapeLikePattern(string value) =>
        IsMssql
            ? value
                .Replace("[", "[[]", StringComparison.Ordinal)
                .Replace("%", "[%]", StringComparison.Ordinal)
                .Replace("_", "[_]", StringComparison.Ordinal)
            : value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);

    private static void ValidateIdentifier(string identifier, string parameterName)
    {
        if (!Regex.IsMatch(identifier, "^[A-Za-z][A-Za-z0-9_]*$"))
        {
            throw new ArgumentException(
                $"Invalid custom auth view identifier '{identifier}'.",
                parameterName
            );
        }
    }
}
