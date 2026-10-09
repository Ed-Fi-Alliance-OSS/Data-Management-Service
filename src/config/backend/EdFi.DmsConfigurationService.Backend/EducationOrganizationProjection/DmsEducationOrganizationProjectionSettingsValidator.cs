// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

/// <summary>
/// The DMS-1440 spec §5.2 validation of <see cref="DmsEducationOrganizationProjectionSettings"/>, run at startup when
/// <c>DmsBaseUrl</c> is set. Every failed rule is reported, and each message names
/// <c>DmsEducationOrganizationProjectionSettings:&lt;Setting&gt;</c>. No message contains the base URL or a credential
/// value, because the URL may carry user information and the messages reach the startup log.
/// </summary>
public sealed class DmsEducationOrganizationProjectionSettingsValidator
    : IValidateOptions<DmsEducationOrganizationProjectionSettings>
{
    private const string Section = DmsEducationOrganizationProjectionSettings.SectionName;

    /// <summary>The §5.2 allowance for one projected item in a page body, in bytes.</summary>
    public const int ItemBytesAllowance = 2048;

    /// <summary>The §5.2 allowance for a page body's envelope, in bytes.</summary>
    public const int EnvelopeBytesAllowance = 1024;

    public ValidateOptionsResult Validate(string? name, DmsEducationOrganizationProjectionSettings options)
    {
        if (string.IsNullOrWhiteSpace(options.DmsBaseUrl))
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];

        ValidateBaseUrl(failures, options.DmsBaseUrl);
        ValidateCredentials(failures, options);
        ValidateContractVersions(failures, options.ContractVersions);

        Range(failures, nameof(options.PageSize), options.PageSize, 1, 10_000);
        Range(failures, nameof(options.DiscoveryTimeoutSeconds), options.DiscoveryTimeoutSeconds, 1, 300);
        Range(
            failures,
            nameof(options.TokenRequestTimeoutSeconds),
            options.TokenRequestTimeoutSeconds,
            1,
            300
        );
        Range(failures, nameof(options.PageRequestTimeoutSeconds), options.PageRequestTimeoutSeconds, 1, 600);
        Range(failures, nameof(options.TotalReadTimeoutSeconds), options.TotalReadTimeoutSeconds, 1, 86_400);
        Range(failures, nameof(options.MaxPages), options.MaxPages, 1, 1_000_000);
        Range(failures, nameof(options.MaxItems), options.MaxItems, 1, 10_000_000);
        Range(failures, nameof(options.MaxWalkRestarts), options.MaxWalkRestarts, 0, 10);
        Range(
            failures,
            nameof(options.TokenExpirySafetyMarginSeconds),
            options.TokenExpirySafetyMarginSeconds,
            0,
            3_600
        );
        Range(failures, nameof(options.DiscoveryCacheSeconds), options.DiscoveryCacheSeconds, 0, 86_400);

        // A full page of the largest items must fit in the body cap. The product is taken in long so a PageSize far
        // outside its range, which already failed above, cannot overflow and hide this failure.
        long minimumBodyBytes = (long)options.PageSize * ItemBytesAllowance + EnvelopeBytesAllowance;
        if (options.MaxResponseBodyBytes < minimumBodyBytes)
        {
            failures.Add(
                $"{Section}:{nameof(options.MaxResponseBodyBytes)} must be at least {Section}:{nameof(options.PageSize)} "
                    + $"({options.PageSize}) x {ItemBytesAllowance} + {EnvelopeBytesAllowance} = {minimumBodyBytes}; "
                    + $"it is {options.MaxResponseBodyBytes}."
            );
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateBaseUrl(List<string> failures, string baseUrl)
    {
        bool valid =
            Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.UserInfo.Length == 0
            && !baseUrl.Contains('?')
            && !baseUrl.Contains('#');

        if (!valid)
        {
            failures.Add(
                $"{Section}:{nameof(DmsEducationOrganizationProjectionSettings.DmsBaseUrl)} must be an absolute http or "
                    + "https URL, optionally with a base path, and with no user information, query or fragment."
            );
        }
    }

    /// <summary>
    /// Each tenant entry must name both values. The shared pair may be absent (both values empty, which also covers
    /// an environment template that sets them to empty strings) but not half set. At least one complete pair must
    /// exist, or every read would fail for want of a credential.
    /// </summary>
    private static void ValidateCredentials(
        List<string> failures,
        DmsEducationOrganizationProjectionSettings options
    )
    {
        bool anyComplete = false;

        DmsEducationOrganizationProjectionCredentials? shared = options.Credentials;
        bool sharedHasId = !string.IsNullOrWhiteSpace(shared?.ClientId);
        bool sharedHasSecret = !string.IsNullOrWhiteSpace(shared?.ClientSecret);
        if (sharedHasId != sharedHasSecret)
        {
            failures.Add(
                $"{Section}:{nameof(options.Credentials)} must set both ClientId and ClientSecret, or neither."
            );
        }
        anyComplete |= sharedHasId && sharedHasSecret;

        foreach (
            (
                string tenant,
                DmsEducationOrganizationProjectionCredentials? credentials
            ) in options.TenantCredentials
        )
        {
            if (
                string.IsNullOrWhiteSpace(credentials?.ClientId)
                || string.IsNullOrWhiteSpace(credentials.ClientSecret)
            )
            {
                failures.Add(
                    $"{Section}:{nameof(options.TenantCredentials)}:{LoggingUtility.SanitizeForLog(tenant)} must set "
                        + "both ClientId and ClientSecret."
                );
            }
            else
            {
                anyComplete = true;
            }
        }

        if (!anyComplete)
        {
            failures.Add(
                $"{Section}:{nameof(options.Credentials)} or at least one {Section}:{nameof(options.TenantCredentials)} "
                    + $"entry must be set when {Section}:{nameof(options.DmsBaseUrl)} is set."
            );
        }
    }

    /// <summary>
    /// The configured versions must be distinct and each one this service can parse; an unparseable version would be
    /// requested and then read with the wrong contract.
    /// </summary>
    private static void ValidateContractVersions(List<string> failures, string[]? contractVersions)
    {
        if (contractVersions is null)
        {
            return;
        }

        bool valid =
            contractVersions.Length > 0
            && contractVersions.Distinct(StringComparer.Ordinal).Count() == contractVersions.Length
            && Array.TrueForAll(
                contractVersions,
                version =>
                    DmsEducationOrganizationProjectionSettings.SupportedContractVersions.Contains(
                        version,
                        StringComparer.Ordinal
                    )
            );

        if (!valid)
        {
            failures.Add(
                $"{Section}:{nameof(DmsEducationOrganizationProjectionSettings.ContractVersions)} must list distinct "
                    + "versions from: "
                    + string.Join(", ", DmsEducationOrganizationProjectionSettings.SupportedContractVersions)
                    + "."
            );
        }
    }

    private static void Range(List<string> failures, string setting, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add($"{Section}:{setting} must be between {minimum} and {maximum}; it is {value}.");
        }
    }
}
