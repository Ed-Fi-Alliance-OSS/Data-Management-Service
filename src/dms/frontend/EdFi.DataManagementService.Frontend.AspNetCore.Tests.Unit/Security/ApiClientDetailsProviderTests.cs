// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Security;

[TestFixture]
[Parallelizable]
public class ApiClientDetailsProviderTests
{
    [Test]
    public void ApiClientDetailsProvider_MovedToCore()
    {
        // JWT token processing including ApiClientDetailsProvider has been moved to Core middleware.
        // The frontend no longer processes JWT tokens - it only passes the Authorization header.
        // Tests for ApiClientDetailsProvider should be in Core.Tests.Unit project.
        Assert.Pass("ApiClientDetailsProvider has been moved to Core as part of JWT refactoring");
    }
}
