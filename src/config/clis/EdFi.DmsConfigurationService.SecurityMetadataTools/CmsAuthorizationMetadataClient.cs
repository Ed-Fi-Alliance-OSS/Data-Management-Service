// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools;

internal enum CmsAuthorizationMetadataFailureKind
{
    MissingCredentials,
    InvalidRequest,
    HttpFailure,
    TransportFailure,
    InvalidMetadata,
}

internal abstract record CmsAuthorizationMetadataResult
{
    internal sealed record Success(AuthorizationMetadataValidationResult.Success Metadata)
        : CmsAuthorizationMetadataResult;

    // Only trusted messages, status codes and validation kinds cross this boundary. Remote bodies,
    // reason phrases, validation values/paths and exception messages can all contain credentials.
    internal sealed record Failure(
        CmsAuthorizationMetadataFailureKind Kind,
        string Message,
        int StatusCode = 0
    ) : CmsAuthorizationMetadataResult
    {
        internal IReadOnlyList<AuthorizationMetadataFailureKind> ValidationFailureKinds { get; init; } = [];
    }
}

/// <summary>Read and validate one complete CMS metadata response using an already-issued bearer token.</summary>
internal sealed class CmsAuthorizationMetadataClient(HttpClient httpClient)
{
    internal async Task<CmsAuthorizationMetadataResult> GetAsync(
        string baseUrl,
        string accessTokenEnvironmentVariable,
        string tenant = "",
        CancellationToken cancellationToken = default
    )
    {
        string token;
        try
        {
            token = Environment.GetEnvironmentVariable(accessTokenEnvironmentVariable) ?? "";
        }
        catch (Exception exception) when (exception is ArgumentException or SecurityException)
        {
            return MissingCredentials();
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return MissingCredentials();
        }

        if (
            !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https")
            || baseUri.UserInfo.Length > 0
            || baseUri.Query.Length > 0
            || baseUri.Fragment.Length > 0
        )
        {
            return new CmsAuthorizationMetadataResult.Failure(
                CmsAuthorizationMetadataFailureKind.InvalidRequest,
                "CMS base URL must be an absolute HTTP or HTTPS URL without credentials, a query, or a fragment."
            );
        }

        using var request = new HttpRequestMessage();
        try
        {
            // A deployment prefix is a directory even when the supplied URL has no trailing slash.
            request.RequestUri = new UriBuilder(baseUri)
            {
                Path = baseUri.AbsolutePath.TrimEnd('/') + "/v3/authorizationMetadata",
            }.Uri;
            request.Method = HttpMethod.Get;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (tenant.Length > 0)
            {
                request.Headers.Add("Tenant", tenant);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return new CmsAuthorizationMetadataResult.Failure(
                CmsAuthorizationMetadataFailureKind.InvalidRequest,
                "CMS request URL or headers are invalid."
            );
        }

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
            // CMS returns 200 for the complete array. Never compare a partial (206) response.
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new CmsAuthorizationMetadataResult.Failure(
                    CmsAuthorizationMetadataFailureKind.HttpFailure,
                    $"CMS authorization metadata request returned HTTP {(int)response.StatusCode}.",
                    (int)response.StatusCode
                );
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            return AuthorizationMetadataValidator.Validate(json) switch
            {
                AuthorizationMetadataValidationResult.Success success
                    when ContainsCredential(success, token) => new CmsAuthorizationMetadataResult.Failure(
                    CmsAuthorizationMetadataFailureKind.InvalidMetadata,
                    "CMS authorization metadata contains credential material and cannot be reported."
                ),
                AuthorizationMetadataValidationResult.Success success =>
                    new CmsAuthorizationMetadataResult.Success(success),
                AuthorizationMetadataValidationResult.Failure failure =>
                    new CmsAuthorizationMetadataResult.Failure(
                        CmsAuthorizationMetadataFailureKind.InvalidMetadata,
                        "CMS returned invalid authorization metadata."
                    )
                    {
                        ValidationFailureKinds = failure.Failures.Select(error => error.Kind).ToArray(),
                    },
                _ => throw new UnreachableException(),
            };
        }
        catch (Exception exception)
            when (exception
                    is HttpRequestException
                        or IOException
                        or OperationCanceledException
                        or InvalidOperationException
                        or ArgumentException
            )
        {
            return new CmsAuthorizationMetadataResult.Failure(
                CmsAuthorizationMetadataFailureKind.TransportFailure,
                "CMS authorization metadata request could not be completed."
            );
        }
    }

    // Even structurally valid remote names are untrusted: later mismatch reports print these values.
    private static bool ContainsCredential(
        AuthorizationMetadataValidationResult.Success metadata,
        string token
    ) =>
        metadata.ClaimSets.Any(set =>
            set.ClaimSetName.Contains(token, StringComparison.Ordinal)
            || set.Claims.Any(claim =>
                claim.Name.Contains(token, StringComparison.Ordinal)
                || claim.Actions.Any(action =>
                    action.Name.Contains(token, StringComparison.Ordinal)
                    || Array.Exists(
                        action.AuthorizationStrategies,
                        strategy => strategy.Name.Contains(token, StringComparison.Ordinal)
                    )
                )
            )
        );

    private static CmsAuthorizationMetadataResult.Failure MissingCredentials() =>
        new(
            CmsAuthorizationMetadataFailureKind.MissingCredentials,
            "The access-token environment variable is missing, empty, or unavailable."
        );
}
