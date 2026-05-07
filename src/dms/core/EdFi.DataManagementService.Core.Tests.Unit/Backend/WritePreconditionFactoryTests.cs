// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Backend;

[TestFixture]
[Parallelizable]
public class WritePreconditionFactoryTests
{
    [Test]
    public void It_returns_none_when_if_match_is_absent()
    {
        var result = WritePreconditionFactory.Create(new Dictionary<string, string>());

        result.Should().BeOfType<WritePrecondition.None>();
    }

    [TestCase("plain-opaque-value")]
    [TestCase("\"72\"")]
    [TestCase("   ")]
    [TestCase("\"72\", W/\"73\"")]
    public void It_preserves_the_exact_if_match_value(string ifMatchValue)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["If-Match"] = ifMatchValue,
        };

        var result = WritePreconditionFactory.Create(headers);

        result.Should().Be(new WritePrecondition.IfMatch(ifMatchValue));
    }
}
