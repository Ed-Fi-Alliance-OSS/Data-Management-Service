// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Profile;
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
        result.EtagProjectionContext.Should().BeNull();
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
        result.EtagProjectionContext.Should().BeNull();
    }

    [Test]
    public void It_threads_the_readable_etag_projection_context_when_if_match_is_absent()
    {
        var etagProjectionContext = CreateReadableEtagProjectionContext();

        var result = WritePreconditionFactory.Create(new Dictionary<string, string>(), etagProjectionContext);

        result.Should().BeOfType<WritePrecondition.None>();
        result.EtagProjectionContext.Should().BeSameAs(etagProjectionContext);
    }

    [Test]
    public void It_threads_the_readable_etag_projection_context_when_if_match_is_present()
    {
        var etagProjectionContext = CreateReadableEtagProjectionContext();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["If-Match"] = "\"72\"",
        };

        var result = WritePreconditionFactory.Create(headers, etagProjectionContext);

        result.Should().Be(new WritePrecondition.IfMatch("\"72\"", etagProjectionContext));
        result.EtagProjectionContext.Should().BeSameAs(etagProjectionContext);
    }

    private static ReadableEtagProjectionContext CreateReadableEtagProjectionContext() =>
        new(
            new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule("studentUniqueId")],
                [],
                [],
                []
            ),
            new HashSet<string>(["studentUniqueId", "schoolReference"], StringComparer.Ordinal)
        );
}
