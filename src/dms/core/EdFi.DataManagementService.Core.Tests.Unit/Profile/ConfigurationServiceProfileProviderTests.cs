// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

// A CMS behind a reverse proxy is reached through a base address WITH a path, e.g.
// ConfigurationServiceSettings:BaseUrl = https://gateway/edfi/config/saas/v8.0/ds5.2/core/.
// Every request must resolve under that path. A request URI with a leading slash is an
// absolute-path reference, which replaces the base path instead of appending to it, so the
// request reaches the proxy root and 404s. A base address without a path cannot detect this,
// because "/v3/x" and "v3/x" resolve identically against "https://host/".
public class ConfigurationServiceProfileProviderTests
{
    private const string BaseAddressWithPath = "https://gateway.example/edfi/config/saas/v8.0/ds5.2/core/";
    private const string BasePath = "/edfi/config/saas/v8.0/ds5.2/core/";

    private static (ConfigurationServiceProfileProvider Provider, CapturingHandler Handler) CreateProvider(
        string responseBody
    )
    {
        var handler = new CapturingHandler(responseBody);
        var client = new HttpClient(handler) { BaseAddress = new Uri(BaseAddressWithPath) };

        var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
        A.CallTo(() =>
                tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._)
            )
            .Returns("cms-token");

        var provider = new ConfigurationServiceProfileProvider(
            new ConfigurationServiceApiClient(client),
            tokenHandler,
            new ConfigurationServiceContext("cms-client", "cms-secret", "cms-scope"),
            NullLogger<ConfigurationServiceProfileProvider>.Instance
        );
        return (provider, handler);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Base_Address_With_A_Path_When_Fetching_Application_Profile_Info
        : ConfigurationServiceProfileProviderTests
    {
        private CapturingHandler _handler = null!;
        private ApplicationProfileInfo? _result;

        [SetUp]
        public async Task Setup()
        {
            (ConfigurationServiceProfileProvider provider, _handler) = CreateProvider(
                """
                {
                  "id": 42,
                  "applicationName": "app",
                  "vendorId": 1,
                  "claimSetName": "SISVendor",
                  "educationOrganizationIds": [],
                  "dataStoreIds": [1],
                  "profileIds": [7]
                }
                """
            );
            _result = await provider.GetApplicationProfileInfoAsync(42, tenantId: null);
        }

        [Test]
        public void It_requests_the_application_under_the_base_path()
        {
            _handler.Request!.RequestUri!.AbsolutePath.Should().Be($"{BasePath}v3/applications/42");
        }

        [Test]
        public void It_returns_the_application_profile_ids()
        {
            _result.Should().NotBeNull();
            _result!.ProfileIds.Should().Equal(7L);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Base_Address_With_A_Path_When_Fetching_A_Profile
        : ConfigurationServiceProfileProviderTests
    {
        private CapturingHandler _handler = null!;
        private CmsProfileResponse? _result;

        [SetUp]
        public async Task Setup()
        {
            (ConfigurationServiceProfileProvider provider, _handler) = CreateProvider(
                """{ "id": 7, "name": "Read-Only", "definition": "<Profile name=\"Read-Only\"/>" }"""
            );
            _result = await provider.GetProfileAsync(7, tenantId: null);
        }

        [Test]
        public void It_requests_the_profile_under_the_base_path()
        {
            _handler.Request!.RequestUri!.AbsolutePath.Should().Be($"{BasePath}v3/profiles/7");
        }

        [Test]
        public void It_returns_the_profile()
        {
            _result.Should().NotBeNull();
            _result!.Name.Should().Be("Read-Only");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Base_Address_With_A_Path_When_Fetching_All_Profiles
        : ConfigurationServiceProfileProviderTests
    {
        private CapturingHandler _handler = null!;
        private IReadOnlyList<CmsProfileResponse> _result = [];

        [SetUp]
        public async Task Setup()
        {
            (ConfigurationServiceProfileProvider provider, _handler) = CreateProvider(
                """[{ "id": 7, "name": "Read-Only", "definition": "<Profile name=\"Read-Only\"/>" }]"""
            );
            _result = await provider.GetProfilesAsync(tenantId: null);
        }

        [Test]
        public void It_requests_the_profiles_under_the_base_path()
        {
            _handler.Request!.RequestUri!.AbsolutePath.Should().Be($"{BasePath}v3/profiles");
        }

        [Test]
        public void It_returns_the_profiles()
        {
            _result.Should().ContainSingle().Which.Id.Should().Be(7);
        }
    }

    protected sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Request = request;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
                }
            );
        }
    }
}
