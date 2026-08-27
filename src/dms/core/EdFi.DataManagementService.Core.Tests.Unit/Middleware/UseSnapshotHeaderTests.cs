// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class UseSnapshotHeaderTests
{
    private static FrontendRequest RequestWithHeaders(Dictionary<string, string> headers) =>
        new(
            Path: "/ed-fi/schools",
            Body: null,
            Form: null,
            Headers: headers,
            QueryParameters: [],
            TraceId: new TraceId("trace"),
            RouteQualifiers: []
        );

    private static bool Requested(string headerValue) =>
        UseSnapshotHeader.TryReadRequested(
            RequestWithHeaders(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Use-Snapshot"] = headerValue,
                }
            )
        );

    [TestCase("true")]
    [TestCase("TRUE")]
    [TestCase("True")]
    [TestCase("tRuE")]
    public void It_reads_a_true_value_case_insensitively(string headerValue)
    {
        Requested(headerValue).Should().BeTrue();
    }

    [TestCase(" true")]
    [TestCase("true ")]
    [TestCase("\ttrue\t")]
    public void It_ignores_surrounding_whitespace(string headerValue)
    {
        Requested(headerValue).Should().BeTrue();
    }

    /// <summary>
    /// Anything that is not boolean true is a request for current data, not an error. The header is an
    /// opt-in, and rejecting an unrecognized value would fail a client that sends "yes" where a client
    /// that sends nothing succeeds.
    /// </summary>
    [TestCase("false")]
    [TestCase("FALSE")]
    [TestCase("yes")]
    [TestCase("1")]
    [TestCase("0")]
    [TestCase("on")]
    [TestCase("truthy")]
    [TestCase("true false")]
    [TestCase("")]
    [TestCase("   ")]
    public void It_does_not_read_anything_else_as_a_request(string headerValue)
    {
        Requested(headerValue).Should().BeFalse();
    }

    [Test]
    public void It_reads_an_absent_header_as_no_request()
    {
        UseSnapshotHeader
            .TryReadRequested(
                RequestWithHeaders(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            )
            .Should()
            .BeFalse();
    }

    /// <summary>
    /// Core receives headers in a case-insensitive dictionary, so a client that spells the header
    /// differently is still asking the same question.
    /// </summary>
    [TestCase("use-snapshot")]
    [TestCase("USE-SNAPSHOT")]
    [TestCase("Use-SnapShot")]
    public void It_matches_the_header_name_case_insensitively(string headerName)
    {
        UseSnapshotHeader
            .TryReadRequested(
                RequestWithHeaders(
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [headerName] = "true" }
                )
            )
            .Should()
            .BeTrue();
    }
}
