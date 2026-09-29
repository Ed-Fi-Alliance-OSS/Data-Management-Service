// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

[TestFixture]
[Parallelizable]
public class HttpDocumentRetrieverTests
{
    private static readonly Uri _metadataAddress = new(
        "http://dms-keycloak:8080/realms/edfi/.well-known/openid-configuration"
    );

    private sealed class RecordingHandler(string body = "document") : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
            );
        }
    }

    private static async Task<(string? Document, Exception? Exception, RecordingHandler Handler)> Fetch(
        string address,
        ILogger<HttpDocumentRetriever>? logger = null
    )
    {
        var handler = new RecordingHandler();
        var retriever = new HttpDocumentRetriever(
            new HttpClient(handler),
            _metadataAddress,
            logger ?? NullLogger<HttpDocumentRetriever>.Instance
        );

        (string? document, Exception? exception) = await TryFetch(retriever, address);
        return (document, exception, handler);
    }

    private static async Task<(string? Document, Exception? Exception)> TryFetch(
        HttpDocumentRetriever retriever,
        string address
    )
    {
        try
        {
            return (await retriever.GetDocumentAsync(address, CancellationToken.None), null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Jwks_Uri_On_The_Metadata_Address_Origin : HttpDocumentRetrieverTests
    {
        private string? _document;

        [SetUp]
        public async Task Setup()
        {
            (_document, _, _) = await Fetch(
                "http://DMS-KEYCLOAK:8080/realms/edfi/protocol/openid-connect/certs"
            );
        }

        [Test]
        public void It_returns_the_document()
        {
            _document.Should().Be("document");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Jwks_Uri_On_Another_Host : HttpDocumentRetrieverTests
    {
        private Exception? _exception;
        private RecordingHandler _handler = null!;

        [SetUp]
        public async Task Setup()
        {
            (_, _exception, _handler) = await Fetch("http://evil.example:8080/.well-known/jwks.json");
        }

        [Test]
        public void It_rejects_the_address_naming_the_allowed_origin()
        {
            _exception
                .Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message.Should()
                .Be(
                    "OIDC document address 'http://evil.example:8080/.well-known/jwks.json' is not on the JwtAuthentication:MetadataAddress origin 'http://dms-keycloak:8080'"
                );
        }

        [Test]
        public void It_sends_no_request()
        {
            _handler.Requests.Should().BeEmpty();
        }
    }

    /// <summary>
    /// A refusal during a background refresh never reaches DMS code, because the configuration
    /// manager swallows it, so the retriever's own log entry is its only trace.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Foreign_Address_Refused_With_A_Logger : HttpDocumentRetrieverTests
    {
        private RecordingLogger<HttpDocumentRetriever> _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new RecordingLogger<HttpDocumentRetriever>();
            await Fetch("http://evil.example/\r\n.well-known/jwks.json", _logger);
        }

        [Test]
        public void It_logs_an_error_naming_the_sanitized_address()
        {
            _logger
                .Records.Should()
                .ContainSingle(entry => entry.Level == LogLevel.Error)
                .Which.Properties["DocumentAddress"]
                .Should()
                .Be("http://evil.example/.well-known/jwks.json");
        }

        [Test]
        public void It_logs_an_error_naming_the_allowed_origin()
        {
            _logger
                .Records.Should()
                .ContainSingle(entry => entry.Level == LogLevel.Error)
                .Which.Properties["AllowedOrigin"]
                .Should()
                .Be("http://dms-keycloak:8080");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Jwks_Uri_On_Another_Port : HttpDocumentRetrieverTests
    {
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            (_, _exception, _) = await Fetch(
                "http://dms-keycloak:9090/realms/edfi/protocol/openid-connect/certs"
            );
        }

        [Test]
        public void It_rejects_the_address()
        {
            _exception.Should().BeOfType<InvalidOperationException>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Jwks_Uri_On_Another_Scheme : HttpDocumentRetrieverTests
    {
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            (_, _exception, _) = await Fetch(
                "https://dms-keycloak:8080/realms/edfi/protocol/openid-connect/certs"
            );
        }

        [Test]
        public void It_rejects_the_address()
        {
            _exception.Should().BeOfType<InvalidOperationException>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Foreign_Address_Containing_Line_Breaks : HttpDocumentRetrieverTests
    {
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            (_, _exception, _) = await Fetch("http://evil.example/\r\nforged-log-line");
        }

        [Test]
        public void It_sanitizes_the_address_in_the_error()
        {
            _exception!.Message.Should().NotContain("\r").And.NotContain("\n");
        }
    }

    /// <summary>
    /// A tampered metadata document: it asserts the configured issuer but names signing keys on
    /// another host. The library fetches jwks_uri through this retriever, so the origin check must
    /// fail the whole retrieval before the foreign host is contacted.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Metadata_Document_Naming_A_Foreign_Jwks_Uri : HttpDocumentRetrieverTests
    {
        private Exception? _exception;
        private RecordingHandler _handler = null!;

        [SetUp]
        public async Task Setup()
        {
            _handler = new RecordingHandler(
                """
                {
                  "issuer": "http://localhost:8045/realms/edfi",
                  "jwks_uri": "https://evil.example/.well-known/jwks.json"
                }
                """
            );
            var retriever = new HttpDocumentRetriever(
                new HttpClient(_handler),
                _metadataAddress,
                NullLogger<HttpDocumentRetriever>.Instance
            );

            try
            {
                await OpenIdConnectConfigurationRetriever.GetAsync(
                    _metadataAddress.ToString(),
                    retriever,
                    CancellationToken.None
                );
            }
            catch (Exception exception)
            {
                _exception = exception;
            }
        }

        [Test]
        public void It_fails_the_retrieval()
        {
            _exception.Should().NotBeNull();
        }

        [Test]
        public void It_contacts_only_the_metadata_address()
        {
            _handler.Requests.Should().Equal(_metadataAddress);
        }
    }

    /// <summary>
    /// Once the automatic refresh is due, the configuration manager retries a failed refresh on every
    /// request, so one misconfigured jwks_uri would otherwise log an Error per request. Each attempt
    /// fetches the metadata document successfully first, which must not end the episode; only a
    /// successful signing-key fetch does.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_Repeated_Refusals_Before_A_Signing_Key_Fetch_Succeeds : HttpDocumentRetrieverTests
    {
        private const string ForeignJwksUri = "http://evil.example/.well-known/jwks.json";
        private const string OnOriginJwksUri =
            "http://dms-keycloak:8080/realms/edfi/protocol/openid-connect/certs";

        private RecordingLogger<HttpDocumentRetriever> _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new RecordingLogger<HttpDocumentRetriever>();
            var retriever = new HttpDocumentRetriever(
                new HttpClient(new RecordingHandler()),
                _metadataAddress,
                _logger
            );

            // Two refresh attempts meeting the foreign jwks_uri, each preceded by a metadata GET.
            await TryFetch(retriever, _metadataAddress.ToString());
            await TryFetch(retriever, ForeignJwksUri);
            await TryFetch(retriever, _metadataAddress.ToString());
            await TryFetch(retriever, ForeignJwksUri);

            // The IdP is fixed, then breaks again.
            await TryFetch(retriever, _metadataAddress.ToString());
            await TryFetch(retriever, OnOriginJwksUri);
            await TryFetch(retriever, _metadataAddress.ToString());
            await TryFetch(retriever, ForeignJwksUri);
        }

        [Test]
        public void It_logs_the_first_refusal_of_each_episode_at_error_and_repeats_at_debug()
        {
            _logger
                .Records.Select(entry => entry.Level)
                .Should()
                .Equal(LogLevel.Error, LogLevel.Debug, LogLevel.Error);
        }
    }

    /// <summary>
    /// With an https MetadataAddress the scheme alone puts an http address off the origin, so the
    /// origin check refuses and logs it.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Https_Metadata_Address_And_An_Http_Address_On_Another_Host
        : HttpDocumentRetrieverTests
    {
        private Exception? _exception;
        private RecordingHandler _handler = null!;
        private RecordingLogger<HttpDocumentRetriever> _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            _handler = new RecordingHandler();
            _logger = new RecordingLogger<HttpDocumentRetriever>();
            var retriever = new HttpDocumentRetriever(
                new HttpClient(_handler),
                new Uri("https://idp.example/realms/edfi/.well-known/openid-configuration"),
                _logger
            );

            (_, _exception) = await TryFetch(retriever, "http://evil.example/.well-known/jwks.json");
        }

        [Test]
        public void It_rejects_the_address_as_off_origin()
        {
            _exception
                .Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message.Should()
                .StartWith("OIDC document address 'http://evil.example/.well-known/jwks.json' is not on");
        }

        [Test]
        public void It_logs_the_refusal_at_error()
        {
            _logger.Records.Should().ContainSingle(entry => entry.Level == LogLevel.Error);
        }

        [Test]
        public void It_sends_no_request()
        {
            _handler.Requests.Should().BeEmpty();
        }
    }

    /// <summary>
    /// The metadata origin written as userinfo in front of another host. A string-prefix origin check
    /// would accept it; the parsed host is evil.example.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Jwks_Uri_Carrying_The_Metadata_Origin_As_Userinfo : HttpDocumentRetrieverTests
    {
        private Exception? _exception;
        private RecordingHandler _handler = null!;

        [SetUp]
        public async Task Setup()
        {
            (_, _exception, _handler) = await Fetch(
                "http://dms-keycloak:8080@evil.example/.well-known/jwks.json"
            );
        }

        [Test]
        public void It_rejects_the_address()
        {
            _exception.Should().BeOfType<InvalidOperationException>();
        }

        [Test]
        public void It_sends_no_request()
        {
            _handler.Requests.Should().BeEmpty();
        }
    }

    /// <summary>
    /// A host that starts with the metadata host. With a default-port origin, a string-prefix origin
    /// check would accept it.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Jwks_Uri_On_A_Host_Extending_The_Metadata_Host : HttpDocumentRetrieverTests
    {
        private Exception? _exception;
        private RecordingHandler _handler = null!;

        [SetUp]
        public async Task Setup()
        {
            _handler = new RecordingHandler();
            var retriever = new HttpDocumentRetriever(
                new HttpClient(_handler),
                new Uri("https://idp.example/realms/edfi/.well-known/openid-configuration"),
                NullLogger<HttpDocumentRetriever>.Instance
            );

            (_, _exception) = await TryFetch(retriever, "https://idp.example.evil.test/jwks");
        }

        [Test]
        public void It_rejects_the_address()
        {
            _exception.Should().BeOfType<InvalidOperationException>();
        }

        [Test]
        public void It_sends_no_request()
        {
            _handler.Requests.Should().BeEmpty();
        }
    }
}
