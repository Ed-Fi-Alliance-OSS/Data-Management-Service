// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.Security;
using FluentAssertions;
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
        string address
    )
    {
        var handler = new RecordingHandler();
        var retriever = new HttpDocumentRetriever(new HttpClient(handler), _metadataAddress)
        {
            RequireHttps = false,
        };

        try
        {
            return (await retriever.GetDocumentAsync(address, CancellationToken.None), null, handler);
        }
        catch (Exception exception)
        {
            return (null, exception, handler);
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
    /// The DMS-1488 shape: the document asserts the configured issuer but names signing keys on
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
            var retriever = new HttpDocumentRetriever(new HttpClient(_handler), _metadataAddress)
            {
                RequireHttps = false,
            };

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
}
