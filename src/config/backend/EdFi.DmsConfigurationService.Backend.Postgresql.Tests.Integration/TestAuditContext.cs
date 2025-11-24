// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Infrastructure;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Test implementation of IAuditContext for integration tests.
/// Returns a fixed test user and current UTC timestamp.
/// </summary>
public class TestAuditContext : IAuditContext
{
    private readonly string _testUser;

    public TestAuditContext(string testUser = "test-user")
    {
        _testUser = testUser;
    }

    public string GetCurrentUser() => _testUser;

    public DateTime GetCurrentTimestamp() => DateTime.UtcNow;
}
