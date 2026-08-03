// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Configuration;

public sealed class DocumentCacheOptions
{
    public const string SectionName = "DataManagement:DocumentCache";

    public List<DocumentCacheTargetOptions> Targets { get; set; } = [];

    public DocumentCacheReadAccelerationOptions ReadAcceleration { get; set; } = new();

    public DocumentCacheProjectorOptions Projector { get; set; } = new();

    public IReadOnlyList<DocumentCacheTargetKey> GetTargetKeys() =>
        Targets
            .Select(target => DocumentCacheTargetKey.Create(target.TenantKey, target.DataStoreId))
            .ToList();
}

public sealed class DocumentCacheTargetOptions
{
    public string TenantKey { get; set; } = string.Empty;

    public long DataStoreId { get; set; }
}

public sealed class DocumentCacheReadAccelerationOptions
{
    public bool Enabled { get; set; }

    public TimeSpan DirectFillTimeout { get; set; } = TimeSpan.FromMilliseconds(250);
}

public sealed class DocumentCacheProjectorOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int PageSize { get; set; } = 100;

    public int MaxConcurrentTargets { get; set; } = 2;

    public TimeSpan FailureBackoff { get; set; } = TimeSpan.FromSeconds(30);

    public int BaselineHighWaterMark { get; set; } = 1000;
}

public sealed class DocumentCacheTargetKey : IEquatable<DocumentCacheTargetKey>
{
    private const string TenantHeaderName = "Tenant";
    private static readonly StringComparer TenantKeyComparer = StringComparer.OrdinalIgnoreCase;

    private DocumentCacheTargetKey(string tenantKey, long dataStoreId)
    {
        TenantKey = tenantKey;
        DataStoreId = dataStoreId;
    }

    public string TenantKey { get; }

    public long DataStoreId { get; }

    public static DocumentCacheTargetKey Create(string? tenantKey, long dataStoreId)
    {
        if (
            !TryCreate(
                tenantKey,
                dataStoreId,
                out DocumentCacheTargetKey? targetKey,
                out string? validationFailure
            )
        )
        {
            throw new ArgumentException(validationFailure, nameof(tenantKey));
        }

        return targetKey;
    }

    public static bool TryCreate(
        string? tenantKey,
        long dataStoreId,
        [NotNullWhen(true)] out DocumentCacheTargetKey? targetKey,
        [NotNullWhen(false)] out string? validationFailure
    )
    {
        targetKey = null;
        validationFailure = null;

        if (dataStoreId <= 0)
        {
            validationFailure = "DataStoreId must be positive.";
            return false;
        }

        string normalizedTenantKey = tenantKey ?? string.Empty;
        if (HasLeadingOrTrailingWhitespace(normalizedTenantKey))
        {
            validationFailure = "TenantKey must not have leading or trailing whitespace.";
            return false;
        }

        if (!CanBeSentAsTenantHeader(normalizedTenantKey))
        {
            validationFailure = "TenantKey must be safe to send as the Tenant HTTP header.";
            return false;
        }

        targetKey = new DocumentCacheTargetKey(normalizedTenantKey, dataStoreId);
        return true;
    }

    public bool Equals(DocumentCacheTargetKey? other) =>
        other is not null
        && DataStoreId == other.DataStoreId
        && TenantKeyComparer.Equals(TenantKey, other.TenantKey);

    public override bool Equals(object? obj) => obj is DocumentCacheTargetKey other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(TenantKeyComparer.GetHashCode(TenantKey), DataStoreId);

    public override string ToString()
    {
        string tenantForDiagnostics =
            TenantKey.Length == 0 ? "(default)" : LoggingSanitizer.SanitizeForLogging(TenantKey);
        return $"{tenantForDiagnostics}:{DataStoreId}";
    }

    private static bool HasLeadingOrTrailingWhitespace(string value) =>
        value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));

    private static bool CanBeSentAsTenantHeader(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        if (value.Any(char.IsControl))
        {
            return false;
        }

        using HttpRequestMessage request = new(HttpMethod.Get, "http://localhost/");
        try
        {
            request.Headers.Add(TenantHeaderName, value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class DocumentCacheOptionsValidator : IValidateOptions<DocumentCacheOptions>
{
    public ValidateOptionsResult Validate(string? name, DocumentCacheOptions options)
    {
        List<string> failures = [];

        ValidateTargets(options, failures);
        ValidateProjector(options, failures);
        ValidateReadAcceleration(options, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateTargets(DocumentCacheOptions options, List<string> failures)
    {
        List<DocumentCacheTargetOptions>? targets = options.Targets;
        if (targets is null)
        {
            failures.Add($"{nameof(DocumentCacheOptions.Targets)} must not be null.");
            return;
        }

        Dictionary<DocumentCacheTargetKey, int> targetIndexes = [];
        for (int index = 0; index < targets.Count; index++)
        {
            DocumentCacheTargetOptions? target = targets[index];
            if (target is null)
            {
                failures.Add($"{nameof(DocumentCacheOptions.Targets)}[{index}] must not be null.");
                continue;
            }

            if (
                !DocumentCacheTargetKey.TryCreate(
                    target.TenantKey,
                    target.DataStoreId,
                    out DocumentCacheTargetKey? targetKey,
                    out string? validationFailure
                )
            )
            {
                failures.Add($"{nameof(DocumentCacheOptions.Targets)}[{index}] {validationFailure}");
                continue;
            }

            if (targetIndexes.TryGetValue(targetKey, out int duplicateIndex))
            {
                failures.Add(
                    $"{nameof(DocumentCacheOptions.Targets)}[{index}] duplicates {nameof(DocumentCacheOptions.Targets)}[{duplicateIndex}] after tenant normalization for target {targetKey}."
                );
                continue;
            }

            targetIndexes.Add(targetKey, index);
        }
    }

    private static void ValidateProjector(DocumentCacheOptions options, List<string> failures)
    {
        DocumentCacheProjectorOptions? projector = options.Projector;
        if (projector is null)
        {
            failures.Add($"{nameof(DocumentCacheOptions.Projector)} must not be null.");
            return;
        }

        AddFailureIfNonPositive(
            projector.PollInterval,
            $"{nameof(DocumentCacheOptions.Projector)}:{nameof(DocumentCacheProjectorOptions.PollInterval)}",
            failures
        );
        AddFailureIfNonPositive(
            projector.PageSize,
            $"{nameof(DocumentCacheOptions.Projector)}:{nameof(DocumentCacheProjectorOptions.PageSize)}",
            failures
        );
        AddFailureIfNonPositive(
            projector.MaxConcurrentTargets,
            $"{nameof(DocumentCacheOptions.Projector)}:{nameof(DocumentCacheProjectorOptions.MaxConcurrentTargets)}",
            failures
        );
        AddFailureIfNonPositive(
            projector.FailureBackoff,
            $"{nameof(DocumentCacheOptions.Projector)}:{nameof(DocumentCacheProjectorOptions.FailureBackoff)}",
            failures
        );
        AddFailureIfNonPositive(
            projector.BaselineHighWaterMark,
            $"{nameof(DocumentCacheOptions.Projector)}:{nameof(DocumentCacheProjectorOptions.BaselineHighWaterMark)}",
            failures
        );
    }

    private static void ValidateReadAcceleration(DocumentCacheOptions options, List<string> failures)
    {
        DocumentCacheReadAccelerationOptions? readAcceleration = options.ReadAcceleration;
        if (readAcceleration is null)
        {
            failures.Add($"{nameof(DocumentCacheOptions.ReadAcceleration)} must not be null.");
            return;
        }

        AddFailureIfNonPositive(
            readAcceleration.DirectFillTimeout,
            $"{nameof(DocumentCacheOptions.ReadAcceleration)}:{nameof(DocumentCacheReadAccelerationOptions.DirectFillTimeout)}",
            failures
        );
    }

    private static void AddFailureIfNonPositive(TimeSpan value, string settingName, List<string> failures)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"{settingName} must be positive.");
        }
    }

    private static void AddFailureIfNonPositive(int value, string settingName, List<string> failures)
    {
        if (value <= 0)
        {
            failures.Add($"{settingName} must be positive.");
        }
    }
}
