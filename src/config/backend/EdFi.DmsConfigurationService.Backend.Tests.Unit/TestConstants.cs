// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// Shared constants for unit tests.
/// These values are intentionally weak and should NEVER be used in production.
/// </summary>
public static class TestConstants
{
    /// <summary>
    /// A placeholder JWT signing key for unit tests only.
    /// WARNING: This is intentionally a weak, publicly visible key.
    /// Do NOT use this value in production or any real environment.
    /// Minimum 32 characters required for HMAC-SHA256.
    /// </summary>
    public const string TestJwtSigningKey = "test-placeholder-secret-key-32-chars-minimum";
}
