// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <summary>
/// Records the provider exceptions the production authorization path raised during a test, so a scenario can
/// assert that a wire response was decoded from a genuine <c>Microsoft.Data.SqlClient.SqlException</c>.
/// </summary>
/// <remarks>
/// The authorization executor probes the extractor from more than one exception filter, so the same exception
/// instance is normally recorded several times; assert on the distinct set.
/// </remarks>
public sealed class ApiIntegrationProviderFailureRecorder
{
    private readonly object _sync = new();
    private readonly List<DbException> _providerFailures = [];

    public IReadOnlyList<DbException> ProviderFailures
    {
        get
        {
            lock (_sync)
            {
                return [.. _providerFailures];
            }
        }
    }

    public void Record(DbException exception)
    {
        lock (_sync)
        {
            _providerFailures.Add(exception);
        }
    }
}

/// <summary>
/// Test-only extractor seam for the in-process API host. It records the real provider exception the production
/// authorization path raised and optionally rewrites the extracted payload, so a scenario can drive a
/// malformed or unmappable AUTH1 payload through the production mapper without altering production SQL.
/// </summary>
/// <remarks>
/// Registered only when a test class supplies a transform, so the default extraction stays in place for every
/// other API integration test.
/// </remarks>
internal sealed class RecordingProviderFailureExtractor(
    ApiIntegrationProviderFailureRecorder recorder,
    Func<RelationshipAuthorizationProviderFailure, RelationshipAuthorizationProviderFailure> transform
) : IRelationshipAuthorizationProviderFailureExtractor
{
    private readonly ApiIntegrationProviderFailureRecorder _recorder =
        recorder ?? throw new ArgumentNullException(nameof(recorder));
    private readonly Func<
        RelationshipAuthorizationProviderFailure,
        RelationshipAuthorizationProviderFailure
    > _transform = transform ?? throw new ArgumentNullException(nameof(transform));

    public RelationshipAuthorizationProviderFailure Extract(DbException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        _recorder.Record(exception);

        return _transform(new RelationshipAuthorizationProviderFailure(null, exception.Message));
    }
}
