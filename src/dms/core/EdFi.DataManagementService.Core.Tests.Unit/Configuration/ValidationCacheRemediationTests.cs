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
/// What the four validation failure responses tell an operator to do about it. The instruction is not
/// cosmetic: for a primary the verdict is retained and a restart really is required, and for a
/// derivative it was already dropped, so the same sentence would send the operator after a cached
/// failure that no longer exists.
/// </summary>
[TestFixture]
[Parallelizable]
public class ValidationCacheRemediationTests
{
    private const string ExpectedHash = "abc123";

    /// <summary>The sentence a derivative body must carry, and a primary body must not.</summary>
    private const string NoRestartRequired = "No restart is required";

    private static DatabaseFingerprint Fingerprint(string hash = ExpectedHash) =>
        new("1.0", hash, 42, new byte[32].ToImmutableArray());

    private static EffectiveSchemaSet SchemaSet() =>
        new(new EffectiveSchemaInfo("1.0", "1.0", ExpectedHash, 2, new byte[32], [], []), []);

    private static EffectiveDataStoreTarget Primary() => EffectiveDataStoreTarget.Primary(ConnectionString);

    private static EffectiveDataStoreTarget Derivative() =>
        new(EffectiveTargetKind.Snapshot, ConnectionString);

    private static IDataStoreSelection SelectionOf(EffectiveDataStoreTarget target)
    {
        var selection = A.Fake<IDataStoreSelection>();
        A.CallTo(() => selection.IsSet).Returns(true);
        A.CallTo(() => selection.GetSelectedDataStore())
            .Returns(
                new DataStore(
                    Id: 1,
                    DataStoreType: "Test",
                    Name: "Test Instance",
                    ConnectionString: ConnectionString,
                    RouteContext: []
                )
            );
        A.CallTo(() => selection.GetEffectiveTarget()).Returns(target);
        return selection;
    }

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

    private sealed class StubFingerprintReader(Func<DatabaseFingerprint?> read) : IDatabaseFingerprintReader
    {
        public Task<DatabaseFingerprint?> ReadFingerprintAsync(EffectiveDataStoreTarget target) =>
            Task.FromResult(read());
    }

    private static async Task<IFrontendResponse> FingerprintResponseAsync(
        EffectiveDataStoreTarget target,
        Func<DatabaseFingerprint?> read
    )
    {
        DatabaseFingerprintProvider provider = new(
            new StubFingerprintReader(read),
            new ControlledTimeProvider(Start),
            SettingsWith()
        );

        var schemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();
        A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(SchemaSet());

        ValidateDatabaseFingerprintMiddleware middleware = new(
            provider,
            schemaSetProvider,
            NullLogger<ValidateDatabaseFingerprintMiddleware>.Instance
        );

        RequestInfo requestInfo = RequestInfoFor(SelectionOf(target), fingerprint: null);
        await middleware.Execute(requestInfo, () => Task.CompletedTask);

        return requestInfo.FrontendResponse;
    }

    private static string BodyOf(IFrontendResponse response) => response.Body!.ToString();

    /// <summary>
    /// Each case is asserted from both directions: the envelope is identical whichever class the
    /// request belongs to, and only the recovery instruction differs.
    /// </summary>
    private static void AssertSameEnvelope(IFrontendResponse primary, IFrontendResponse derivative)
    {
        derivative.StatusCode.Should().Be(primary.StatusCode);
        derivative.ContentType.Should().Be(primary.ContentType);
        derivative.Headers.Should().BeEquivalentTo(primary.Headers);

        // Same problem type and title; only the detail and errors differ.
        BodyOf(derivative).Should().Contain(TypeOf(BodyOf(primary)));
        BodyOf(derivative).Should().Contain(TitleOf(BodyOf(primary)));
    }

    private static string TypeOf(string body) => Between(body, "\"type\": \"", "\"");

    private static string TitleOf(string body) => Between(body, "\"title\": \"", "\"");

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal) + start.Length;
        int to = text.IndexOf(end, from, StringComparison.Ordinal);
        return text[from..to];
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Malformed_Fingerprint : ValidationCacheRemediationTests
    {
        private static Task<IFrontendResponse> ResponseAsync(EffectiveDataStoreTarget target) =>
            FingerprintResponseAsync(
                target,
                () => throw new DatabaseFingerprintValidationException(["malformed"])
            );

        [Test]
        public async Task It_still_tells_a_primary_caller_to_restart()
        {
            string body = BodyOf(await ResponseAsync(Primary()));

            body.Should().NotContain(NoRestartRequired);
            body.Should().MatchRegex("(?i)restart", "a retained primary verdict really does need one");
        }

        [Test]
        public async Task It_does_not_tell_a_derivative_caller_to_restart()
        {
            BodyOf(await ResponseAsync(Derivative())).Should().Contain(NoRestartRequired);
        }

        [Test]
        public async Task It_tells_a_derivative_caller_the_next_request_revalidates()
        {
            BodyOf(await ResponseAsync(Derivative())).Should().Contain("next request will revalidate");
        }

        [Test]
        public async Task It_keeps_the_same_envelope_for_both()
        {
            AssertSameEnvelope(await ResponseAsync(Primary()), await ResponseAsync(Derivative()));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Unprovisioned_Database : ValidationCacheRemediationTests
    {
        private static Task<IFrontendResponse> ResponseAsync(EffectiveDataStoreTarget target) =>
            FingerprintResponseAsync(target, () => null);

        [Test]
        public async Task It_still_tells_a_primary_caller_to_restart()
        {
            string body = BodyOf(await ResponseAsync(Primary()));

            body.Should().NotContain(NoRestartRequired);
            body.Should().MatchRegex("(?i)restart", "a retained primary verdict really does need one");
        }

        [Test]
        public async Task It_does_not_tell_a_derivative_caller_to_restart()
        {
            BodyOf(await ResponseAsync(Derivative())).Should().Contain(NoRestartRequired);
        }

        [Test]
        public async Task It_still_tells_a_derivative_caller_to_provision()
        {
            BodyOf(await ResponseAsync(Derivative())).Should().Contain("ddl provision");
        }

        [Test]
        public async Task It_keeps_the_same_envelope_for_both()
        {
            AssertSameEnvelope(await ResponseAsync(Primary()), await ResponseAsync(Derivative()));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Schema_Hash_Mismatch : ValidationCacheRemediationTests
    {
        private static Task<IFrontendResponse> ResponseAsync(EffectiveDataStoreTarget target) =>
            FingerprintResponseAsync(target, () => Fingerprint("a-different-hash"));

        [Test]
        public async Task It_still_tells_a_primary_caller_to_restart()
        {
            string body = BodyOf(await ResponseAsync(Primary()));

            body.Should().NotContain(NoRestartRequired);
            body.Should().MatchRegex("(?i)restart", "a retained primary verdict really does need one");
        }

        [Test]
        public async Task It_does_not_tell_a_derivative_caller_to_restart()
        {
            BodyOf(await ResponseAsync(Derivative())).Should().Contain(NoRestartRequired);
        }

        [Test]
        public async Task It_still_tells_a_derivative_caller_to_reprovision()
        {
            BodyOf(await ResponseAsync(Derivative())).Should().Contain("reprovisioned");
        }

        [Test]
        public async Task It_keeps_the_same_envelope_for_both()
        {
            AssertSameEnvelope(await ResponseAsync(Primary()), await ResponseAsync(Derivative()));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Resource_Key_Mismatch : ValidationCacheRemediationTests
    {
        private static async Task<IFrontendResponse> ResourceKeyResponseAsync(EffectiveDataStoreTarget target)
        {
            ResourceKeyValidationCacheProvider cacheProvider = new(
                new ControlledTimeProvider(Start),
                SettingsWith()
            );

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
                .Returns(new ResourceKeyValidationResult.ValidationFailure("diff"));

            var schemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(SchemaSet());

            ValidateResourceKeySeedMiddleware middleware = new(
                validator,
                cacheProvider,
                schemaSetProvider,
                NullLogger<ValidateResourceKeySeedMiddleware>.Instance
            );

            RequestInfo requestInfo = RequestInfoFor(SelectionOf(target), Fingerprint());
            await middleware.Execute(requestInfo, () => Task.CompletedTask);

            return requestInfo.FrontendResponse;
        }

        [Test]
        public async Task It_still_tells_a_primary_caller_to_restart()
        {
            string body = BodyOf(await ResourceKeyResponseAsync(Primary()));

            body.Should().NotContain(NoRestartRequired);
            body.Should().MatchRegex("(?i)restart", "a retained primary verdict really does need one");
        }

        [Test]
        public async Task It_does_not_tell_a_derivative_caller_to_restart()
        {
            BodyOf(await ResourceKeyResponseAsync(Derivative())).Should().Contain(NoRestartRequired);
        }

        [Test]
        public async Task It_still_tells_a_derivative_caller_to_reprovision()
        {
            BodyOf(await ResourceKeyResponseAsync(Derivative())).Should().Contain("reprovisioned");
        }

        [Test]
        public async Task It_keeps_the_same_envelope_for_both()
        {
            AssertSameEnvelope(
                await ResourceKeyResponseAsync(Primary()),
                await ResourceKeyResponseAsync(Derivative())
            );
        }
    }
}
