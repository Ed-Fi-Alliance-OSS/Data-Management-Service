// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_PlanSqlDialectFactory
{
    [Test]
    public void It_should_reject_unsupported_sql_dialects()
    {
        var unsupportedDialect = (SqlDialect)999;

        var act = () => PlanSqlDialectFactory.Create(unsupportedDialect);

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("dialect")
            .WithMessage("Unsupported SQL dialect. (Parameter 'dialect')*");
    }
}
