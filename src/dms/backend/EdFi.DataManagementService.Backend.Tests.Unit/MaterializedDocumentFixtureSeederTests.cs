// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
                    "CREATE TABLE IF NOT EXISTS \"edfi\".\"StudentSchoolAssociation\"",
                    StringComparison.Ordinal
                )
            );

        var documentInsert = commands.Single(command =>
            command.CommandText.StartsWith("INSERT INTO \"dms\".\"Document\"", StringComparison.Ordinal)
            && command.Parameters.Any(parameter => Equals(parameter.Value, 970101L))
        );

        documentInsert.Parameters.Should().Contain(parameter => Equals(parameter.Value, 970101L));
        documentInsert
            .Parameters.Any(parameter => Equals(parameter.Value, "2026-07-30T14:15:16.1234567+00:00"))
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
                Equals(parameter.Value, _failureFixture.SourceSetup.Documents[0].ContentLastModifiedAt)
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
