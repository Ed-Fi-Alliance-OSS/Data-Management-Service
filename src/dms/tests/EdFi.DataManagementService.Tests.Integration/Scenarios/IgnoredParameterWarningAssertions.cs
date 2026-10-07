// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Middleware;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// The <c>X-EdFi-Warning</c> header naming the query parameters an operation ignored, asserted as the
/// assembled host emits it.
/// </summary>
internal static class IgnoredParameterWarningAssertions
{
    /// <summary>
    /// Asserts that <paramref name="response"/> carries exactly one warning naming
    /// <paramref name="expectedNames"/>, in that order.
    /// </summary>
    internal static void AssertWarnsOf(HttpResponseMessage response, params string[] expectedNames)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Headers.TryGetValues(
            IgnoredQueryParameterWarning.HeaderName,
            out IEnumerable<string>? values
        );

        values.Should().Equal(IgnoredQueryParameterWarning.HeaderPrefix + string.Join(", ", expectedNames));
    }

    /// <summary>
    /// Asserts that <paramref name="response"/> carries no warning, as a request that ignored nothing
    /// does.
    /// </summary>
    internal static void AssertNoWarning(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Headers.Contains(IgnoredQueryParameterWarning.HeaderName).Should().BeFalse();
    }
}
