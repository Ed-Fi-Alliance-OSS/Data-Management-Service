// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

internal interface ICdcBindingStateStore
{
    Task<CdcCreateBindingStateStoreResult> CreateBindingIfAbsentAsync(
        CdcBinding binding,
        CancellationToken cancellationToken
    );

    Task<CdcReadBindingStateStoreResult> ReadBindingAsync(
        CdcBindingIdentity identity,
        CancellationToken cancellationToken
    );

    Task<CdcExactMatchBindingStateStoreResult> ExactMatchBindingAsync(
        CdcBinding binding,
        CancellationToken cancellationToken
    );

    Task<CdcListBindingsStateStoreResult> ListBindingsAsync(
        string deploymentKey,
        CancellationToken cancellationToken
    );

    Task<CdcLatchIncidentStateStoreResult> LatchSourceHistoryLossAsync(
        CdcIncident incident,
        CancellationToken cancellationToken
    );

    Task<CdcImportBindingStateStoreResult> ImportVerifiedBindingAsync(
        CdcAdoptionProof verifiedAdoptionProof,
        CancellationToken cancellationToken
    );

    Task<CdcDeleteBindingStateStoreResult> DeleteStateAfterVerifiedCleanupAsync(
        CdcCleanupProof verifiedCleanupProof,
        CancellationToken cancellationToken
    );
}

internal sealed record CdcStoredBindingState(CdcBinding Binding, CdcIncident? Incident);

internal enum CdcStateStoreFailureKind
{
    LocalStateUnavailable,
    InvalidPersistedBinding,
    InvalidPersistedIncident,
    InvalidOperation,
}

internal sealed record CdcStateStoreFailure
{
    public CdcStateStoreFailure(
        CdcStateStoreFailureKind kind,
        string message,
        IReadOnlyList<CdcDiagnostic> diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        Kind = kind;
        Message = CdcStateStoreText.SanitizeRequired(message);
        Diagnostics = diagnostics;
    }

    public CdcStateStoreFailureKind Kind { get; }

    public string Message { get; }

    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; }

    public static CdcStateStoreFailure LocalStateUnavailable(string path, string message) =>
        new(
            CdcStateStoreFailureKind.LocalStateUnavailable,
            message,
            [new(CdcDiagnosticCategory.LocalStateUnavailable, path, message)]
        );

    public static CdcStateStoreFailure InvalidPersistedBinding(IReadOnlyList<CdcDiagnostic> diagnostics) =>
        new(
            CdcStateStoreFailureKind.InvalidPersistedBinding,
            "CDC persisted binding state is invalid.",
            diagnostics
        );

    public static CdcStateStoreFailure InvalidPersistedIncident(IReadOnlyList<CdcDiagnostic> diagnostics) =>
        new(
            CdcStateStoreFailureKind.InvalidPersistedIncident,
            "CDC persisted incident state is invalid.",
            diagnostics
        );

    public static CdcStateStoreFailure InvalidOperation(IReadOnlyList<CdcDiagnostic> diagnostics) =>
        new(CdcStateStoreFailureKind.InvalidOperation, "CDC state-store operation is invalid.", diagnostics);
}

internal abstract record CdcCreateBindingStateStoreResult
{
    private CdcCreateBindingStateStoreResult() { }

    internal sealed record Created(CdcStoredBindingState State) : CdcCreateBindingStateStoreResult;

    internal sealed record ExistingExactMatch(CdcStoredBindingState State) : CdcCreateBindingStateStoreResult;

    internal sealed record BindingMismatch(CdcBindingMismatch Mismatch) : CdcCreateBindingStateStoreResult;

    internal sealed record StateStoreFailure(CdcStateStoreFailure Failure) : CdcCreateBindingStateStoreResult;
}

internal abstract record CdcReadBindingStateStoreResult
{
    private CdcReadBindingStateStoreResult() { }

    internal sealed record Found(CdcStoredBindingState State) : CdcReadBindingStateStoreResult;

    internal sealed record Missing(CdcBindingIdentity Identity) : CdcReadBindingStateStoreResult;

    internal sealed record StateStoreFailure(CdcStateStoreFailure Failure) : CdcReadBindingStateStoreResult;
}

internal abstract record CdcExactMatchBindingStateStoreResult
{
    private CdcExactMatchBindingStateStoreResult() { }

    internal sealed record ExactMatch(CdcStoredBindingState State) : CdcExactMatchBindingStateStoreResult;

    internal sealed record BindingMissing(CdcBindingIdentity Identity) : CdcExactMatchBindingStateStoreResult;

    internal sealed record BindingMismatch(CdcBindingMismatch Mismatch)
        : CdcExactMatchBindingStateStoreResult;

    internal sealed record StateStoreFailure(CdcStateStoreFailure Failure)
        : CdcExactMatchBindingStateStoreResult;
}

internal abstract record CdcListBindingsStateStoreResult
{
    private CdcListBindingsStateStoreResult() { }

    internal sealed record Listed(IReadOnlyList<CdcStoredBindingState> States)
        : CdcListBindingsStateStoreResult;

    internal sealed record StateStoreFailure(CdcStateStoreFailure Failure) : CdcListBindingsStateStoreResult;
}

internal abstract record CdcLatchIncidentStateStoreResult
{
    private CdcLatchIncidentStateStoreResult() { }

    internal sealed record Latched(CdcStoredBindingState State) : CdcLatchIncidentStateStoreResult;

    internal sealed record AlreadyLatched(CdcStoredBindingState State) : CdcLatchIncidentStateStoreResult;

    internal sealed record BindingMissing(CdcBindingIdentity Identity) : CdcLatchIncidentStateStoreResult;

    internal sealed record BindingMismatch(CdcBindingMismatch Mismatch) : CdcLatchIncidentStateStoreResult;

    internal sealed record StateStoreFailure(CdcStateStoreFailure Failure) : CdcLatchIncidentStateStoreResult;
}

internal abstract record CdcImportBindingStateStoreResult
{
    private CdcImportBindingStateStoreResult() { }

    internal sealed record Imported(CdcStoredBindingState State) : CdcImportBindingStateStoreResult;

    internal sealed record ExistingExactMatch(CdcStoredBindingState State) : CdcImportBindingStateStoreResult;

    internal sealed record BindingMismatch(CdcBindingMismatch Mismatch) : CdcImportBindingStateStoreResult;

    internal sealed record StateStoreFailure(CdcStateStoreFailure Failure) : CdcImportBindingStateStoreResult;
}

internal abstract record CdcDeleteBindingStateStoreResult
{
    private CdcDeleteBindingStateStoreResult() { }

    internal sealed record Deleted(CdcCompleteBindingIdentity BindingIdentity)
        : CdcDeleteBindingStateStoreResult;

    internal sealed record BindingMissing(CdcCompleteBindingIdentity BindingIdentity)
        : CdcDeleteBindingStateStoreResult;

    internal sealed record StateStoreFailure(CdcStateStoreFailure Failure) : CdcDeleteBindingStateStoreResult;
}

internal enum CdcBindingFieldDifferenceKind
{
    MissingField,
    ExtraField,
    DuplicateField,
    DifferentValue,
}

internal sealed record CdcBindingFieldDifference
{
    public CdcBindingFieldDifference(
        CdcBindingFieldDifferenceKind kind,
        string fieldName,
        string? expectedValue,
        string? persistedValue
    )
    {
        Kind = kind;
        FieldName = CdcStateStoreText.SanitizeRequired(fieldName);
        ExpectedValue = CdcStateStoreText.SanitizeOptional(expectedValue);
        PersistedValue = CdcStateStoreText.SanitizeOptional(persistedValue);
    }

    public CdcBindingFieldDifferenceKind Kind { get; }

    public string FieldName { get; }

    public string? ExpectedValue { get; }

    public string? PersistedValue { get; }
}

internal sealed record CdcBindingMismatch(
    CdcBinding ExpectedBinding,
    CdcBinding? PersistedBinding,
    IReadOnlyList<CdcBindingFieldDifference> Differences
);

internal sealed record CdcBindingExactMatchResult
{
    public CdcBindingExactMatchResult(
        CdcBinding expectedBinding,
        CdcBinding? persistedBinding,
        IReadOnlyList<CdcBindingFieldDifference> differences,
        IReadOnlyList<CdcDiagnostic> diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(expectedBinding);
        ArgumentNullException.ThrowIfNull(differences);
        ArgumentNullException.ThrowIfNull(diagnostics);

        ExpectedBinding = expectedBinding;
        PersistedBinding = persistedBinding;
        Differences = differences;
        Diagnostics = diagnostics;
    }

    public CdcBinding ExpectedBinding { get; }

    public CdcBinding? PersistedBinding { get; }

    public IReadOnlyList<CdcBindingFieldDifference> Differences { get; }

    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; }

    public bool Succeeded => PersistedBinding is not null && Differences.Count == 0 && Diagnostics.Count == 0;

    public CdcBindingMismatch ToMismatch() => new(ExpectedBinding, PersistedBinding, Differences);
}

internal static class CdcBindingExactMatch
{
    private static readonly string[] BindingFieldNames =
    [
        "version",
        "deploymentKey",
        "tenantKey",
        "dataStoreId",
        "instanceKey",
        "generation",
        "provider",
        "physicalSourceFingerprint",
        "connectorName",
        "topicName",
        "partitionCount",
        "partitionerAlgorithm",
        "contractVersion",
    ];

    private static readonly HashSet<string> BindingFieldNameSet = [.. BindingFieldNames];

    public static CdcBindingExactMatchResult Compare(CdcBinding expectedBinding, string persistedBindingJson)
    {
        ArgumentNullException.ThrowIfNull(expectedBinding);
        ArgumentNullException.ThrowIfNull(persistedBindingJson);

        CdcDiagnosticCollector diagnostics = new();
        using JsonDocument? persistedDocument = ParseBindingJson(persistedBindingJson, diagnostics);
        if (persistedDocument is null)
        {
            return new(expectedBinding, null, [], diagnostics.Diagnostics);
        }

        if (persistedDocument.RootElement.ValueKind != JsonValueKind.Object)
        {
            diagnostics.MalformedPayload("$", "CDC persisted binding state must be a JSON object.");
            return new(expectedBinding, null, [], diagnostics.Diagnostics);
        }

        using JsonDocument expectedDocument = JsonDocument.Parse(CdcJsonContract.Serialize(expectedBinding));
        IReadOnlyList<CdcBindingFieldDifference> differences = CompareBindingFields(
            expectedDocument.RootElement,
            persistedDocument.RootElement
        );
        CdcContractReadResult<CdcBinding> readResult = CdcJsonContract.Deserialize<CdcBinding>(
            persistedBindingJson
        );
        CdcContractValidationResult bindingValidation = readResult.Contract is null
            ? CdcContractValidationResult.Success
            : CdcBindingValidator.Validate(readResult.Contract);

        return new(
            expectedBinding,
            readResult.Contract,
            differences,
            [.. readResult.Diagnostics, .. bindingValidation.Diagnostics]
        );
    }

    private static JsonDocument? ParseBindingJson(
        string persistedBindingJson,
        CdcDiagnosticCollector diagnostics
    )
    {
        try
        {
            return JsonDocument.Parse(
                persistedBindingJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                }
            );
        }
        catch (JsonException exception)
        {
            diagnostics.MalformedPayload(
                string.IsNullOrWhiteSpace(exception.Path) ? "$" : exception.Path,
                "CDC persisted binding state is malformed JSON."
            );
            return null;
        }
    }

    private static IReadOnlyList<CdcBindingFieldDifference> CompareBindingFields(
        JsonElement expectedRoot,
        JsonElement persistedRoot
    )
    {
        Dictionary<string, List<JsonElement>> persistedProperties = new(StringComparer.Ordinal);
        foreach (JsonProperty property in persistedRoot.EnumerateObject())
        {
            if (!persistedProperties.TryGetValue(property.Name, out List<JsonElement>? values))
            {
                values = [];
                persistedProperties.Add(property.Name, values);
            }

            values.Add(property.Value);
        }

        List<CdcBindingFieldDifference> differences = [];
        foreach (string fieldName in BindingFieldNames)
        {
            JsonElement expectedValue = expectedRoot.GetProperty(fieldName);
            if (!persistedProperties.TryGetValue(fieldName, out List<JsonElement>? persistedValues))
            {
                differences.Add(
                    new(
                        CdcBindingFieldDifferenceKind.MissingField,
                        fieldName,
                        FormatValue(expectedValue),
                        null
                    )
                );
                continue;
            }

            if (persistedValues.Count != 1)
            {
                differences.Add(
                    new(
                        CdcBindingFieldDifferenceKind.DuplicateField,
                        fieldName,
                        FormatValue(expectedValue),
                        null
                    )
                );
                continue;
            }

            JsonElement persistedValue = persistedValues[0];
            if (!JsonValuesEqual(expectedValue, persistedValue))
            {
                differences.Add(
                    new(
                        CdcBindingFieldDifferenceKind.DifferentValue,
                        fieldName,
                        FormatValue(expectedValue),
                        FormatValue(persistedValue)
                    )
                );
            }
        }

        differences.AddRange(
            persistedProperties
                .Keys.Where(fieldName => !BindingFieldNameSet.Contains(fieldName))
                .Order(StringComparer.Ordinal)
                .Select(fieldName => new CdcBindingFieldDifference(
                    CdcBindingFieldDifferenceKind.ExtraField,
                    fieldName,
                    null,
                    null
                ))
        );

        return differences;
    }

    private static bool JsonValuesEqual(JsonElement expectedValue, JsonElement persistedValue)
    {
        if (expectedValue.ValueKind != persistedValue.ValueKind)
        {
            return false;
        }

        return expectedValue.ValueKind switch
        {
            JsonValueKind.String => string.Equals(
                expectedValue.GetString(),
                persistedValue.GetString(),
                StringComparison.Ordinal
            ),
            JsonValueKind.Number => string.Equals(
                expectedValue.GetRawText(),
                persistedValue.GetRawText(),
                StringComparison.Ordinal
            ),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => string.Equals(
                expectedValue.GetRawText(),
                persistedValue.GetRawText(),
                StringComparison.Ordinal
            ),
        };
    }

    private static string FormatValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
}

internal static class CdcStateStoreText
{
    private const int MaximumTextLength = 512;

    public static string SanitizeRequired(string? value)
    {
        string? sanitized = SanitizeOptional(value);
        return sanitized is null ? "CDC state-store value unavailable." : sanitized;
    }

    public static string? SanitizeOptional(string? value)
    {
        string sanitized = LoggingSanitizer.SanitizeForLogging(value);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return null;
        }

        return sanitized.Length <= MaximumTextLength ? sanitized : sanitized[..MaximumTextLength];
    }
}
