// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using FluentValidation.Results;
using Microsoft.OpenApi;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class VendorModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapSecuredPost("/v3/vendors/", InsertVendor)
            .Produces(201)
            .Produces(200)
            .AddOpenApiOperationTransformer(
                (operation, context, ct) =>
                {
                    if (operation.Responses is null)
                    {
                        return Task.CompletedTask;
                    }

                    foreach (var code in new[] { "201", "200" })
                    {
                        if (
                            operation.Responses.TryGetValue(code, out var iResponse)
                            && iResponse is OpenApiResponse response
                        )
                        {
                            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
                            response.Headers["Location"] = new OpenApiHeader
                            {
                                Description = "The absolute URL of the vendor resource.",
                                Required = true,
                                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri" },
                            };
                        }
                    }

                    return Task.CompletedTask;
                }
            );
        endpoints.MapSecuredGet("/v3/vendors/", GetAll);
        endpoints.MapSecuredGet($"/v3/vendors/{{id}}", GetById);
        endpoints.MapSecuredPut($"/v3/vendors/{{id}}", Update);
        endpoints.MapSecuredDelete($"/v3/vendors/{{id}}", Delete);
        endpoints
            .MapSecuredGet($"/v3/vendors/{{id}}/applications", GetApplicationsByVendorId)
            .Produces<List<ApplicationResponse>>(200);
    }

    private static async Task<IResult> InsertVendor(
        VendorInsertCommand entity,
        VendorInsertCommand.Validator validator,
        HttpContext httpContext,
        IVendorRepository repository
    )
    {
        await validator.GuardAsync(entity);
        var insertResult = await repository.InsertVendor(entity);

        var request = httpContext.Request;
        var locationUrl =
            $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/";

        if (insertResult is VendorInsertResult.Success success)
        {
            var resourceUrl = $"{locationUrl}{success.Id}";
            if (success.IsNewVendor)
            {
                return Results.Created(resourceUrl, null);
            }

            httpContext.Response.Headers.Location = resourceUrl;
            return Results.Ok();
        }

        return insertResult switch
        {
            VendorInsertResult.FailureDuplicateCompanyName => Results.Json(
                FailureResponse.ForDataValidation(
                    [
                        new ValidationFailure(
                            "Name",
                            "A vendor name already exists in the database. Please enter a unique name."
                        ),
                    ],
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.BadRequest
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetAll(
        IVendorRepository repository,
        [AsParameters] FrontendVendorQuery query,
        VendorPagingQueryValidator validator,
        HttpContext httpContext
    )
    {
        await validator.GuardAsync(query);
        VendorQueryResult getResult = await repository.QueryVendor(query.ToQuery());
        return getResult switch
        {
            VendorQueryResult.Success success => Results.Ok(success.VendorResponses),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetById(
        int id,
        HttpContext httpContext,
        IVendorRepository repository,
        ILogger<VendorModule> logger
    )
    {
        logger.LogDebug("Entering Vendor GetById for id: {Id}", id);
        VendorGetResult getResult = await repository.GetVendor(id);
        return getResult switch
        {
            VendorGetResult.Success success => Results.Ok(success.VendorResponse),
            VendorGetResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"Vendor {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    /// <summary>
    /// Updates the vendor and re-applies the resulting namespace prefixes to the identity
    /// provider client of every ApiClient the vendor owns.
    ///
    /// Every affected client's stable identity is resolved before any state is mutated, and the
    /// aggregate lock of every application owning one of those clients is held across the whole
    /// mutation and its compensation. The identity provider is mutated before the database
    /// commit, so a provider failure returns with the vendor row untouched, and the UUID each
    /// successful provider call reports is persisted to that client's row, under a guard that
    /// refuses to overwrite a newer writer, before the next client is touched. A provider that
    /// preserves the client's identity reports the stored UUID, which the guard resolves as
    /// already applied.
    ///
    /// A failure part way through restores the clients this request already changed, so the
    /// operation never returns success with the database and the identity provider disagreeing.
    /// </summary>
    private static async Task<IResult> Update(
        int id,
        VendorUpdateCommand command,
        VendorUpdateCommand.Validator validator,
        HttpContext httpContext,
        IVendorRepository repository,
        IApiClientRepository apiClientRepository,
        IIdentityProviderRepository clientRepository,
        IApplicationLockManager lockManager,
        ILogger<VendorModule> logger
    )
    {
        PutGuards.GuardRouteIdMatchesBodyId(id, command.Id);

        await validator.GuardAsync(command);

        // Invalid input never consumes a lock: acquisition follows the guards above.
        (IResult? lockFailure, VendorUpdateState? lockedState, IAsyncDisposable? heldLock) =
            await AcquireVendorLocksAsync(id, repository, lockManager, httpContext, logger);
        if (lockFailure is not null)
        {
            return lockFailure;
        }

        VendorUpdateState state = lockedState!;

        // Each client this request has changed, with the identity-provider client it is now
        // known to carry, so compensation addresses the client that exists rather than the one
        // the snapshot named.
        List<(VendorApiClient Client, Guid CurrentUuid)> mutatedClients = [];

        try
        {
            foreach (VendorApiClient client in state.Clients)
            {
                if (await ApplyNamespaceClaimAsync(client) is { } clientFailure)
                {
                    return clientFailure;
                }
            }

            VendorUpdateResult vendorUpdateResult;
            try
            {
                vendorUpdateResult = await repository.UpdateVendor(command);
            }
            catch (Exception ex)
            {
                // A thrown repository failure enters the same authoritative outcome resolution
                // as a returned unknown failure.
                logger.LogError(ex, "Repository update threw for Vendor {Id}", id);
                vendorUpdateResult = new VendorUpdateResult.FailureUnknown(
                    "The repository update threw an exception."
                );
            }

            switch (vendorUpdateResult)
            {
                case VendorUpdateResult.Success:
                    return Results.NoContent();
                case VendorUpdateResult.FailureNotExists:
                    // The vendor vanished under the locks, taking its applications and their
                    // ApiClient rows with it, so a provider client that is already gone and a
                    // row that no longer exists are both the expected end state.
                    return await RollbackMutatedClientsAsync(acceptMissing: true)
                        ? VendorNotFound(id, httpContext)
                        : FailureResults.Unknown(httpContext.TraceIdentifier);
                case VendorUpdateResult.FailureUnknown updateFailure:
                    logger.LogError(
                        "Repository update failed for Vendor {Id}: {Message}",
                        id,
                        SanitizeForLog(updateFailure.FailureMessage)
                    );
                    return await ResolveAmbiguousOutcomeAsync();
                default:
                    logger.LogError("Unexpected repository update result for Vendor {Id}", id);
                    return await ResolveAmbiguousOutcomeAsync();
            }
        }
        finally
        {
            await DisposeLockAsync(heldLock);
        }

        // Returns null once the client is done: its provider claim carries the requested
        // prefixes and the UUID the provider reported is stored on its row. Any other outcome
        // compensates and returns the response the caller receives.
        async Task<IResult?> ApplyNamespaceClaimAsync(VendorApiClient client)
        {
            ClientUpdateResult clientUpdateResult;
            try
            {
                clientUpdateResult = await clientRepository.UpdateClientNamespaceClaimAsync(
                    client.ClientUuid.ToString(),
                    command.NamespacePrefixes
                );
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "The namespace claim update threw for ApiClient {ApiClientId} of Vendor {Id}",
                    client.Id,
                    id
                );
                return await CompensateAmbiguousClientAsync(
                    client,
                    FailureResults.Unknown(httpContext.TraceIdentifier)
                );
            }

            switch (clientUpdateResult)
            {
                case ClientUpdateResult.Success success:
                    return await PersistClientUuidAsync(client, success.ClientUuid);
                case ClientUpdateResult.FailureIdentityProvider failureIdentityProvider:
                    logger.LogError(
                        "Identity provider error updating the namespace claim of ApiClient {ApiClientId} of Vendor {Id}: {Message}",
                        client.Id,
                        id,
                        SanitizeForLog(failureIdentityProvider.IdentityProviderError.FailureMessage)
                    );
                    return await CompensateAmbiguousClientAsync(
                        client,
                        FailureResults.BadGateway(
                            "Identity provider error during client update",
                            httpContext.TraceIdentifier
                        )
                    );
                case ClientUpdateResult.FailureNotFound notFound:
                    // The stored identity-provider client disappeared: an internal consistency
                    // failure, not caller input and not an upstream fault. There is nothing to
                    // restore for this client, and a client this workflow did not delete is
                    // never recreated.
                    logger.LogError(
                        "Client not found in the identity provider while updating ApiClient {ApiClientId} of Vendor {Id}: {Message}",
                        client.Id,
                        id,
                        SanitizeForLog(notFound.FailureMessage)
                    );
                    return await FinishCompensationAsync(FailureResults.Unknown(httpContext.TraceIdentifier));
                case ClientUpdateResult.FailureUnknown unknownFailure:
                    logger.LogError(
                        "Error updating the namespace claim of ApiClient {ApiClientId} of Vendor {Id}: {Message}",
                        client.Id,
                        id,
                        SanitizeForLog(unknownFailure.FailureMessage)
                    );
                    return await CompensateAmbiguousClientAsync(
                        client,
                        FailureResults.Unknown(httpContext.TraceIdentifier)
                    );
                default:
                    logger.LogError(
                        "Unexpected identity provider result updating ApiClient {ApiClientId} of Vendor {Id}",
                        client.Id,
                        id
                    );
                    return await CompensateAmbiguousClientAsync(
                        client,
                        FailureResults.Unknown(httpContext.TraceIdentifier)
                    );
            }
        }

        async Task<IResult?> PersistClientUuidAsync(VendorApiClient client, Guid reportedClientUuid)
        {
            ApiClientUuidSyncResult syncResult;
            try
            {
                syncResult = await apiClientRepository.SyncApiClientUuid(
                    client.Id,
                    client.ClientUuid,
                    reportedClientUuid
                );
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Persisting the identity-provider client UUID threw for ApiClient {ApiClientId} of Vendor {Id}",
                    client.Id,
                    id
                );
                return await CompensateUnpersistedClientAsync(client, reportedClientUuid);
            }

            switch (syncResult)
            {
                case ApiClientUuidSyncResult.Success or ApiClientUuidSyncResult.AlreadyApplied:
                    mutatedClients.Add((client, reportedClientUuid));
                    return null;
                case ApiClientUuidSyncResult.FailureStaleState:
                    logger.LogError(
                        "The stored client state for ApiClient {ApiClientId} of Vendor {Id} changed during this request; the reported client UUID was not persisted",
                        client.Id,
                        id
                    );
                    return await CompensateUnpersistedClientAsync(client, reportedClientUuid);
                case ApiClientUuidSyncResult.FailureNotExistsSafeToDelete:
                    logger.LogError(
                        "ApiClient {ApiClientId} of Vendor {Id} no longer exists; the reported client UUID was not persisted",
                        client.Id,
                        id
                    );
                    if (reportedClientUuid != client.ClientUuid)
                    {
                        // The provider replaced the client and no row references the
                        // replacement, so it is removed rather than left orphaned. A replacement
                        // that cannot be removed is logged as a known inconsistency; the
                        // response is the sanitized server error either way.
                        await TryDeleteClientAsync(client, reportedClientUuid);
                        return await FinishCompensationAsync(
                            FailureResults.Unknown(httpContext.TraceIdentifier)
                        );
                    }

                    return await CompensateUnpersistedClientAsync(
                        client,
                        reportedClientUuid,
                        acceptMissing: true
                    );
                case ApiClientUuidSyncResult.FailureNotExists:
                    logger.LogError(
                        "ApiClient {ApiClientId} of Vendor {Id} no longer exists and its reported client UUID is still referenced; nothing was deleted",
                        client.Id,
                        id
                    );
                    return await CompensateUnpersistedClientAsync(
                        client,
                        reportedClientUuid,
                        acceptMissing: true
                    );
                case ApiClientUuidSyncResult.FailureUnknown syncFailure:
                    logger.LogError(
                        "Failed to persist the identity-provider client UUID for ApiClient {ApiClientId} of Vendor {Id}: {Message}",
                        client.Id,
                        id,
                        SanitizeForLog(syncFailure.FailureMessage)
                    );
                    return await CompensateUnpersistedClientAsync(client, reportedClientUuid);
                default:
                    logger.LogError(
                        "Unexpected result persisting the identity-provider client UUID for ApiClient {ApiClientId} of Vendor {Id}",
                        client.Id,
                        id
                    );
                    return await CompensateUnpersistedClientAsync(client, reportedClientUuid);
            }
        }

        // An unknown or thrown repository outcome leaves the commit's fate unknown, so it is
        // resolved with the authoritative row-locking read, which waits out any in-flight commit
        // before classifying the state. The read happens under the locks this request still
        // holds, so nothing else can move the vendor while it is classified.
        //
        // The read can only classify the outcome when the requested values differ from the
        // original ones. Only a state that moved off the original values proves the commit
        // landed: these predicates compare business values, and the commit also writes audit
        // fields they never see, so a state that still matches the original values is never
        // proof of a commit however it got there. An unproven commit is answered conservatively,
        // never as success.
        async Task<IResult> ResolveAmbiguousOutcomeAsync()
        {
            VendorUpdateStateResult resolution;
            try
            {
                resolution = await repository.GetVendorUpdateState(id);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Resolving the outcome of the failed update for Vendor {Id} threw; no compensation was attempted and stored client state may be inconsistent",
                    id
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            }

            switch (resolution)
            {
                case VendorUpdateStateResult.Success resolved
                    when MatchesCommand(resolved.State) && !MatchesOriginal(resolved.State):
                    // The row moved off the original values and onto the requested ones, which
                    // only the commit could have done; the identity provider and the database
                    // already hold the intended state.
                    return Results.NoContent();
                case VendorUpdateStateResult.Success resolved when MatchesOriginal(resolved.State):
                    // The row still holds the original values. Either the transaction did not
                    // commit, or the request was a no-op whose requested values equal the
                    // original ones and the reread establishes neither commit nor rollback. The
                    // unresolved case takes the same conservative path as the proven rollback:
                    // the clients this request changed are restored to the vendor's stored
                    // prefixes — which a no-op has just asked for anyway, so restoring them
                    // leaves the provider holding the values the caller wanted either way — and
                    // the unknown failure stays a server error rather than an unproven 204.
                    await RollbackMutatedClientsAsync(acceptMissing: false);
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                case VendorUpdateStateResult.Success:
                    logger.LogError(
                        "Vendor {Id} is in a partially matching state after an ambiguous update; no compensation was attempted and stored client state may be inconsistent",
                        id
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                case VendorUpdateStateResult.FailureNotExists:
                    // The vendor vanished, taking its applications and their ApiClient rows with
                    // it, so an already-absent provider client and an already-absent row are the
                    // expected end state.
                    await RollbackMutatedClientsAsync(acceptMissing: true);
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                case VendorUpdateStateResult.FailureUnknown resolutionFailure:
                    logger.LogError(
                        "Could not resolve the outcome of the failed update for Vendor {Id}: {Message}; no compensation was attempted and stored client state may be inconsistent",
                        id,
                        SanitizeForLog(resolutionFailure.FailureMessage)
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                default:
                    logger.LogError(
                        "Could not resolve the outcome of the failed update for Vendor {Id}; no compensation was attempted and stored client state may be inconsistent",
                        id
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
            }
        }

        bool MatchesCommand(VendorUpdateState resolved) =>
            SameText(resolved.Company, command.Company)
            && SameText(resolved.ContactName, command.ContactName)
            && SameText(resolved.ContactEmailAddress, command.ContactEmailAddress)
            && SamePrefixes(resolved.NamespacePrefixes, command.NamespacePrefixes);

        bool MatchesOriginal(VendorUpdateState resolved) =>
            SameText(resolved.Company, state.Company)
            && SameText(resolved.ContactName, state.ContactName)
            && SameText(resolved.ContactEmailAddress, state.ContactEmailAddress)
            && SamePrefixes(resolved.NamespacePrefixes, state.NamespacePrefixes);

        // The current client's outcome is ambiguous: a returned failure or a thrown call does
        // not prove the provider left it unchanged, so it is restored against the UUID resolved
        // under the lock. When that cannot be proven the possible inconsistency is logged and
        // the original classification stands, because an unreachable provider that refused the
        // update most likely refused the rollback too and this client's state is unknown rather
        // than known wrong.
        async Task<IResult> CompensateAmbiguousClientAsync(VendorApiClient client, IResult originalFailure)
        {
            if (!await RestoreClientAsync(client, client.ClientUuid, client.ClientUuid, acceptMissing: false))
            {
                logger.LogError(
                    "Could not restore the namespace claim of ApiClient {ApiClientId} of Vendor {Id} after its update failed; stored client state may be inconsistent",
                    client.Id,
                    id
                );
            }

            return await FinishCompensationAsync(originalFailure);
        }

        // The provider applied the claim but the database did not accept the UUID, so the
        // provider client this request mutated — the one the provider reported, which a
        // replacing provider has already made the live client — is restored, guarded by the UUID
        // the row still holds because the forward sync did not persist the reported one. Its row
        // is left to whichever writer owns it.
        async Task<IResult> CompensateUnpersistedClientAsync(
            VendorApiClient client,
            Guid reportedClientUuid,
            bool acceptMissing = false
        )
        {
            if (!await RestoreClientAsync(client, reportedClientUuid, client.ClientUuid, acceptMissing))
            {
                logger.LogError(
                    "Could not restore the namespace claim of ApiClient {ApiClientId} of Vendor {Id} after its UUID could not be persisted; stored client state may be inconsistent",
                    client.Id,
                    id
                );
            }

            return await FinishCompensationAsync(FailureResults.Unknown(httpContext.TraceIdentifier));
        }

        // Restores every client this request changed before the failure. A failure here is a
        // KNOWN inconsistency — the client was definitely changed and definitely not restored —
        // so it replaces the original classification with the sanitized server error.
        async Task<IResult> FinishCompensationAsync(IResult originalFailure) =>
            await RollbackMutatedClientsAsync(acceptMissing: false)
                ? originalFailure
                : FailureResults.Unknown(httpContext.TraceIdentifier);

        // Every client is attempted even after one restoration fails, so the smallest possible
        // number of clients is left carrying prefixes the vendor row never received.
        async Task<bool> RollbackMutatedClientsAsync(bool acceptMissing)
        {
            bool allRestored = true;
            for (int index = mutatedClients.Count - 1; index >= 0; index--)
            {
                (VendorApiClient client, Guid currentUuid) = mutatedClients[index];
                // The forward sync succeeded for these, so the row holds the UUID the provider
                // client already carries.
                if (!await RestoreClientAsync(client, currentUuid, currentUuid, acceptMissing))
                {
                    allRestored = false;
                    logger.LogError(
                        "Could not restore the namespace claim of ApiClient {ApiClientId} of Vendor {Id}; stored client state is inconsistent",
                        client.Id,
                        id
                    );
                }
            }

            mutatedClients.Clear();
            return allRestored;
        }

        // Re-applies the vendor's stored prefixes to one provider client and persists whatever
        // UUID the rollback reports. The provider client this request mutated and the UUID its
        // row still holds are not always the same one: a provider that replaced the client
        // reports the replacement before the forward sync persists it, so the rollback must
        // address the replacement while the guard states the UUID the row is expected to carry.
        async Task<bool> RestoreClientAsync(
            VendorApiClient client,
            Guid providerClientUuid,
            Guid expectedStoredUuid,
            bool acceptMissing
        )
        {
            ClientUpdateResult rollbackResult;
            try
            {
                rollbackResult = await clientRepository.UpdateClientNamespaceClaimAsync(
                    providerClientUuid.ToString(),
                    state.NamespacePrefixes
                );
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "The namespace claim rollback threw for ApiClient {ApiClientId} of Vendor {Id}",
                    client.Id,
                    id
                );
                return false;
            }

            if (rollbackResult is ClientUpdateResult.FailureNotFound)
            {
                // A provider client that is already gone is the expected end state only when
                // the aggregate itself disappeared.
                return acceptMissing;
            }

            if (rollbackResult is not ClientUpdateResult.Success rollbackSuccess)
            {
                return false;
            }

            ApiClientUuidSyncResult syncResult;
            try
            {
                syncResult = await apiClientRepository.SyncApiClientUuid(
                    client.Id,
                    expectedStoredUuid,
                    rollbackSuccess.ClientUuid
                );
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Persisting the rolled-back client UUID threw for ApiClient {ApiClientId} of Vendor {Id}",
                    client.Id,
                    id
                );
                return false;
            }

            switch (syncResult)
            {
                case ApiClientUuidSyncResult.Success or ApiClientUuidSyncResult.AlreadyApplied:
                    return true;
                case ApiClientUuidSyncResult.FailureNotExistsSafeToDelete:
                    // The row is gone and nothing references the client the rollback produced,
                    // so it is removed rather than left orphaned. A replacement that cannot be
                    // removed survives as an orphaned provider client, which is never a clean
                    // outcome whatever became of the row.
                    if (
                        rollbackSuccess.ClientUuid != providerClientUuid
                        && !await TryDeleteClientAsync(client, rollbackSuccess.ClientUuid)
                    )
                    {
                        return false;
                    }

                    return acceptMissing;
                case ApiClientUuidSyncResult.FailureNotExists:
                    return acceptMissing;
                default:
                    return false;
            }
        }

        async Task<bool> TryDeleteClientAsync(VendorApiClient client, Guid clientUuid)
        {
            ClientDeleteResult deleteResult;
            try
            {
                deleteResult = await clientRepository.DeleteClientAsync(clientUuid.ToString());
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Deleting the replacement identity-provider client of ApiClient {ApiClientId} of Vendor {Id} threw; stored client state is inconsistent",
                    client.Id,
                    id
                );
                return false;
            }

            if (deleteResult is ClientDeleteResult.Success or ClientDeleteResult.FailureClientNotFound)
            {
                return true;
            }

            logger.LogError(
                "Could not delete the replacement identity-provider client of ApiClient {ApiClientId} of Vendor {Id}; stored client state is inconsistent",
                client.Id,
                id
            );
            return false;
        }
    }

    /// <summary>
    /// Resolves the vendor, acquires the aggregate lock of every application that owns one of
    /// its clients as a single lock set on one database session — deduplicated and in ascending
    /// application id order, the same total order the Application and ApiClient workflows use,
    /// so no cycle between them is possible — and rereads the vendor under those locks. The
    /// reread is authoritative: it reflects any workflow that committed while this one waited. A
    /// vendor whose set of owning applications changed while the locks were being acquired is
    /// retried a bounded number of times, and persistent drift is answered as a retriable
    /// concurrency conflict. A vendor that owns no clients mutates no provider client, so it
    /// takes no lock at all. The connection cost of the lock set is one session however many
    /// applications it spans, so the fan-out needs no cap.
    /// </summary>
    private static async Task<(
        IResult? Failure,
        VendorUpdateState? State,
        IAsyncDisposable? Lock
    )> AcquireVendorLocksAsync(
        int id,
        IVendorRepository repository,
        IApplicationLockManager lockManager,
        HttpContext httpContext,
        ILogger<VendorModule> logger
    )
    {
        const int maxAttempts = 3;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            (IResult? preReadFailure, VendorUpdateState? preReadState) = await ReadVendorUpdateStateAsync(
                id,
                repository,
                httpContext,
                logger
            );
            if (preReadFailure is not null)
            {
                return (preReadFailure, null, null);
            }

            int[] applicationIdsToLock = ApplicationIdsOf(preReadState!);
            if (applicationIdsToLock.Length == 0)
            {
                return (null, preReadState, null);
            }

            ApplicationLockResult lockResult = await lockManager.AcquireAllAsync(
                applicationIdsToLock,
                httpContext.RequestAborted
            );
            if (LockFailureResult(lockResult, httpContext, logger) is { } lockFailure)
            {
                return (lockFailure, null, null);
            }

            IAsyncDisposable heldLock = ((ApplicationLockResult.Acquired)lockResult).Handle;
            try
            {
                (IResult? underLockFailure, VendorUpdateState? underLockState) =
                    await ReadVendorUpdateStateAsync(id, repository, httpContext, logger);
                if (underLockFailure is not null)
                {
                    await DisposeLockAsync(heldLock);
                    return (underLockFailure, null, null);
                }

                if (!ApplicationIdsOf(underLockState!).SequenceEqual(applicationIdsToLock))
                {
                    // An application was added to or removed from the vendor, or a client moved
                    // between applications, while the locks were being acquired; retry against
                    // the new set rather than mutate a client whose aggregate is unlocked.
                    await DisposeLockAsync(heldLock);
                    continue;
                }

                return (null, underLockState, heldLock);
            }
            catch
            {
                // A thrown reread — including a propagated cancellation — must not leak the
                // locks already held.
                await DisposeLockAsync(heldLock);
                throw;
            }
        }

        logger.LogWarning(
            "The applications owning the clients of Vendor {Id} kept changing during lock acquisition",
            id
        );
        return (RetriableConflict(httpContext), null, null);
    }

    private static int[] ApplicationIdsOf(VendorUpdateState state) =>
        [.. state.Clients.Select(client => client.ApplicationId).Distinct().Order()];

    private static async Task<(IResult? Failure, VendorUpdateState? State)> ReadVendorUpdateStateAsync(
        int id,
        IVendorRepository repository,
        HttpContext httpContext,
        ILogger<VendorModule> logger
    )
    {
        switch (await repository.GetVendorUpdateState(id))
        {
            case VendorUpdateStateResult.Success success:
                return (null, success.State);
            case VendorUpdateStateResult.FailureNotExists:
                return (VendorNotFound(id, httpContext), null);
            case VendorUpdateStateResult.FailureUnknown failure:
                logger.LogError(
                    "Error reading the update state of Vendor {Id}: {Message}",
                    id,
                    SanitizeForLog(failure.FailureMessage)
                );
                return (FailureResults.Unknown(httpContext.TraceIdentifier), null);
            default:
                logger.LogError("Unexpected result reading the update state of Vendor {Id}", id);
                return (FailureResults.Unknown(httpContext.TraceIdentifier), null);
        }
    }

    /// <summary>
    /// Maps a failed lock acquisition: a timeout is a retriable concurrency conflict, and an
    /// infrastructure failure is a sanitized server error. Returns null when the lock was
    /// acquired.
    /// </summary>
    private static IResult? LockFailureResult(
        ApplicationLockResult lockResult,
        HttpContext httpContext,
        ILogger<VendorModule> logger
    )
    {
        switch (lockResult)
        {
            case ApplicationLockResult.FailureTimeout:
                return RetriableConflict(httpContext);
            case ApplicationLockResult.FailureUnknown failure:
                logger.LogError(
                    "Failed to acquire the application lock: {Message}",
                    SanitizeForLog(failure.FailureMessage)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            default:
                return null;
        }
    }

    private static IResult RetriableConflict(HttpContext httpContext) =>
        Results.Json(
            FailureResponse.ForConflict(
                "Unable to process the request due to a concurrent modification. Retry the request.",
                httpContext.TraceIdentifier
            ),
            contentType: "application/problem+json",
            statusCode: (int)HttpStatusCode.Conflict
        );

    private static async Task DisposeLockAsync(IAsyncDisposable? heldLock)
    {
        if (heldLock is not null)
        {
            await heldLock.DisposeAsync();
        }
    }

    private static IResult VendorNotFound(int id, HttpContext httpContext) =>
        Results.Json(
            FailureResponse.ForNotFound(
                $"Vendor {id} not found. It may have been recently deleted.",
                httpContext.TraceIdentifier
            ),
            statusCode: (int)HttpStatusCode.NotFound
        );

    /// <summary>
    /// A missing contact value and an empty one are the same absence, so a backend that stores
    /// one as the other cannot turn a complete match into a partial one.
    /// </summary>
    private static bool SameText(string? left, string? right) =>
        string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);

    /// <summary>
    /// Namespace prefixes are a set: the stored order carries no meaning, and blank entries and
    /// surrounding whitespace are stripped on write, so the comparison normalizes both sides the
    /// same way the repository does.
    /// </summary>
    private static bool SamePrefixes(string left, string right) =>
        NormalizePrefixes(left).SetEquals(NormalizePrefixes(right));

    private static HashSet<string> NormalizePrefixes(string prefixes) =>
        [.. prefixes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static string SanitizeForLog(string? input)
    {
        return LoggingUtility.SanitizeForLog(input);
    }

    private static async Task<IResult> Delete(
        int id,
        HttpContext httpContext,
        IVendorRepository repository,
        ILogger<VendorModule> logger
    )
    {
        logger.LogDebug("Entering Vendor Delete for id: {Id}", id);
        VendorDeleteResult deleteResult = await repository.DeleteVendor(id);
        return deleteResult switch
        {
            VendorDeleteResult.Success => Results.NoContent(),
            VendorDeleteResult.FailureNotExists => Results.Json(
                FailureResponse.ForNotFound(
                    $"Vendor {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetApplicationsByVendorId(
        int id,
        IVendorRepository repository,
        HttpContext httpContext
    )
    {
        var getResult = await repository.GetVendorApplications(id);

        return getResult switch
        {
            VendorApplicationsResult.Success success => Results.Ok(success.ApplicationResponses),
            VendorApplicationsResult.FailureNotExists => FailureResults.NotFound(
                $"Vendor {id} not found. It may have been recently deleted.",
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }
}
