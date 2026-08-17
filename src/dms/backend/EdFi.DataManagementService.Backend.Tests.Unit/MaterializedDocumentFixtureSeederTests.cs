// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_MaterializedDocumentFixtureSeeder
{
    private MaterializedDocumentFixture _ordinaryFixture = null!;
    private MaterializedDocumentFixture _descriptorFixture = null!;
    private MaterializedDocumentFixture _extensionFixture = null!;
    private MaterializedDocumentFixture _failureFixture = null!;

    [SetUp]
    public void Setup()
    {
        _ordinaryFixture = MaterializedDocumentFixtureCatalog.LoadCase(
            TestContext.CurrentContext.TestDirectory,
            "ordinary-link-bearing-student-school-association"
        );
        _descriptorFixture = MaterializedDocumentFixtureCatalog.LoadCase(
            TestContext.CurrentContext.TestDirectory,
            "descriptor-school-type"
        );
        _extensionFixture = MaterializedDocumentFixtureCatalog.LoadCase(
            TestContext.CurrentContext.TestDirectory,
            "extension-student-school-association"
        );
        _failureFixture = MaterializedDocumentFixtureCatalog.LoadCase(
            TestContext.CurrentContext.TestDirectory,
            "invariant-missing-school-body"
        );
    }

    [Test]
    public void It_builds_Postgresql_setup_from_provider_neutral_fixture_rows()
    {
        var commands = new MaterializedDocumentFixtureSeeder(
            MaterializedDocumentFixtureSqlDialect.Postgresql
        ).BuildSetupCommands(_ordinaryFixture);

        commands
            .Select(command => command.CommandText)
            .Should()
            .Contain(command =>
                command.Contains("CREATE SCHEMA IF NOT EXISTS \"edfi\"", StringComparison.Ordinal)
            );
        commands
            .Select(command => command.CommandText)
            .Should()
            .Contain(command =>
                command.Contains("CREATE TABLE IF NOT EXISTS \"dms\".\"Document\"", StringComparison.Ordinal)
            );
        commands
            .Select(command => command.CommandText)
            .Should()
            .Contain(command =>
                command.Contains(
                    "\"ContentLastModifiedAt\" timestamp with time zone",
                    StringComparison.Ordinal
                )
            );
        commands
            .Select(command => command.CommandText)
            .Should()
            .Contain(command =>
                command.Contains(
                    "CREATE TABLE IF NOT EXISTS \"edfi\".\"StudentSchoolAssociation\"",
                    StringComparison.Ordinal
                )
            );

        var documentInsert = commands.Single(command =>
            command.CommandText.StartsWith("INSERT INTO \"dms\".\"Document\"", StringComparison.Ordinal)
            && command.Parameters.Any(parameter => Equals(parameter.Value, 970101L))
        );

        documentInsert
            .CommandText.Should()
            .Be(
                "INSERT INTO \"dms\".\"Document\" (\"DocumentId\", \"DocumentUuid\", \"ResourceKeyId\", \"CreatedByOwnershipTokenId\", \"ContentVersion\", \"ContentLastModifiedAt\", \"CreatedAt\") VALUES (@p0, @p1, @p2, NULL, @p3, @p4, @p4)"
            );
        documentInsert.Parameters.Should().Contain(parameter => Equals(parameter.Value, 970101L));
        documentInsert
            .Parameters.Any(parameter =>
                parameter.Value is DateTimeOffset value
                && value == _ordinaryFixture.SourceSetup.Documents[0].ContentLastModifiedAt
            )
            .Should()
            .BeTrue();

        var sourceInsert = commands.Single(command =>
            command.CommandText.StartsWith(
                "INSERT INTO \"edfi\".\"StudentSchoolAssociation\"",
                StringComparison.Ordinal
            )
        );
        sourceInsert.Parameters.Should().Contain(parameter => Equals(parameter.Value, 970201L));
        sourceInsert.Parameters.Should().Contain(parameter => Equals(parameter.Value, 255901));

        commands
            .Select(command => command.CommandText)
            .Should()
            .Contain(command =>
                command.Contains("\"ContentLastModifiedAt\" = @p1", StringComparison.Ordinal)
            );
    }

    [Test]
    public void It_builds_Mssql_setup_with_provider_specific_identifiers_and_scalar_types()
    {
        var commands = new MaterializedDocumentFixtureSeeder(
            MaterializedDocumentFixtureSqlDialect.Mssql
        ).BuildSetupCommands(_descriptorFixture);

        commands
            .Select(command => command.CommandText)
            .Should()
            .Contain(command => command.Contains("IF SCHEMA_ID(N'dms') IS NULL", StringComparison.Ordinal));
        commands
            .Select(command => command.CommandText)
            .Should()
            .Contain(command =>
                command.Contains("IF OBJECT_ID(N'[dms].[Document]', N'U') IS NULL", StringComparison.Ordinal)
            );
        commands
            .Select(command => command.CommandText)
            .Should()
            .Contain(command =>
                command.Contains("[ContentLastModifiedAt] datetime2(7)", StringComparison.Ordinal)
            );

        var documentInsert = commands.Single(command =>
            command.CommandText.StartsWith("INSERT INTO [dms].[Document]", StringComparison.Ordinal)
        );
        documentInsert
            .CommandText.Should()
            .Be(
                "INSERT INTO [dms].[Document] ([DocumentId], [DocumentUuid], [ResourceKeyId], [CreatedByOwnershipTokenId], [ContentVersion], [ContentLastModifiedAt], [CreatedAt]) VALUES (@p0, @p1, @p2, NULL, @p3, @p4, @p4)"
            );

        var descriptorInsert = commands.Single(command =>
            command.CommandText.StartsWith("INSERT INTO [dms].[Descriptor]", StringComparison.Ordinal)
        );

        descriptorInsert
            .CommandText.Should()
            .Contain("[EffectiveBeginDate]")
            .And.Contain("[Discriminator]")
            .And.Contain("[Uri]");
        descriptorInsert
            .Parameters.Any(parameter =>
                parameter.Value is DateOnly value && value == new DateOnly(2025, 1, 15)
            )
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_builds_descriptor_setup_when_optional_descriptor_fields_are_absent()
    {
        var commands = new MaterializedDocumentFixtureSeeder(
            MaterializedDocumentFixtureSqlDialect.Postgresql
        ).BuildSetupCommands(_extensionFixture);

        var descriptorInsert = commands.Single(command =>
            command.CommandText.StartsWith("INSERT INTO \"dms\".\"Descriptor\"", StringComparison.Ordinal)
            && command.Parameters.Any(parameter => Equals(parameter.Value, "MembershipTypeDescriptor"))
        );

        descriptorInsert.Parameters.Any(parameter => parameter.Value is null).Should().BeTrue();
    }

    [Test]
    public void It_keeps_date_shaped_strings_as_text_when_the_column_is_not_a_date_column()
    {
        var fixture = new MaterializedDocumentFixture(
            CaseDirectory: "",
            Manifest: new MaterializedDocumentFixtureManifest(
                FixtureVersion: "materialized-document-fixture-v1",
                CaseName: "date-shaped-text-column",
                CoverageTags: null,
                SourceSetupPath: "source-setup.json",
                ExpectedCacheRowPath: null,
                ExpectedStreamEtagPath: null,
                ExpectedPublicCdcDocumentPath: null,
                ExpectedProjectionFailurePath: null
            ),
            SourceSetup: new MaterializedDocumentSourceSetup(
                Documents:
                [
                    new MaterializedDocumentSourceDocument(
                        DocumentId: 1,
                        DocumentUuid: "11111111-2222-3333-4444-555555555555",
                        ResourceKeyId: 1,
                        ContentVersion: 1,
                        ContentLastModifiedAt: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
                    ),
                ],
                Descriptors: [],
                ConcreteRootRows:
                [
                    new MaterializedDocumentSourceTableRow(
                        Schema: "edfi",
                        Table: "Student",
                        DocumentId: 1,
                        Values: new JsonObject { ["BirthDate"] = "2026-01-02", ["LocalCode"] = "2026-01-02" }
                    ),
                ],
                ChildRows: [],
                ExtensionRows: [],
                ReferentialIdentityRows: []
            ),
            ExpectedCacheRow: null,
            ExpectedStreamEtag: null,
            ExpectedPublicCdcDocument: null,
            ExpectedProjectionFailure: null
        );

        var commands = new MaterializedDocumentFixtureSeeder(
            MaterializedDocumentFixtureSqlDialect.Postgresql
        ).BuildSetupCommands(fixture);

        commands
            .Select(command => command.CommandText)
            .Should()
            .Contain(command =>
                command.Contains("\"BirthDate\" date", StringComparison.Ordinal)
                && command.Contains("\"LocalCode\" varchar(1024)", StringComparison.Ordinal)
            );

        var sourceInsert = commands.Single(command =>
            command.CommandText.StartsWith("INSERT INTO \"edfi\".\"Student\"", StringComparison.Ordinal)
        );
        sourceInsert
            .Parameters.Any(parameter =>
                parameter.Value is DateOnly value && value == new DateOnly(2026, 1, 2)
            )
            .Should()
            .BeTrue();
        sourceInsert.Parameters.Should().Contain(parameter => Equals(parameter.Value, "2026-01-02"));
    }

    [Test]
    public void It_can_target_an_existing_generated_schema_without_creating_tables()
    {
        var commands = new MaterializedDocumentFixtureSeeder(
            MaterializedDocumentFixtureSqlDialect.Postgresql,
            new MaterializedDocumentFixtureSeederOptions { CreateSchemasAndTables = false }
        ).BuildSetupCommands(_failureFixture);

        commands
            .Select(command => command.CommandText)
            .Should()
            .NotContain(command => command.Contains("CREATE TABLE", StringComparison.Ordinal));
        commands
            .Select(command => command.CommandText)
            .Should()
            .Contain(command =>
                command.StartsWith("INSERT INTO \"dms\".\"Document\"", StringComparison.Ordinal)
                && command.Contains("OVERRIDING SYSTEM VALUE", StringComparison.Ordinal)
            );
        commands
            .Select(command => command.CommandText)
            .Should()
            .Contain(command => command.StartsWith("UPDATE \"dms\".\"Document\"", StringComparison.Ordinal));

        var stampUpdate = commands.Single(command =>
            command.CommandText.StartsWith("UPDATE \"dms\".\"Document\"", StringComparison.Ordinal)
        );
        stampUpdate
            .Parameters.Any(parameter =>
                parameter.Value is DateTimeOffset value
                && value == _failureFixture.SourceSetup.Documents[0].ContentLastModifiedAt
            )
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_generates_a_valid_mssql_document_stamp_update_without_a_trailing_comma()
    {
        var commands = new MaterializedDocumentFixtureSeeder(
            MaterializedDocumentFixtureSqlDialect.Mssql,
            new MaterializedDocumentFixtureSeederOptions { CreateSchemasAndTables = false }
        ).BuildSetupCommands(_failureFixture);

        var stampUpdate = commands.Single(command =>
            command.CommandText.StartsWith("UPDATE [dms].[Document]", StringComparison.Ordinal)
        );

        stampUpdate
            .CommandText.Should()
            .Be(
                """
                UPDATE [dms].[Document]
                SET [ContentVersion] = @p0,
                    [ContentLastModifiedAt] = @p1
                WHERE [DocumentId] = @p2
                """
            );
    }

    [Test]
    public void It_wraps_document_identity_insert_when_targeting_an_existing_mssql_generated_schema()
    {
        var commands = new MaterializedDocumentFixtureSeeder(
            MaterializedDocumentFixtureSqlDialect.Mssql,
            new MaterializedDocumentFixtureSeederOptions { CreateSchemasAndTables = false }
        ).BuildSetupCommands(_failureFixture);

        commands
            .Select(command => command.CommandText)
            .Should()
            .NotContain(command => command.Contains("CREATE TABLE", StringComparison.Ordinal));

        var documentInsert = commands.Single(command =>
            command.CommandText.Contains("INSERT INTO [dms].[Document]", StringComparison.Ordinal)
            && command.Parameters.Any(parameter =>
                Equals(parameter.Value, _failureFixture.SourceSetup.Documents[0].DocumentId)
            )
        );

        documentInsert
            .CommandText.Should()
            .Contain("SET IDENTITY_INSERT [dms].[Document] ON")
            .And.Contain("INSERT INTO [dms].[Document]")
            .And.Contain("SET IDENTITY_INSERT [dms].[Document] OFF");
        documentInsert
            .Parameters.Any(parameter =>
                parameter.Value is DateTime value
                && value == _failureFixture.SourceSetup.Documents[0].ContentLastModifiedAt.UtcDateTime
                && parameter.ConfigureParameter is not null
            )
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_asserts_success_candidates_against_fixture_cache_rows_structurally()
    {
        var expected = _ordinaryFixture.ExpectedCacheRow!;
        var candidate = new MaterializedDocumentFixtureActualCacheRow(
            expected.DocumentId,
            expected.DocumentUuid,
            expected.ProjectName,
            expected.ResourceName,
            expected.ResourceVersion,
            expected.ContentVersion,
            expected.LastModifiedAt,
            expected.StreamEtag,
            expected.DocumentJson
        );

        var act = () =>
            MaterializedDocumentFixtureAssertions.AssertCandidateMatchesFixture(candidate, _ordinaryFixture);

        act.Should().NotThrow();
    }

    [Test]
    public void It_rejects_success_candidates_when_document_json_differs()
    {
        var expected = _ordinaryFixture.ExpectedCacheRow!;
        var documentJson = expected.DocumentJson.DeepClone().AsObject();
        documentJson["schoolReference"]!["schoolId"] = 999999;
        var candidate = new MaterializedDocumentFixtureActualCacheRow(
            expected.DocumentId,
            expected.DocumentUuid,
            expected.ProjectName,
            expected.ResourceName,
            expected.ResourceVersion,
            expected.ContentVersion,
            expected.LastModifiedAt,
            expected.StreamEtag,
            documentJson
        );

        var act = () =>
            MaterializedDocumentFixtureAssertions.AssertCandidateMatchesFixture(candidate, _ordinaryFixture);

        act.Should().Throw<InvalidOperationException>().WithMessage("*DocumentJson mismatch*");
    }

    [Test]
    public void It_asserts_projection_failures_against_fixture_expectations()
    {
        var expected = _failureFixture.ExpectedProjectionFailure!;
        var failure = new MaterializedDocumentFixtureActualProjectionFailure(
            expected.Reason,
            expected.DocumentId,
            expected.ResourceKeyId,
            expected.ProjectName,
            expected.ResourceName,
            expected.ResourceVersion
        );

        var act = () =>
            MaterializedDocumentFixtureAssertions.AssertProjectionFailureMatchesFixture(
                failure,
                _failureFixture
            );

        act.Should().NotThrow();
    }
}
