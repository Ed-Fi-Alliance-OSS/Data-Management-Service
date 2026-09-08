// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
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
    public void It_delivers_a_repeated_authorization_header_unreduced()
    {
        // A repeated Authorization header is unparseable per RFC 7235. Delivering the comma-joined
        // value lets core reject it as malformed ("Invalid Authorization header.") instead of
        // authenticating the first value.
        var headers = ExtractHeaders(request =>
            request.Headers["Authorization"] = new StringValues(["Bearer valid", "junk"])
        );

        headers["Authorization"].Should().Be("Bearer valid,junk");
    }

    [Test]
    public void It_delivers_a_repeated_authorization_header_with_a_blank_value_unreduced()
    {
        // A blank duplicate must survive the join (StringValues.ToString() would drop it),
        // otherwise a valid token sent alongside a blank Authorization line authenticates.
        var headers = ExtractHeaders(request =>
            request.Headers["Authorization"] = new StringValues(["Bearer valid", ""])
        );

        headers["Authorization"].Should().Be("Bearer valid,");
    }

    [Test]
    public void It_delivers_a_repeated_authorization_header_with_a_leading_blank_value_unreduced()
    {
        // Ordering matters: a blank first value must survive the join in position, so core sees
        // ",Bearer valid" (a fold artifact, rejected as malformed) rather than a lone valid token.
        var headers = ExtractHeaders(request =>
            request.Headers["Authorization"] = new StringValues(["", "Bearer valid"])
        );

        headers["Authorization"].Should().Be(",Bearer valid");
    }

    [Test]
    public void It_delivers_a_repeated_content_type_unreduced()
    {
        var headers = ExtractHeaders(request =>
            request.Headers["Content-Type"] = new StringValues(["application/json", "text/plain"])
        );

        headers["Content-Type"].Should().Be("application/json,text/plain");
    }

    [Test]
    public void It_delivers_a_multi_valued_if_none_match_header_unreduced()
    {
        var headers = ExtractHeaders(request =>
            request.Headers["If-None-Match"] = new StringValues([
                "\"1-stale\"",
                "W/\"5-current\"",
                "\"6-other\"",
            ])
        );

        headers["If-None-Match"].Should().Be("\"1-stale\",W/\"5-current\",\"6-other\"");
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

    [TestCase("gzip", ResponseContentCoding.Gzip)]
    [TestCase("br", ResponseContentCoding.Brotli)]
    [TestCase("identity", ResponseContentCoding.Identity)]
    [TestCase("gzip;q=0, br", ResponseContentCoding.Brotli)]
    public void It_uses_the_registered_response_compression_provider_to_select_content_coding(
        string acceptEncoding,
        ResponseContentCoding expectedContentCoding
    )
    {
        var responseCompressionProvider = A.Fake<IResponseCompressionProvider>();
        A.CallTo(() => responseCompressionProvider.CheckRequestAcceptsCompression(A<HttpContext>._))
            .Returns(true);

        if (expectedContentCoding is ResponseContentCoding.Identity)
        {
            A.CallTo(() => responseCompressionProvider.GetCompressionProvider(A<HttpContext>._))
                .Returns(null);
        }
        else
        {
            string encodingName = expectedContentCoding is ResponseContentCoding.Brotli ? "br" : "gzip";
            var compressionProvider = CompressionProvider(encodingName);
            A.CallTo(() => responseCompressionProvider.GetCompressionProvider(A<HttpContext>._))
                .Returns(compressionProvider);
        }

        var services = new ServiceCollection()
            .AddSingleton(responseCompressionProvider)
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Request.Headers.AcceptEncoding = acceptEncoding;

        AspNetCoreFrontend.ResolveResponseContentCoding(httpContext).Should().Be(expectedContentCoding);
    }

    [Test]
    public void It_selects_identity_when_response_compression_is_not_registered()
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
        httpContext.Request.Headers.AcceptEncoding = "gzip";

        AspNetCoreFrontend
            .ResolveResponseContentCoding(httpContext)
            .Should()
            .Be(ResponseContentCoding.Identity);
    }

    private static ICompressionProvider CompressionProvider(string encodingName)
    {
        var compressionProvider = A.Fake<ICompressionProvider>();
        A.CallTo(() => compressionProvider.EncodingName).Returns(encodingName);
        return compressionProvider;
    }
}
