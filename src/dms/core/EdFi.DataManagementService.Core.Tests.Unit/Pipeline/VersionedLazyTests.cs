// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Pipeline;

[TestFixture]
[Parallelizable]
public class VersionedLazyTests
{
    [TestFixture]
    [Parallelizable]
    public class BasicFunctionalityTests
    {
        [Test]
        public void Value_FirstAccess_CallsFactory()
        {
            // Arrange
            var factoryCalled = false;
            var versionedLazy = new VersionedLazy<string>(
                () =>
                {
                    factoryCalled = true;
                    return "test value";
                },
                () => Guid.NewGuid()
            );

            // Act
            var value = versionedLazy.Value;

            // Assert
            factoryCalled.Should().BeTrue();
            value.Should().Be("test value");
        }

        [Test]
        public void Value_SameVersion_ReturnsCachedValue()
        {
            // Arrange
            var callCount = 0;
            var version = Guid.NewGuid();
            var versionedLazy = new VersionedLazy<string>(
                () =>
                {
                    callCount++;
                    return $"value {callCount}";
                },
                () => version
            );

            // Act
            var value1 = versionedLazy.Value;
            var value2 = versionedLazy.Value;
            var value3 = versionedLazy.Value;

            // Assert
            callCount.Should().Be(1, "factory should only be called once");
            value1.Should().Be("value 1");
            value2.Should().Be("value 1");
            value3.Should().Be("value 1");
        }

        [Test]
        public void Value_DifferentVersion_RecreatesValue()
        {
            // Arrange
            var callCount = 0;
            var currentVersion = Guid.NewGuid();
            var versionedLazy = new VersionedLazy<string>(
                () =>
                {
                    callCount++;
                    return $"value {callCount}";
                },
                () => currentVersion
            );

            // Act
            var value1 = versionedLazy.Value;
            currentVersion = Guid.NewGuid(); // Change version
            var value2 = versionedLazy.Value;
            currentVersion = Guid.NewGuid(); // Change version again
            var value3 = versionedLazy.Value;

            // Assert
            callCount.Should().Be(3, "factory should be called for each version change");
            value1.Should().Be("value 1");
            value2.Should().Be("value 2");
            value3.Should().Be("value 3");
        }

        [Test]
        public void Constructor_NullFactory_ThrowsArgumentNullException()
        {
            // Act & Assert
            var act = () => new VersionedLazy<string>(null!, () => Guid.NewGuid());
            act.Should().Throw<ArgumentNullException>().WithParameterName("valueFactory");
        }

        [Test]
        public void Constructor_NullVersionProvider_ThrowsArgumentNullException()
        {
            // Act & Assert
            var act = () => new VersionedLazy<string>(() => "test", null!);
            act.Should().Throw<ArgumentNullException>().WithParameterName("versionProvider");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class ThreadSafetyTests
    {
        [Test]
        public async Task Value_ConcurrentAccess_ThreadSafe()
        {
            // Arrange
            var factoryCallCount = 0;
            var version = Guid.NewGuid();
            var barrier = new Barrier(10);

            var versionedLazy = new VersionedLazy<TestObject>(
                () =>
                {
                    Interlocked.Increment(ref factoryCallCount);
                    Task.Delay(10).Wait(); // Simulate work
                    return new TestObject { Id = factoryCallCount };
                },
                () => version
            );

            var results = new List<TestObject>();
            var tasks = new List<Task>();

            // Act
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(
                    Task.Run(() =>
                    {
                        barrier.SignalAndWait();
                        var result = versionedLazy.Value;
                        lock (results)
                        {
                            results.Add(result);
                        }
                    })
                );
            }

            await Task.WhenAll(tasks);

            // Assert
            factoryCallCount.Should().Be(1, "factory should only be called once despite concurrent access");
            results.Should().HaveCount(10);
            results.Should().AllBeEquivalentTo(results[0], "all threads should get the same instance");
        }

        [Test]
        public async Task Value_VersionChangeDuringAccess_HandledCorrectly()
        {
            // Arrange
            var currentVersion = Guid.NewGuid();
            var factoryExecutions = new List<(DateTime time, Guid version)>();
            var versionLock = new object();

            var versionedLazy = new VersionedLazy<string>(
                () =>
                {
                    Guid capturedVersion;
                    lock (versionLock)
                    {
                        capturedVersion = currentVersion;
                    }

                    Task.Delay(50).Wait(); // Simulate work

                    lock (factoryExecutions)
                    {
                        factoryExecutions.Add((DateTime.UtcNow, capturedVersion));
                    }

                    return $"value for version {capturedVersion}";
                },
                () =>
                {
                    lock (versionLock)
                    {
                        return currentVersion;
                    }
                }
            );

            // Act
            var tasks = new List<Task<string>>();

            // Start multiple reads
            for (int i = 0; i < 5; i++)
            {
                tasks.Add(Task.Run(() => versionedLazy.Value));
            }

            // Change version while reads are in progress
            await Task.Delay(25);
            lock (versionLock)
            {
                currentVersion = Guid.NewGuid();
            }

            // Start more reads with new version
            for (int i = 0; i < 5; i++)
            {
                tasks.Add(Task.Run(() => versionedLazy.Value));
            }

            var results = await Task.WhenAll(tasks);

            // Assert
            factoryExecutions
                .Should()
                .HaveCountGreaterOrEqualTo(2, "factory should be called at least once per version");
            results
                .Distinct()
                .Should()
                .HaveCountGreaterOrEqualTo(2, "should have values for multiple versions");
        }

        [Test]
        public void Value_FactoryThrows_PropagatesException()
        {
            // Arrange
            var versionedLazy = new VersionedLazy<string>(
                () => throw new InvalidOperationException("Factory failed"),
                () => Guid.NewGuid()
            );

            // Act & Assert
            var act = () => versionedLazy.Value;
            act.Should().Throw<InvalidOperationException>().WithMessage("Factory failed");
        }

        [Test]
        public void Value_FactoryThrowsThenSucceeds_RetriesOnVersionChange()
        {
            // Arrange
            var attemptCount = 0;
            var currentVersion = Guid.NewGuid();

            var versionedLazy = new VersionedLazy<string>(
                () =>
                {
                    attemptCount++;
                    if (attemptCount == 1)
                    {
                        throw new InvalidOperationException("First attempt fails");
                    }
                    return "success";
                },
                () => currentVersion
            );

            // Act & Assert
            var firstAct = () => versionedLazy.Value;
            firstAct.Should().Throw<InvalidOperationException>();

            // Change version
            currentVersion = Guid.NewGuid();

            // Should succeed on retry with new version
            var value = versionedLazy.Value;
            value.Should().Be("success");
            attemptCount.Should().Be(2);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class ComplexScenarioTests
    {
        [Test]
        public void Value_WithComplexObject_MaintainsObjectIdentity()
        {
            // Arrange
            var version = Guid.NewGuid();
            var complexObject = new ComplexTestObject
            {
                Id = 1,
                Name = "Test",
                Children = new List<ComplexTestObject>
                {
                    new() { Id = 2, Name = "Child1" },
                    new() { Id = 3, Name = "Child2" },
                },
            };

            var versionedLazy = new VersionedLazy<ComplexTestObject>(() => complexObject, () => version);

            // Act
            var value1 = versionedLazy.Value;
            var value2 = versionedLazy.Value;

            // Modify the object
            value1.Children.Add(new ComplexTestObject { Id = 4, Name = "Child3" });

            // Assert
            value2.Children.Should().HaveCount(3, "modifications should be visible in all references");
            ReferenceEquals(value1, value2).Should().BeTrue("should return the same instance");
        }

        [Test]
        public async Task Value_HighConcurrencyMixedOperations_MaintainsConsistency()
        {
            // Arrange
            var currentVersion = Guid.NewGuid();
            var versionLock = new object();
            var factoryCallCount = 0;
            var results = new List<(string value, int threadId, DateTime time)>();

            var versionedLazy = new VersionedLazy<string>(
                () =>
                {
                    var count = Interlocked.Increment(ref factoryCallCount);
                    Task.Delay(Random.Shared.Next(1, 10)).Wait(); // Variable work time
                    return $"value-{count}";
                },
                () =>
                {
                    lock (versionLock)
                    {
                        return currentVersion;
                    }
                }
            );

            // Act
            var tasks = new List<Task>();
            using var cts = new CancellationTokenSource();

            // Reader tasks
            for (int i = 0; i < 20; i++)
            {
                var threadId = i;
                tasks.Add(
                    Task.Run(async () =>
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            var value = versionedLazy.Value;
                            lock (results)
                            {
                                results.Add((value, threadId, DateTime.UtcNow));
                            }
                            await Task.Delay(Random.Shared.Next(1, 5));
                        }
                    })
                );
            }

            // Version changer task
            tasks.Add(
                Task.Run(async () =>
                {
                    for (int i = 0; i < 10; i++)
                    {
                        await Task.Delay(50);
                        lock (versionLock)
                        {
                            currentVersion = Guid.NewGuid();
                        }
                    }
                    // Add a small delay to ensure readers can see the final version
                    await Task.Delay(10);
                    await cts.CancelAsync();
                })
            );

            await Task.WhenAll(tasks);

            // Assert
            factoryCallCount
                .Should()
                .BeGreaterOrEqualTo(10, "factory should be called for each version change");

            // Group results by value and verify consistency
            var groupedResults = results.GroupBy(r => r.value).ToList();
            foreach (var group in groupedResults)
            {
                var values = group.ToList();
                values.Should().NotBeEmpty($"each cached value should be returned at least once");
            }
        }
    }

    private class TestObject
    {
        public int Id { get; set; }
    }

    private class ComplexTestObject
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<ComplexTestObject> Children { get; set; } = new();
    }
}
