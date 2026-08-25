// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security;
using System.Text;
using System.Text.Json;

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

internal sealed class LocalCdcBindingStateStore : ICdcBindingStateStore
{
    public const string DefaultRootPath = "eng/docker-compose/.cdc-state";

    private readonly CdcStateStorePathResolver _pathResolver;
    private readonly ICdcLocalStateStorePermissions _permissions;
    private readonly ICdcLocalStateStoreFileSystem _fileSystem;

    public LocalCdcBindingStateStore(string rootPath = DefaultRootPath)
        : this(rootPath, CdcLocalStateStorePermissions.Current) { }

    internal LocalCdcBindingStateStore(string rootPath, ICdcLocalStateStorePermissions permissions)
        : this(rootPath, permissions, CdcLocalStateStoreFileSystem.Current) { }

    internal LocalCdcBindingStateStore(
        string rootPath,
        ICdcLocalStateStorePermissions permissions,
        ICdcLocalStateStoreFileSystem fileSystem
    )
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(fileSystem);

        _pathResolver = new(rootPath);
        _permissions = permissions;
        _fileSystem = fileSystem;
    }

    public async Task<CdcCreateBindingStateStoreResult> CreateBindingIfAbsentAsync(
        CdcBinding binding,
        CancellationToken cancellationToken
    )
    {
        LocalCreateBindingResult result = await CreateOrExactMatchBindingAsync(binding, cancellationToken);

        return result switch
        {
            { Outcome: LocalCreateBindingOutcome.Created, State: not null } =>
                new CdcCreateBindingStateStoreResult.Created(result.State),
            { Outcome: LocalCreateBindingOutcome.ExistingExactMatch, State: not null } =>
                new CdcCreateBindingStateStoreResult.ExistingExactMatch(result.State),
            { Mismatch: not null } => new CdcCreateBindingStateStoreResult.BindingMismatch(result.Mismatch),
            { Failure: not null } => new CdcCreateBindingStateStoreResult.StateStoreFailure(result.Failure),
            _ => new CdcCreateBindingStateStoreResult.StateStoreFailure(
                CdcStateStoreFailure.LocalStateUnavailable("$", "CDC local binding create failed.")
            ),
        };
    }

    public async Task<CdcReadBindingStateStoreResult> ReadBindingAsync(
        CdcBindingIdentity identity,
        CancellationToken cancellationToken
    )
    {
        LocalBindingReadResult readResult = await ReadBindingStateAsync(identity, cancellationToken);

        return readResult switch
        {
            { State: not null } => new CdcReadBindingStateStoreResult.Found(readResult.State.State),
            { Missing: true } => new CdcReadBindingStateStoreResult.Missing(identity),
            { Failure: not null } => new CdcReadBindingStateStoreResult.StateStoreFailure(readResult.Failure),
            _ => new CdcReadBindingStateStoreResult.StateStoreFailure(
                CdcStateStoreFailure.LocalStateUnavailable("$", "CDC local binding read failed.")
            ),
        };
    }

    public async Task<CdcExactMatchBindingStateStoreResult> ExactMatchBindingAsync(
        CdcBinding binding,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(binding);

        CdcStateStoreFailure? bindingInputFailure = ValidateBindingInput(binding);
        if (bindingInputFailure is not null)
        {
            return new CdcExactMatchBindingStateStoreResult.StateStoreFailure(bindingInputFailure);
        }

        CdcBindingIdentity identity = binding.ToBindingIdentity();
        LocalBindingReadResult readResult = await ReadBindingStateAsync(identity, cancellationToken);
        if (readResult.Failure is not null)
        {
            return new CdcExactMatchBindingStateStoreResult.StateStoreFailure(readResult.Failure);
        }

        if (readResult.Missing)
        {
            return new CdcExactMatchBindingStateStoreResult.BindingMissing(identity);
        }

        CdcBindingExactMatchResult exactMatch = CdcBindingExactMatch.Compare(
            binding,
            readResult.State!.BindingJson
        );

        return exactMatch.Succeeded
            ? new CdcExactMatchBindingStateStoreResult.ExactMatch(readResult.State.State)
            : new CdcExactMatchBindingStateStoreResult.BindingMismatch(exactMatch.ToMismatch());
    }

    public async Task<CdcListBindingsStateStoreResult> ListBindingsAsync(
        string deploymentKey,
        CancellationToken cancellationToken
    )
    {
        CdcDeploymentStateStorePathResolution deploymentPath = _pathResolver.ResolveDeploymentPath(
            deploymentKey
        );
        if (!deploymentPath.Succeeded)
        {
            return new CdcListBindingsStateStoreResult.StateStoreFailure(deploymentPath.ToFailure());
        }

        CdcStateStoreFailure? collisionFailure = CheckPathCaseCollision(
            CdcStateStorePathResolver.BindingsDirectoryName,
            deploymentPath.DeploymentKey!
        );
        if (collisionFailure is not null)
        {
            return new CdcListBindingsStateStoreResult.StateStoreFailure(collisionFailure);
        }

        Dictionary<CdcBindingIdentity, CdcStoredBindingState> statesByIdentity = [];
        if (Directory.Exists(deploymentPath.BindingDeploymentDirectoryPath!))
        {
            CdcStateStoreFailure? bindingFailure = await ReadDeploymentBindingsAsync(
                deploymentPath,
                statesByIdentity,
                cancellationToken
            );
            if (bindingFailure is not null)
            {
                return new CdcListBindingsStateStoreResult.StateStoreFailure(bindingFailure);
            }
        }

        CdcStateStoreFailure? incidentFailure = await ValidateDeploymentIncidentsAsync(
            deploymentPath,
            statesByIdentity.Keys.ToHashSet(),
            cancellationToken
        );
        if (incidentFailure is not null)
        {
            return new CdcListBindingsStateStoreResult.StateStoreFailure(incidentFailure);
        }

        IReadOnlyList<CdcStoredBindingState> states = statesByIdentity
            .Values.OrderBy(state => state.Binding.DeploymentKey, StringComparer.Ordinal)
            .ThenBy(state => state.Binding.TenantKey, StringComparer.Ordinal)
            .ThenBy(state => state.Binding.DataStoreId, StringComparer.Ordinal)
            .ThenBy(state => state.Binding.InstanceKey, StringComparer.Ordinal)
            .ThenBy(state => state.Binding.Generation)
            .ToArray();

        return new CdcListBindingsStateStoreResult.Listed(states);
    }

    public async Task<CdcLatchIncidentStateStoreResult> LatchSourceHistoryLossAsync(
        CdcIncident incident,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(incident);

        CdcStateStoreFailure? incidentInputFailure = ValidateIncidentInput(incident);
        if (incidentInputFailure is not null)
        {
            return new CdcLatchIncidentStateStoreResult.StateStoreFailure(incidentInputFailure);
        }

        CdcBindingIdentity identity = incident.BindingIdentity.ToBindingIdentity();
        LocalBindingReadResult readResult = await ReadBindingStateAsync(identity, cancellationToken);
        if (readResult.Failure is not null)
        {
            return new CdcLatchIncidentStateStoreResult.StateStoreFailure(readResult.Failure);
        }

        if (readResult.Missing)
        {
            return new CdcLatchIncidentStateStoreResult.BindingMissing(identity);
        }

        CdcStoredBindingState currentState = readResult.State!.State;
        if (currentState.Binding.ToCompleteBindingIdentity() != incident.BindingIdentity)
        {
            return new CdcLatchIncidentStateStoreResult.BindingMismatch(
                CompleteIdentityMismatch(currentState.Binding, incident.BindingIdentity)
            );
        }

        CdcStateStoreFailure? bindingIncidentFailure = ValidateIncidentForBinding(
            incident,
            currentState.Binding
        );
        if (bindingIncidentFailure is not null)
        {
            return new CdcLatchIncidentStateStoreResult.StateStoreFailure(bindingIncidentFailure);
        }

        if (currentState.Incident is not null)
        {
            return new CdcLatchIncidentStateStoreResult.AlreadyLatched(currentState);
        }

        CdcStateStorePathResolution incidentPath = _pathResolver.ResolveIncidentPath(identity);
        if (!incidentPath.Succeeded)
        {
            return new CdcLatchIncidentStateStoreResult.StateStoreFailure(incidentPath.ToFailure());
        }

        CdcStateStoreFailure? directoryFailure = EnsureIncidentDirectory(incidentPath);
        if (directoryFailure is not null)
        {
            return new CdcLatchIncidentStateStoreResult.StateStoreFailure(directoryFailure);
        }

        CdcStateStoreFailure? collisionFailure = CheckPathCaseCollision(
            CdcStateStorePathResolver.IncidentsDirectoryName,
            identity.DeploymentKey,
            identity.InstanceKey,
            $"{identity.Generation}.json"
        );
        if (collisionFailure is not null)
        {
            return new CdcLatchIncidentStateStoreResult.StateStoreFailure(collisionFailure);
        }

        try
        {
            await WriteContractFileCreateNewAsync(
                incidentPath.FilePath!,
                CdcJsonContract.Serialize(incident),
                cancellationToken
            );
        }
        catch (IOException) when (File.Exists(incidentPath.FilePath!))
        {
            LocalIncidentReadResult existingIncidentResult = await ReadIncidentStateAsync(
                currentState.Binding,
                cancellationToken
            );
            return existingIncidentResult switch
            {
                { Incident: not null } => new CdcLatchIncidentStateStoreResult.AlreadyLatched(
                    currentState with
                    {
                        Incident = existingIncidentResult.Incident,
                    }
                ),
                { Failure: not null } => new CdcLatchIncidentStateStoreResult.StateStoreFailure(
                    existingIncidentResult.Failure
                ),
                _ => new CdcLatchIncidentStateStoreResult.StateStoreFailure(
                    CdcStateStoreFailure.LocalStateUnavailable(
                        incidentPath.FilePath!,
                        "CDC local incident state changed during latch."
                    )
                ),
            };
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return new CdcLatchIncidentStateStoreResult.StateStoreFailure(
                FileSystemFailure(incidentPath.FilePath!, "write incident state")
            );
        }

        CdcStateStoreFailure? permissionFailure = ApplyOwnerOnlyFilePermissions(incidentPath.FilePath!);
        if (permissionFailure is not null)
        {
            CdcStateStoreFailure? deleteFailure = DeleteIncompleteIncidentFile(incidentPath.FilePath!);
            return new CdcLatchIncidentStateStoreResult.StateStoreFailure(deleteFailure ?? permissionFailure);
        }

        LocalIncidentReadResult readBackIncident = await ReadIncidentStateAsync(
            currentState.Binding,
            cancellationToken
        );
        if (readBackIncident.Failure is not null)
        {
            return new CdcLatchIncidentStateStoreResult.StateStoreFailure(readBackIncident.Failure);
        }

        if (readBackIncident.Incident is null)
        {
            return new CdcLatchIncidentStateStoreResult.StateStoreFailure(
                CdcStateStoreFailure.LocalStateUnavailable(
                    incidentPath.FilePath!,
                    "CDC local incident state was not readable after latch."
                )
            );
        }

        return new CdcLatchIncidentStateStoreResult.Latched(
            currentState with
            {
                Incident = readBackIncident.Incident,
            }
        );
    }

    public async Task<CdcImportBindingStateStoreResult> ImportVerifiedBindingAsync(
        CdcAdoptionProof verifiedAdoptionProof,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(verifiedAdoptionProof);

        CdcStateStoreFailure? proofFailure = ValidateAdoptionProof(verifiedAdoptionProof);
        if (proofFailure is not null)
        {
            return new CdcImportBindingStateStoreResult.StateStoreFailure(proofFailure);
        }

        LocalCreateBindingResult result = await CreateOrExactMatchBindingAsync(
            verifiedAdoptionProof.Binding,
            cancellationToken
        );

        return result switch
        {
            { Outcome: LocalCreateBindingOutcome.Created, State: not null } =>
                new CdcImportBindingStateStoreResult.Imported(result.State),
            { Outcome: LocalCreateBindingOutcome.ExistingExactMatch, State: not null } =>
                new CdcImportBindingStateStoreResult.ExistingExactMatch(result.State),
            { Mismatch: not null } => new CdcImportBindingStateStoreResult.BindingMismatch(result.Mismatch),
            { Failure: not null } => new CdcImportBindingStateStoreResult.StateStoreFailure(result.Failure),
            _ => new CdcImportBindingStateStoreResult.StateStoreFailure(
                CdcStateStoreFailure.LocalStateUnavailable("$", "CDC local binding import failed.")
            ),
        };
    }

    public async Task<CdcDeleteBindingStateStoreResult> DeleteStateAfterVerifiedCleanupAsync(
        CdcCleanupProof verifiedCleanupProof,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(verifiedCleanupProof);

        CdcStateStoreFailure? proofStructureFailure = ValidateCleanupProofStructure(verifiedCleanupProof);
        if (proofStructureFailure is not null)
        {
            return new CdcDeleteBindingStateStoreResult.StateStoreFailure(proofStructureFailure);
        }

        CdcCompleteBindingIdentity completeIdentity = verifiedCleanupProof.BindingIdentity;
        CdcBindingIdentity identity = completeIdentity.ToBindingIdentity();
        LocalBindingReadResult readResult = await ReadBindingStateAsync(identity, cancellationToken);
        if (readResult.Failure is not null)
        {
            return new CdcDeleteBindingStateStoreResult.StateStoreFailure(readResult.Failure);
        }

        if (readResult.Missing)
        {
            return new CdcDeleteBindingStateStoreResult.BindingMissing(completeIdentity);
        }

        CdcStateStoreFailure? proofFailure = ValidateCleanupProofForBinding(
            verifiedCleanupProof,
            readResult.State!.State.Binding
        );
        if (proofFailure is not null)
        {
            return new CdcDeleteBindingStateStoreResult.StateStoreFailure(proofFailure);
        }

        CdcStateStorePathResolution bindingPath = _pathResolver.ResolveBindingPath(identity);
        CdcStateStorePathResolution incidentPath = _pathResolver.ResolveIncidentPath(identity);
        if (!bindingPath.Succeeded)
        {
            return new CdcDeleteBindingStateStoreResult.StateStoreFailure(bindingPath.ToFailure());
        }

        if (!incidentPath.Succeeded)
        {
            return new CdcDeleteBindingStateStoreResult.StateStoreFailure(incidentPath.ToFailure());
        }

        CdcStateStoreFailure? incidentDeleteFailure = DeleteStateFileIfPresent(
            incidentPath.FilePath!,
            "delete incident state"
        );
        if (incidentDeleteFailure is not null)
        {
            return new CdcDeleteBindingStateStoreResult.StateStoreFailure(incidentDeleteFailure);
        }

        CdcStateStoreFailure? bindingDeleteFailure = DeleteStateFile(
            bindingPath.FilePath!,
            "delete binding state"
        );
        if (bindingDeleteFailure is not null)
        {
            return new CdcDeleteBindingStateStoreResult.StateStoreFailure(bindingDeleteFailure);
        }

        return new CdcDeleteBindingStateStoreResult.Deleted(completeIdentity);
    }

    private async Task<LocalCreateBindingResult> CreateOrExactMatchBindingAsync(
        CdcBinding binding,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(binding);

        CdcStateStoreFailure? bindingInputFailure = ValidateBindingInput(binding);
        if (bindingInputFailure is not null)
        {
            return LocalCreateBindingResult.Failed(bindingInputFailure);
        }

        CdcStateStorePathResolution bindingPath = _pathResolver.ResolveBindingPath(
            binding.ToBindingIdentity()
        );
        if (!bindingPath.Succeeded)
        {
            return LocalCreateBindingResult.Failed(bindingPath.ToFailure());
        }

        CdcStateStoreFailure? directoryFailure = EnsureBindingDirectory(bindingPath);
        if (directoryFailure is not null)
        {
            return LocalCreateBindingResult.Failed(directoryFailure);
        }

        CdcStateStoreFailure? collisionFailure = CheckPathCaseCollision(
            CdcStateStorePathResolver.BindingsDirectoryName,
            binding.DeploymentKey,
            binding.InstanceKey,
            $"{binding.Generation}.json"
        );
        if (collisionFailure is not null)
        {
            return LocalCreateBindingResult.Failed(collisionFailure);
        }

        try
        {
            await WriteContractFileCreateNewAsync(
                bindingPath.FilePath!,
                CdcJsonContract.Serialize(binding),
                cancellationToken
            );
        }
        catch (IOException) when (File.Exists(bindingPath.FilePath!))
        {
            return await ExistingBindingResultAsync(binding, cancellationToken);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return LocalCreateBindingResult.Failed(
                FileSystemFailure(bindingPath.FilePath!, "write binding state")
            );
        }

        CdcStateStoreFailure? permissionFailure = ApplyOwnerOnlyFilePermissions(bindingPath.FilePath!);
        if (permissionFailure is not null)
        {
            CdcStateStoreFailure? deleteFailure = DeleteIncompleteBindingFile(bindingPath.FilePath!);
            return LocalCreateBindingResult.Failed(deleteFailure ?? permissionFailure);
        }

        LocalBindingReadResult readBack = await ReadBindingStateAsync(
            binding.ToBindingIdentity(),
            cancellationToken
        );
        if (readBack.Failure is not null)
        {
            return LocalCreateBindingResult.Failed(readBack.Failure);
        }

        if (readBack.Missing)
        {
            return LocalCreateBindingResult.Failed(
                CdcStateStoreFailure.LocalStateUnavailable(
                    bindingPath.FilePath!,
                    "CDC local binding state was not readable after create."
                )
            );
        }

        CdcBindingExactMatchResult exactMatch = CdcBindingExactMatch.Compare(
            binding,
            readBack.State!.BindingJson
        );
        return exactMatch.Succeeded
            ? LocalCreateBindingResult.Created(readBack.State.State)
            : LocalCreateBindingResult.Failed(ToInvalidPersistedBindingFailure(exactMatch));
    }

    private async Task<LocalCreateBindingResult> ExistingBindingResultAsync(
        CdcBinding expectedBinding,
        CancellationToken cancellationToken
    )
    {
        LocalBindingReadResult readResult = await ReadBindingStateAsync(
            expectedBinding.ToBindingIdentity(),
            cancellationToken
        );
        if (readResult.Failure is not null)
        {
            return LocalCreateBindingResult.Failed(readResult.Failure);
        }

        if (readResult.Missing)
        {
            return LocalCreateBindingResult.Failed(
                CdcStateStoreFailure.LocalStateUnavailable(
                    "$",
                    "CDC local binding state changed during create."
                )
            );
        }

        CdcBindingExactMatchResult exactMatch = CdcBindingExactMatch.Compare(
            expectedBinding,
            readResult.State!.BindingJson
        );
        return exactMatch.Succeeded
            ? LocalCreateBindingResult.Existing(readResult.State.State)
            : LocalCreateBindingResult.Mismatched(exactMatch.ToMismatch());
    }

    private async Task<CdcStateStoreFailure?> ReadDeploymentBindingsAsync(
        CdcDeploymentStateStorePathResolution deploymentPath,
        Dictionary<CdcBindingIdentity, CdcStoredBindingState> statesByIdentity,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<string>? instanceDirectories = EnumerateFileSystemEntries(
            deploymentPath.BindingDeploymentDirectoryPath!,
            out CdcStateStoreFailure? enumerationFailure
        );
        if (enumerationFailure is not null)
        {
            return enumerationFailure;
        }

        foreach (string instanceDirectory in instanceDirectories!.Order(StringComparer.Ordinal))
        {
            CdcStateStoreFailure? instanceFailure = ValidateDirectoryEntry(
                instanceDirectory,
                "$.instanceKey",
                "instanceKey"
            );
            if (instanceFailure is not null)
            {
                return instanceFailure;
            }

            string instanceKey = Path.GetFileName(instanceDirectory);
            IReadOnlyList<string>? bindingFiles = EnumerateFileSystemEntries(
                instanceDirectory,
                out CdcStateStoreFailure? bindingEnumerationFailure
            );
            if (bindingEnumerationFailure is not null)
            {
                return bindingEnumerationFailure;
            }

            foreach (string bindingFile in bindingFiles!.Order(StringComparer.Ordinal))
            {
                CdcStateStoreFailure? regularFileFailure = ValidateRegularFile(bindingFile);
                if (regularFileFailure is not null)
                {
                    return regularFileFailure;
                }

                CdcStateStoreFailure? permissionFailure = ValidateOwnerOnlyFilePermissions(bindingFile);
                if (permissionFailure is not null)
                {
                    return permissionFailure;
                }

                if (!TryParseGenerationFileName(bindingFile, out long generation))
                {
                    return CdcStateStoreFailure.LocalStateUnavailable(
                        bindingFile,
                        "CDC local binding state file name is invalid."
                    );
                }

                LocalBindingFileReadResult bindingRead = await ReadBindingFileAsync(
                    bindingFile,
                    cancellationToken
                );
                if (bindingRead.Failure is not null)
                {
                    return bindingRead.Failure;
                }

                CdcBinding binding = bindingRead.Binding!;
                if (
                    !string.Equals(
                        binding.DeploymentKey,
                        deploymentPath.DeploymentKey,
                        StringComparison.Ordinal
                    )
                    || !string.Equals(binding.InstanceKey, instanceKey, StringComparison.Ordinal)
                    || binding.Generation != generation
                )
                {
                    return CdcStateStoreFailure.InvalidPersistedBinding([
                        IdentityMismatchDiagnostic("$.bindingIdentity", "binding", "state-store path"),
                    ]);
                }

                CdcBindingIdentity identity = binding.ToBindingIdentity();
                if (!statesByIdentity.TryAdd(identity, new(binding, null)))
                {
                    return CdcStateStoreFailure.LocalStateUnavailable(
                        bindingFile,
                        "CDC local binding state contains duplicate files for one identity."
                    );
                }
            }
        }

        foreach (CdcBindingIdentity identity in statesByIdentity.Keys.ToArray())
        {
            CdcBinding binding = statesByIdentity[identity].Binding;
            LocalIncidentReadResult incidentRead = await ReadIncidentStateAsync(binding, cancellationToken);
            if (incidentRead.Failure is not null)
            {
                return incidentRead.Failure;
            }

            statesByIdentity[identity] = statesByIdentity[identity] with { Incident = incidentRead.Incident };
        }

        return null;
    }

    private async Task<CdcStateStoreFailure?> ValidateDeploymentIncidentsAsync(
        CdcDeploymentStateStorePathResolution deploymentPath,
        HashSet<CdcBindingIdentity> bindingIdentities,
        CancellationToken cancellationToken
    )
    {
        CdcStateStoreFailure? collisionFailure = CheckPathCaseCollision(
            CdcStateStorePathResolver.IncidentsDirectoryName,
            deploymentPath.DeploymentKey!
        );
        if (collisionFailure is not null)
        {
            return collisionFailure;
        }

        if (!Directory.Exists(deploymentPath.IncidentDeploymentDirectoryPath!))
        {
            return null;
        }

        IReadOnlyList<string>? instanceDirectories = EnumerateFileSystemEntries(
            deploymentPath.IncidentDeploymentDirectoryPath!,
            out CdcStateStoreFailure? enumerationFailure
        );
        if (enumerationFailure is not null)
        {
            return enumerationFailure;
        }

        foreach (string instanceDirectory in instanceDirectories!.Order(StringComparer.Ordinal))
        {
            CdcStateStoreFailure? instanceFailure = ValidateDirectoryEntry(
                instanceDirectory,
                "$.instanceKey",
                "instanceKey"
            );
            if (instanceFailure is not null)
            {
                return instanceFailure;
            }

            string instanceKey = Path.GetFileName(instanceDirectory);
            IReadOnlyList<string>? incidentFiles = EnumerateFileSystemEntries(
                instanceDirectory,
                out CdcStateStoreFailure? incidentEnumerationFailure
            );
            if (incidentEnumerationFailure is not null)
            {
                return incidentEnumerationFailure;
            }

            foreach (string incidentFile in incidentFiles!.Order(StringComparer.Ordinal))
            {
                CdcStateStoreFailure? regularFileFailure = ValidateRegularFile(incidentFile);
                if (regularFileFailure is not null)
                {
                    return regularFileFailure;
                }

                if (!TryParseGenerationFileName(incidentFile, out long generation))
                {
                    return CdcStateStoreFailure.LocalStateUnavailable(
                        incidentFile,
                        "CDC local incident state file name is invalid."
                    );
                }

                LocalIncidentFileReadResult incidentRead = await ReadIncidentFileAsync(
                    incidentFile,
                    cancellationToken
                );
                if (incidentRead.Failure is not null)
                {
                    return incidentRead.Failure;
                }

                CdcIncident incident = incidentRead.Incident!;
                CdcBindingIdentity identity = incident.BindingIdentity.ToBindingIdentity();
                if (
                    !string.Equals(
                        identity.DeploymentKey,
                        deploymentPath.DeploymentKey,
                        StringComparison.Ordinal
                    )
                    || !string.Equals(identity.InstanceKey, instanceKey, StringComparison.Ordinal)
                    || identity.Generation != generation
                    || !bindingIdentities.Contains(identity)
                )
                {
                    return CdcStateStoreFailure.InvalidPersistedIncident([
                        IdentityMismatchDiagnostic("$.bindingIdentity", "incident", "binding state"),
                    ]);
                }
            }
        }

        return null;
    }

    private async Task<LocalBindingReadResult> ReadBindingStateAsync(
        CdcBindingIdentity identity,
        CancellationToken cancellationToken
    )
    {
        CdcStateStorePathResolution bindingPath = _pathResolver.ResolveBindingPath(identity);
        if (!bindingPath.Succeeded)
        {
            return LocalBindingReadResult.Failed(bindingPath.ToFailure());
        }

        CdcStateStoreFailure? collisionFailure = CheckPathCaseCollision(
            CdcStateStorePathResolver.BindingsDirectoryName,
            identity.DeploymentKey,
            identity.InstanceKey,
            $"{identity.Generation}.json"
        );
        if (collisionFailure is not null)
        {
            return LocalBindingReadResult.Failed(collisionFailure);
        }

        if (!File.Exists(bindingPath.FilePath!))
        {
            return LocalBindingReadResult.MissingBinding();
        }

        CdcStateStoreFailure? regularFileFailure = ValidateRegularFile(bindingPath.FilePath!);
        if (regularFileFailure is not null)
        {
            return LocalBindingReadResult.Failed(regularFileFailure);
        }

        CdcStateStoreFailure? permissionFailure = ValidateOwnerOnlyFilePermissions(bindingPath.FilePath!);
        if (permissionFailure is not null)
        {
            return LocalBindingReadResult.Failed(permissionFailure);
        }

        LocalBindingFileReadResult bindingRead = await ReadBindingFileAsync(
            bindingPath.FilePath!,
            cancellationToken
        );
        if (bindingRead.Failure is not null)
        {
            return LocalBindingReadResult.Failed(bindingRead.Failure);
        }

        CdcBinding binding = bindingRead.Binding!;
        if (binding.ToBindingIdentity() != identity)
        {
            return LocalBindingReadResult.Failed(
                CdcStateStoreFailure.InvalidPersistedBinding([
                    IdentityMismatchDiagnostic("$.bindingIdentity", "binding", "state-store path"),
                ])
            );
        }

        LocalIncidentReadResult incidentRead = await ReadIncidentStateAsync(binding, cancellationToken);
        if (incidentRead.Failure is not null)
        {
            return LocalBindingReadResult.Failed(incidentRead.Failure);
        }

        return LocalBindingReadResult.Found(
            new(new(binding, incidentRead.Incident), bindingRead.BindingJson!)
        );
    }

    private static async Task<LocalBindingFileReadResult> ReadBindingFileAsync(
        string filePath,
        CancellationToken cancellationToken
    )
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(filePath, cancellationToken);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return LocalBindingFileReadResult.Failed(FileSystemFailure(filePath, "read binding state"));
        }

        IReadOnlyList<CdcDiagnostic> duplicateDiagnostics = DetectDuplicateRootProperties(json, "binding");
        if (duplicateDiagnostics.Count != 0)
        {
            return LocalBindingFileReadResult.Failed(
                CdcStateStoreFailure.InvalidPersistedBinding(duplicateDiagnostics)
            );
        }

        CdcContractReadResult<CdcBinding> readResult = CdcJsonContract.Deserialize<CdcBinding>(json);
        if (!readResult.Succeeded)
        {
            return LocalBindingFileReadResult.Failed(
                CdcStateStoreFailure.InvalidPersistedBinding(readResult.Diagnostics)
            );
        }

        CdcContractValidationResult bindingValidation = CdcBindingValidator.Validate(readResult.Contract!);
        return bindingValidation.Succeeded
            ? LocalBindingFileReadResult.Read(readResult.Contract!, json)
            : LocalBindingFileReadResult.Failed(
                CdcStateStoreFailure.InvalidPersistedBinding(bindingValidation.Diagnostics)
            );
    }

    private async Task<LocalIncidentReadResult> ReadIncidentStateAsync(
        CdcBinding binding,
        CancellationToken cancellationToken
    )
    {
        CdcStateStorePathResolution incidentPath = _pathResolver.ResolveIncidentPath(
            binding.ToBindingIdentity()
        );
        if (!incidentPath.Succeeded)
        {
            return LocalIncidentReadResult.Failed(incidentPath.ToFailure());
        }

        CdcStateStoreFailure? collisionFailure = CheckPathCaseCollision(
            CdcStateStorePathResolver.IncidentsDirectoryName,
            binding.DeploymentKey,
            binding.InstanceKey,
            $"{binding.Generation}.json"
        );
        if (collisionFailure is not null)
        {
            return LocalIncidentReadResult.Failed(collisionFailure);
        }

        if (!File.Exists(incidentPath.FilePath!))
        {
            return LocalIncidentReadResult.Absent();
        }

        CdcStateStoreFailure? regularFileFailure = ValidateRegularFile(incidentPath.FilePath!);
        if (regularFileFailure is not null)
        {
            return LocalIncidentReadResult.Failed(regularFileFailure);
        }

        LocalIncidentFileReadResult incidentRead = await ReadIncidentFileAsync(
            incidentPath.FilePath!,
            cancellationToken
        );
        if (incidentRead.Failure is not null)
        {
            return LocalIncidentReadResult.Failed(incidentRead.Failure);
        }

        CdcIncident incident = incidentRead.Incident!;
        if (incident.BindingIdentity != binding.ToCompleteBindingIdentity())
        {
            return LocalIncidentReadResult.Failed(
                CdcStateStoreFailure.InvalidPersistedIncident([
                    IdentityMismatchDiagnostic("$.bindingIdentity", "incident", "binding state"),
                ])
            );
        }

        CdcStateStoreFailure? validationFailure = ValidateIncidentForBinding(incident, binding);
        if (validationFailure is not null)
        {
            return LocalIncidentReadResult.Failed(
                CdcStateStoreFailure.InvalidPersistedIncident(validationFailure.Diagnostics)
            );
        }

        return LocalIncidentReadResult.Read(incident);
    }

    private static async Task<LocalIncidentFileReadResult> ReadIncidentFileAsync(
        string filePath,
        CancellationToken cancellationToken
    )
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(filePath, cancellationToken);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return LocalIncidentFileReadResult.Failed(FileSystemFailure(filePath, "read incident state"));
        }

        IReadOnlyList<CdcDiagnostic> duplicateDiagnostics = DetectDuplicateRootProperties(json, "incident");
        if (duplicateDiagnostics.Count != 0)
        {
            return LocalIncidentFileReadResult.Failed(
                CdcStateStoreFailure.InvalidPersistedIncident(duplicateDiagnostics)
            );
        }

        CdcContractReadResult<CdcIncident> readResult = CdcJsonContract.Deserialize<CdcIncident>(json);
        if (!readResult.Succeeded)
        {
            return LocalIncidentFileReadResult.Failed(
                CdcStateStoreFailure.InvalidPersistedIncident(readResult.Diagnostics)
            );
        }

        CdcContractValidationResult validationResult = CdcIncidentValidator.Validate(
            readResult.Contract!,
            DateTimeOffset.UtcNow
        );

        return validationResult.Succeeded
            ? LocalIncidentFileReadResult.Read(readResult.Contract!)
            : LocalIncidentFileReadResult.Failed(
                CdcStateStoreFailure.InvalidPersistedIncident(validationResult.Diagnostics)
            );
    }

    private CdcStateStoreFailure? EnsureBindingDirectory(CdcStateStorePathResolution path) =>
        EnsureDirectoryTree([
            CdcStateStorePathResolver.BindingsDirectoryName,
            path.DeploymentKey!,
            path.InstanceKey!,
        ]);

    private CdcStateStoreFailure? EnsureIncidentDirectory(CdcStateStorePathResolution path) =>
        EnsureDirectoryTree([
            CdcStateStorePathResolver.IncidentsDirectoryName,
            path.DeploymentKey!,
            path.InstanceKey!,
        ]);

    private CdcStateStoreFailure? EnsureDirectoryTree(IReadOnlyList<string> segments)
    {
        CdcStateStoreFailure? rootFailure = EnsureDirectory(_pathResolver.RootPath);
        if (rootFailure is not null)
        {
            return rootFailure;
        }

        string currentPath = _pathResolver.RootPath;
        foreach (string segment in segments)
        {
            CdcStateStoreFailure? collisionFailure = CheckCaseCollision(currentPath, segment);
            if (collisionFailure is not null)
            {
                return collisionFailure;
            }

            currentPath = Path.Combine(currentPath, segment);
            CdcStateStoreFailure? directoryFailure = EnsureDirectory(currentPath);
            if (directoryFailure is not null)
            {
                return directoryFailure;
            }
        }

        return null;
    }

    private CdcStateStoreFailure? EnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return FileSystemFailure(path, "create state directory");
        }

        CdcStateStoreFailure? directoryFailure = ValidateDirectory(path);
        if (directoryFailure is not null)
        {
            return directoryFailure;
        }

        return ApplyOwnerOnlyDirectoryPermissions(path);
    }

    private CdcStateStoreFailure? CheckPathCaseCollision(params string[] segments)
    {
        string currentPath = _pathResolver.RootPath;
        foreach (string segment in segments)
        {
            if (!Directory.Exists(currentPath))
            {
                return null;
            }

            CdcStateStoreFailure? collisionFailure = CheckCaseCollision(currentPath, segment);
            if (collisionFailure is not null)
            {
                return collisionFailure;
            }

            currentPath = Path.Combine(currentPath, segment);
        }

        return null;
    }

    private static CdcStateStoreFailure? CheckCaseCollision(string parentDirectory, string segment)
    {
        try
        {
            foreach (string entryPath in Directory.EnumerateFileSystemEntries(parentDirectory))
            {
                string entryName = Path.GetFileName(entryPath);
                if (
                    string.Equals(entryName, segment, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(entryName, segment, StringComparison.Ordinal)
                )
                {
                    return CdcStateStoreFailure.LocalStateUnavailable(
                        entryPath,
                        "CDC local state path has a case-colliding entry."
                    );
                }
            }
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return FileSystemFailure(parentDirectory, "inspect state directory");
        }

        return null;
    }

    private static IReadOnlyList<string>? EnumerateFileSystemEntries(
        string directoryPath,
        out CdcStateStoreFailure? failure
    )
    {
        failure = null;

        CdcStateStoreFailure? directoryFailure = ValidateDirectory(directoryPath);
        if (directoryFailure is not null)
        {
            failure = directoryFailure;
            return null;
        }

        try
        {
            return Directory.EnumerateFileSystemEntries(directoryPath).ToArray();
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            failure = FileSystemFailure(directoryPath, "list state directory");
            return null;
        }
    }

    private static CdcStateStoreFailure? ValidateDirectoryEntry(
        string directoryPath,
        string path,
        string fieldName
    )
    {
        CdcStateStoreFailure? directoryFailure = ValidateDirectory(directoryPath);
        if (directoryFailure is not null)
        {
            return directoryFailure;
        }

        CdcDiagnosticCollector diagnostics = new();
        CdcKafkaSafeTokenValidator.Validate(Path.GetFileName(directoryPath), path, fieldName, diagnostics);
        return diagnostics.HasDiagnostics
            ? CdcStateStoreFailure.LocalStateUnavailable(
                directoryPath,
                "CDC local state directory name is invalid."
            )
            : null;
    }

    private static CdcStateStoreFailure? ValidateDirectory(string path)
    {
        FileInfo fileInfo = new(path);
        if (fileInfo.Exists)
        {
            return CdcStateStoreFailure.LocalStateUnavailable(
                path,
                "CDC local state path is an unexpected non-directory file."
            );
        }

        DirectoryInfo directoryInfo = new(path);
        directoryInfo.Refresh();
        if (!directoryInfo.Exists)
        {
            return CdcStateStoreFailure.LocalStateUnavailable(
                path,
                "CDC local state directory is unavailable."
            );
        }

        return IsSymbolicLink(directoryInfo)
            ? CdcStateStoreFailure.LocalStateUnavailable(
                path,
                "CDC local state directory must not be a symlink."
            )
            : null;
    }

    private static CdcStateStoreFailure? ValidateRegularFile(string path)
    {
        if (Directory.Exists(path))
        {
            return CdcStateStoreFailure.LocalStateUnavailable(
                path,
                "CDC local state file is an unexpected directory."
            );
        }

        FileInfo fileInfo = new(path);
        fileInfo.Refresh();
        if (!fileInfo.Exists)
        {
            return CdcStateStoreFailure.LocalStateUnavailable(path, "CDC local state file is unavailable.");
        }

        return IsSymbolicLink(fileInfo)
            ? CdcStateStoreFailure.LocalStateUnavailable(path, "CDC local state file must not be a symlink.")
            : null;
    }

    private CdcStateStoreFailure? ApplyOwnerOnlyDirectoryPermissions(string path)
    {
        CdcLocalStateStorePermissionResult result = _permissions.ApplyOwnerOnlyDirectory(path);
        return result.Succeeded || result.Unsupported
            ? null
            : CdcStateStoreFailure.LocalStateUnavailable(
                path,
                result.Message ?? "CDC local state directory permissions could not be applied."
            );
    }

    private CdcStateStoreFailure? ApplyOwnerOnlyFilePermissions(string path)
    {
        CdcLocalStateStorePermissionResult result = _permissions.ApplyOwnerOnlyFile(path);
        return result.Succeeded || result.Unsupported
            ? null
            : CdcStateStoreFailure.LocalStateUnavailable(
                path,
                result.Message ?? "CDC local state file permissions could not be applied."
            );
    }

    private CdcStateStoreFailure? ValidateOwnerOnlyFilePermissions(string path)
    {
        CdcLocalStateStorePermissionResult result = _permissions.ValidateOwnerOnlyFile(path);
        return result.Succeeded || result.Unsupported
            ? null
            : CdcStateStoreFailure.LocalStateUnavailable(
                path,
                result.Message ?? "CDC local state file permissions are not owner-only."
            );
    }

    private static async Task WriteContractFileCreateNewAsync(
        string filePath,
        string payload,
        CancellationToken cancellationToken
    )
    {
        await using FileStream fileStream = new(
            filePath,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
            }
        );
        await using StreamWriter writer = new(fileStream, new UTF8Encoding(false));
        await writer.WriteAsync(payload.AsMemory(), cancellationToken);
    }

    private static IReadOnlyList<CdcDiagnostic> DetectDuplicateRootProperties(
        string json,
        string contractName
    )
    {
        CdcDiagnosticCollector diagnostics = new();
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                }
            );

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            HashSet<string> rootProperties = new(StringComparer.Ordinal);
            foreach (
                string propertyName in document
                    .RootElement.EnumerateObject()
                    .Select(property => property.Name)
            )
            {
                if (!rootProperties.Add(propertyName))
                {
                    diagnostics.MalformedPayload(
                        $"$.{propertyName}",
                        $"CDC persisted {contractName} state contains duplicate JSON property."
                    );
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return diagnostics.Diagnostics;
    }

    private static bool TryParseGenerationFileName(string filePath, out long generation)
    {
        generation = 0;

        string fileName = Path.GetFileName(filePath);
        if (!fileName.EndsWith(".json", StringComparison.Ordinal))
        {
            return false;
        }

        string value = fileName[..^".json".Length];
        if (value.Length == 0 || (value.Length > 1 && value[0] == '0'))
        {
            return false;
        }

        return value.All(character => character is >= '0' and <= '9')
            && long.TryParse(value, out generation)
            && generation > 0;
    }

    private static CdcStateStoreFailure? ValidateIncidentInput(CdcIncident incident)
    {
        CdcContractValidationResult validationResult = CdcIncidentValidator.Validate(
            incident,
            DateTimeOffset.UtcNow
        );

        return validationResult.Succeeded
            ? null
            : CdcStateStoreFailure.InvalidOperation(validationResult.Diagnostics);
    }

    private static CdcStateStoreFailure? ValidateBindingInput(CdcBinding binding)
    {
        CdcContractValidationResult validationResult = CdcBindingValidator.Validate(binding);

        return validationResult.Succeeded
            ? null
            : CdcStateStoreFailure.InvalidOperation(validationResult.Diagnostics);
    }

    private static CdcStateStoreFailure? ValidateIncidentForBinding(CdcIncident incident, CdcBinding binding)
    {
        CdcContractValidationResult validationResult = CdcIncidentValidator.ValidateForBinding(
            incident,
            binding,
            DateTimeOffset.UtcNow
        );

        return validationResult.Succeeded
            ? null
            : CdcStateStoreFailure.InvalidOperation(validationResult.Diagnostics);
    }

    private static CdcStateStoreFailure? ValidateAdoptionProof(CdcAdoptionProof proof)
    {
        CdcContractValidationResult validationResult = CdcAdoptionProofValidator.Validate(
            proof,
            DateTimeOffset.UtcNow
        );

        return validationResult.Succeeded
            ? null
            : CdcStateStoreFailure.InvalidOperation(validationResult.Diagnostics);
    }

    private static CdcStateStoreFailure? ValidateCleanupProofStructure(CdcCleanupProof proof)
    {
        CdcContractValidationResult validationResult = CdcCleanupProofValidator.ValidateStructure(
            proof,
            DateTimeOffset.UtcNow
        );

        return validationResult.Succeeded
            ? null
            : CdcStateStoreFailure.InvalidOperation(validationResult.Diagnostics);
    }

    private static CdcStateStoreFailure? ValidateCleanupProofForBinding(
        CdcCleanupProof proof,
        CdcBinding binding
    )
    {
        CdcContractValidationResult validationResult = CdcCleanupProofValidator.Validate(
            proof,
            binding,
            DateTimeOffset.UtcNow
        );

        return validationResult.Succeeded
            ? null
            : CdcStateStoreFailure.InvalidOperation(validationResult.Diagnostics);
    }

    private CdcStateStoreFailure? DeleteIncompleteBindingFile(string filePath) =>
        DeleteStateFileIfPresent(filePath, "remove incomplete binding state");

    private CdcStateStoreFailure? DeleteIncompleteIncidentFile(string filePath) =>
        DeleteStateFileIfPresent(filePath, "remove incomplete incident state");

    private CdcStateStoreFailure? DeleteStateFileIfPresent(string filePath, string action)
    {
        try
        {
            if (_fileSystem.FileExists(filePath))
            {
                _fileSystem.DeleteFile(filePath);
            }

            return null;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return FileSystemFailure(filePath, action);
        }
    }

    private CdcStateStoreFailure? DeleteStateFile(string filePath, string action)
    {
        try
        {
            _fileSystem.DeleteFile(filePath);
            return null;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return FileSystemFailure(filePath, action);
        }
    }

    private static CdcBindingMismatch CompleteIdentityMismatch(
        CdcBinding persistedBinding,
        CdcCompleteBindingIdentity expectedIdentity
    ) =>
        new(
            persistedBinding,
            persistedBinding,
            [
                new(
                    CdcBindingFieldDifferenceKind.DifferentValue,
                    "bindingIdentity",
                    expectedIdentity.ToString(),
                    persistedBinding.ToCompleteBindingIdentity().ToString()
                ),
            ]
        );

    private static CdcStateStoreFailure ToInvalidPersistedBindingFailure(
        CdcBindingExactMatchResult exactMatch
    )
    {
        IReadOnlyList<CdcDiagnostic> diagnostics =
            exactMatch.Diagnostics.Count != 0
                ? exactMatch.Diagnostics
                : exactMatch
                    .Differences.Select(difference => new CdcDiagnostic(
                        CdcDiagnosticCategory.MalformedPayload,
                        $"$.{difference.FieldName}",
                        "CDC persisted binding state did not match the expected immutable field."
                    ))
                    .ToArray();

        return CdcStateStoreFailure.InvalidPersistedBinding(diagnostics);
    }

    private static CdcDiagnostic IdentityMismatchDiagnostic(
        string path,
        string contractName,
        string expectedLocation
    ) =>
        new(
            CdcDiagnosticCategory.MalformedPayload,
            path,
            $"CDC persisted {contractName} identity does not match the {expectedLocation}."
        );

    private static CdcStateStoreFailure FileSystemFailure(string path, string action) =>
        CdcStateStoreFailure.LocalStateUnavailable(path, $"CDC local state store could not {action}.");

    private static bool IsSymbolicLink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static bool IsFileSystemException(Exception exception) =>
        exception
            is IOException
                or UnauthorizedAccessException
                or SecurityException
                or NotSupportedException
                or ArgumentException;

    private enum LocalCreateBindingOutcome
    {
        Created,
        ExistingExactMatch,
        BindingMismatch,
        StateStoreFailure,
    }

    private sealed record LocalCreateBindingResult(
        LocalCreateBindingOutcome Outcome,
        CdcStoredBindingState? State,
        CdcBindingMismatch? Mismatch,
        CdcStateStoreFailure? Failure
    )
    {
        public static LocalCreateBindingResult Created(CdcStoredBindingState state) =>
            new(LocalCreateBindingOutcome.Created, state, null, null);

        public static LocalCreateBindingResult Existing(CdcStoredBindingState state) =>
            new(LocalCreateBindingOutcome.ExistingExactMatch, state, null, null);

        public static LocalCreateBindingResult Mismatched(CdcBindingMismatch mismatch) =>
            new(LocalCreateBindingOutcome.BindingMismatch, null, mismatch, null);

        public static LocalCreateBindingResult Failed(CdcStateStoreFailure failure) =>
            new(LocalCreateBindingOutcome.StateStoreFailure, null, null, failure);
    }

    private sealed record LocalStoredBindingState(CdcStoredBindingState State, string BindingJson);

    private sealed record LocalBindingReadResult(
        LocalStoredBindingState? State,
        bool Missing,
        CdcStateStoreFailure? Failure
    )
    {
        public static LocalBindingReadResult Found(LocalStoredBindingState state) => new(state, false, null);

        public static LocalBindingReadResult MissingBinding() => new(null, true, null);

        public static LocalBindingReadResult Failed(CdcStateStoreFailure failure) =>
            new(null, false, failure);
    }

    private sealed record LocalBindingFileReadResult(
        CdcBinding? Binding,
        string? BindingJson,
        CdcStateStoreFailure? Failure
    )
    {
        public static LocalBindingFileReadResult Read(CdcBinding binding, string bindingJson) =>
            new(binding, bindingJson, null);

        public static LocalBindingFileReadResult Failed(CdcStateStoreFailure failure) =>
            new(null, null, failure);
    }

    private sealed record LocalIncidentReadResult(CdcIncident? Incident, CdcStateStoreFailure? Failure)
    {
        public static LocalIncidentReadResult Read(CdcIncident incident) => new(incident, null);

        public static LocalIncidentReadResult Absent() => new(null, null);

        public static LocalIncidentReadResult Failed(CdcStateStoreFailure failure) => new(null, failure);
    }

    private sealed record LocalIncidentFileReadResult(CdcIncident? Incident, CdcStateStoreFailure? Failure)
    {
        public static LocalIncidentFileReadResult Read(CdcIncident incident) => new(incident, null);

        public static LocalIncidentFileReadResult Failed(CdcStateStoreFailure failure) => new(null, failure);
    }
}

internal sealed record CdcStateStorePathResolution(
    string? RootPath,
    string? DeploymentKey,
    string? InstanceKey,
    long? Generation,
    string? FilePath,
    IReadOnlyList<CdcDiagnostic> Diagnostics
)
{
    public bool Succeeded => FilePath is not null && Diagnostics.Count == 0;

    public CdcStateStoreFailure ToFailure() =>
        new(
            CdcStateStoreFailureKind.LocalStateUnavailable,
            "CDC local state-store path is invalid.",
            Diagnostics
        );
}

internal sealed record CdcDeploymentStateStorePathResolution(
    string? RootPath,
    string? DeploymentKey,
    string? BindingDeploymentDirectoryPath,
    string? IncidentDeploymentDirectoryPath,
    IReadOnlyList<CdcDiagnostic> Diagnostics
)
{
    public bool Succeeded =>
        BindingDeploymentDirectoryPath is not null
        && IncidentDeploymentDirectoryPath is not null
        && Diagnostics.Count == 0;

    public CdcStateStoreFailure ToFailure() =>
        new(
            CdcStateStoreFailureKind.LocalStateUnavailable,
            "CDC local deployment state-store path is invalid.",
            Diagnostics
        );
}

internal sealed class CdcStateStorePathResolver
{
    public const string BindingsDirectoryName = "bindings";
    public const string IncidentsDirectoryName = "incidents";

    public CdcStateStorePathResolver(string rootPath = LocalCdcBindingStateStore.DefaultRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        RootPath = Path.GetFullPath(rootPath);
    }

    public string RootPath { get; }

    public CdcStateStorePathResolution ResolveBindingPath(CdcBindingIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return ResolveFilePath(identity, BindingsDirectoryName);
    }

    public CdcStateStorePathResolution ResolveIncidentPath(CdcBindingIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return ResolveFilePath(identity, IncidentsDirectoryName);
    }

    public CdcDeploymentStateStorePathResolution ResolveDeploymentPath(string? deploymentKey)
    {
        CdcDiagnosticCollector diagnostics = new();
        string? validatedDeploymentKey = CdcKafkaSafeTokenValidator.Validate(
            deploymentKey,
            "$.deploymentKey",
            "deploymentKey",
            diagnostics
        );
        if (diagnostics.HasDiagnostics || validatedDeploymentKey is null)
        {
            return new(null, null, null, null, diagnostics.Diagnostics);
        }

        return new(
            RootPath,
            validatedDeploymentKey,
            Path.Combine(RootPath, BindingsDirectoryName, validatedDeploymentKey),
            Path.Combine(RootPath, IncidentsDirectoryName, validatedDeploymentKey),
            []
        );
    }

    private CdcStateStorePathResolution ResolveFilePath(
        CdcBindingIdentity identity,
        string stateDirectoryName
    )
    {
        CdcContractValidationResult validationResult = CdcTargetValidator.ValidateBindingIdentity(identity);
        if (!validationResult.Succeeded)
        {
            return new(null, null, null, null, null, validationResult.Diagnostics);
        }

        return new(
            RootPath,
            identity.DeploymentKey,
            identity.InstanceKey,
            identity.Generation,
            Path.Combine(
                RootPath,
                stateDirectoryName,
                identity.DeploymentKey,
                identity.InstanceKey,
                $"{identity.Generation}.json"
            ),
            []
        );
    }
}

internal interface ICdcLocalStateStorePermissions
{
    CdcLocalStateStorePermissionResult ApplyOwnerOnlyDirectory(string path);

    CdcLocalStateStorePermissionResult ApplyOwnerOnlyFile(string path);

    CdcLocalStateStorePermissionResult ValidateOwnerOnlyFile(string path);
}

internal sealed record CdcLocalStateStorePermissionResult(bool Succeeded, bool Unsupported, string? Message)
{
    public static CdcLocalStateStorePermissionResult Success { get; } = new(true, false, null);

    public static CdcLocalStateStorePermissionResult UnsupportedPlatform { get; } = new(false, true, null);

    public static CdcLocalStateStorePermissionResult Failure(string message) => new(false, false, message);
}

internal sealed class CdcLocalStateStorePermissions : ICdcLocalStateStorePermissions
{
    private const UnixFileMode OwnerOnlyDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode OwnerOnlyFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static CdcLocalStateStorePermissions Current { get; } = new();

    public CdcLocalStateStorePermissionResult ApplyOwnerOnlyDirectory(string path) =>
        ApplyOwnerOnlyMode(path, OwnerOnlyDirectoryMode);

    public CdcLocalStateStorePermissionResult ApplyOwnerOnlyFile(string path) =>
        ApplyOwnerOnlyMode(path, OwnerOnlyFileMode);

    public CdcLocalStateStorePermissionResult ValidateOwnerOnlyFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return CdcLocalStateStorePermissionResult.UnsupportedPlatform;
        }

        try
        {
#pragma warning disable CA1416
            UnixFileMode mode = File.GetUnixFileMode(path);
#pragma warning restore CA1416
            return mode == OwnerOnlyFileMode
                ? CdcLocalStateStorePermissionResult.Success
                : CdcLocalStateStorePermissionResult.Failure(
                    "CDC local state file permissions are not owner-only."
                );
        }
        catch (PlatformNotSupportedException)
        {
            return CdcLocalStateStorePermissionResult.UnsupportedPlatform;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return CdcLocalStateStorePermissionResult.Failure(
                "CDC local state owner-only permissions could not be validated."
            );
        }
    }

    private static CdcLocalStateStorePermissionResult ApplyOwnerOnlyMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return CdcLocalStateStorePermissionResult.UnsupportedPlatform;
        }

        try
        {
#pragma warning disable CA1416
            File.SetUnixFileMode(path, mode);
#pragma warning restore CA1416
            return CdcLocalStateStorePermissionResult.Success;
        }
        catch (PlatformNotSupportedException)
        {
            return CdcLocalStateStorePermissionResult.UnsupportedPlatform;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return CdcLocalStateStorePermissionResult.Failure(
                "CDC local state owner-only permissions could not be applied."
            );
        }
    }
}

internal interface ICdcLocalStateStoreFileSystem
{
    bool FileExists(string path);

    void DeleteFile(string path);
}

internal sealed class CdcLocalStateStoreFileSystem : ICdcLocalStateStoreFileSystem
{
    public static CdcLocalStateStoreFileSystem Current { get; } = new();

    public bool FileExists(string path) => File.Exists(path);

    public void DeleteFile(string path) => File.Delete(path);
}
