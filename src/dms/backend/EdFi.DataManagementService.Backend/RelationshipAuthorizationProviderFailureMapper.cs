// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal enum RelationshipAuthorizationProviderFailureMappingCategory
{
    PayloadNotExtracted,
    PayloadParseFailed,
    EmittedAuth1IndexMismatch,
    PayloadMappingFailed,
}

internal sealed record RelationshipAuthorizationProviderFailureDiagnostic(
    SqlDialect Dialect,
    int ExpectedEmittedAuth1Index,
    string ProviderErrorCode,
    string ProviderMessage,
    RelationshipAuthorizationProviderFailureMappingCategory MappingFailureCategory
);

internal static class RelationshipAuthorizationProviderFailureMapper
{
    private const int MaxProviderMessageFragmentLength = 512;

    public const string InvalidFailurePayloadSecurityConfigurationError =
        "The relationship authorization failure payload returned by the authorization provider is invalid and cannot be mapped to the configured relationship authorization plan.";

    public static bool TryMapRelationshipAuthorizationFailure(
        SqlDialect dialect,
        DbException exception,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor,
        int expectedEmittedAuth1Index,
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs,
        IReadOnlyList<long> claimEducationOrganizationIds,
        out RelationshipAuthorizationFailure? relationshipFailure,
        out RelationshipAuthorizationProviderFailureDiagnostic? invalidFailureDiagnostic
    )
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(providerFailureExtractor);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedEmittedAuth1Index);
        ArgumentNullException.ThrowIfNull(checkSpecs);
        ArgumentNullException.ThrowIfNull(claimEducationOrganizationIds);

        relationshipFailure = null;
        invalidFailureDiagnostic = null;

        var providerFailure = providerFailureExtractor.Extract(exception);
        var providerErrorCode = providerFailure.ErrorCode ?? "none";
        var providerMessage = providerFailure.Message ?? string.Empty;

        if (
            !RelationshipAuthorizationAuth1FailurePayloadCodec.IsProviderFailure(
                dialect,
                providerFailure.ErrorCode,
                providerMessage
            )
        )
        {
            return false;
        }

        if (
            !RelationshipAuthorizationAuth1FailurePayloadCodec.TryExtractProviderPayload(
                dialect,
                providerFailure.ErrorCode,
                providerMessage,
                out var payloadText
            )
        )
        {
            invalidFailureDiagnostic = BuildDiagnostic(
                dialect,
                expectedEmittedAuth1Index,
                providerErrorCode,
                providerMessage,
                RelationshipAuthorizationProviderFailureMappingCategory.PayloadNotExtracted
            );
            return false;
        }

        if (
            !RelationshipAuthorizationAuth1FailurePayloadCodec.TryParsePayload(payloadText, out var payload)
            || payload is null
        )
        {
            invalidFailureDiagnostic = BuildDiagnostic(
                dialect,
                expectedEmittedAuth1Index,
                providerErrorCode,
                providerMessage,
                RelationshipAuthorizationProviderFailureMappingCategory.PayloadParseFailed
            );
            return false;
        }

        if (payload.EmittedAuth1Index != expectedEmittedAuth1Index)
        {
            invalidFailureDiagnostic = BuildDiagnostic(
                dialect,
                expectedEmittedAuth1Index,
                providerErrorCode,
                providerMessage,
                RelationshipAuthorizationProviderFailureMappingCategory.EmittedAuth1IndexMismatch
            );
            return false;
        }

        if (
            RelationshipAuthorizationFailureMapper.TryMapAuth1Failure(
                payload,
                expectedEmittedAuth1Index,
                checkSpecs,
                claimEducationOrganizationIds,
                out relationshipFailure
            )
        )
        {
            return true;
        }

        invalidFailureDiagnostic = BuildDiagnostic(
            dialect,
            expectedEmittedAuth1Index,
            providerErrorCode,
            providerMessage,
            RelationshipAuthorizationProviderFailureMappingCategory.PayloadMappingFailed
        );
        return false;
    }

    public static void LogInvalidFailurePayload(
        ILogger logger,
        RelationshipAuthorizationProviderFailureDiagnostic diagnostic
    )
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(diagnostic);

        logger.LogError(
            "Invalid relationship authorization AUTH1 provider failure payload. Dialect: {Dialect}; ExpectedEmittedAuth1Index: {ExpectedEmittedAuth1Index}; ProviderErrorCode: {ProviderErrorCode}; ProviderMessageFragment: {ProviderMessageFragment}; ProviderMessageLength: {ProviderMessageLength}; MappingFailureCategory: {MappingFailureCategory}",
            diagnostic.Dialect,
            diagnostic.ExpectedEmittedAuth1Index,
            SanitizeProviderDiagnosticText(diagnostic.ProviderErrorCode),
            SanitizeProviderDiagnosticText(diagnostic.ProviderMessage),
            diagnostic.ProviderMessage.Length,
            diagnostic.MappingFailureCategory
        );
    }

    private static RelationshipAuthorizationProviderFailureDiagnostic BuildDiagnostic(
        SqlDialect dialect,
        int expectedEmittedAuth1Index,
        string providerErrorCode,
        string providerMessage,
        RelationshipAuthorizationProviderFailureMappingCategory mappingFailureCategory
    ) => new(dialect, expectedEmittedAuth1Index, providerErrorCode, providerMessage, mappingFailureCategory);

    private static string SanitizeProviderDiagnosticText(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var maxLength = Math.Min(input.Length, MaxProviderMessageFragmentLength);
        Span<char> sanitizedCharacters = stackalloc char[maxLength];
        var sanitizedLength = 0;

        foreach (var character in input)
        {
            if (sanitizedLength >= maxLength)
            {
                break;
            }

            if (IsAllowedProviderDiagnosticCharacter(character))
            {
                sanitizedCharacters[sanitizedLength++] = character;
            }
        }

        return new string(sanitizedCharacters[..sanitizedLength]);
    }

    private static bool IsAllowedProviderDiagnosticCharacter(char character) =>
        !char.IsControl(character)
        && (
            char.IsLetterOrDigit(character)
            || character is ' ' or '_' or '-' or '.' or ':' or '/' or '\\' or '|' or ','
        );
}
