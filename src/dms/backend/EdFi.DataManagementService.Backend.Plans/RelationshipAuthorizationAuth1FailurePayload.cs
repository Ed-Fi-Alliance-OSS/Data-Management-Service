// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Runtime failure kinds encoded in the compact AUTH1 relationship-authorization payload.
/// </summary>
public enum RelationshipAuthorizationAuth1SubjectFailureKind
{
    NoRelationship,
    StoredValueNull,
    ProposedValueMissing,
}

/// <summary>
/// One failed strategy/subject ordinal emitted by the AUTH1 relationship-authorization payload.
/// </summary>
public sealed record RelationshipAuthorizationAuth1SubjectFailure
{
    public RelationshipAuthorizationAuth1SubjectFailure(
        int strategyOrdinal,
        int subjectOrdinal,
        RelationshipAuthorizationAuth1SubjectFailureKind failureKind
    )
    {
        if (strategyOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(strategyOrdinal),
                strategyOrdinal,
                "AUTH1 strategy ordinal cannot be negative."
            );
        }

        if (subjectOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(subjectOrdinal),
                subjectOrdinal,
                "AUTH1 subject ordinal cannot be negative."
            );
        }

        StrategyOrdinal = strategyOrdinal;
        SubjectOrdinal = subjectOrdinal;
        FailureKind = failureKind;
    }

    public int StrategyOrdinal { get; init; }

    public int SubjectOrdinal { get; init; }

    public RelationshipAuthorizationAuth1SubjectFailureKind FailureKind { get; init; }
}

/// <summary>
/// Provider-independent AUTH1 failure-set payload for one failed relationship authorization OR group.
/// </summary>
public sealed record RelationshipAuthorizationAuth1FailurePayload
{
    public RelationshipAuthorizationAuth1FailurePayload(
        int emittedAuth1Index,
        IReadOnlyList<RelationshipAuthorizationAuth1SubjectFailure> subjectFailures
    )
    {
        ArgumentNullException.ThrowIfNull(subjectFailures);

        if (emittedAuth1Index < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(emittedAuth1Index),
                emittedAuth1Index,
                "Emitted AUTH1 index cannot be negative."
            );
        }

        if (subjectFailures.Count == 0)
        {
            throw new ArgumentException(
                "AUTH1 relationship authorization payload requires at least one subject failure.",
                nameof(subjectFailures)
            );
        }

        EmittedAuth1Index = emittedAuth1Index;
        SubjectFailures = subjectFailures;
    }

    public int EmittedAuth1Index { get; init; }

    public IReadOnlyList<RelationshipAuthorizationAuth1SubjectFailure> SubjectFailures { get; init; }
}

/// <summary>
/// Encodes, extracts, and parses the compact Slice 2 AUTH1 relationship-authorization payload.
/// </summary>
public static class RelationshipAuthorizationAuth1FailurePayloadCodec
{
    public const string ProviderFailureCode = "AUTH1";
    public const string PayloadVersion = "1";

    private const string MssqlPayloadMarker = "AUTH1 - ";

    public static string Encode(RelationshipAuthorizationAuth1FailurePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{PayloadVersion}|{payload.EmittedAuth1Index}|{payload.SubjectFailures.Count}|{string.Join(",", payload.SubjectFailures.Select(EncodeSubjectFailure))}"
        );
    }

    public static bool TryParsePayload(
        string payloadText,
        out RelationshipAuthorizationAuth1FailurePayload? payload
    )
    {
        payload = null;

        if (string.IsNullOrWhiteSpace(payloadText))
        {
            return false;
        }

        var payloadSections = payloadText.Split('|');

        if (
            payloadSections.Length is not 4
            || !string.Equals(payloadSections[0], PayloadVersion, StringComparison.Ordinal)
            || !TryParseNonNegativeInt(payloadSections[1], out var emittedAuth1Index)
            || !TryParsePositiveInt(payloadSections[2], out var expectedFailureCount)
        )
        {
            return false;
        }

        var failureTexts = payloadSections[3].Split(',');

        if (failureTexts.Length != expectedFailureCount)
        {
            return false;
        }

        List<RelationshipAuthorizationAuth1SubjectFailure> subjectFailures = [];
        HashSet<(int StrategyOrdinal, int SubjectOrdinal)> seenOrdinals = [];

        foreach (var failureText in failureTexts)
        {
            if (!TryParseSubjectFailure(failureText, out var subjectFailure))
            {
                return false;
            }

            if (!seenOrdinals.Add((subjectFailure.StrategyOrdinal, subjectFailure.SubjectOrdinal)))
            {
                return false;
            }

            subjectFailures.Add(subjectFailure);
        }

        payload = new RelationshipAuthorizationAuth1FailurePayload(emittedAuth1Index, subjectFailures);
        return true;
    }

    public static bool TryParseProviderFailure(
        SqlDialect dialect,
        string? providerErrorCode,
        string providerMessage,
        out RelationshipAuthorizationAuth1FailurePayload? payload
    )
    {
        if (!TryExtractProviderPayload(dialect, providerErrorCode, providerMessage, out var payloadText))
        {
            payload = null;
            return false;
        }

        return TryParsePayload(payloadText, out payload);
    }

    public static bool IsProviderFailure(
        SqlDialect dialect,
        string? providerErrorCode,
        string? providerMessage
    ) =>
        dialect switch
        {
            SqlDialect.Pgsql => string.Equals(
                providerErrorCode,
                ProviderFailureCode,
                StringComparison.Ordinal
            ),
            SqlDialect.Mssql => !string.IsNullOrWhiteSpace(providerMessage)
                && providerMessage.Contains(MssqlPayloadMarker, StringComparison.Ordinal),
            _ => false,
        };

    public static bool TryExtractProviderPayload(
        SqlDialect dialect,
        string? providerErrorCode,
        string providerMessage,
        out string payloadText
    )
    {
        payloadText = string.Empty;

        if (string.IsNullOrWhiteSpace(providerMessage))
        {
            return false;
        }

        return dialect switch
        {
            SqlDialect.Pgsql => TryExtractPostgresqlPayload(
                providerErrorCode,
                providerMessage,
                out payloadText
            ),
            SqlDialect.Mssql => TryExtractMssqlPayload(providerMessage, out payloadText),
            _ => false,
        };
    }

    private static string EncodeSubjectFailure(RelationshipAuthorizationAuth1SubjectFailure subjectFailure) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{subjectFailure.StrategyOrdinal}:{subjectFailure.SubjectOrdinal}:{EncodeFailureKind(subjectFailure.FailureKind)}"
        );

    private static bool TryParseSubjectFailure(
        string failureText,
        out RelationshipAuthorizationAuth1SubjectFailure subjectFailure
    )
    {
        subjectFailure = new RelationshipAuthorizationAuth1SubjectFailure(
            0,
            0,
            RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
        );

        var sections = failureText.Split(':');

        if (
            sections.Length is not 3
            || !TryParseNonNegativeInt(sections[0], out var strategyOrdinal)
            || !TryParseNonNegativeInt(sections[1], out var subjectOrdinal)
            || !TryDecodeFailureKind(sections[2], out var failureKind)
        )
        {
            return false;
        }

        subjectFailure = new RelationshipAuthorizationAuth1SubjectFailure(
            strategyOrdinal,
            subjectOrdinal,
            failureKind
        );
        return true;
    }

    private static bool TryParseNonNegativeInt(string text, out int value)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0)
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryParsePositiveInt(string text, out int value)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0)
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static string EncodeFailureKind(RelationshipAuthorizationAuth1SubjectFailureKind failureKind) =>
        failureKind switch
        {
            RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship => "n",
            RelationshipAuthorizationAuth1SubjectFailureKind.StoredValueNull => "s",
            RelationshipAuthorizationAuth1SubjectFailureKind.ProposedValueMissing => "p",
            _ => throw new ArgumentOutOfRangeException(
                nameof(failureKind),
                failureKind,
                "Unsupported AUTH1 relationship failure kind."
            ),
        };

    private static bool TryDecodeFailureKind(
        string failureKindCode,
        out RelationshipAuthorizationAuth1SubjectFailureKind failureKind
    )
    {
        switch (failureKindCode)
        {
            case "n":
                failureKind = RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship;
                return true;
            case "s":
                failureKind = RelationshipAuthorizationAuth1SubjectFailureKind.StoredValueNull;
                return true;
            case "p":
                failureKind = RelationshipAuthorizationAuth1SubjectFailureKind.ProposedValueMissing;
                return true;
            default:
                failureKind = RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship;
                return false;
        }
    }

    private static bool TryExtractPostgresqlPayload(
        string? providerErrorCode,
        string providerMessage,
        out string payloadText
    )
    {
        if (!string.Equals(providerErrorCode, ProviderFailureCode, StringComparison.Ordinal))
        {
            payloadText = string.Empty;
            return false;
        }

        payloadText = providerMessage;
        return true;
    }

    private static bool TryExtractMssqlPayload(string providerMessage, out string payloadText)
    {
        payloadText = string.Empty;
        var markerIndex = providerMessage.IndexOf(MssqlPayloadMarker, StringComparison.Ordinal);

        if (markerIndex < 0)
        {
            return false;
        }

        var payloadStartIndex = markerIndex + MssqlPayloadMarker.Length;
        var payloadEndIndex = payloadStartIndex;

        while (
            payloadEndIndex < providerMessage.Length
            && IsMssqlPayloadCharacter(providerMessage[payloadEndIndex])
        )
        {
            payloadEndIndex++;
        }

        if (payloadEndIndex == payloadStartIndex)
        {
            return false;
        }

        payloadText = providerMessage[payloadStartIndex..payloadEndIndex];
        return true;
    }

    private static bool IsMssqlPayloadCharacter(char value) =>
        value is >= '0' and <= '9' || value is >= 'a' and <= 'z' || value is '|' or ':' or ',';
}
