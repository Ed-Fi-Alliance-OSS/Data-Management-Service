// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_SecurableElementColumnPathResolver_With_Same_Name_EdOrg_Elements
{
    private InvalidOperationException _exception = null!;

    [SetUp]
    public void Setup()
    {
        var subjectResource = SecurableElementColumnPathResolverTestSupport.CreateSubjectResource(
            new ResourceSecurableElements(
                [
                    new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId"),
                    new EdOrgSecurableElement("$.sessionReference.schoolId", "SchoolId"),
                ],
                [],
                [],
                [],
                []
            ),
            [
                SecurableElementColumnPathResolverTestSupport.CreateColumn(
                    "CourseOffering_SchoolReferenceSchoolId",
                    "$.schoolReference.schoolId"
                ),
            ]
        );

        var act = () => SecurableElementColumnPathResolver.ResolveAll(subjectResource, [subjectResource]);

        _exception = act.Should().Throw<InvalidOperationException>().Which;
    }

    [Test]
    public void It_reports_the_distinct_unresolved_path_even_when_its_readable_name_resolves_elsewhere()
    {
        _exception.Message.Should().Contain("$.sessionReference.schoolId");
        _exception.Message.Should().NotContain("$.schoolReference.schoolId");
    }
}

[TestFixture]
[Parallelizable]
public class Given_SecurableElementColumnPathResolver_With_Duplicate_Unresolved_EdOrg_Paths
{
    private InvalidOperationException _exception = null!;

    [SetUp]
    public void Setup()
    {
        var subjectResource = SecurableElementColumnPathResolverTestSupport.CreateSubjectResource(
            new ResourceSecurableElements(
                [
                    new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId"),
                    new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolReferenceSchoolId"),
                ],
                [],
                [],
                [],
                []
            ),
            []
        );

        var act = () => SecurableElementColumnPathResolver.ResolveAll(subjectResource, [subjectResource]);

        _exception = act.Should().Throw<InvalidOperationException>().Which;
    }

    [Test]
    public void It_reports_each_unresolved_json_path_once()
    {
        CountOrdinalOccurrences(_exception.Message, "$.schoolReference.schoolId").Should().Be(1);
    }

    private static int CountOrdinalOccurrences(string value, string text) =>
        value.Split(text, StringSplitOptions.None).Length - 1;
}

internal static class SecurableElementColumnPathResolverTestSupport
{
    public static ConcreteResourceModel CreateSubjectResource(
        ResourceSecurableElements securableElements,
        IReadOnlyList<DbColumnModel> additionalColumns
    )
    {
        var resource = new QualifiedResourceName("Ed-Fi", "CourseOffering");
        var rootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "CourseOffering"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_CourseOffering",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                .. additionalColumns,
            ],
            []
        );
        var relationalModel = new RelationalResourceModel(
            resource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            [],
            []
        );

        return new ConcreteResourceModel(
            new ResourceKeyEntry(1, resource, "1.0.0", false),
            ResourceStorageKind.RelationalTables,
            relationalModel
        )
        {
            SecurableElements = securableElements,
        };
    }

    public static DbColumnModel CreateColumn(string columnName, string jsonPath) =>
        new(
            new DbColumnName(columnName),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            false,
            new JsonPathExpression(jsonPath, []),
            null
        );
}
