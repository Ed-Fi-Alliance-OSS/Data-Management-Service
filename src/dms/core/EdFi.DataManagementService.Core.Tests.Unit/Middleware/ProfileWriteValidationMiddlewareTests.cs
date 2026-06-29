// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class Given_Profile_Write_Validation_Middleware
{
    private bool _nextCalled;

    [SetUp]
    public async Task Setup()
    {
        var requestInfo = new RequestInfo(
            new FrontendRequest(
                Path: "/ed-fi/students",
                Body: "{}",
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("test-trace-id"),
                RouteQualifiers: []
            ),
            RequestMethod.POST,
            No.ServiceProvider
        );

        _nextCalled = false;

        await new ProfileWriteValidationMiddleware().Execute(
            requestInfo,
            () =>
            {
                _nextCalled = true;
                return Task.CompletedTask;
            }
        );
    }

    [Test]
    public void It_leaves_profile_write_processing_to_the_relational_profile_write_pipeline()
    {
        _nextCalled.Should().BeTrue();
    }
}
