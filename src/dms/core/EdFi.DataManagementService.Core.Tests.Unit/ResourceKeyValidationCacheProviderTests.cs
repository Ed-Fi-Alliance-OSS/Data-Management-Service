// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit;

[TestFixture]
[Parallelizable]
public class ResourceKeyValidationCacheProviderTests
{
    private static readonly ResourceKeyValidationResult _successResult =
        new ResourceKeyValidationResult.ValidationSuccess();

    private static readonly ResourceKeyValidationResult _failureResult =
        new ResourceKeyValidationResult.ValidationFailure("test diff");

    [TestFixture]
    [Parallelizable]
    public class Given_First_Call_For_A_Connection_String : ResourceKeyValidationCacheProviderTests
    {
        private ResourceKeyValidationResult _result1 = null!;
        private ResourceKeyValidationResult _result2 = null!;
        private int _factoryCallCount;

        [SetUp]
        public async Task Setup()
        {
            _factoryCallCount = 0;
            var provider = new ResourceKeyValidationCacheProvider();

            _result1 = await provider.GetOrValidateAsync(
                "conn1",
                () =>
                {
                    _factoryCallCount++;
                    return Task.FromResult<ResourceKeyValidationResult>(_successResult);
                }
            );

            _result2 = await provider.GetOrValidateAsync(
                "conn1",
                () =>
                {
                    _factoryCallCount++;
                    return Task.FromResult<ResourceKeyValidationResult>(_successResult);
                }
            );
        }

        [Test]
        public void It_invokes_factory_exactly_once()
        {
            _factoryCallCount.Should().Be(1);
        }

        [Test]
        public void It_returns_success_on_first_call()
        {
            _result1.Should().BeOfType<ResourceKeyValidationResult.ValidationSuccess>();
        }

        [Test]
        public void It_returns_same_cached_result_on_second_call()
        {
            _result2.Should().BeSameAs(_result1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Calls_For_Different_Connection_Strings : ResourceKeyValidationCacheProviderTests
    {
        private ResourceKeyValidationResult _result1 = null!;
        private ResourceKeyValidationResult _result2 = null!;
        private int _factoryCallCount;

        [SetUp]
        public async Task Setup()
        {
            _factoryCallCount = 0;
            var provider = new ResourceKeyValidationCacheProvider();

            _result1 = await provider.GetOrValidateAsync(
                "conn1",
                () =>
                {
                    _factoryCallCount++;
                    return Task.FromResult<ResourceKeyValidationResult>(_successResult);
                }
            );

            _result2 = await provider.GetOrValidateAsync(
                "conn2",
                () =>
                {
                    _factoryCallCount++;
                    return Task.FromResult<ResourceKeyValidationResult>(
                        new ResourceKeyValidationResult.ValidationFailure("diff for conn2")
                    );
                }
            );
        }

        [Test]
        public void It_invokes_factory_for_each_connection_string()
        {
            _factoryCallCount.Should().Be(2);
        }

        [Test]
        public void It_returns_success_for_conn1()
        {
            _result1.Should().BeOfType<ResourceKeyValidationResult.ValidationSuccess>();
        }

        [Test]
        public void It_returns_failure_for_conn2()
        {
            _result2.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_First_Call_Throws_Transient_Exception_Then_Succeeds
        : ResourceKeyValidationCacheProviderTests
    {
        private static async Task<Exception?> CatchExceptionAsync(Task task)
        {
            try
            {
                await task;
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private Exception? _firstException;
        private ResourceKeyValidationResult _secondResult = null!;
        private int _factoryCallCount;

        [SetUp]
        public async Task Setup()
        {
            _factoryCallCount = 0;
            var provider = new ResourceKeyValidationCacheProvider();

            _firstException = await CatchExceptionAsync(
                provider.GetOrValidateAsync(
                    "conn1",
                    () =>
                    {
                        _factoryCallCount++;
                        throw new TimeoutException("connection refused");
                    }
                )
            );

            _secondResult = await provider.GetOrValidateAsync(
                "conn1",
                () =>
                {
                    _factoryCallCount++;
                    return Task.FromResult<ResourceKeyValidationResult>(_successResult);
                }
            );
        }

        [Test]
        public void It_throws_on_first_call()
        {
            _firstException.Should().BeOfType<TimeoutException>();
            _firstException!.Message.Should().Be("connection refused");
        }

        [Test]
        public void It_retries_and_returns_success_on_second_call()
        {
            _secondResult.Should().BeOfType<ResourceKeyValidationResult.ValidationSuccess>();
        }

        [Test]
        public void It_invokes_factory_twice()
        {
            _factoryCallCount.Should().Be(2);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Validation_Failure_Is_Returned : ResourceKeyValidationCacheProviderTests
    {
        private ResourceKeyValidationResult _result1 = null!;
        private ResourceKeyValidationResult _result2 = null!;
        private int _factoryCallCount;

        [SetUp]
        public async Task Setup()
        {
            _factoryCallCount = 0;
            var provider = new ResourceKeyValidationCacheProvider();

            _result1 = await provider.GetOrValidateAsync(
                "conn1",
                () =>
                {
                    _factoryCallCount++;
                    return Task.FromResult<ResourceKeyValidationResult>(_failureResult);
                }
            );

            _result2 = await provider.GetOrValidateAsync(
                "conn1",
                () =>
                {
                    _factoryCallCount++;
                    // If the factory were called again, it would return success.
                    // But it should NOT be called because failure is cached.
                    return Task.FromResult<ResourceKeyValidationResult>(_successResult);
                }
            );
        }

        [Test]
        public void It_returns_failure_on_first_call()
        {
            _result1.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>();
        }

        [Test]
        public void It_returns_cached_failure_on_second_call()
        {
            _result2.Should().BeSameAs(_result1);
        }

        [Test]
        public void It_invokes_factory_only_once()
        {
            _factoryCallCount.Should().Be(1);
        }

        [Test]
        public void It_does_not_evict_failure_result()
        {
            // The second call's factory would return success, but it's never invoked.
            // The cached failure persists.
            _result2.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>();
        }
    }
}
