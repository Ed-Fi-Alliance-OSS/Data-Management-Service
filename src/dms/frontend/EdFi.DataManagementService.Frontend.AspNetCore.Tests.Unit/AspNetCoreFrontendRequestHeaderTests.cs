// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_AspNetCoreFrontend_Request_Header_Extraction
{
    private static readonly MethodInfo _extractHeadersFrom =
        typeof(AspNetCoreFrontend).GetMethod(
            "ExtractHeadersFrom",
            BindingFlags.NonPublic | BindingFlags.Static
        ) ?? throw new InvalidOperationException("Could not locate AspNetCoreFrontend.ExtractHeadersFrom.");

    private static Dictionary<string, string> ExtractHeaders(Action<HttpRequest> configureRequest)
    {
        var httpContext = new DefaultHttpContext();
        configureRequest(httpContext.Request);

        return (Dictionary<string, string>?)_extractHeadersFrom.Invoke(null, [httpContext.Request])
            ?? throw new InvalidOperationException("ExtractHeadersFrom returned null.");
    }

    [Test]
    public void It_preserves_an_explicit_empty_content_type()
    {
        // A blank Content-Type must survive extraction so core can reject it with 415 instead of
        // treating it as a missing header.
        var headers = ExtractHeaders(request => request.Headers["Content-Type"] = "");

        headers.Should().ContainKey("Content-Type");
        headers["Content-Type"].Should().BeEmpty();
    }

    [Test]
    public void It_preserves_an_explicit_whitespace_content_type()
    {
        var headers = ExtractHeaders(request => request.Headers["Content-Type"] = "   ");

        headers.Should().ContainKey("Content-Type");
        headers["Content-Type"].Should().Be("   ");
    }

    [Test]
    public void It_omits_a_missing_content_type()
    {
        // A genuinely absent header stays absent, leaving core's missing-header exemption intact.
        var headers = ExtractHeaders(_ => { });

        headers.Should().NotContainKey("Content-Type");
    }

    [Test]
    public void It_keeps_a_valid_content_type()
    {
        var headers = ExtractHeaders(request => request.Headers["Content-Type"] = "application/json");

        headers["Content-Type"].Should().Be("application/json");
    }

    [Test]
    public void It_preserves_an_explicit_empty_authorization_header()
    {
        // A blank Authorization must survive extraction so core classifies it as a malformed
        // header ("Invalid Authorization header.") rather than a missing one.
        var headers = ExtractHeaders(request => request.Headers["Authorization"] = "");

        headers.Should().ContainKey("Authorization");
        headers["Authorization"].Should().BeEmpty();
    }

    [Test]
    public void It_preserves_an_explicit_whitespace_authorization_header()
    {
        var headers = ExtractHeaders(request => request.Headers["Authorization"] = "   ");

        headers.Should().ContainKey("Authorization");
        headers["Authorization"].Should().Be("   ");
    }

    [Test]
    public void It_omits_a_missing_authorization_header()
    {
        // A genuinely absent Authorization stays absent, so core reports it as missing rather
        // than malformed.
        var headers = ExtractHeaders(_ => { });

        headers.Should().NotContainKey("Authorization");
    }

    [Test]
    public void It_still_drops_other_blank_headers()
    {
        // Blank-value preservation is intentionally scoped to the headers core must distinguish
        // from missing (Content-Type, Authorization); blank values of other headers continue to
        // be dropped as before.
        var headers = ExtractHeaders(request => request.Headers["X-Custom"] = "");

        headers.Should().NotContainKey("X-Custom");
    }
}
