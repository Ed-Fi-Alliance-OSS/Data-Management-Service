// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// The stored hash carries a version byte, a salt length, the salt, and the subkey, but not the
/// iteration count, so verification derives at whatever count is configured now. These fixtures pin
/// the operator-visible consequence documented in CONFIGURATION.md: changing the count invalidates
/// every secret hashed at the old one.
/// </summary>
public class ClientSecretHasherTests
{
    private const string Secret = "Sup3r-Secret-Client-Value-0123456789";

    // Low counts keep the fixtures fast; the property under test does not depend on magnitude.
    private const int HashedAtIterations = 1000;
    private const int VerifiedAtIterations = 2000;

    private static ClientSecretHasher CreateHasher(
        int iterations,
        ILogger<ClientSecretHasher>? logger = null
    ) =>
        new(
            logger ?? NullLogger<ClientSecretHasher>.Instance,
            Options.Create(new IdentityOptions { ClientSecretHashingIterations = iterations })
        );

    private static IEnumerable<FakeItEasy.Core.ICompletedFakeObjectCall> LogCalls(
        ILogger<ClientSecretHasher> logger
    ) => Fake.GetCalls(logger).Where(call => call.Method.Name == nameof(ILogger.Log));

    [TestFixture]
    public class Given_a_secret_verified_at_the_count_it_was_hashed_at
    {
        private bool _verified;

        [SetUp]
        public async Task Setup()
        {
            string hash = await CreateHasher(HashedAtIterations).HashSecretAsync(Secret);
            _verified = await CreateHasher(HashedAtIterations).VerifySecretAsync(Secret, hash);
        }

        [Test]
        public void It_verifies() => _verified.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_secret_verified_at_a_different_count_than_it_was_hashed_at
    {
        private bool _verified;

        [SetUp]
        public async Task Setup()
        {
            string hash = await CreateHasher(HashedAtIterations).HashSecretAsync(Secret);
            _verified = await CreateHasher(VerifiedAtIterations).VerifySecretAsync(Secret, hash);
        }

        [Test]
        public void It_fails_verification() => _verified.Should().BeFalse();
    }

    // DMS-1327: token revocation needs to tell "wrong secret" from "could not verify". The
    // failure-preserving entry point answers false only for a genuine mismatch and lets every
    // failure escape, logging nothing about it; the lenient entry point the token endpoint uses is
    // unchanged.

    [TestFixture]
    public class Given_a_failure_preserving_verification_of_the_right_secret
    {
        private bool _verified;

        [SetUp]
        public async Task Setup()
        {
            string hash = await CreateHasher(HashedAtIterations).HashSecretAsync(Secret);
            _verified = await CreateHasher(HashedAtIterations)
                .VerifySecretPreservingFailuresAsync(Secret, hash);
        }

        [Test]
        public void It_verifies() => _verified.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_failure_preserving_verification_of_a_wrong_secret
    {
        private bool _verified;

        [SetUp]
        public async Task Setup()
        {
            string hash = await CreateHasher(HashedAtIterations).HashSecretAsync(Secret);
            _verified = await CreateHasher(HashedAtIterations)
                .VerifySecretPreservingFailuresAsync("not-the-secret", hash);
        }

        [Test]
        public void It_reports_a_mismatch() => _verified.Should().BeFalse();
    }

    [TestFixture]
    public class Given_a_failure_preserving_verification_with_nothing_to_compare
    {
        private bool _emptyPresented;
        private bool _emptyStored;

        [SetUp]
        public async Task Setup()
        {
            string hash = await CreateHasher(HashedAtIterations).HashSecretAsync(Secret);
            ClientSecretHasher hasher = CreateHasher(HashedAtIterations);
            _emptyPresented = await hasher.VerifySecretPreservingFailuresAsync(string.Empty, hash);
            _emptyStored = await hasher.VerifySecretPreservingFailuresAsync(Secret, string.Empty);
        }

        [Test]
        public void It_reports_an_empty_presented_secret_as_a_mismatch() =>
            _emptyPresented.Should().BeFalse();

        [Test]
        public void It_reports_a_client_without_a_stored_secret_as_a_mismatch() =>
            _emptyStored.Should().BeFalse();
    }

    [TestFixture]
    public class Given_a_failure_preserving_verification_when_the_iteration_count_is_invalid
    {
        private ILogger<ClientSecretHasher> _logger = null!;
        private Func<Task> _act = null!;

        [SetUp]
        public async Task Setup()
        {
            string hash = await CreateHasher(HashedAtIterations).HashSecretAsync(Secret);
            _logger = A.Fake<ILogger<ClientSecretHasher>>();
            ClientSecretHasher hasher = CreateHasher(0, _logger);
            _act = () => hasher.VerifySecretPreservingFailuresAsync(Secret, hash);
        }

        [Test]
        public async Task It_lets_the_failure_escape() =>
            await _act.Should().ThrowAsync<ArgumentOutOfRangeException>();

        [Test]
        public async Task It_logs_no_warning_and_attaches_no_exception()
        {
            await _act.Should().ThrowAsync<ArgumentOutOfRangeException>();
            LogCalls(_logger).Should().NotContain(call => call.GetArgument<LogLevel>(0) >= LogLevel.Warning);
            LogCalls(_logger).Should().NotContain(call => call.Arguments[3] != null);
        }
    }

    [TestFixture]
    public class Given_a_failure_preserving_verification_of_an_unreadable_stored_hash
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            ClientSecretHasher hasher = CreateHasher(HashedAtIterations);
            _act = () =>
                hasher.VerifySecretPreservingFailuresAsync(Secret, Convert.ToBase64String([1, 2, 3]));
        }

        [Test]
        public async Task It_lets_the_failure_escape() =>
            await _act.Should().ThrowAsync<EndOfStreamException>();
    }

    // The token endpoint's contract is unchanged: the same failure is answered false and logged.
    [TestFixture]
    public class Given_a_lenient_verification_when_the_iteration_count_is_invalid
    {
        private ILogger<ClientSecretHasher> _logger = null!;
        private bool _verified;

        [SetUp]
        public async Task Setup()
        {
            string hash = await CreateHasher(HashedAtIterations).HashSecretAsync(Secret);
            _logger = A.Fake<ILogger<ClientSecretHasher>>();
            _verified = await CreateHasher(0, _logger).VerifySecretAsync(Secret, hash);
        }

        [Test]
        public void It_still_answers_false() => _verified.Should().BeFalse();

        [Test]
        public void It_still_logs_the_failure_as_a_warning() =>
            LogCalls(_logger)
                .Should()
                .ContainSingle(call =>
                    call.GetArgument<LogLevel>(0) == LogLevel.Warning
                    && call.Arguments[3] is ArgumentOutOfRangeException
                );
    }

    /// <summary>
    /// A real generated hash with its last <paramref name="bytesRemoved"/> decoded bytes dropped. One
    /// byte leaves the salt intact and a 31-byte subkey; more reaches into the salt.
    /// </summary>
    private static async Task<string> TruncatedRealHash(int bytesRemoved)
    {
        byte[] decoded = Convert.FromBase64String(
            await CreateHasher(HashedAtIterations).HashSecretAsync(Secret)
        );
        return Convert.ToBase64String(decoded[..^bytesRemoved]);
    }

    // BinaryReader.ReadBytes returns a short array rather than throwing, so a hash missing only its
    // final byte still decodes, and the fixed-time comparison of a 32- and a 31-byte subkey answers
    // false. On the failure-preserving path that is corruption, not a wrong secret.
    [TestFixture]
    public class Given_a_failure_preserving_verification_of_a_hash_missing_its_last_byte
    {
        private ILogger<ClientSecretHasher> _logger = null!;
        private Func<Task> _act = null!;

        [SetUp]
        public async Task Setup()
        {
            string truncated = await TruncatedRealHash(1);
            _logger = A.Fake<ILogger<ClientSecretHasher>>();
            ClientSecretHasher hasher = CreateHasher(HashedAtIterations, _logger);
            _act = () => hasher.VerifySecretPreservingFailuresAsync(Secret, truncated);
        }

        [Test]
        public async Task It_reports_the_hash_as_incomplete() =>
            await _act.Should()
                .ThrowAsync<InvalidDataException>()
                .WithMessage("The stored client secret hash is incomplete.");

        [Test]
        public async Task It_logs_no_warning_and_attaches_no_exception()
        {
            await _act.Should().ThrowAsync<InvalidDataException>();
            LogCalls(_logger).Should().NotContain(call => call.GetArgument<LogLevel>(0) >= LogLevel.Warning);
            LogCalls(_logger).Should().NotContain(call => call.Arguments[3] != null);
        }
    }

    [TestFixture]
    public class Given_a_failure_preserving_verification_of_a_hash_truncated_inside_its_salt
    {
        private Func<Task> _act = null!;

        [SetUp]
        public async Task Setup()
        {
            // 1 version byte + 4 length bytes + 16 salt + 32 subkey = 53; dropping 40 leaves 8 salt bytes.
            string truncated = await TruncatedRealHash(40);
            ClientSecretHasher hasher = CreateHasher(HashedAtIterations);
            _act = () => hasher.VerifySecretPreservingFailuresAsync(Secret, truncated);
        }

        [Test]
        public async Task It_reports_the_hash_as_incomplete() =>
            await _act.Should().ThrowAsync<InvalidDataException>();
    }

    // The lenient entry point the token endpoint uses keeps its established behaviour for the same
    // corrupt value: a quiet false, exactly as before the structural check existed.
    [TestFixture]
    public class Given_a_lenient_verification_of_a_hash_missing_its_last_byte
    {
        private ILogger<ClientSecretHasher> _logger = null!;
        private bool _verified;

        [SetUp]
        public async Task Setup()
        {
            string truncated = await TruncatedRealHash(1);
            _logger = A.Fake<ILogger<ClientSecretHasher>>();
            _verified = await CreateHasher(HashedAtIterations, _logger).VerifySecretAsync(Secret, truncated);
        }

        [Test]
        public void It_still_answers_false() => _verified.Should().BeFalse();

        [Test]
        public void It_still_logs_no_warning() =>
            LogCalls(_logger).Should().NotContain(call => call.GetArgument<LogLevel>(0) >= LogLevel.Warning);
    }
}
