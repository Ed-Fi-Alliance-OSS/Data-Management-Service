// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Configuration;

public sealed class DocumentCacheOptions
{
    public const string SectionName = "DataManagement:DocumentCache";

    public List<DocumentCacheTargetOptions> Targets { get; set; } = [];

    public DocumentCacheReadAccelerationOptions ReadAcceleration { get; set; } = new();

    public DocumentCacheProjectorOptions Projector { get; set; } = new();

    public DocumentCacheAdministrationOptions Administration { get; set; } = new();

    public DocumentCacheStatusOptions Status { get; set; } = new();

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
    public const int DefaultPageSize = 100;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int PageSize { get; set; } = DefaultPageSize;

    public int MaxConcurrentTargets { get; set; } = 2;

    public TimeSpan FailureBackoff { get; set; } = TimeSpan.FromSeconds(30);

    public int BaselineHighWaterMark { get; set; } = 1000;
}

public sealed class DocumentCacheAdministrationOptions
{
    public TimeSpan WorkflowTimeout { get; set; } = TimeSpan.FromHours(24);
}

public sealed class DocumentCacheStatusOptions
{
    public const int MaximumRequiredRoleLength = 256;

    public static readonly TimeSpan DefaultStatusObservationTimeout = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan DefaultEndpointTimeout = TimeSpan.FromSeconds(30);

    public TimeSpan StatusObservationTimeout { get; set; } = DefaultStatusObservationTimeout;

    public TimeSpan EndpointTimeout { get; set; } = DefaultEndpointTimeout;

    [JsonIgnore]
    public string? RequiredRole { get; set; }

    public bool TryGetRequiredRoleForEndpointMapping(
        [NotNullWhen(true)] out string? requiredRoleForEndpointMapping
    )
    {
        if (IsValidRequiredRoleForEndpointMapping(RequiredRole))
        {
            requiredRoleForEndpointMapping = RequiredRole;
            return true;
        }

        requiredRoleForEndpointMapping = null;
        return false;
    }

    public static bool IsValidRequiredRoleForEndpointMapping([NotNullWhen(true)] string? requiredRole)
    {
        if (requiredRole is null || requiredRole.Length == 0)
        {
            return false;
        }

        if (requiredRole.Length > MaximumRequiredRoleLength)
        {
            return false;
        }

        return !requiredRole.Any(IsInvalidRequiredRoleCharacter);
    }

    private static bool IsInvalidRequiredRoleCharacter(char character) =>
        character is <= '\u001f' or '\u007f' or ' ' or ',' or ';' or '"' or '\'' or '[' or ']' or '{' or '}'
        || char.IsWhiteSpace(character);
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
    private const long MaximumCancelAfterTimeoutMilliseconds = 4_294_967_294;
    private const string StatusSectionName = $"{DocumentCacheOptions.SectionName}:Status";
    private const string JsonConfigurationProviderTypeName =
        "Microsoft.Extensions.Configuration.Json.JsonConfigurationProvider";
    private const string JsonStreamConfigurationProviderTypeName =
        "Microsoft.Extensions.Configuration.Json.JsonStreamConfigurationProvider";
    private static readonly JsonDocumentOptions JsonDocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly IConfiguration? _configuration;

    public DocumentCacheOptionsValidator(IConfiguration? configuration = null)
    {
        _configuration = configuration;
    }

    public ValidateOptionsResult Validate(string? name, DocumentCacheOptions options)
    {
        List<string> failures = [];

        ValidateStatusConfigurationShape(failures);
        ValidateTargets(options, failures);
        ValidateProjector(options, failures);
        ValidateReadAcceleration(options, failures);
        ValidateAdministration(options, failures);
        ValidateStatus(options, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private void ValidateStatusConfigurationShape(List<string> failures)
    {
        if (_configuration is null)
        {
            return;
        }

        switch (GetStatusConfigurationShape(_configuration))
        {
            case StatusConfigurationShape.Omitted:
            case StatusConfigurationShape.Object:
                return;
            case StatusConfigurationShape.Null:
                failures.Add($"{nameof(DocumentCacheOptions.Status)} section must not be null.");
                return;
            case StatusConfigurationShape.Scalar:
                failures.Add($"{nameof(DocumentCacheOptions.Status)} section must be an object.");
                return;
            default:
                throw new InvalidOperationException("Unknown DocumentCache status configuration shape.");
        }
    }

    private static StatusConfigurationShape GetStatusConfigurationShape(IConfiguration configuration)
    {
        if (configuration is IConfigurationRoot configurationRoot)
        {
            foreach (IConfigurationProvider provider in configurationRoot.Providers.Reverse())
            {
                if (!provider.TryGet(StatusSectionName, out string? value))
                {
                    continue;
                }

                if (value is not null)
                {
                    return StatusConfigurationShape.Scalar;
                }

                if (
                    TryGetJsonProviderStatusConfigurationShape(
                        provider,
                        out StatusConfigurationShape jsonShape
                    )
                )
                {
                    return jsonShape;
                }

                return StatusConfigurationShape.Null;
            }
        }

        IConfigurationSection statusSection = configuration.GetSection(StatusSectionName);
        if (statusSection.GetChildren().Any())
        {
            return StatusConfigurationShape.Object;
        }

        bool explicitStatusSection = configuration
            .AsEnumerable()
            .Any(setting => string.Equals(setting.Key, StatusSectionName, StringComparison.Ordinal));

        if (!explicitStatusSection)
        {
            return StatusConfigurationShape.Omitted;
        }

        return statusSection.Value is null ? StatusConfigurationShape.Null : StatusConfigurationShape.Scalar;
    }

    private static bool TryGetJsonProviderStatusConfigurationShape(
        IConfigurationProvider provider,
        out StatusConfigurationShape shape
    )
    {
        shape = StatusConfigurationShape.Omitted;

        // IConfiguration flattens JSON empty objects and nulls to the same value; inspect
        // the source JSON when provider metadata is available so Status: {} can mean defaults.
        return provider switch
        {
            { } when IsProviderType(provider, JsonStreamConfigurationProviderTypeName) =>
                TryGetJsonStreamStatusConfigurationShape(
                    GetProviderSourceProperty(provider, "Stream") as Stream,
                    restorePosition: true,
                    out shape
                ),
            { } when IsProviderType(provider, JsonConfigurationProviderTypeName) =>
                TryGetJsonFileStatusConfigurationShape(provider, out shape),
            _ => false,
        };
    }

    private static bool IsProviderType(IConfigurationProvider provider, string providerTypeName) =>
        string.Equals(provider.GetType().FullName, providerTypeName, StringComparison.Ordinal);

    private static object? GetProviderSourceProperty(IConfigurationProvider provider, string propertyName)
    {
        object? source = provider.GetType().GetProperty("Source")?.GetValue(provider);
        return source?.GetType().GetProperty(propertyName)?.GetValue(source);
    }

    private static bool TryGetJsonFileStatusConfigurationShape(
        IConfigurationProvider provider,
        out StatusConfigurationShape shape
    )
    {
        shape = StatusConfigurationShape.Omitted;

        object? fileProvider = GetProviderSourceProperty(provider, "FileProvider");
        string? path = GetProviderSourceProperty(provider, "Path") as string;
        if (fileProvider is null || path is null)
        {
            return false;
        }

        object? fileInfo = fileProvider
            .GetType()
            .GetMethod("GetFileInfo", [typeof(string)])
            ?.Invoke(fileProvider, [path]);
        if (fileInfo is null)
        {
            return false;
        }

        bool fileExists =
            fileInfo.GetType().GetProperty("Exists")?.GetValue(fileInfo) is bool exists && exists;

        if (!fileExists)
        {
            return false;
        }

        try
        {
            object? stream = fileInfo
                .GetType()
                .GetMethod("CreateReadStream", Type.EmptyTypes)
                ?.Invoke(fileInfo, []);

            if (stream is not Stream readableStream)
            {
                return false;
            }

            using (readableStream)
            {
                return TryGetJsonStreamStatusConfigurationShape(
                    readableStream,
                    restorePosition: false,
                    out shape
                );
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryGetJsonStreamStatusConfigurationShape(
        Stream? stream,
        bool restorePosition,
        out StatusConfigurationShape shape
    )
    {
        shape = StatusConfigurationShape.Omitted;

        if (stream is null)
        {
            return false;
        }

        if (restorePosition && !stream.CanSeek)
        {
            return false;
        }

        long originalPosition = stream.CanSeek ? stream.Position : 0;
        try
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            using JsonDocument document = JsonDocument.Parse(stream, JsonDocumentOptions);
            if (!TryGetStatusJsonElement(document.RootElement, out JsonElement statusElement))
            {
                shape = StatusConfigurationShape.Omitted;
                return true;
            }

            shape = statusElement.ValueKind switch
            {
                JsonValueKind.Object => StatusConfigurationShape.Object,
                JsonValueKind.Null => StatusConfigurationShape.Null,
                _ => StatusConfigurationShape.Scalar,
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            if (restorePosition && stream.CanSeek)
            {
                stream.Position = originalPosition;
            }
        }
    }

    private static bool TryGetStatusJsonElement(JsonElement rootElement, out JsonElement statusElement)
    {
        JsonElement currentElement = rootElement;
        foreach (string pathSegment in StatusSectionName.Split(':'))
        {
            if (
                currentElement.ValueKind != JsonValueKind.Object
                || !TryGetPropertyIgnoreCase(currentElement, pathSegment, out currentElement)
            )
            {
                statusElement = default;
                return false;
            }
        }

        statusElement = currentElement;
        return true;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement jsonObject,
        string propertyName,
        out JsonElement propertyValue
    )
    {
        JsonProperty matchingProperty = jsonObject
            .EnumerateObject()
            .FirstOrDefault(property =>
                string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
            );

        if (matchingProperty.Value.ValueKind == JsonValueKind.Undefined)
        {
            propertyValue = default;
            return false;
        }

        propertyValue = matchingProperty.Value;
        return true;
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
        AddFailureIfInvalidBaselineHighWaterMark(
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

        AddFailureIfInvalidCancelAfterTimeout(
            readAcceleration.DirectFillTimeout,
            $"{nameof(DocumentCacheOptions.ReadAcceleration)}:{nameof(DocumentCacheReadAccelerationOptions.DirectFillTimeout)}",
            failures
        );
    }

    private static void ValidateAdministration(DocumentCacheOptions options, List<string> failures)
    {
        DocumentCacheAdministrationOptions? administration = options.Administration;
        if (administration is null)
        {
            failures.Add($"{nameof(DocumentCacheOptions.Administration)} must not be null.");
            return;
        }

        AddFailureIfNonPositive(
            administration.WorkflowTimeout,
            $"{nameof(DocumentCacheOptions.Administration)}:{nameof(DocumentCacheAdministrationOptions.WorkflowTimeout)}",
            failures
        );
    }

    private static void ValidateStatus(DocumentCacheOptions options, List<string> failures)
    {
        DocumentCacheStatusOptions? status = options.Status;
        if (status is null)
        {
            failures.Add($"{nameof(DocumentCacheOptions.Status)} must not be null.");
            return;
        }

        AddFailureIfInvalidCancelAfterTimeout(
            status.StatusObservationTimeout,
            $"{nameof(DocumentCacheOptions.Status)}:{nameof(DocumentCacheStatusOptions.StatusObservationTimeout)}",
            failures
        );
        AddFailureIfInvalidCancelAfterTimeout(
            status.EndpointTimeout,
            $"{nameof(DocumentCacheOptions.Status)}:{nameof(DocumentCacheStatusOptions.EndpointTimeout)}",
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

    private static void AddFailureIfInvalidCancelAfterTimeout(
        TimeSpan value,
        string settingName,
        List<string> failures
    )
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"{settingName} must be positive.");
            return;
        }

        if (value.TotalMilliseconds > MaximumCancelAfterTimeoutMilliseconds)
        {
            failures.Add(
                $"{settingName} must be no greater than {MaximumCancelAfterTimeoutMilliseconds} milliseconds."
            );
        }
    }

    private static void AddFailureIfNonPositive(int value, string settingName, List<string> failures)
    {
        if (value <= 0)
        {
            failures.Add($"{settingName} must be positive.");
        }
    }

    private static void AddFailureIfInvalidBaselineHighWaterMark(
        int value,
        string settingName,
        List<string> failures
    )
    {
        if (value <= 0)
        {
            failures.Add($"{settingName} must be positive.");
            return;
        }

        if (value == int.MaxValue)
        {
            failures.Add(
                $"{settingName} must be less than int.MaxValue to leave room for high-water-plus-one observation."
            );
        }
    }

    private enum StatusConfigurationShape
    {
        Omitted,
        Object,
        Null,
        Scalar,
    }
}
