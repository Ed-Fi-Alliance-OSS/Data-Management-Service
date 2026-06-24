// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_NamespacePrefixSqlHelper
{
    private static readonly NamespacePrefixParameterization UnsupportedParameterization = new(
        (NamespacePrefixParameterizationKind)int.MaxValue,
        "namespacePrefixes",
        ["uri://ed-fi.org/"],
        ["uri://ed-fi.org/%"],
        ["namespacePrefixes"]
    );

    [Test]
    public void It_rejects_unsupported_parameterization_kinds_when_building_filter_parameters()
    {
        var act = () => NamespacePrefixSqlHelper.BuildFilterParametersInOrder(UnsupportedParameterization);

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Unsupported namespace prefix parameterization kind.*")
            .WithParameterName("namespacePrefixParameterization");
    }

    [Test]
    public void It_rejects_unsupported_parameterization_kinds_when_appending_like_matches()
    {
        var writer = new SqlWriter(SqlDialectFactory.Create(SqlDialect.Pgsql));
        var act = () =>
            NamespacePrefixSqlHelper.AppendLikeMatch(
                writer,
                static lhsWriter => lhsWriter.Append("d.\"Namespace\""),
                UnsupportedParameterization
            );

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Unsupported namespace prefix parameterization kind.*")
            .WithParameterName("namespacePrefixParameterization");
    }
}
