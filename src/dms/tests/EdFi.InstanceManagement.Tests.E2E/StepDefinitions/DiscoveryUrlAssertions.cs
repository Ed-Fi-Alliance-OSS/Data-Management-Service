// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.InstanceManagement.Tests.E2E.StepDefinitions;

internal static class DiscoveryUrlAssertions
{
    /// <summary>
    /// Rejects advertised URLs that are relative, from another origin, lack the original route
    /// segments, or still contain template placeholders.
    /// </summary>
    public static Uri AssertConcreteContextUrl(string url, Uri discoveryUri)
    {
        url.Should().NotContain("{", "advertised URLs must not contain placeholders");
        url.Should().NotContain("}", "advertised URLs must not contain placeholders");
        Uri.UnescapeDataString(url).Should().NotContain("{").And.NotContain("}");

        Uri.TryCreate(url, UriKind.Absolute, out var uri).Should().BeTrue("advertised URLs must be absolute");
        uri!
            .GetLeftPart(UriPartial.Authority)
            .Should()
            .Be(discoveryUri.GetLeftPart(UriPartial.Authority), "advertised URLs must keep the origin");

        string[] expected = discoveryUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] actual = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        actual
            .Take(expected.Length)
            .Should()
            .Equal(expected, "advertised URLs must keep the complete original route as path segments");
        return uri;
    }
}
