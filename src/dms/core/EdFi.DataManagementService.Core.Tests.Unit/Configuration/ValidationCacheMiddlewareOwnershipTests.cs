// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.Configuration.ValidationCacheSupport;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

/// <summary>
/// The half of the invalidation contract the providers cannot own. A missing dms.EffectiveSchema row,
/// a schema-hash mismatch, and a resource-key mismatch are all successful reads of a bad answer, not
/// faults, so only the middleware that interprets them can decide the verdict must not be kept.
/// </summary>
[TestFixture]
[Parallelizable]
public class ValidationCacheMiddlewareOwnershipTests
{
    private const string ExpectedHash = "abc123";

    private static DatabaseFingerprint Fingerprint(string hash = ExpectedHash) =>
        new("1.0", hash, 42, new byte[32].ToImmutableArray());

    private static EffectiveSchemaSet SchemaSet() =>
        new(new EffectiveSchemaInfo("1.0", "1.0", ExpectedHash, 2, new byte[32], [], []), []);

    private static DataStore TestDataStore() =>
        new(
            Id: 1,
            DataStoreType: "Test",
            Name: "Test Instance",
            ConnectionString: ConnectionString,
            RouteContext: []
        );

    private static RequestInfo RequestInfoFor(IDataStoreSelection selection, DatabaseFingerprint? fingerprint)
    {
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDataStoreSelection))).Returns(selection);

        FrontendRequest frontendRequest = new(
            Path: "/ed-fi/students",
            Body: null,
            Form: null,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId("trace"),
            RouteQualifiers: []
        );

        return new RequestInfo(frontendRequest, RequestMethod.GET, serviceProvider)
        {
            ClientAuthorizations = new ClientAuthorizations(
                TokenId: "token",
                ClientId: "client",
                ClaimSetName: "test",
                EducationOrganizationIds: [],
                NamespacePrefixes: [],
                DataStoreIds: [new DataStoreId(1)]
            ),
            DatabaseFingerprint = fingerprint,
        };
    }

    private static IDataStoreSelection SelectionOf(EffectiveDataStoreTarget target)
    {
        var selection = A.Fake<IDataStoreSelection>();
        A.CallTo(() => selection.IsSet).Returns(true);
        A.CallTo(() => selection.GetSelectedDataStore()).Returns(TestDataStore());
        A.CallTo(() => selection.GetEffectiveTarget()).Returns(target);
        return selection;
    }

    /// <summary>
    /// Counts reads so "the verdict was dropped" is observed as a second database read rather than
    /// asserted about cache internals.
    /// </summary>
    private sealed class CountingFingerprintReader(Func<DatabaseFingerprint?> read)
        : IDatabaseFingerprintReader
    {
        public int Reads { get; private set; }

        public Task<DatabaseFingerprint?> ReadFingerprintAsync(EffectiveDataStoreTarget target)
        {
            Reads++;
            return Task.FromResult(read());
        }
    }

    private static async Task<int> FingerprintReadsForTwoRequestsAsync(
        EffectiveDataStoreTarget target,
        Func<DatabaseFingerprint?> read
    )
    {
        CountingFingerprintReader reader = new(read);
        ControlledTimeProvider time = new(Start);
        DatabaseFingerprintProvider provider = new(reader, time, SettingsWith());

        var schemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();
        A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(SchemaSet());

        ValidateDatabaseFingerprintMiddleware middleware = new(
            provider,
            schemaSetProvider,
            NullLogger<ValidateDatabaseFingerprintMiddleware>.Instance
        );

        var selection = SelectionOf(target);

        for (int i = 0; i < 2; i++)
        {
            await middleware.Execute(RequestInfoFor(selection, fingerprint: null), () => Task.CompletedTask);
        }

        return reader.Reads;
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Derivative_Whose_Database_Is_Not_Provisioned
        : ValidationCacheMiddlewareOwnershipTests
    {
        /// <summary>
        /// A snapshot may simply not exist yet when the first request arrives. Keeping that verdict
        /// would make the service answer 503 for it until a restart, long after it was created.
        /// </summary>
        [Test]
        public async Task It_drops_the_cached_null_so_the_next_request_re_reads()
        {
            int reads = await FingerprintReadsForTwoRequestsAsync(
                new EffectiveDataStoreTarget(EffectiveTargetKind.Snapshot, ConnectionString),
                () => null
            );

            reads.Should().Be(2);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Derivative_Whose_Schema_Hash_Does_Not_Match : ValidationCacheMiddlewareOwnershipTests
    {
        /// <summary>
        /// The provider cannot see this: it reads the fingerprint but never compares it to what the
        /// process expects, so this verdict can only be dropped by the middleware that made it.
        /// </summary>
        [Test]
        public async Task It_drops_the_cached_fingerprint_so_the_next_request_re_reads()
        {
            int reads = await FingerprintReadsForTwoRequestsAsync(
                new EffectiveDataStoreTarget(EffectiveTargetKind.ReadReplica, ConnectionString),
                () => Fingerprint("a-different-hash")
            );

            reads.Should().Be(2);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Primary_With_The_Same_Two_Verdicts : ValidationCacheMiddlewareOwnershipTests
    {
        /// <summary>
        /// Unchanged behavior, and the reason the primary token is a no-op: repairing a primary needs
        /// an operator and a restart either way, so re-reading per request buys nothing.
        /// </summary>
        [Test]
        public async Task It_keeps_a_cached_null()
        {
            int reads = await FingerprintReadsForTwoRequestsAsync(
                EffectiveDataStoreTarget.Primary(ConnectionString),
                () => null
            );

            reads.Should().Be(1);
        }

        [Test]
        public async Task It_keeps_a_cached_mismatched_fingerprint()
        {
            int reads = await FingerprintReadsForTwoRequestsAsync(
                EffectiveDataStoreTarget.Primary(ConnectionString),
                () => Fingerprint("a-different-hash")
            );

            reads.Should().Be(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Resource_Key_Validation_Failure : ValidationCacheMiddlewareOwnershipTests
    {
        private static async Task<int> ValidationsForTwoRequestsAsync(EffectiveDataStoreTarget target)
        {
            ControlledTimeProvider time = new(Start);
            ResourceKeyValidationCacheProvider cacheProvider = new(time, SettingsWith());
            int validations = 0;

            var validator = A.Fake<IResourceKeyValidator>();
            A.CallTo(() =>
                    validator.ValidateAsync(
                        A<DatabaseFingerprint>._,
                        A<short>._,
                        A<ImmutableArray<byte>>._,
                        A<IReadOnlyList<ResourceKeyRow>>._,
                        A<EffectiveDataStoreTarget>._,
                        A<CancellationToken>._
                    )
                )
                .ReturnsLazily(() =>
                {
                    validations++;
                    return Task.FromResult<ResourceKeyValidationResult>(
                        new ResourceKeyValidationResult.ValidationFailure("diff")
                    );
                });

            var schemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(SchemaSet());

            ValidateResourceKeySeedMiddleware middleware = new(
                validator,
                cacheProvider,
                schemaSetProvider,
                NullLogger<ValidateResourceKeySeedMiddleware>.Instance
            );

            var selection = SelectionOf(target);

            for (int i = 0; i < 2; i++)
            {
                await middleware.Execute(RequestInfoFor(selection, Fingerprint()), () => Task.CompletedTask);
            }

            return validations;
        }

        /// <summary>
        /// A mismatch is a returned result, not a thrown exception, so the provider never sees it.
        /// </summary>
        [Test]
        public async Task It_is_dropped_for_a_derivative()
        {
            int validations = await ValidationsForTwoRequestsAsync(
                new EffectiveDataStoreTarget(EffectiveTargetKind.ReadReplica, ConnectionString)
            );

            validations.Should().Be(2);
        }

        [Test]
        public async Task It_is_kept_for_a_primary()
        {
            int validations = await ValidationsForTwoRequestsAsync(
                EffectiveDataStoreTarget.Primary(ConnectionString)
            );

            validations.Should().Be(1);
        }
    }
}
