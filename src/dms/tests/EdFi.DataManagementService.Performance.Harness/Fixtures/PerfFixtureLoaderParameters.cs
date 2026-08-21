// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// Bare parameter names (no @ prefix) shared by both dialects' loader SQL. Chunking is done
/// through parameter values, so one SQL text per statement serves every chunk.
/// </summary>
public static class PerfFixtureLoaderParameters
{
    public const string FromOrdinal = "fromOrdinal";
    public const string ToOrdinal = "toOrdinal";
    public const string ResourceKeyId = "resourceKeyId";
    public const string DescriptorDocumentId = "descriptorDocumentId";
    public const string DescriptorDocumentUuid = "descriptorDocumentUuid";
    public const string DescriptorReferentialId = "descriptorReferentialId";
    public const string BirthSexDescriptorId = "birthSexDescriptorId";
    public const string OtherNameTypeDescriptorId = "otherNameTypeDescriptorId";
    public const string IdentificationDocumentUseDescriptorId = "identificationDocumentUseDescriptorId";
    public const string PersonalInformationVerificationDescriptorId =
        "personalInformationVerificationDescriptorId";
    public const string VisaDescriptorId = "visaDescriptorId";
}

/// <summary>
/// One loader verification query with the value it must return for the fixture to count as
/// correctly loaded. Expected values are computed analytically from the definition.
/// </summary>
public sealed record PerfVerificationQuery(string Name, string Sql, long ExpectedValue);
