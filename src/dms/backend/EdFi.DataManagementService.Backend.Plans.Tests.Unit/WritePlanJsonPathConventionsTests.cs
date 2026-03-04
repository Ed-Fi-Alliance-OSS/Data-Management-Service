// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_WritePlanJsonPathConventions
{
    [Test]
    public void It_should_strip_the_scope_prefix_to_derive_a_scope_relative_json_path()
    {
        var relativePath = WritePlanJsonPathConventions.DeriveScopeRelativePath(
            Path("$.addresses[*]"),
            Path("$.addresses[*].city")
        );

        relativePath.Canonical.Should().Be("$.city");
        relativePath.Segments.Should().HaveCount(1);
    }

    [Test]
    public void It_should_return_value_at_scope_when_source_path_equals_scope_path()
    {
        var relativePath = WritePlanJsonPathConventions.DeriveScopeRelativePath(
            Path("$.addresses[*]"),
            Path("$.addresses[*]")
        );

        relativePath.Canonical.Should().Be("$");
        relativePath.Segments.Should().BeEmpty();
    }

    [Test]
    public void It_should_fail_fast_when_scope_is_not_a_prefix_of_the_source_path()
    {
        var act = () =>
            WritePlanJsonPathConventions.DeriveScopeRelativePath(
                Path("$.addresses[*]"),
                Path("$.studentUniqueId")
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*scope '$.addresses[*]' is not a prefix*");
    }

    [Test]
    public void It_should_fail_fast_when_prefix_stripping_leaves_array_wildcards()
    {
        var act = () =>
            WritePlanJsonPathConventions.DeriveScopeRelativePath(
                Path("$.addresses[*]"),
                Path("$.addresses[*].periods[*].beginDate")
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*contains '[*]'*");
    }

    private static JsonPathExpression Path(string canonicalPath)
    {
        return JsonPathExpressionCompiler.Compile(canonicalPath);
    }
}
