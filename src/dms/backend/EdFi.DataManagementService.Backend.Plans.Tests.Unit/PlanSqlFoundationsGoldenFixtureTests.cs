// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_PlanSqlFoundations_GoldenFixture
{
    private Dictionary<string, string> _diffByFileName = null!;

    [SetUp]
    public void Setup()
    {
        var projectRoot = GoldenFixtureTestHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory,
            "EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj"
        );
        var expectedRoot = Path.Combine(
            projectRoot,
            "Fixtures",
            "plan-sql-foundations",
            "minimal",
            "expected"
        );
        var actualRoot = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "plan-sql-foundations",
            "minimal",
            "actual"
        );
        var generatedSqlByFileName = BuildGeneratedSqlByFileName();

        Directory.CreateDirectory(actualRoot);

        foreach (var (fileName, sql) in generatedSqlByFileName)
        {
            File.WriteAllText(Path.Combine(actualRoot, fileName), sql);
        }

        if (GoldenFixtureTestHelpers.ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(expectedRoot);

            foreach (var (fileName, sql) in generatedSqlByFileName)
            {
                File.WriteAllText(Path.Combine(expectedRoot, fileName), sql);
            }
        }

        _diffByFileName = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var fileName in generatedSqlByFileName.Keys.OrderBy(file => file, StringComparer.Ordinal))
        {
            var expectedPath = Path.Combine(expectedRoot, fileName);
            var actualPath = Path.Combine(actualRoot, fileName);

            File.Exists(expectedPath)
                .Should()
                .BeTrue($"golden SQL fixture missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate.");

            _diffByFileName[fileName] = GoldenFixtureTestHelpers.RunGitDiff(expectedPath, actualPath);
        }
    }

    [Test]
    public void It_should_match_pgsql_page_document_id_sql_golden()
    {
        AssertGoldenMatches("pgsql.page-document-id.sql");
    }

    [Test]
    public void It_should_match_mssql_page_document_id_sql_golden()
    {
        AssertGoldenMatches("mssql.page-document-id.sql");
    }

    [Test]
    public void It_should_match_pgsql_descriptor_page_document_id_sql_golden()
    {
        AssertGoldenMatches("pgsql.descriptor-page-document-id.sql");
    }

    [Test]
    public void It_should_match_mssql_descriptor_page_document_id_sql_golden()
    {
        AssertGoldenMatches("mssql.descriptor-page-document-id.sql");
    }

    [Test]
    public void It_should_match_pgsql_descriptor_total_count_sql_golden()
    {
        AssertGoldenMatches("pgsql.descriptor-total-count.sql");
    }

    [Test]
    public void It_should_match_mssql_descriptor_total_count_sql_golden()
    {
        AssertGoldenMatches("mssql.descriptor-total-count.sql");
    }

    [Test]
    public void It_should_match_pgsql_minimal_insert_sql_golden()
    {
        AssertGoldenMatches("pgsql.insert.sql");
    }

    [Test]
    public void It_should_match_mssql_minimal_insert_sql_golden()
    {
        AssertGoldenMatches("mssql.insert.sql");
    }

    private void AssertGoldenMatches(string fileName)
    {
        _diffByFileName.TryGetValue(fileName, out var diffOutput).Should().BeTrue();

        if (!string.IsNullOrWhiteSpace(diffOutput))
        {
            Assert.Fail(diffOutput);
        }
    }

    private static Dictionary<string, string> BuildGeneratedSqlByFileName()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pgsql.page-document-id.sql"] = BuildPageDocumentIdSql(SqlDialect.Pgsql),
            ["mssql.page-document-id.sql"] = BuildPageDocumentIdSql(SqlDialect.Mssql),
            ["pgsql.descriptor-page-document-id.sql"] = BuildDescriptorPageDocumentIdSql(SqlDialect.Pgsql),
            ["mssql.descriptor-page-document-id.sql"] = BuildDescriptorPageDocumentIdSql(SqlDialect.Mssql),
            ["pgsql.descriptor-total-count.sql"] = BuildDescriptorTotalCountSql(SqlDialect.Pgsql),
            ["mssql.descriptor-total-count.sql"] = BuildDescriptorTotalCountSql(SqlDialect.Mssql),
            ["pgsql.insert.sql"] = BuildSimpleInsertSql(SqlDialect.Pgsql),
            ["mssql.insert.sql"] = BuildSimpleInsertSql(SqlDialect.Mssql),
        };
    }

    private static string BuildPageDocumentIdSql(SqlDialect dialect)
    {
        return PlanSqlGoldenFixtureQueryPlans.CompileFoundationsPageDocumentIdPlan(dialect).PageDocumentIdSql;
    }

    private static string BuildDescriptorPageDocumentIdSql(SqlDialect dialect)
    {
        return PlanSqlGoldenFixtureQueryPlans.CompileDescriptorPageDocumentIdPlan(dialect).PageDocumentIdSql;
    }

    private static string BuildDescriptorTotalCountSql(SqlDialect dialect)
    {
        return PlanSqlGoldenFixtureQueryPlans.CompileDescriptorPageDocumentIdPlan(dialect).TotalCountSql!;
    }

    private static string BuildSimpleInsertSql(SqlDialect dialect)
    {
        return new SimpleInsertSqlEmitter(dialect).Emit(
            table: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            orderedColumns:
            [
                new DbColumnName("SchoolId"),
                new DbColumnName("SchoolYear"),
                new DbColumnName("StudentUniqueId"),
            ],
            orderedParameterNames: ["schoolId", "schoolYear", "studentUniqueId"]
        );
    }
}
