// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

/// <summary>Whether the job layer should retry a failed projection read (DMS-1440 spec §5.5).</summary>
public enum EducationOrganizationProjectionFailureCategory
{
    Transient,
    Permanent,
}

/// <summary>Why a projection read failed (DMS-1440 spec §5.1, §5.5).</summary>
public enum EducationOrganizationProjectionFailureCode
{
    NotConfigured,
    DiscoveryUnavailable,
    DiscoveryInvalid,
    UnsupportedContract,
    TokenUnavailable,
    TokenRejected,
    Unauthorized,
    Forbidden,
    TargetNotFound,
    TargetNotRoutable,
    TargetSchemaIncompatible,
    Unsupported,
    DataInvalid,
    ProjectionChanged,
    RateLimited,
    ServiceUnavailable,
    NetworkError,
    Timeout,
    MalformedResponse,
    UnexpectedResponse,
    InvalidRequest,
    LimitExceeded,
}

/// <summary>The DMS call a projection read failure came from.</summary>
public enum EducationOrganizationProjectionStage
{
    Discovery,
    Token,
    Page,
}

/// <summary>
/// A failed projection read (DMS-1440 spec §5.1). It carries no URL, body, header, credential, cursor or exception
/// text: <see cref="ProblemType"/> and <see cref="CorrelationId"/> are the sanitized values <see cref="Create"/> keeps.
/// </summary>
public sealed record EducationOrganizationProjectionFailure(
    EducationOrganizationProjectionFailureCategory Category,
    EducationOrganizationProjectionFailureCode Code,
    EducationOrganizationProjectionStage Stage,
    int? HttpStatus,
    string? ProblemType,
    string? CorrelationId,
    int PagesRead,
    int Restarts
)
{
    /// <summary>The longest problem <c>type</c> kept, in characters.</summary>
    public const int MaxProblemTypeLength = 256;

    /// <summary>The longest DMS <c>correlationId</c> kept, in characters.</summary>
    public const int MaxCorrelationIdLength = 128;

    /// <summary>
    /// A failure whose category is the one §5.5 assigns to <paramref name="code"/>, with the problem type and
    /// correlation id reduced to <c>[A-Za-z0-9:._-]</c> and their length limits (<c>null</c> when nothing remains).
    /// </summary>
    public static EducationOrganizationProjectionFailure Create(
        EducationOrganizationProjectionFailureCode code,
        EducationOrganizationProjectionStage stage,
        int? httpStatus = null,
        string? problemType = null,
        string? correlationId = null,
        int pagesRead = 0,
        int restarts = 0
    ) =>
        new(
            CategoryOf(code),
            code,
            stage,
            httpStatus,
            Sanitize(problemType, MaxProblemTypeLength),
            Sanitize(correlationId, MaxCorrelationIdLength),
            pagesRead,
            restarts
        );

    /// <summary>The §5.5 category of each code: a code is either always retried or never.</summary>
    public static EducationOrganizationProjectionFailureCategory CategoryOf(
        EducationOrganizationProjectionFailureCode code
    ) =>
        code switch
        {
            EducationOrganizationProjectionFailureCode.DiscoveryUnavailable
            or EducationOrganizationProjectionFailureCode.TokenUnavailable
            or EducationOrganizationProjectionFailureCode.ProjectionChanged
            or EducationOrganizationProjectionFailureCode.RateLimited
            or EducationOrganizationProjectionFailureCode.ServiceUnavailable
            or EducationOrganizationProjectionFailureCode.NetworkError
            or EducationOrganizationProjectionFailureCode.Timeout =>
                EducationOrganizationProjectionFailureCategory.Transient,

            EducationOrganizationProjectionFailureCode.NotConfigured
            or EducationOrganizationProjectionFailureCode.DiscoveryInvalid
            or EducationOrganizationProjectionFailureCode.UnsupportedContract
            or EducationOrganizationProjectionFailureCode.TokenRejected
            or EducationOrganizationProjectionFailureCode.Unauthorized
            or EducationOrganizationProjectionFailureCode.Forbidden
            or EducationOrganizationProjectionFailureCode.TargetNotFound
            or EducationOrganizationProjectionFailureCode.TargetNotRoutable
            or EducationOrganizationProjectionFailureCode.TargetSchemaIncompatible
            or EducationOrganizationProjectionFailureCode.Unsupported
            or EducationOrganizationProjectionFailureCode.DataInvalid
            or EducationOrganizationProjectionFailureCode.MalformedResponse
            or EducationOrganizationProjectionFailureCode.UnexpectedResponse
            or EducationOrganizationProjectionFailureCode.InvalidRequest
            or EducationOrganizationProjectionFailureCode.LimitExceeded =>
                EducationOrganizationProjectionFailureCategory.Permanent,

            _ => throw new ArgumentOutOfRangeException(nameof(code)),
        };

    private static string? Sanitize(string? value, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        Span<char> kept = stackalloc char[maxLength];
        int length = 0;
        foreach (char character in value)
        {
            if (length == maxLength)
            {
                break;
            }
            if (char.IsAsciiLetterOrDigit(character) || character is ':' or '.' or '_' or '-')
            {
                kept[length++] = character;
            }
        }
        return length == 0 ? null : new string(kept[..length]);
    }
}
