// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.Composite;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Builds natural-key capture predicates over the compiled DS 5.2 mapping set. The identity values below
/// cover every binding shape the capture writes: a scalar own key (School), an own descriptor part and an
/// abstract reference target (Program), key-unified target columns fed by one reference path (Section), and
/// descriptor parts inside referenced identities (Grade).
/// </summary>
internal static class NaturalKeyCaptureTestIdentities
{
    public static readonly QualifiedResourceName School = new("Ed-Fi", "School");
    public static readonly QualifiedResourceName Program = new("Ed-Fi", "Program");
    public static readonly QualifiedResourceName Section = new("Ed-Fi", "Section");
    public static readonly QualifiedResourceName Grade = new("Ed-Fi", "Grade");

    public static DocumentIdentity SchoolIdentity(string schoolId = "255901") =>
        Identity(("$.schoolId", schoolId));

    public static DocumentIdentity ProgramIdentity(
        string programName = "Robotics",
        string programTypeDescriptor = "uri://ed-fi.org/ProgramTypeDescriptor#STEM",
        string educationOrganizationId = "255901"
    ) =>
        Identity(
            ("$.educationOrganizationReference.educationOrganizationId", educationOrganizationId),
            ("$.programName", programName),
            ("$.programTypeDescriptor", programTypeDescriptor)
        );

    public static DocumentIdentity SectionIdentity() =>
        Identity(
            ("$.courseOfferingReference.localCourseCode", "ALG-1"),
            ("$.courseOfferingReference.schoolId", "255901"),
            ("$.courseOfferingReference.schoolYear", "2026"),
            ("$.courseOfferingReference.sessionName", "Fall"),
            ("$.sectionIdentifier", "S-1")
        );

    public static DocumentIdentity GradeIdentity() =>
        Identity(
            ("$.gradeTypeDescriptor", "uri://ed-fi.org/GradeTypeDescriptor#Final"),
            (
                "$.gradingPeriodReference.gradingPeriodDescriptor",
                "uri://ed-fi.org/GradingPeriodDescriptor#First"
            ),
            ("$.gradingPeriodReference.gradingPeriodName", "GP-1"),
            ("$.gradingPeriodReference.schoolId", "255901"),
            ("$.gradingPeriodReference.schoolYear", "2026"),
            ("$.studentSectionAssociationReference.beginDate", "2026-08-20"),
            ("$.studentSectionAssociationReference.localCourseCode", "ALG-1"),
            ("$.studentSectionAssociationReference.schoolId", "255901"),
            ("$.studentSectionAssociationReference.schoolYear", "2026"),
            ("$.studentSectionAssociationReference.sectionIdentifier", "S-1"),
            ("$.studentSectionAssociationReference.sessionName", "Fall"),
            ("$.studentSectionAssociationReference.studentUniqueId", "604824")
        );

    public static DocumentIdentity Identity(params (string Path, string Value)[] elements) =>
        new([
            .. elements.Select(element => new DocumentIdentityElement(
                new JsonPath(element.Path),
                element.Value
            )),
        ]);

    public static RelationalCommand Build(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentIdentity identity
    ) =>
        RelationalWriteTargetLookupSupport.BuildNaturalKeyCaptureTargetPredicate(
            mappingSet,
            mappingSet.WritePlansByResource[resource],
            identity
        );
}

[TestFixture]
public class Given_The_Natural_Key_Capture_Predicate_Builder
{
    private const string PgsqlNoMatch = """d."DocumentId" IS NULL AND d."ResourceKeyId" = @resourceKeyId""";
    private const string MssqlNoMatch = "d.[DocumentId] IS NULL AND d.[ResourceKeyId] = @resourceKeyId";

    private static MappingSet MappingSet(SqlDialect dialect) => NaturalKeyLookupTestMappingSets.Ds52(dialect);

    [TestCase(
        SqlDialect.Pgsql,
        """d."DocumentId" = (SELECT root."DocumentId" FROM "edfi"."School" root WHERE root."SchoolId" = @nk1) AND d."ResourceKeyId" = @resourceKeyId"""
    )]
    [TestCase(
        SqlDialect.Mssql,
        "d.[DocumentId] = (SELECT root.[DocumentId] FROM [edfi].[School] root WHERE root.[SchoolId] = @nk1) AND d.[ResourceKeyId] = @resourceKeyId"
    )]
    public void It_captures_a_scalar_own_natural_key(SqlDialect dialect, string expectedSql)
    {
        var mappingSet = MappingSet(dialect);

        var command = NaturalKeyCaptureTestIdentities.Build(
            mappingSet,
            NaturalKeyCaptureTestIdentities.School,
            NaturalKeyCaptureTestIdentities.SchoolIdentity()
        );

        command.CommandText.Should().Be(expectedSql);
        command
            .Parameters.Select(parameter => (parameter.Name, parameter.Value))
            .Should()
            .Equal(
                ("@nk1", (object?)255901L),
                ("@resourceKeyId", mappingSet.ResourceKeyIdByResource[NaturalKeyCaptureTestIdentities.School])
            );
    }

    [TestCase(
        SqlDialect.Pgsql,
        """d."DocumentId" = (SELECT root."DocumentId" FROM "edfi"."Program" root WHERE root."EducationOrganization_DocumentId" = (SELECT t1."DocumentId" FROM "edfi"."EducationOrganizationIdentity" t1 WHERE t1."EducationOrganizationId" = @nk1) AND root."ProgramName" = @nk2 AND root."ProgramTypeDescriptor_DescriptorId" = (SELECT x1."DescriptorId" FROM "dms"."Descriptor" x1 WHERE lower((x1."Namespace" || '#' || x1."CodeValue") COLLATE "pg_c_utf8") = lower(@nk3 COLLATE "pg_c_utf8") AND x1."ResourceKeyId" = CAST({0} AS smallint))) AND d."ResourceKeyId" = @resourceKeyId"""
    )]
    [TestCase(
        SqlDialect.Mssql,
        "d.[DocumentId] = (SELECT root.[DocumentId] FROM [edfi].[Program] root WHERE root.[EducationOrganization_DocumentId] = (SELECT t1.[DocumentId] FROM [edfi].[EducationOrganizationIdentity] t1 WHERE t1.[EducationOrganizationId] = @nk1) AND root.[ProgramName] = @nk2 AND root.[ProgramTypeDescriptor_DescriptorId] = (SELECT x1.[DescriptorId] FROM [dms].[Descriptor] x1 WHERE x1.[UriLowered] = LOWER(@nk3 COLLATE SQL_Latin1_General_CP1_CI_AS) AND x1.[ResourceKeyId] = CAST({0} AS smallint))) AND d.[ResourceKeyId] = @resourceKeyId"
    )]
    public void It_captures_an_own_descriptor_part_and_an_abstract_reference_target(
        SqlDialect dialect,
        string expectedSqlFormat
    )
    {
        var mappingSet = MappingSet(dialect);

        var command = NaturalKeyCaptureTestIdentities.Build(
            mappingSet,
            NaturalKeyCaptureTestIdentities.Program,
            NaturalKeyCaptureTestIdentities.ProgramIdentity()
        );

        command
            .CommandText.Should()
            .Be(
                string.Format(
                    expectedSqlFormat,
                    mappingSet.ResourceKeyIdByResource[NaturalKeyLookupTestMappingSets.ProgramTypeDescriptor]
                )
            );
        command
            .Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal(
                255901L,
                "Robotics",
                "uri://ed-fi.org/ProgramTypeDescriptor#STEM",
                mappingSet.ResourceKeyIdByResource[NaturalKeyCaptureTestIdentities.Program]
            );
    }

    [TestCase(
        SqlDialect.Pgsql,
        """d."DocumentId" = (SELECT root."DocumentId" FROM "edfi"."Section" root WHERE root."CourseOffering_DocumentId" = (SELECT t1."DocumentId" FROM "edfi"."CourseOffering" t1 WHERE t1."LocalCourseCode" = @nk1 AND t1."SchoolId_Unified" = @nk2 AND t1."Session_SchoolYear" = @nk3 AND t1."Session_SessionName" = @nk4) AND root."SectionIdentifier" = @nk5) AND d."ResourceKeyId" = @resourceKeyId"""
    )]
    [TestCase(
        SqlDialect.Mssql,
        "d.[DocumentId] = (SELECT root.[DocumentId] FROM [edfi].[Section] root WHERE root.[CourseOffering_DocumentId] = (SELECT t1.[DocumentId] FROM [edfi].[CourseOffering] t1 WHERE t1.[LocalCourseCode] = @nk1 AND t1.[SchoolId_Unified] = @nk2 AND t1.[Session_SchoolYear] = @nk3 AND t1.[Session_SessionName] = @nk4) AND root.[SectionIdentifier] = @nk5) AND d.[ResourceKeyId] = @resourceKeyId"
    )]
    public void It_captures_a_reference_once_and_writes_a_key_unified_target_column_once(
        SqlDialect dialect,
        string expectedSql
    )
    {
        // CourseOffering's schoolReference.schoolId and sessionReference.schoolId share SchoolId_Unified, and
        // Section feeds both from $.courseOfferingReference.schoolId.
        var command = NaturalKeyCaptureTestIdentities.Build(
            MappingSet(dialect),
            NaturalKeyCaptureTestIdentities.Section,
            NaturalKeyCaptureTestIdentities.SectionIdentity()
        );

        command.CommandText.Should().Be(expectedSql);
        command
            .Parameters.Take(5)
            .Select(parameter => parameter.Value)
            .Should()
            .Equal("ALG-1", 255901L, 2026, "Fall", "S-1");
    }

    [TestCase(
        SqlDialect.Pgsql,
        """d."DocumentId" = (SELECT root."DocumentId" FROM "edfi"."Grade" root WHERE root."GradeTypeDescriptor_DescriptorId" = (SELECT x1."DescriptorId" FROM "dms"."Descriptor" x1 WHERE lower((x1."Namespace" || '#' || x1."CodeValue") COLLATE "pg_c_utf8") = lower(@nk1 COLLATE "pg_c_utf8") AND x1."ResourceKeyId" = CAST({0} AS smallint)) AND root."GradingPeriodGradingPeriod_DocumentId" = (SELECT t1."DocumentId" FROM "edfi"."GradingPeriod" t1 WHERE t1."GradingPeriodDescriptor_DescriptorId" = (SELECT x2."DescriptorId" FROM "dms"."Descriptor" x2 WHERE lower((x2."Namespace" || '#' || x2."CodeValue") COLLATE "pg_c_utf8") = lower(@nk2 COLLATE "pg_c_utf8") AND x2."ResourceKeyId" = CAST({1} AS smallint)) AND t1."GradingPeriodName" = @nk3 AND t1."School_SchoolId" = @nk4 AND t1."SchoolYear_SchoolYear" = @nk5) AND root."StudentSectionAssociation_DocumentId" = (SELECT t2."DocumentId" FROM "edfi"."StudentSectionAssociation" t2 WHERE t2."BeginDate" = @nk6 AND t2."Section_LocalCourseCode" = @nk7 AND t2."Section_SchoolId" = @nk8 AND t2."Section_SchoolYear" = @nk9 AND t2."Section_SectionIdentifier" = @nk10 AND t2."Section_SessionName" = @nk11 AND t2."Student_StudentUniqueId" = @nk12)) AND d."ResourceKeyId" = @resourceKeyId"""
    )]
    [TestCase(
        SqlDialect.Mssql,
        "d.[DocumentId] = (SELECT root.[DocumentId] FROM [edfi].[Grade] root WHERE root.[GradeTypeDescriptor_DescriptorId] = (SELECT x1.[DescriptorId] FROM [dms].[Descriptor] x1 WHERE x1.[UriLowered] = LOWER(@nk1 COLLATE SQL_Latin1_General_CP1_CI_AS) AND x1.[ResourceKeyId] = CAST({0} AS smallint)) AND root.[GradingPeriodGradingPeriod_DocumentId] = (SELECT t1.[DocumentId] FROM [edfi].[GradingPeriod] t1 WHERE t1.[GradingPeriodDescriptor_DescriptorId] = (SELECT x2.[DescriptorId] FROM [dms].[Descriptor] x2 WHERE x2.[UriLowered] = LOWER(@nk2 COLLATE SQL_Latin1_General_CP1_CI_AS) AND x2.[ResourceKeyId] = CAST({1} AS smallint)) AND t1.[GradingPeriodName] = @nk3 AND t1.[School_SchoolId] = @nk4 AND t1.[SchoolYear_SchoolYear] = @nk5) AND root.[StudentSectionAssociation_DocumentId] = (SELECT t2.[DocumentId] FROM [edfi].[StudentSectionAssociation] t2 WHERE t2.[BeginDate] = @nk6 AND t2.[Section_LocalCourseCode] = @nk7 AND t2.[Section_SchoolId] = @nk8 AND t2.[Section_SchoolYear] = @nk9 AND t2.[Section_SectionIdentifier] = @nk10 AND t2.[Section_SessionName] = @nk11 AND t2.[Student_StudentUniqueId] = @nk12)) AND d.[ResourceKeyId] = @resourceKeyId"
    )]
    public void It_resolves_descriptor_parts_inside_referenced_identities_to_descriptor_ids(
        SqlDialect dialect,
        string expectedSqlFormat
    )
    {
        var mappingSet = MappingSet(dialect);

        var command = NaturalKeyCaptureTestIdentities.Build(
            mappingSet,
            NaturalKeyCaptureTestIdentities.Grade,
            NaturalKeyCaptureTestIdentities.GradeIdentity()
        );

        command
            .CommandText.Should()
            .Be(
                string.Format(
                    expectedSqlFormat,
                    mappingSet.ResourceKeyIdByResource[
                        new QualifiedResourceName("Ed-Fi", "GradeTypeDescriptor")
                    ],
                    mappingSet.ResourceKeyIdByResource[
                        new QualifiedResourceName("Ed-Fi", "GradingPeriodDescriptor")
                    ]
                )
            );
        command.Parameters[5].Value.Should().Be(new DateOnly(2026, 8, 20));
    }

    [Test]
    public void It_writes_only_scalar_equality_subselects_and_never_uses_batch_lookup_parameter_names(
        [Values(SqlDialect.Pgsql, SqlDialect.Mssql)] SqlDialect dialect
    )
    {
        var mappingSet = MappingSet(dialect);
        RelationalCommand[] commands =
        [
            NaturalKeyCaptureTestIdentities.Build(
                mappingSet,
                NaturalKeyCaptureTestIdentities.School,
                NaturalKeyCaptureTestIdentities.SchoolIdentity()
            ),
            NaturalKeyCaptureTestIdentities.Build(
                mappingSet,
                NaturalKeyCaptureTestIdentities.Program,
                NaturalKeyCaptureTestIdentities.ProgramIdentity()
            ),
            NaturalKeyCaptureTestIdentities.Build(
                mappingSet,
                NaturalKeyCaptureTestIdentities.Section,
                NaturalKeyCaptureTestIdentities.SectionIdentity()
            ),
            NaturalKeyCaptureTestIdentities.Build(
                mappingSet,
                NaturalKeyCaptureTestIdentities.Grade,
                NaturalKeyCaptureTestIdentities.GradeIdentity()
            ),
        ];

        foreach (var command in commands)
        {
            foreach (var forbidden in new[] { " TOP ", " LIMIT ", " IN ", " IN(", "EXISTS", " JOIN ", "@g" })
            {
                command.CommandText.Should().NotContainEquivalentOf(forbidden);
            }

            command
                .Parameters.Select(parameter => parameter.Name)
                .Should()
                .OnlyContain(name => name.StartsWith("@nk") || name == "@resourceKeyId");
        }
    }

    [Test]
    public void It_matches_nothing_and_binds_no_value_when_a_scalar_exceeds_its_column(
        [Values(SqlDialect.Pgsql, SqlDialect.Mssql)] SqlDialect dialect
    )
    {
        var command = NaturalKeyCaptureTestIdentities.Build(
            MappingSet(dialect),
            NaturalKeyCaptureTestIdentities.Program,
            NaturalKeyCaptureTestIdentities.ProgramIdentity(programName: new string('p', 61))
        );

        AssertNoMatch(dialect, command);
    }

    [Test]
    public void It_matches_nothing_and_binds_no_value_when_a_descriptor_uri_cannot_be_stored(
        [Values(SqlDialect.Pgsql, SqlDialect.Mssql)] SqlDialect dialect
    )
    {
        var command = NaturalKeyCaptureTestIdentities.Build(
            MappingSet(dialect),
            NaturalKeyCaptureTestIdentities.Program,
            NaturalKeyCaptureTestIdentities.ProgramIdentity(
                programTypeDescriptor: "uri://ed-fi.org/ProgramTypeDescriptor#"
                    + new string('x', NaturalKeyLookupLimits.MaxMssqlDescriptorUriProbeLength)
            )
        );

        AssertNoMatch(dialect, command);
    }

    [Test]
    public void It_matches_nothing_on_postgresql_when_a_value_contains_nul()
    {
        var command = NaturalKeyCaptureTestIdentities.Build(
            MappingSet(SqlDialect.Pgsql),
            NaturalKeyCaptureTestIdentities.Program,
            NaturalKeyCaptureTestIdentities.ProgramIdentity(programName: "Robo\0tics")
        );

        AssertNoMatch(SqlDialect.Pgsql, command);
    }

    [Test]
    public void It_matches_nothing_when_a_referenced_identity_value_does_not_parse(
        [Values(SqlDialect.Pgsql, SqlDialect.Mssql)] SqlDialect dialect
    )
    {
        var command = NaturalKeyCaptureTestIdentities.Build(
            MappingSet(dialect),
            NaturalKeyCaptureTestIdentities.Program,
            NaturalKeyCaptureTestIdentities.ProgramIdentity(educationOrganizationId: "not-a-number")
        );

        AssertNoMatch(dialect, command);
    }

    [Test]
    public void It_rejects_an_identity_missing_an_own_key_path()
    {
        var act = () =>
            NaturalKeyCaptureTestIdentities.Build(
                MappingSet(SqlDialect.Pgsql),
                NaturalKeyCaptureTestIdentities.Program,
                NaturalKeyCaptureTestIdentities.Identity(
                    ("$.educationOrganizationReference.educationOrganizationId", "255901"),
                    ("$.programName", "Robotics")
                )
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*$.programTypeDescriptor*");
    }

    [Test]
    public void It_rejects_an_identity_with_an_extra_path()
    {
        var act = () =>
            NaturalKeyCaptureTestIdentities.Build(
                MappingSet(SqlDialect.Mssql),
                NaturalKeyCaptureTestIdentities.School,
                NaturalKeyCaptureTestIdentities.Identity(("$.schoolId", "255901"), ("$.extra", "1"))
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*$.extra*");
    }

    [Test]
    public void It_keeps_its_text_through_the_composite_statement_rewriter(
        [Values(SqlDialect.Pgsql, SqlDialect.Mssql)] SqlDialect dialect
    )
    {
        var command = NaturalKeyCaptureTestIdentities.Build(
            MappingSet(dialect),
            NaturalKeyCaptureTestIdentities.Grade,
            NaturalKeyCaptureTestIdentities.GradeIdentity()
        );

        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            command,
            new RelationalCompositeParameterAllocator(),
            statementOrdinal: 0
        );

        // @nk1 and @nk10..@nk12 share a prefix; each token must rename to its own issued name.
        rewritten.Sql.Should().Be(Regex.Replace(command.CommandText, @"@(\w+)", "@$1_s0"));
        rewritten
            .Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal(command.Parameters.Select(parameter => parameter.Value));
    }

    private static void AssertNoMatch(SqlDialect dialect, RelationalCommand command)
    {
        command.CommandText.Should().Be(dialect == SqlDialect.Pgsql ? PgsqlNoMatch : MssqlNoMatch);
        command.Parameters.Select(parameter => parameter.Name).Should().Equal("@resourceKeyId");
    }
}

[TestFixture]
public class Given_The_Descriptor_Uri_Fold_Helpers
{
    [TestCase(
        SqlDialect.Pgsql,
        "lower((d.\"Namespace\" || '#' || d.\"CodeValue\") COLLATE \"pg_c_utf8\") = lower(@uri COLLATE \"pg_c_utf8\")"
    )]
    [TestCase(SqlDialect.Mssql, "d.[UriLowered] = LOWER(@uri COLLATE SQL_Latin1_General_CP1_CI_AS)")]
    public void It_keeps_the_live_fold_for_a_uri_parameter(SqlDialect dialect, string expectedSql)
    {
        NaturalKeyCapturePredicateBuilder
            .BuildDescriptorUriFoldPredicate(
                dialect,
                DescriptorProbeTarget.SharedDescriptorTable,
                "d",
                "@uri"
            )
            .Should()
            .Be(expectedSql);
    }

    [TestCase(SqlDialect.Pgsql, "(c.\"OldNamespace\" || '#' || c.\"OldCodeValue\")")]
    [TestCase(SqlDialect.Mssql, "(c.[OldNamespace] + N'#' + c.[OldCodeValue])")]
    public void It_reconstructs_a_tracked_descriptor_uri(SqlDialect dialect, string expectedSql)
    {
        NaturalKeyCapturePredicateBuilder
            .BuildTrackedDescriptorUri(dialect, "c", new("OldNamespace"), new("OldCodeValue"))
            .Should()
            .Be(expectedSql);
    }

    [TestCase(
        SqlDialect.Pgsql,
        "lower((d.\"Namespace\" || '#' || d.\"CodeValue\") COLLATE \"pg_c_utf8\") = lower((c.\"OldNamespace\" || '#' || c.\"OldCodeValue\") COLLATE \"pg_c_utf8\")"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "d.[UriLowered] = LOWER((c.[OldNamespace] + N'#' + c.[OldCodeValue]) COLLATE SQL_Latin1_General_CP1_CI_AS)"
    )]
    public void It_folds_a_tracked_uri_against_the_live_descriptor_index(
        SqlDialect dialect,
        string expectedSql
    )
    {
        NaturalKeyCapturePredicateBuilder
            .BuildDescriptorUriFoldPredicate(
                dialect,
                DescriptorProbeTarget.SharedDescriptorTable,
                "d",
                NaturalKeyCapturePredicateBuilder.BuildTrackedDescriptorUri(
                    dialect,
                    "c",
                    new("OldNamespace"),
                    new("OldCodeValue")
                )
            )
            .Should()
            .Be(expectedSql);
    }

    [TestCase(
        SqlDialect.Pgsql,
        "lower((t.\"OldNamespace\" || '#' || t.\"OldCodeValue\") COLLATE \"pg_c_utf8\") = lower((c.\"OldKind_Namespace\" || '#' || c.\"OldKind_CodeValue\") COLLATE \"pg_c_utf8\")"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "LOWER((t.[OldNamespace] + N'#' + t.[OldCodeValue]) COLLATE SQL_Latin1_General_CP1_CI_AS) = LOWER((c.[OldKind_Namespace] + N'#' + c.[OldKind_CodeValue]) COLLATE SQL_Latin1_General_CP1_CI_AS)"
    )]
    public void It_folds_two_tracked_uris_on_both_sides(SqlDialect dialect, string expectedSql)
    {
        NaturalKeyCapturePredicateBuilder
            .BuildTrackedDescriptorUriFoldPredicate(
                dialect,
                NaturalKeyCapturePredicateBuilder.BuildTrackedDescriptorUri(
                    dialect,
                    "t",
                    new("OldNamespace"),
                    new("OldCodeValue")
                ),
                NaturalKeyCapturePredicateBuilder.BuildTrackedDescriptorUri(
                    dialect,
                    "c",
                    new("OldKind_Namespace"),
                    new("OldKind_CodeValue")
                )
            )
            .Should()
            .Be(expectedSql);
    }
}
