// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <summary>
/// Stable identifiers and well-known names shared by the external-doubles wired up for
/// API integration tests. Values are intentionally fixed so request/response payloads and
/// log lines remain deterministic across runs.
/// </summary>
internal static class ExternalDoublesConstants
{
    public const string SmokeToken = "smoke-token";
    public const string SmokeClientId = "smoke-client";
    public const string SmokeClaimSetName = "SIS-Vendor";
    public static readonly Guid StableClientUuid = Guid.Parse("11111111-1111-4111-8111-111111111111");
    public const long StableApplicationId = 1;
    public const long StableApplicationContextId = 1;
    public const long StableDmsInstanceId = 1;
}
