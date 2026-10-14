// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// UX_Descriptor_UriLowered_ResourceKeyId pins pg_c_utf8, so it folds the same way on a database whose default
/// collation folds non-ASCII text and on a C-locale database whose default does not.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Descriptor_Lowered_Uri_Index_On_Default_And_C_Locale_Databases
{
    private const string LoweredIndexName = "UX_Descriptor_UriLowered_ResourceKeyId";
    private const short BenchResourceKeyId = 32000;

    private static readonly string FixtureRelativePath = Path.Combine(
        "src",
        "dms",
        "backend",
        "EdFi.DataManagementService.Backend.Ddl.Tests.Unit",
        "Fixtures",
        "small",
        "minimal"
    );

    private PostgresqlGeneratedDdlTestDatabase _defaultDatabase = null!;
    private PostgresqlGeneratedDdlTestDatabase _cLocaleDatabase = null!;

    private IEnumerable<PostgresqlGeneratedDdlTestDatabase> Databases => [_defaultDatabase, _cLocaleDatabase];

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _defaultDatabase = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(
            fixture.GeneratedDdl
        );
        _cLocaleDatabase = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(
            fixture.GeneratedDdl,
            createDatabaseOptions: "TEMPLATE template0 ENCODING 'UTF8' LC_COLLATE 'C' LC_CTYPE 'C'"
        );

        foreach (var database in Databases)
        {
            await database.ExecuteNonQueryAsync(
                $"""
                INSERT INTO dms."ResourceKey" ("ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")
                VALUES ({BenchResourceKeyId}, 'Bench', 'BenchDescriptor', '1.0.0');
                """
            );
        }
    }

    [SetUp]
    public async Task Setup()
    {
        foreach (var database in Databases)
        {
            await database.ExecuteNonQueryAsync(
                """
                DELETE FROM dms."Descriptor";
                DELETE FROM dms."Document";
                """
            );
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_defaultDatabase is not null)
        {
            await _defaultDatabase.DisposeAsync();
        }
        if (_cLocaleDatabase is not null)
        {
            await _cLocaleDatabase.DisposeAsync();
        }
    }

    [Test]
    public async Task It_creates_a_c_locale_control_database_whose_default_does_not_fold_non_ascii_text()
    {
        var locale = await _cLocaleDatabase.QueryRowsAsync(
            """SELECT datcollate, datctype FROM pg_database WHERE datname = current_database();"""
        );
        locale.Single()["datcollate"].Should().Be("C");
        locale.Single()["datctype"].Should().Be("C");

        // An index that followed the database default would not fold É here.
        (await _cLocaleDatabase.ExecuteScalarAsync<string>("SELECT lower('É');"))
            .Should()
            .Be("É");
    }

    [Test]
    public async Task It_reports_the_same_pinned_index_definition_on_both_databases()
    {
        var definitions = new List<string>();
        foreach (var database in Databases)
        {
            definitions.Add(
                await database.ExecuteScalarAsync<string>(
                    $"""SELECT pg_get_indexdef('dms."{LoweredIndexName}"'::regclass);"""
                )
            );
        }

        definitions.Distinct().Should().ContainSingle();
        definitions[0].Should().Contain("COLLATE pg_c_utf8");

        // The PostgreSQL upgrade playbook tells operators to recreate the index from this exact definition.
        var upgradeGuide = await File.ReadAllTextAsync(
            Path.Combine(
                FixturePathResolver.FindRepositoryRoot(TestContext.CurrentContext.TestDirectory),
                "docs",
                "POSTGRESQL-MAJOR-UPGRADE.md"
            )
        );
        upgradeGuide.Should().Contain(definitions[0] + ";");
    }

    [Test]
    public async Task It_rejects_a_non_ascii_case_variant_under_one_resource_key_on_both_databases()
    {
        foreach (var database in Databases)
        {
            var resourceKeyId = await MinimalResourceKeyIdAsync(database);
            await InsertDescriptorAsync(database, resourceKeyId, "École");

            var act = async () => await InsertDescriptorAsync(database, resourceKeyId, "école");

            var exception = (await act.Should().ThrowAsync<PostgresException>()).Which;
            exception.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
            exception.ConstraintName.Should().Be(LoweredIndexName);
        }
    }

    [Test]
    public async Task It_allows_the_same_case_variant_under_another_resource_key_on_both_databases()
    {
        foreach (var database in Databases)
        {
            await InsertDescriptorAsync(database, await MinimalResourceKeyIdAsync(database), "École");
            await InsertDescriptorAsync(database, BenchResourceKeyId, "école");

            (await database.ExecuteScalarAsync<long>("""SELECT COUNT(*) FROM dms."Descriptor";"""))
                .Should()
                .Be(2L);
        }
    }

    [TestCase("uri://ed-fi.org/SexDescriptor", "ABC")]
    [TestCase("uri://ed-fi.org/SexDescriptor", "ÀÉÎÕÜ")]
    [TestCase("uri://ed-fi.org/SexDescriptor", "ΑΒΓΣ")]
    public async Task It_folds_the_pinned_expression_identically_on_both_databases(
        string ns,
        string codeValue
    )
    {
        var folded = new List<string>();
        foreach (var database in Databases)
        {
            folded.Add(
                await database.ExecuteScalarAsync<string>(
                    """SELECT lower((@ns || '#' || @codeValue) COLLATE "pg_c_utf8");""",
                    new NpgsqlParameter("ns", ns),
                    new NpgsqlParameter("codeValue", codeValue)
                )
            );
        }

        folded.Distinct().Should().ContainSingle();
        folded[0].Should().Be((ns + "#" + codeValue).ToLowerInvariant());
    }

    private static Task<short> MinimalResourceKeyIdAsync(PostgresqlGeneratedDdlTestDatabase database) =>
        database.ExecuteScalarAsync<short>("""SELECT MIN("ResourceKeyId") FROM dms."ResourceKey";""");

    private static async Task InsertDescriptorAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        short resourceKeyId,
        string codeValue
    )
    {
        var documentId = await database.ExecuteScalarAsync<long>(
            """
            INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@uuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("uuid", Guid.NewGuid()),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO dms."Descriptor" ("DocumentId", "ResourceKeyId", "Namespace", "CodeValue", "ShortDescription")
            VALUES (@documentId, @resourceKeyId, 'uri://ed-fi.org/SexDescriptor', @codeValue, @codeValue);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId),
            new NpgsqlParameter("codeValue", codeValue)
        );
    }
}
