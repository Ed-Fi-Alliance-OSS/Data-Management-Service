// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_KeyUnificationPresenceConventions
{
    [Test]
    public void It_should_identify_nullable_boolean_scalar_columns_without_source_paths_as_synthetic_presence_columns()
    {
        var column = CreateColumn();

        KeyUnificationPresenceConventions.IsSyntheticPresenceColumn(column).Should().BeTrue();
    }

    [Test]
    public void It_should_not_identify_non_scalar_columns_as_synthetic_presence_columns()
    {
        var column = CreateColumn(kind: ColumnKind.DocumentFk);

        KeyUnificationPresenceConventions.IsSyntheticPresenceColumn(column).Should().BeFalse();
    }

    [Test]
    public void It_should_not_identify_non_nullable_columns_as_synthetic_presence_columns()
    {
        var column = CreateColumn(isNullable: false);

        KeyUnificationPresenceConventions.IsSyntheticPresenceColumn(column).Should().BeFalse();
    }

    [Test]
    public void It_should_not_identify_non_boolean_columns_as_synthetic_presence_columns()
    {
        var column = CreateColumn(scalarKind: ScalarKind.String);

        KeyUnificationPresenceConventions.IsSyntheticPresenceColumn(column).Should().BeFalse();
    }

    [Test]
    public void It_should_not_identify_columns_with_source_paths_as_synthetic_presence_columns()
    {
        var column = CreateColumn(sourceJsonPath: SourcePath());

        KeyUnificationPresenceConventions.IsSyntheticPresenceColumn(column).Should().BeFalse();
    }

    private static DbColumnModel CreateColumn(
        ColumnKind kind = ColumnKind.Scalar,
        bool isNullable = true,
        ScalarKind scalarKind = ScalarKind.Boolean,
        JsonPathExpression? sourceJsonPath = null
    )
    {
        return new DbColumnModel(
            ColumnName: new DbColumnName("SchoolYearTypeDescriptorSecondary_Present"),
            Kind: kind,
            ScalarType: new RelationalScalarType(scalarKind),
            IsNullable: isNullable,
            SourceJsonPath: sourceJsonPath,
            TargetResource: null
        );
    }

    private static JsonPathExpression SourcePath()
    {
        return new JsonPathExpression(
            "$.schoolReference.schoolId",
            [new JsonPathSegment.Property("schoolReference"), new JsonPathSegment.Property("schoolId")]
        );
    }
}
