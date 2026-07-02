// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_TrackedChangeNameConventions
{
    [Test]
    public void It_should_prefix_old_and_new_without_removing_internal_underscores()
    {
        var sourceColumn = new DbColumnName("Student_DocumentId");

        TrackedChangeNameConventions.OldValueColumn(sourceColumn).Value.Should().Be("OldStudent_DocumentId");
        TrackedChangeNameConventions.NewValueColumn(sourceColumn).Value.Should().Be("NewStudent_DocumentId");
    }
}
