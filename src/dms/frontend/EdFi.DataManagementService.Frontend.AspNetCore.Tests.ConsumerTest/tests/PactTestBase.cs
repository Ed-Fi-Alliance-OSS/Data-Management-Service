// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FakeItEasy;
using NUnit.Framework;
using PactNet;
using PactNet.Matchers;
using System.Text.Json;
using EdFi.DataManagementService.Core.OAuth;
using EdFi.DataManagementService.Core;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Frontend.AspNetCore.ContractTest;

[TestFixture]
public class PactTestBase
{
    public IPactBuilderV3? pact;
    public IHttpClientWrapper? fakeHttpClientWrapper;
    //protected IPactBuilderV3? pactBuilder;
    public OAuthManager? oAuthManager;
    public string _mockProviderServiceBaseUri = "http://localhost:9222"; // Mock service URI

    private string pactDir = Path.Join("..", "..", "..", "pacts");
    //private readonly int port = 9876;

    [SetUp]
    public void SetUp()
    {
        var Config = new PactConfig
        {
            PactDir = pactDir,
            DefaultJsonSettings = new JsonSerializerOptions()
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }
        };

        //pactBuilder = pact.UsingNativeBackend();
        pact = Pact.V3("ConfigurationService-API", "DMS-API").WithHttpInteractions();

        fakeHttpClientWrapper = A.Fake<IHttpClientWrapper>();
        var fakeLogger = A.Fake<ILogger<OAuthManager>>();
        oAuthManager = new OAuthManager(fakeLogger);
    }
}


