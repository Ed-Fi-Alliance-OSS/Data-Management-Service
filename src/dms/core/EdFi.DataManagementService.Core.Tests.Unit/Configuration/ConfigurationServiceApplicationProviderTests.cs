// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

[TestFixture]
public class Given_ConfigurationServiceApplicationProvider
{
    [Test]
    public async Task It_Should_Return_A_Successful_Application_Context_From_A_Valid_Response()
    {
        using var fixture = new ProviderFixture(HttpStatusCode.OK, ValidApplicationContextJson());

        ApplicationContextResult result = await fixture.Provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        var success = result.Should().BeOfType<ApplicationContextResult.Success>().Subject;
        success.ApplicationContext.Id.Should().Be(1);
        success.ApplicationContext.ApplicationId.Should().Be(2);
        success.ApplicationContext.ClientId.Should().Be("client-id");
        success.ApplicationContext.DataStoreIds.Should().Equal(3L, 4L);
        success.ApplicationContext.CreatorOwnershipTokenId.Should().Be(5);
        success.ApplicationContext.OwnershipTokenIds.Should().Equal((short)6, (short)7);
        fixture.Handler.Request!.Method.Should().Be(HttpMethod.Get);
        fixture.Handler.Request.RequestUri!.PathAndQuery.Should().Be("/v3/apiClients/client-id");
        fixture
            .Handler.Request.Headers.Authorization.Should()
            .BeEquivalentTo(new AuthenticationHeaderValue("Bearer", "cms-token"));
    }

    [Test]
    public async Task It_Should_Return_NotFound_For_A_404_Response()
    {
        using var fixture = new ProviderFixture(HttpStatusCode.NotFound, "");

        ApplicationContextResult result = await fixture.Provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        result.Should().BeOfType<ApplicationContextResult.NotFound>();
    }

    [Test]
    public async Task It_Should_Return_NotFound_For_A_404_Response_Through_The_Production_Response_Handler()
    {
        using var fixture = new ProviderFixture(HttpStatusCode.NotFound, "", useResponseHandler: true);

        ApplicationContextResult result = await fixture.Provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        result.Should().BeOfType<ApplicationContextResult.NotFound>();
    }

    [TestCase(HttpStatusCode.InternalServerError)]
    [TestCase(HttpStatusCode.Unauthorized)]
    public async Task It_Should_Return_Unavailable_For_A_NonSuccess_Response(HttpStatusCode statusCode)
    {
        using var fixture = new ProviderFixture(statusCode, "");

        ApplicationContextResult result = await fixture.Provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        result.Should().BeOfType<ApplicationContextResult.Unavailable>();
    }

    [Test]
    public async Task It_Should_Return_Unavailable_For_A_Transport_Failure()
    {
        using var fixture = new ProviderFixture(new HttpRequestException("transport failure"));

        ApplicationContextResult result = await fixture.Provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        result.Should().BeOfType<ApplicationContextResult.Unavailable>();
    }

    [Test]
    public async Task It_Should_Return_Unavailable_For_Malformed_Json()
    {
        using var fixture = new ProviderFixture(HttpStatusCode.OK, "not-json");

        ApplicationContextResult result = await fixture.Provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        result.Should().BeOfType<ApplicationContextResult.Unavailable>();
    }

    [Test]
    public async Task It_Should_Return_Unavailable_When_Required_Application_Context_Structure_Is_Missing()
    {
        using var fixture = new ProviderFixture(HttpStatusCode.OK, "{\"id\":1}");

        ApplicationContextResult result = await fixture.Provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        result.Should().BeOfType<ApplicationContextResult.Unavailable>();
    }

    [TestCase("other-client")]
    [TestCase("")]
    [TestCase("   ")]
    public async Task It_Should_Return_Unavailable_When_Response_Client_Id_Is_Not_The_Requested_Client(
        string responseClientId
    )
    {
        using var fixture = new ProviderFixture(
            HttpStatusCode.OK,
            ValidApplicationContextJson(clientId: responseClientId)
        );

        ApplicationContextResult result = await fixture.Provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        result.Should().BeOfType<ApplicationContextResult.Unavailable>();
    }

    [Test]
    public async Task It_Should_Log_A_Distinct_Error_When_Response_Client_Id_Is_A_Different_Client()
    {
        RecordingLogger<ConfigurationServiceApplicationProvider> logger = new();
        using var fixture = new ProviderFixture(
            HttpStatusCode.OK,
            ValidApplicationContextJson(clientId: "other-client"),
            logger: logger
        );

        ApplicationContextResult result = await fixture.Provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        result.Should().BeOfType<ApplicationContextResult.Unavailable>();

        LogRecord rejection = logger
            .Records.Should()
            .ContainSingle(record => record.Level == LogLevel.Error)
            .Subject;
        rejection.Message.Should().NotContain("Failed to deserialize application context");
        rejection
            .Message.Should()
            .Be(
                "Configuration Service returned an application context for a different clientId. Requested clientId: client-id, returned clientId: other-client"
            );
        rejection.Properties.Should().Contain("RequestedClientId", "client-id");
        rejection.Properties.Should().Contain("ResponseClientId", "other-client");
    }

    [Test]
    public async Task It_Should_Return_Unavailable_When_Response_Client_Uuid_Is_Empty()
    {
        using var fixture = new ProviderFixture(
            HttpStatusCode.OK,
            ValidApplicationContextJson(clientUuid: Guid.Empty)
        );

        ApplicationContextResult result = await fixture.Provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        result.Should().BeOfType<ApplicationContextResult.Unavailable>();
    }

    [TestCase(
        "{\"id\":0,\"applicationId\":2,\"clientId\":\"client-id\",\"clientUuid\":\"8c58fef1-7d9b-4423-bb3c-f1581e77e922\",\"dataStoreIds\":[3],\"creatorOwnershipTokenId\":null,\"ownershipTokenIds\":[]}"
    )]
    [TestCase(
        "{\"id\":-1,\"applicationId\":2,\"clientId\":\"client-id\",\"clientUuid\":\"8c58fef1-7d9b-4423-bb3c-f1581e77e922\",\"dataStoreIds\":[3],\"creatorOwnershipTokenId\":null,\"ownershipTokenIds\":[]}"
    )]
    [TestCase(
        "{\"id\":1,\"applicationId\":0,\"clientId\":\"client-id\",\"clientUuid\":\"8c58fef1-7d9b-4423-bb3c-f1581e77e922\",\"dataStoreIds\":[3],\"creatorOwnershipTokenId\":null,\"ownershipTokenIds\":[]}"
    )]
    [TestCase(
        "{\"id\":1,\"applicationId\":-1,\"clientId\":\"client-id\",\"clientUuid\":\"8c58fef1-7d9b-4423-bb3c-f1581e77e922\",\"dataStoreIds\":[3],\"creatorOwnershipTokenId\":null,\"ownershipTokenIds\":[]}"
    )]
    public async Task It_Should_Return_Unavailable_For_NonPositive_Application_Identifiers(
        string responseBody
    )
    {
        using var fixture = new ProviderFixture(HttpStatusCode.OK, responseBody);

        ApplicationContextResult result = await fixture.Provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        result.Should().BeOfType<ApplicationContextResult.Unavailable>();
    }

    [TestCase(
        "{\"id\":1,\"applicationId\":2,\"clientId\":\"client-id\",\"clientUuid\":\"8c58fef1-7d9b-4423-bb3c-f1581e77e922\",\"dataStoreIds\":[3],\"creatorOwnershipTokenId\":32768,\"ownershipTokenIds\":[]}"
    )]
    [TestCase(
        "{\"id\":1,\"applicationId\":2,\"clientId\":\"client-id\",\"clientUuid\":\"8c58fef1-7d9b-4423-bb3c-f1581e77e922\",\"dataStoreIds\":[3],\"creatorOwnershipTokenId\":null,\"ownershipTokenIds\":[32768]}"
    )]
    [TestCase(
        "{\"id\":1,\"applicationId\":2,\"clientId\":\"client-id\",\"clientUuid\":\"8c58fef1-7d9b-4423-bb3c-f1581e77e922\",\"dataStoreIds\":[3],\"creatorOwnershipTokenId\":0,\"ownershipTokenIds\":[]}"
    )]
    [TestCase(
        "{\"id\":1,\"applicationId\":2,\"clientId\":\"client-id\",\"clientUuid\":\"8c58fef1-7d9b-4423-bb3c-f1581e77e922\",\"dataStoreIds\":[3],\"creatorOwnershipTokenId\":-1,\"ownershipTokenIds\":[]}"
    )]
    [TestCase(
        "{\"id\":1,\"applicationId\":2,\"clientId\":\"client-id\",\"clientUuid\":\"8c58fef1-7d9b-4423-bb3c-f1581e77e922\",\"dataStoreIds\":[3],\"creatorOwnershipTokenId\":null,\"ownershipTokenIds\":[0]}"
    )]
    [TestCase(
        "{\"id\":1,\"applicationId\":2,\"clientId\":\"client-id\",\"clientUuid\":\"8c58fef1-7d9b-4423-bb3c-f1581e77e922\",\"dataStoreIds\":[3],\"creatorOwnershipTokenId\":null,\"ownershipTokenIds\":[-1]}"
    )]
    public async Task It_Should_Return_Unavailable_For_OutOfRange_Ownership_Token_Ids(string responseBody)
    {
        using var fixture = new ProviderFixture(HttpStatusCode.OK, responseBody);

        ApplicationContextResult result = await fixture.Provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        result.Should().BeOfType<ApplicationContextResult.Unavailable>();
    }

    [TestCase("7,7", new short[] { 7 })]
    [TestCase("7,6", new short[] { 6, 7 })]
    [TestCase("7,6,7", new short[] { 6, 7 })]
    public async Task It_Should_Normalize_Ownership_Token_Ids_To_Sorted_Distinct(
        string responseOwnershipTokenIds,
        short[] expectedOwnershipTokenIds
    )
    {
        using var fixture = new ProviderFixture(
            HttpStatusCode.OK,
            $$"""
            {
              "id": 1,
              "applicationId": 2,
              "clientId": "client-id",
              "clientUuid": "8c58fef1-7d9b-4423-bb3c-f1581e77e922",
              "dataStoreIds": [3],
              "creatorOwnershipTokenId": null,
              "ownershipTokenIds": [{{responseOwnershipTokenIds}}]
            }
            """
        );

        ApplicationContextResult result = await fixture.Provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        var success = result.Should().BeOfType<ApplicationContextResult.Success>().Subject;
        success.ApplicationContext.OwnershipTokenIds.Should().Equal(expectedOwnershipTokenIds);
    }

    [Test]
    public async Task It_Should_Return_Unavailable_When_Ownership_Token_List_Is_Too_Large()
    {
        string ownershipTokenIds = string.Join(",", Enumerable.Repeat("7", 2000));
        using var fixture = new ProviderFixture(
            HttpStatusCode.OK,
            $$"""
            {
              "id": 1,
              "applicationId": 2,
              "clientId": "client-id",
              "clientUuid": "8c58fef1-7d9b-4423-bb3c-f1581e77e922",
              "dataStoreIds": [3],
              "creatorOwnershipTokenId": null,
              "ownershipTokenIds": [{{ownershipTokenIds}}]
            }
            """
        );

        ApplicationContextResult result = await fixture.Provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        result.Should().BeOfType<ApplicationContextResult.Unavailable>();
    }

    [Test]
    public async Task It_Should_Accept_A_Response_That_Omits_The_Nullable_Creator_Token()
    {
        using var fixture = new ProviderFixture(
            HttpStatusCode.OK,
            """
            {
              "id": 1,
              "applicationId": 2,
              "clientId": "client-id",
              "clientUuid": "8c58fef1-7d9b-4423-bb3c-f1581e77e922",
              "dataStoreIds": [3],
              "ownershipTokenIds": [7, 6]
            }
            """
        );

        ApplicationContextResult result = await fixture.Provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        var success = result.Should().BeOfType<ApplicationContextResult.Success>().Subject;
        success.ApplicationContext.CreatorOwnershipTokenId.Should().BeNull();
        success.ApplicationContext.OwnershipTokenIds.Should().Equal((short)6, (short)7);
    }

    [Test]
    public async Task It_Should_Accept_A_Null_Creator_And_Empty_Ownership_Tokens()
    {
        using var fixture = new ProviderFixture(
            HttpStatusCode.OK,
            ValidApplicationContextJson(creatorOwnershipTokenId: null, ownershipTokenIds: [])
        );

        ApplicationContextResult result = await fixture.Provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        var success = result.Should().BeOfType<ApplicationContextResult.Success>().Subject;
        success.ApplicationContext.CreatorOwnershipTokenId.Should().BeNull();
        success.ApplicationContext.OwnershipTokenIds.Should().BeEmpty();
    }

    [Test]
    public async Task It_Should_Send_The_Original_Case_Tenant_Header_Only_When_A_Tenant_Is_Supplied()
    {
        using var tenantFixture = new ProviderFixture(HttpStatusCode.OK, ValidApplicationContextJson());
        await tenantFixture.Provider.GetApplicationByClientIdAsync("client-id", "Tenant-MixedCase");

        tenantFixture
            .Handler.Request!.Headers.Should()
            .Contain(header => header.Key == "Tenant" && header.Value.Single() == "Tenant-MixedCase");
        tenantFixture.Handler.Request!.Headers.GetValues("Tenant").Should().ContainSingle("Tenant-MixedCase");

        using var singleTenantFixture = new ProviderFixture(HttpStatusCode.OK, ValidApplicationContextJson());
        await singleTenantFixture.Provider.GetApplicationByClientIdAsync("client-id", tenant: null);

        singleTenantFixture.Handler.Request!.Headers.Contains("Tenant").Should().BeFalse();
    }

    [Test]
    public async Task It_Should_Not_Mutate_HttpClient_Default_Request_Headers()
    {
        using var fixture = new ProviderFixture(HttpStatusCode.OK, ValidApplicationContextJson());
        fixture.Client.DefaultRequestHeaders.Add("Existing-Header", "existing-value");

        await fixture.Provider.GetApplicationByClientIdAsync("client-id", "Tenant-A");

        fixture.Client.DefaultRequestHeaders.Authorization.Should().BeNull();
        fixture.Client.DefaultRequestHeaders.Contains("Tenant").Should().BeFalse();
        fixture
            .Client.DefaultRequestHeaders.GetValues("Existing-Header")
            .Should()
            .ContainSingle("existing-value");
    }

    private static string ValidApplicationContextJson(
        string clientId = "client-id",
        Guid? clientUuid = null,
        short? creatorOwnershipTokenId = 5,
        short[]? ownershipTokenIds = null
    )
    {
        var creator = creatorOwnershipTokenId.HasValue ? creatorOwnershipTokenId.Value.ToString() : "null";
        var ownershipTokens = ownershipTokenIds is null ? "6,7" : string.Join(",", ownershipTokenIds);

        return $$"""
            {
              "id": 1,
              "applicationId": 2,
              "clientId": "{{clientId}}",
              "clientUuid": "{{clientUuid ?? Guid.Parse("8c58fef1-7d9b-4423-bb3c-f1581e77e922")}}",
              "dataStoreIds": [3, 4],
              "creatorOwnershipTokenId": {{creator}},
              "ownershipTokenIds": [{{ownershipTokens}}]
            }
            """;
    }

    private sealed class ProviderFixture : IDisposable
    {
        // csharpier-ignore - IDE0055 requires this empty delegating constructor body shape.
        public ProviderFixture(
            HttpStatusCode statusCode,
            string responseBody,
            bool useResponseHandler = false,
            ILogger<ConfigurationServiceApplicationProvider>? logger = null
        )
            : this(
                new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
                },
                useResponseHandler,
                logger
            )
        { }

        public ProviderFixture(Exception exception)
        {
            Handler = new CapturingHttpMessageHandler(exception);
            Client = new HttpClient(Handler) { BaseAddress = new Uri("https://cms.example/") };
            Provider = CreateProvider(Client);
        }

        private ProviderFixture(
            HttpResponseMessage response,
            bool useResponseHandler = false,
            ILogger<ConfigurationServiceApplicationProvider>? logger = null
        )
        {
            Handler = new CapturingHttpMessageHandler(response);
            HttpMessageHandler messageHandler = useResponseHandler
                ? new ConfigurationServiceResponseHandler(
                    NullLogger<ConfigurationServiceResponseHandler>.Instance
                )
                {
                    InnerHandler = Handler,
                }
                : Handler;
            Client = new HttpClient(messageHandler) { BaseAddress = new Uri("https://cms.example/") };
            Provider = CreateProvider(Client, logger);
        }

        public HttpClient Client { get; }

        public CapturingHttpMessageHandler Handler { get; }

        public ConfigurationServiceApplicationProvider Provider { get; }

        public void Dispose()
        {
            Client.Dispose();
            Handler.Dispose();
        }

        private static ConfigurationServiceApplicationProvider CreateProvider(
            HttpClient httpClient,
            ILogger<ConfigurationServiceApplicationProvider>? logger = null
        )
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() =>
                    tokenHandler.GetTokenAsync(
                        "cms-client",
                        "cms-secret",
                        "cms-scope",
                        A<CancellationToken>._
                    )
                )
                .Returns("cms-token");

            return new ConfigurationServiceApplicationProvider(
                new ConfigurationServiceApiClient(httpClient),
                tokenHandler,
                new ConfigurationServiceContext("cms-client", "cms-secret", "cms-scope"),
                logger ?? NullLogger<ConfigurationServiceApplicationProvider>.Instance
            );
        }
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Exception? _exception;
        private readonly HttpResponseMessage? _response;

        public CapturingHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public CapturingHttpMessageHandler(Exception exception)
        {
            _exception = exception;
        }

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Request = request;

            if (_exception is not null)
            {
                return Task.FromException<HttpResponseMessage>(_exception);
            }

            return Task.FromResult(_response!);
        }
    }
}
