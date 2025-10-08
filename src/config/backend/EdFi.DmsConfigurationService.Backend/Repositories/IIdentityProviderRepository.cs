// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IIdentityProviderRepository
{
    Task<ClientCreateResult> CreateClientAsync(
        string clientId,
        string clientSecret,
        string role,
        string displayName,
        string scope,
        string namespacePrefixes,
        string educationOrganizationIds,
        long[]? dmsInstanceIds = null
    );

    Task<ClientUpdateResult> UpdateClientAsync(
        string clientUuid,
        string displayName,
        string scope,
        string educationOrganizationIds,
        long[]? dmsInstanceIds = null
    );

    Task<ClientUpdateResult> UpdateClientNamespaceClaimAsync(string clientUuid, string namespacePrefixes);

    Task<ClientClientsResult> GetAllClientsAsync();

    Task<ClientDeleteResult> DeleteClientAsync(string clientUuid);

    Task<ClientResetResult> ResetCredentialsAsync(string clientUuid);
}

public record ClientCreateResult
{
    public record Success(Guid ClientUuid) : ClientCreateResult;

    public record FailureIdentityProvider(IdentityProviderError IdentityProviderError) : ClientCreateResult();

    public record FailureUnknown(string FailureMessage) : ClientCreateResult();
}

public record ClientDeleteResult
{
    public record Success() : ClientDeleteResult;

    public record FailureIdentityProvider(IdentityProviderError IdentityProviderError) : ClientDeleteResult();

    public record FailureClientNotFound(string FailureMessage) : ClientDeleteResult();

    public record FailureUnknown(string FailureMessage) : ClientDeleteResult();
}

public record ClientResetResult
{
    public record Success(string ClientSecret) : ClientResetResult;

    public record FailureIdentityProvider(IdentityProviderError IdentityProviderError) : ClientResetResult();

    public record FailureClientNotFound(string FailureMessage) : ClientResetResult();

    public record FailureUnknown(string FailureMessage) : ClientResetResult();
}

public record ClientUpdateResult
{
    public record Success(Guid ClientUuid) : ClientUpdateResult;

    public record FailureIdentityProvider(IdentityProviderError IdentityProviderError) : ClientUpdateResult();

    public record FailureUnknown(string FailureMessage) : ClientUpdateResult();

    public record FailureNotFound(string FailureMessage) : ClientUpdateResult();
}

public record ClientClientsResult
{
    public record Success(IEnumerable<string> ClientList) : ClientClientsResult;

    public record FailureIdentityProvider(IdentityProviderError IdentityProviderError) : ClientClientsResult;

    public record FailureUnknown(string FailureMessage) : ClientClientsResult();
}
