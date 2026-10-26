// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using EdFi.DmsConfigurationService.Secrets;
using EdFi.DmsConfigurationService.Tests.TimingEvidence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// Collects in-process timing samples for <see cref="OpenIddictTokenManager.GetAccessTokenAsync"/>
/// with the real client secret hasher and a repository whose lookup costs the same for every client.
/// It records samples and asserts nothing about them: the offline analysis decides. Explicit, so it
/// never runs in an ordinary test run; run it only in the documented controlled environment with
/// the <c>TIMING_EVIDENCE_*</c> settings.
/// <para>
/// Arms: an absent client; a known client with a wrong secret; the original defect's shape (an
/// absent client against a hasher that performs no derivation); and an absent client whose lookup
/// busy-waits three times the in-process bound, a control the analysis must find clearly outside it.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Timing evidence collector; run only in the documented controlled environment.")]
[Category("TimingEvidence")]
[NonParallelizable]
public class Given_in_process_token_requests_measured_for_timing_evidence
{
    private const int Iterations = 20_000;
    private const double BoundMicroseconds = 25;
    private const double OutsideControlMicroseconds = 3 * BoundMicroseconds;
    private const string AbsentClientId = "timing-absent-client";
    private const string KnownClientId = "timing-known-client";
    private const string KnownSecret = "timing-known-secret-0123456789";
    private const string WrongSecret = "timing-wrong-secret-0123456789";

    private readonly List<IDisposable> _owned = [];
    private TimingRunSettings _settings = null!;
    private TimingRunFiles _files = null!;
    private int _arms;

    /// <summary>
    /// Answers every lookup from precomputed results, so the repository adds the same small cost to
    /// every arm. An absent client's lookup can be made to busy-wait, never sleep, for a fixed time.
    /// </summary>
    private sealed class FixedCostTokenRepository(
        string knownClientId,
        string storedHash,
        TimeSpan absentLookupWait
    ) : IOpenIddictTokenRepository
    {
        private readonly Task<ApplicationInfo?> _absent = Task.FromResult<ApplicationInfo?>(null);

        private readonly Task<ApplicationInfo?> _known = Task.FromResult<ApplicationInfo?>(
            new ApplicationInfo
            {
                Id = Guid.NewGuid(),
                ClientId = knownClientId,
                ClientSecret = storedHash,
                IsApproved = true,
            }
        );

        private readonly long _waitTicks = (long)(absentLookupWait.TotalSeconds * Stopwatch.Frequency);

        public Task<ApplicationInfo?> GetApplicationByClientIdAsync(string clientId)
        {
            if (string.Equals(clientId, knownClientId, StringComparison.Ordinal))
            {
                return _known;
            }

            long until = Stopwatch.GetTimestamp() + _waitTicks;
            while (Stopwatch.GetTimestamp() < until)
            {
                Thread.SpinWait(20);
            }

            return _absent;
        }

        public Task<TokenInfo?> GetTokenByIdAsync(Guid tokenId) => throw new NotSupportedException();

        public Task<TokenStoreOutcome> StoreTokenAsync(
            Guid tokenId,
            Guid applicationId,
            string subject,
            DateTimeOffset expiration,
            int maxActiveTokens
        ) => throw new NotSupportedException();

        public Task<string?> GetTokenStatusAsync(Guid tokenId) => throw new NotSupportedException();

        public Task<string?> GetTokenStatusForApplicationAsync(Guid tokenId, Guid applicationId) =>
            throw new NotSupportedException();

        public Task<bool> RevokeTokenAsync(Guid tokenId, Guid applicationId) =>
            throw new NotSupportedException();

        public Task<int> DeleteExpiredTokensAsync(DateTimeOffset expiredBefore) =>
            throw new NotSupportedException();

        public Task<PrivateKeyInfo?> GetActivePrivateKeyAsync(string encryptionKey) =>
            throw new NotSupportedException();

        public Task<IEnumerable<PublicKeyInfo>> GetActivePublicKeysAsync() =>
            throw new NotSupportedException();

        public Task<IEnumerable<PublicKeyInfo>> GetActivePublicKeysAsync(
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IEnumerable<string>> GetClientRolesAsync(Guid clientId) =>
            throw new NotSupportedException();
    }

    /// <summary>The original defect's shape: a hasher that derives nothing, so an absent client costs only the lookup.</summary>
    private sealed class NoDerivationHasher : IClientSecretHasher
    {
        private static readonly Task<bool> _mismatch = Task.FromResult(false);

        public Task<string> HashSecretAsync(string plainTextSecret) =>
            Task.FromResult("no-derivation-placeholder");

        public Task<bool> VerifySecretAsync(string plainTextSecret, string hashedSecret) => _mismatch;

        public bool IsSecretHashed(string secret) => true;
    }

    private async Task<OpenIddictTokenManager> ManagerAsync(
        IClientSecretHasher hasher,
        IOpenIddictTokenRepository repository
    )
    {
        IOptions<IdentityOptions> options = Options.Create(new IdentityOptions());
        SigningKeySnapshotProvider signingKeyProvider = new(
            new DatabaseSigningKeySource(repository, NullLogger<DatabaseSigningKeySource>.Instance),
            options,
            TimeProvider.System,
            NullLogger<SigningKeySnapshotProvider>.Instance
        );
        DevelopmentCertificateStore certificateStore = new(
            options,
            NullLogger<DevelopmentCertificateStore>.Instance
        );
        _owned.Add(signingKeyProvider);
        _owned.Add(certificateStore);

        // As the host does at startup, before any request.
        ClientSecretPlaceholderHash placeholderHash = new(hasher);
        await placeholderHash.InitializeAsync();

        return new OpenIddictTokenManager(
            options,
            NullLogger<OpenIddictTokenManager>.Instance,
            hasher,
            repository,
            signingKeyProvider,
            certificateStore,
            placeholderHash
        );
    }

    private static List<KeyValuePair<string, string>> Credentials(string clientId, string secret) =>
        [new("client_id", clientId), new("client_secret", secret)];

    private static bool IsInvalidClient(TokenResult result) =>
        result is TokenResult.FailureAuthentication { Error: "invalid_client" };

    [OneTimeSetUp]
    public async Task Record()
    {
        _settings = TimingRunSettings.FromEnvironment();

        ClientSecretHasher realHasher = new(
            NullLogger<ClientSecretHasher>.Instance,
            Options.Create(new IdentityOptions { ClientSecretHashingIterations = Iterations })
        );
        string storedHash = await realHasher.HashSecretAsync(KnownSecret);

        OpenIddictTokenManager measured = await ManagerAsync(
            realHasher,
            new FixedCostTokenRepository(KnownClientId, storedHash, TimeSpan.Zero)
        );
        OpenIddictTokenManager noDerivation = await ManagerAsync(
            new NoDerivationHasher(),
            new FixedCostTokenRepository(KnownClientId, storedHash, TimeSpan.Zero)
        );
        OpenIddictTokenManager slowAbsentLookup = await ManagerAsync(
            realHasher,
            new FixedCostTokenRepository(
                KnownClientId,
                storedHash,
                TimeSpan.FromTicks((long)(OutsideControlMicroseconds * TimeSpan.TicksPerMicrosecond))
            )
        );

        // The absent and wrong-secret arms present the same secret, so the derivation input is alike.
        List<KeyValuePair<string, string>> absent = Credentials(AbsentClientId, WrongSecret);
        List<KeyValuePair<string, string>> wrongSecret = Credentials(KnownClientId, WrongSecret);

        TimingArm<TokenResult>[] arms =
        [
            new("absent", () => measured.GetAccessTokenAsync(absent), IsInvalidClient),
            new("wrong_secret", () => measured.GetAccessTokenAsync(wrongSecret), IsInvalidClient),
            new("c0_no_derivation", () => noDerivation.GetAccessTokenAsync(absent), IsInvalidClient),
            new(
                "c1_absent_plus_3_bounds",
                () => slowAbsentLookup.GetAccessTokenAsync(absent),
                IsInvalidClient
            ),
        ];
        _arms = arms.Length;

        Dictionary<string, string> context = new()
        {
            ["iterations"] = Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["bound_us"] = BoundMicroseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["c1_wait_us"] = OutsideControlMicroseconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            ),
            ["estimand"] = "median(absent) - median(wrong_secret)",
            ["repository"] = "fixed-cost in-memory stub; no database",
            ["target_build"] = "the collector's own build: the token manager under test is compiled into it",
        };

        _files = await TimingEvidenceRecorder.RunAsync(_settings, "in-process", context, arms);
        await TestContext.Out.WriteLineAsync($"Samples: {_files.SamplesPath}");
        await TestContext.Out.WriteLineAsync($"Header: {_files.HeaderPath}");
    }

    [OneTimeTearDown]
    public void Release()
    {
        foreach (IDisposable owned in _owned)
        {
            owned.Dispose();
        }
    }

    [Test]
    public void It_records_one_sample_per_arm_per_round() =>
        File.ReadAllLines(_files.SamplesPath).Length.Should().Be(1 + (_settings.Rounds * _arms));

    [Test]
    public void It_writes_the_run_header() => File.Exists(_files.HeaderPath).Should().BeTrue();
}
