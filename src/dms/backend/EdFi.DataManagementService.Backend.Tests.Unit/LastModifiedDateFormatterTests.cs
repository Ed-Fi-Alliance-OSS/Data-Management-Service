// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_LastModifiedDateFormatter
{
    [Test]
    public void It_formats_UTC_values_as_whole_second_last_modified_dates()
    {
        LastModifiedDateFormatter
            .Format(new DateTimeOffset(2026, 4, 3, 14, 10, 11, 987, TimeSpan.Zero))
            .Should()
            .Be("2026-04-03T14:10:11Z");
    }

    [Test]
    public void It_converts_offsets_to_UTC_before_formatting()
    {
        LastModifiedDateFormatter
            .Format(new DateTimeOffset(2026, 4, 3, 9, 10, 11, TimeSpan.FromHours(-5)))
            .Should()
            .Be("2026-04-03T14:10:11Z");
    }

    [Test]
    public void It_treats_unspecified_DateTime_values_as_UTC()
    {
        LastModifiedDateFormatter
            .Format(new DateTime(2026, 4, 3, 14, 10, 11, 987, DateTimeKind.Unspecified))
            .Should()
            .Be("2026-04-03T14:10:11Z");
    }
}
