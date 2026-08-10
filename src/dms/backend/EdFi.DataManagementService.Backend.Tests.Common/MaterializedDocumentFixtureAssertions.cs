// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.Tests.Common;

public static class MaterializedDocumentFixtureAssertions
{
    public static void AssertCandidateMatchesFixture(
        MaterializedDocumentFixtureActualCacheRow candidate,
        MaterializedDocumentFixture fixture
    )
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(fixture);

        var expected =
            fixture.ExpectedCacheRow
            ?? throw new InvalidOperationException(
                $"Fixture '{fixture.CaseName}' does not declare an expected cache row."
            );

        AssertEqual(expected.DocumentId, candidate.DocumentId, fixture, "DocumentId");
        AssertEqual(expected.DocumentUuid, candidate.DocumentUuid, fixture, "DocumentUuid");
        AssertEqual(expected.ProjectName, candidate.ProjectName, fixture, "ProjectName");
        AssertEqual(expected.ResourceName, candidate.ResourceName, fixture, "ResourceName");
        AssertEqual(expected.ResourceVersion, candidate.ResourceVersion, fixture, "ResourceVersion");
        AssertEqual(expected.ContentVersion, candidate.ContentVersion, fixture, "ContentVersion");
        AssertEqual(expected.LastModifiedAt, candidate.LastModifiedAt, fixture, "LastModifiedAt");
        AssertEqual(expected.StreamEtag, candidate.StreamEtag, fixture, "StreamEtag");

        if (candidate.DocumentJson.ContainsKey("_etag"))
        {
            throw new InvalidOperationException(
                $"Fixture '{fixture.CaseName}' expected DocumentJson without _etag."
            );
        }

        if (!JsonNode.DeepEquals(expected.DocumentJson, candidate.DocumentJson))
        {
            throw new InvalidOperationException(
                $"Fixture '{fixture.CaseName}' DocumentJson mismatch. "
                    + $"Expected: {expected.DocumentJson.ToJsonString()}; Actual: {candidate.DocumentJson.ToJsonString()}."
            );
        }
    }

    public static void AssertProjectionFailureMatchesFixture(
        MaterializedDocumentFixtureActualProjectionFailure failure,
        MaterializedDocumentFixture fixture
    )
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(fixture);

        var expected =
            fixture.ExpectedProjectionFailure
            ?? throw new InvalidOperationException(
                $"Fixture '{fixture.CaseName}' does not declare an expected projection failure."
            );

        AssertEqual(expected.Reason, failure.Reason, fixture, "ProjectionFailure.Reason");
        AssertEqual(expected.DocumentId, failure.DocumentId, fixture, "DocumentId");
        AssertEqual(expected.ResourceKeyId, failure.ResourceKeyId, fixture, "ResourceKeyId");
        AssertEqual(expected.ProjectName, failure.ProjectName, fixture, "ProjectName");
        AssertEqual(expected.ResourceName, failure.ResourceName, fixture, "ResourceName");
        AssertEqual(expected.ResourceVersion, failure.ResourceVersion, fixture, "ResourceVersion");
    }

    public static void AssertSchoolAddressDescriptorAbsence(
        JsonObject actualDocumentJson,
        MaterializedDocumentFixture fixture
    )
    {
        ArgumentNullException.ThrowIfNull(actualDocumentJson);
        ArgumentNullException.ThrowIfNull(fixture);

        AssertSchoolAddressDescriptorAbsence(
            fixture.ExpectedCacheRow?.DocumentJson
                ?? throw new InvalidOperationException(
                    $"Fixture '{fixture.CaseName}' does not declare an expected cache document."
                ),
            fixture,
            "expected cache document"
        );
        AssertSchoolAddressDescriptorAbsence(
            fixture.ExpectedPublicCdcDocument?.Document
                ?? throw new InvalidOperationException(
                    $"Fixture '{fixture.CaseName}' does not declare an expected public CDC document."
                ),
            fixture,
            "expected public CDC document"
        );
        AssertSchoolAddressDescriptorAbsence(actualDocumentJson, fixture, "actual materialized document");
    }

    private static void AssertSchoolAddressDescriptorAbsence(
        JsonObject documentJson,
        MaterializedDocumentFixture fixture,
        string documentDescription
    )
    {
        var addresses =
            documentJson["addresses"]?.AsArray()
            ?? throw new InvalidOperationException(
                $"Fixture '{fixture.CaseName}' {documentDescription} must include addresses array."
            );

        if (addresses.Count != 2)
        {
            throw new InvalidOperationException(
                $"Fixture '{fixture.CaseName}' {documentDescription} must include exactly two addresses."
            );
        }

        var firstAddress =
            addresses[0]?.AsObject()
            ?? throw new InvalidOperationException(
                $"Fixture '{fixture.CaseName}' {documentDescription} first address must be an object."
            );
        var secondAddress =
            addresses[1]?.AsObject()
            ?? throw new InvalidOperationException(
                $"Fixture '{fixture.CaseName}' {documentDescription} second address must be an object."
            );

        if (
            !firstAddress.TryGetPropertyValue("addressTypeDescriptor", out var descriptorNode)
            || descriptorNode is null
        )
        {
            throw new InvalidOperationException(
                $"Fixture '{fixture.CaseName}' {documentDescription} first address must include addressTypeDescriptor."
            );
        }

        if (secondAddress.ContainsKey("addressTypeDescriptor"))
        {
            throw new InvalidOperationException(
                $"Fixture '{fixture.CaseName}' {documentDescription} second address must omit addressTypeDescriptor, not emit null."
            );
        }

        if (!secondAddress.ContainsKey("city"))
        {
            throw new InvalidOperationException(
                $"Fixture '{fixture.CaseName}' {documentDescription} second address must keep scalar fields."
            );
        }
    }

    private static void AssertEqual<T>(
        T expected,
        T actual,
        MaterializedDocumentFixture fixture,
        string fieldName
    )
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Fixture '{fixture.CaseName}' field '{fieldName}' mismatch. Expected '{expected}', actual '{actual}'."
            );
        }
    }
}

public sealed record MaterializedDocumentFixtureActualCacheRow(
    long DocumentId,
    string DocumentUuid,
    string ProjectName,
    string ResourceName,
    string ResourceVersion,
    long ContentVersion,
    DateTimeOffset LastModifiedAt,
    string StreamEtag,
    JsonObject DocumentJson
);

public sealed record MaterializedDocumentFixtureActualProjectionFailure(
    string Reason,
    long DocumentId,
    short? ResourceKeyId,
    string? ProjectName,
    string? ResourceName,
    string? ResourceVersion
);
