// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

/// <summary>
/// The Core-owned publication seam: what a reconciler is handed after a successful configuration load,
/// when it is not handed anything at all, and what ordering the provider guarantees between overlapping
/// tenant loads.
/// </summary>
[TestFixture]
public class DataStoreOwnershipPublicationTests
{
    private const string EncryptionKey = "TestEncryptionKey123456789012345678901234567890";

    private const string TenantAPrimary = "host=tenant-a;database=edfi;";
    private const string TenantBPrimary = "host=tenant-b;database=edfi;";
    private const string SharedReplica = "host=shared-replica;database=edfi;";
    private const string SnapshotConnection = "host=snapshot;database=edfi;";

    private static string Encrypt(string plainText)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(EncryptionKey.PadRight(32, '0')[..32]);
        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        byte[] result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    private static object Derivative(string derivativeType, string connectionString) =>
        new
        {
            Id = 10L,
            DataStoreId = 1L,
            DerivativeType = derivativeType,
            ConnectionString = Encrypt(connectionString),
        };

    private static object DataStoreJson(
        long id,
        string name,
        string? primaryConnectionString,
        params object[] derivatives
    ) =>
        new
        {
            Id = id,
            DataStoreType = "Production",
            Name = name,
            ConnectionString = primaryConnectionString is null ? null : Encrypt(primaryConnectionString),
            DataStoreContexts = Array.Empty<object>(),
            DataStoreDerivatives = derivatives,
        };

    /// <summary>Records every snapshot it is given, in the order it was given them.</summary>
    private sealed class RecordingReconciler(Action<DataStoreOwnershipSnapshot>? onReconcile = null)
        : IDataStoreOwnershipReconciler
    {
        private readonly List<DataStoreOwnershipSnapshot> _snapshots = [];

        public IReadOnlyList<DataStoreOwnershipSnapshot> Snapshots
        {
            get
            {
                lock (_snapshots)
                {
                    return [.. _snapshots];
                }
            }
        }

        public void Reconcile(DataStoreOwnershipSnapshot snapshot)
        {
            onReconcile?.Invoke(snapshot);

            lock (_snapshots)
            {
                _snapshots.Add(snapshot);
            }
        }
    }

    private sealed class ThrowingReconciler(Exception exception) : IDataStoreOwnershipReconciler
    {
        public int Calls { get; private set; }

        public void Reconcile(DataStoreOwnershipSnapshot snapshot)
        {
            Calls++;
            throw exception;
        }
    }

    private sealed class ResponseHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(_respond(request));
    }

    /// <summary>
    /// Serves a per-tenant body, so one provider can load two tenants with different configuration.
    /// The tenant is read from the header the provider sends.
    /// </summary>
    private static HttpMessageHandler HandlerFor(IReadOnlyDictionary<string, object> responsesByTenant) =>
        new ResponseHandler(request =>
        {
            string tenant = request.Headers.TryGetValues("Tenant", out IEnumerable<string>? values)
                ? values.FirstOrDefault() ?? string.Empty
                : string.Empty;

            return responsesByTenant.TryGetValue(tenant, out object? response)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(response)),
                }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{}"),
                };
        });

    private static ConfigurationServiceDataStoreProvider CreateProvider(
        HttpMessageHandler handler,
        ILogger<ConfigurationServiceDataStoreProvider> logger,
        params IDataStoreOwnershipReconciler[] reconcilers
    )
    {
        var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
        A.CallTo(() =>
                tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._)
            )
            .Returns("valid-token");

        HttpClient httpClient = new(handler) { BaseAddress = new Uri("https://api.example.com/") };

        return new ConfigurationServiceDataStoreProvider(
            new ConfigurationServiceApiClient(httpClient),
            tokenHandler,
            new ConfigurationServiceContext("clientId", "secret", "scope"),
            logger,
            new ConnectionStringDecryptionService(EncryptionKey),
            cacheSettings: null,
            timeProvider: null,
            reconcilers: reconcilers
        );
    }

    private static IEnumerable<string> ConnectionStringsOf(DataStoreOwnershipSnapshot snapshot) =>
        snapshot.Owners.Select(owner => owner.ConfiguredConnectionString);

    [TestFixture]
    public class Given_Two_Tenants_Loaded_In_Turn : DataStoreOwnershipPublicationTests
    {
        private RecordingReconciler _reconciler = null!;

        [SetUp]
        public async Task Setup()
        {
            _reconciler = new RecordingReconciler();

            Dictionary<string, object> responses = new()
            {
                ["tenant-a"] = new[]
                {
                    DataStoreJson(1, "A", TenantAPrimary, Derivative("ReadReplica", SharedReplica)),
                },
                ["tenant-b"] = new[]
                {
                    DataStoreJson(2, "B", TenantBPrimary, Derivative("Snapshot", SnapshotConnection)),
                },
            };

            var provider = CreateProvider(
                HandlerFor(responses),
                new RecordingLogger<ConfigurationServiceDataStoreProvider>(),
                _reconciler
            );

            await provider.LoadDataStores("tenant-a");
            await provider.LoadDataStores("tenant-b");
        }

        [Test]
        public void It_reconciles_once_per_publication()
        {
            _reconciler.Snapshots.Should().HaveCount(2);
        }

        [Test]
        public void It_versions_monotonically()
        {
            _reconciler.Snapshots.Select(snapshot => snapshot.Version).Should().Equal(1L, 2L);
        }

        /// <summary>
        /// The second publication carries both tenants, not only the one that was loaded. A consumer
        /// deciding what it may stop owning cannot answer that from one tenant's configuration.
        /// </summary>
        [Test]
        public void It_publishes_the_union_of_every_loaded_tenant()
        {
            ConnectionStringsOf(_reconciler.Snapshots[1])
                .Should()
                .BeEquivalentTo(TenantAPrimary, SharedReplica, TenantBPrimary, SnapshotConnection);
        }

        [Test]
        public void It_publishes_only_the_first_tenant_before_the_second_is_loaded()
        {
            ConnectionStringsOf(_reconciler.Snapshots[0])
                .Should()
                .BeEquivalentTo(TenantAPrimary, SharedReplica);
        }

        [Test]
        public void It_names_the_tenant_and_data_store_of_each_owner()
        {
            _reconciler
                .Snapshots[1]
                .Owners.Should()
                .Contain(owner =>
                    owner.TenantKey == "tenant-b"
                    && owner.ParentDataStoreId == 2
                    && owner.Kind == EffectiveTargetKind.Snapshot
                    && owner.ConfiguredConnectionString == SnapshotConnection
                );
        }
    }

    /// <summary>
    /// Two tenants configured with the same connection string both own it. A consumer must see both
    /// claims, or dropping one tenant would look like the string becoming unowned.
    /// </summary>
    [TestFixture]
    public class Given_Two_Tenants_Sharing_A_Connection_String : DataStoreOwnershipPublicationTests
    {
        [Test]
        public async Task It_publishes_one_owner_per_claiming_tenant()
        {
            RecordingReconciler reconciler = new();

            Dictionary<string, object> responses = new()
            {
                ["tenant-a"] = new[] { DataStoreJson(1, "A", SharedReplica) },
                ["tenant-b"] = new[] { DataStoreJson(2, "B", SharedReplica) },
            };

            var provider = CreateProvider(
                HandlerFor(responses),
                new RecordingLogger<ConfigurationServiceDataStoreProvider>(),
                reconciler
            );

            await provider.LoadDataStores("tenant-a");
            await provider.LoadDataStores("tenant-b");

            ImmutableArray<ConfiguredTargetOwner> owners = reconciler.Snapshots[1].Owners;

            owners.Should().HaveCount(2);
            owners.Select(owner => owner.TenantKey).Should().BeEquivalentTo("tenant-a", "tenant-b");
            owners.Should().OnlyContain(owner => owner.ConfiguredConnectionString == SharedReplica);
        }
    }

    [TestFixture]
    public class Given_A_Failed_Configuration_Load : DataStoreOwnershipPublicationTests
    {
        private RecordingReconciler _reconciler = null!;
        private ConfigurationServiceDataStoreProvider _provider = null!;

        [SetUp]
        public void Setup()
        {
            _reconciler = new RecordingReconciler();

            _provider = CreateProvider(
                new ResponseHandler(_ => throw new HttpRequestException("the service is down")),
                new RecordingLogger<ConfigurationServiceDataStoreProvider>(),
                _reconciler
            );
        }

        [Test]
        public async Task It_fails_the_load()
        {
            Func<Task> load = () => _provider.LoadDataStores("tenant-a");

            await load.Should().ThrowAsync<InvalidOperationException>();
        }

        /// <summary>
        /// Nothing was published, so nothing may be reconciled: a reconciler that acted on a failed
        /// load would retire owners the configuration still claims.
        /// </summary>
        [Test]
        public async Task It_reconciles_nothing()
        {
            try
            {
                await _provider.LoadDataStores("tenant-a");
            }
            catch (InvalidOperationException)
            {
                // The throw is asserted separately.
            }

            _reconciler.Snapshots.Should().BeEmpty();
        }

        /// <summary>
        /// And it consumes no version, so the next successful load is still version 1 - a reconciler
        /// that rejects non-increasing versions must not be handed a gap it reads as a lost snapshot.
        /// </summary>
        [Test]
        public async Task It_consumes_no_version()
        {
            try
            {
                await _provider.LoadDataStores("tenant-a");
            }
            catch (InvalidOperationException)
            {
                // Asserted separately.
            }

            RecordingReconciler reconciler = new();
            var provider = CreateProvider(
                HandlerFor(
                    new Dictionary<string, object>
                    {
                        ["tenant-a"] = new[] { DataStoreJson(1, "A", TenantAPrimary) },
                    }
                ),
                new RecordingLogger<ConfigurationServiceDataStoreProvider>(),
                reconciler
            );

            await provider.LoadDataStores("tenant-a");

            reconciler.Snapshots[0].Version.Should().Be(1);
        }
    }

    [TestFixture]
    public class Given_A_Reconciler_That_Throws : DataStoreOwnershipPublicationTests
    {
        private RecordingLogger<ConfigurationServiceDataStoreProvider> _logger = null!;
        private ThrowingReconciler _throwing = null!;
        private RecordingReconciler _after = null!;
        private IList<DataStore> _loaded = null!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new RecordingLogger<ConfigurationServiceDataStoreProvider>();
            _throwing = new ThrowingReconciler(new InvalidOperationException("reconciler failed"));
            _after = new RecordingReconciler();

            var provider = CreateProvider(
                HandlerFor(
                    new Dictionary<string, object>
                    {
                        ["tenant-a"] = new[]
                        {
                            DataStoreJson(1, "A", TenantAPrimary, Derivative("Snapshot", SnapshotConnection)),
                        },
                    }
                ),
                _logger,
                _throwing,
                _after
            );

            _loaded = await provider.LoadDataStores("tenant-a");
        }

        [Test]
        public void It_does_not_fail_the_load()
        {
            _loaded.Should().ContainSingle();
        }

        /// <summary>
        /// The configuration is already published and correct by the time reconcilers run, so one
        /// failing must not deprive the others of a snapshot they can act on.
        /// </summary>
        [Test]
        public void It_still_invokes_the_next_reconciler()
        {
            _throwing.Calls.Should().Be(1);
            _after.Snapshots.Should().ContainSingle();
        }

        [Test]
        public void It_warns_about_the_failure()
        {
            _logger
                .Records.Where(record => record.Level == LogLevel.Warning)
                .Should()
                .ContainSingle()
                .Which.Message.Should()
                .Contain("ThrowingReconciler");
        }

        /// <summary>
        /// The warning names the reconciler and the tenant. It must not name what the reconciler was
        /// reconciling: a connection string in a log is a secret in a log.
        /// </summary>
        [Test]
        public void It_logs_no_connection_material()
        {
            IEnumerable<string> loggedText =
            [
                .. _logger.Records.Select(record => record.Message),
                .. _logger.Records.SelectMany(record =>
                    record.Properties.Values.Select(value => value?.ToString() ?? string.Empty)
                ),
                .. _logger.Records.Select(record => record.Exception?.ToString() ?? string.Empty),
            ];

            loggedText.Should().NotContain(text => text.Contains(TenantAPrimary, StringComparison.Ordinal));
            loggedText
                .Should()
                .NotContain(text => text.Contains(SnapshotConnection, StringComparison.Ordinal));
        }
    }

    [TestFixture]
    public class Given_Overlapping_Tenant_Publications : DataStoreOwnershipPublicationTests
    {
        /// <summary>
        /// Two loads that are genuinely in flight at once must serialize: their versions must be
        /// distinct and increasing, and one reconciliation must finish before the next begins. A
        /// reconciler that observed two snapshots at once could retire an owner the newer
        /// configuration still claims.
        /// </summary>
        /// <remarks>
        /// The reconciler is the detector: it counts how many reconciliations are inside it at once and
        /// holds each one there long enough that a second, if the lock did not exist, would arrive
        /// while the first is still present. With the lock the count never exceeds one.
        /// </remarks>
        [TestCase("tenant-a", "tenant-b")]
        [TestCase("tenant-b", "tenant-a")]
        public async Task It_reconciles_one_publication_at_a_time(string first, string second)
        {
            int inside = 0;
            bool overlapped = false;
            List<long> versions = [];

            RecordingReconciler reconciler = new(snapshot =>
            {
                if (Interlocked.Increment(ref inside) > 1)
                {
                    overlapped = true;
                }

                // Held until the other load has been given every chance to arrive. Waiting on an
                // event that is never set is the hold: it expires on its own, so nothing here depends
                // on a sleep the analyzer would rightly object to, and with the lock in place the
                // other reconciliation simply cannot be inside while this one waits.
                using (ManualResetEventSlim hold = new(initialState: false))
                {
                    hold.Wait(TimeSpan.FromMilliseconds(100));
                }

                lock (versions)
                {
                    versions.Add(snapshot.Version);
                }

                Interlocked.Decrement(ref inside);
            });

            Dictionary<string, object> responses = new()
            {
                ["tenant-a"] = new[] { DataStoreJson(1, "A", TenantAPrimary) },
                ["tenant-b"] = new[] { DataStoreJson(2, "B", TenantBPrimary) },
            };

            var provider = CreateProvider(
                HandlerFor(responses),
                new RecordingLogger<ConfigurationServiceDataStoreProvider>(),
                reconciler
            );

            // Started on the thread pool rather than awaited in turn: the fake token handler and HTTP
            // handler both complete synchronously, so awaiting them in sequence would let the first
            // load finish before the second began and there would be nothing to serialize.
            Task<IList<DataStore>> firstLoad = Task.Run(() => provider.LoadDataStores(first));
            Task<IList<DataStore>> secondLoad = Task.Run(() => provider.LoadDataStores(second));

            await Task.WhenAll(firstLoad, secondLoad);

            overlapped.Should().BeFalse("publication is serialized by the provider-wide lock");
            versions.Order().Should().Equal(1L, 2L);

            // Whichever order they completed in, the final snapshot is the full union.
            ConnectionStringsOf(reconciler.Snapshots[^1])
                .Should()
                .BeEquivalentTo(TenantAPrimary, TenantBPrimary);
        }

        /// <summary>
        /// Two concurrent loads of the *same* tenant still serialize, and the last one to publish is
        /// the one whose configuration is left in place.
        /// </summary>
        [Test]
        public async Task It_serializes_two_loads_of_one_tenant()
        {
            RecordingReconciler reconciler = new();

            var provider = CreateProvider(
                HandlerFor(
                    new Dictionary<string, object>
                    {
                        ["tenant-a"] = new[] { DataStoreJson(1, "A", TenantAPrimary) },
                    }
                ),
                new RecordingLogger<ConfigurationServiceDataStoreProvider>(),
                reconciler
            );

            await Task.WhenAll(
                Task.Run(() => provider.LoadDataStores("tenant-a")),
                Task.Run(() => provider.LoadDataStores("tenant-a"))
            );

            reconciler.Snapshots.Select(snapshot => snapshot.Version).Should().Equal(1L, 2L);
            ConnectionStringsOf(reconciler.Snapshots[1]).Should().BeEquivalentTo(TenantAPrimary);
        }
    }

    [TestFixture]
    public class Given_A_Refresh_That_Triggers_A_Publication : DataStoreOwnershipPublicationTests
    {
        /// <summary>
        /// The expiry refresh takes its per-tenant lock and then calls the load, which takes the
        /// publication lock. The order is always tenant then publication and there is no inverse path,
        /// so the nesting cannot deadlock. This drives it end to end rather than reasoning about it.
        /// </summary>
        [Test]
        public async Task It_publishes_without_deadlocking()
        {
            RecordingReconciler reconciler = new();
            FakeTimeProvider time = new(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));

            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() =>
                    tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._)
                )
                .Returns("valid-token");

            HttpClient httpClient = new(
                HandlerFor(
                    new Dictionary<string, object>
                    {
                        ["tenant-a"] = new[] { DataStoreJson(1, "A", TenantAPrimary) },
                    }
                )
            )
            {
                BaseAddress = new Uri("https://api.example.com/"),
            };

            ConfigurationServiceDataStoreProvider provider = new(
                new ConfigurationServiceApiClient(httpClient),
                tokenHandler,
                new ConfigurationServiceContext("clientId", "secret", "scope"),
                new RecordingLogger<ConfigurationServiceDataStoreProvider>(),
                new ConnectionStringDecryptionService(EncryptionKey),
                new CacheSettings
                {
                    DataStoreCacheRefreshEnabled = true,
                    DataStoreCacheExpirationSeconds = 60,
                },
                time,
                [reconciler]
            );

            await provider.LoadDataStores("tenant-a");
            time.Advance(TimeSpan.FromSeconds(61));

            Task refresh = provider.RefreshInstancesIfExpiredAsync("tenant-a");

            Task completed = await Task.WhenAny(refresh, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.Should().BeSameAs(refresh, "the nested locks must not deadlock");
            await refresh;

            reconciler.Snapshots.Select(snapshot => snapshot.Version).Should().Equal(1L, 2L);
        }

        private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
        {
            private DateTimeOffset _now = start;

            public override DateTimeOffset GetUtcNow() => _now;

            public void Advance(TimeSpan amount) => _now += amount;
        }
    }

    [TestFixture]
    public class Given_A_Provider_Invalid_Derivative : DataStoreOwnershipPublicationTests
    {
        /// <summary>
        /// Publication copies strings and parses nothing, so a configured value no provider could open
        /// is published like any other owner. Parsing here would abort the publication of an entire
        /// tenant over one bad derivative.
        /// </summary>
        [Test]
        public async Task It_is_published_verbatim()
        {
            const string NotAConnectionString = "  this is not a connection string at all ;; ";
            RecordingReconciler reconciler = new();

            var provider = CreateProvider(
                HandlerFor(
                    new Dictionary<string, object>
                    {
                        ["tenant-a"] = new[]
                        {
                            DataStoreJson(
                                1,
                                "A",
                                TenantAPrimary,
                                Derivative("ReadReplica", NotAConnectionString)
                            ),
                        },
                    }
                ),
                new RecordingLogger<ConfigurationServiceDataStoreProvider>(),
                reconciler
            );

            await provider.LoadDataStores("tenant-a");

            reconciler
                .Snapshots[0]
                .Owners.Should()
                .Contain(owner =>
                    owner.Kind == EffectiveTargetKind.ReadReplica
                    && owner.ConfiguredConnectionString == NotAConnectionString
                );
        }
    }

    [TestFixture]
    public class Given_No_Registered_Reconcilers : DataStoreOwnershipPublicationTests
    {
        /// <summary>
        /// How this unit ships: the seam exists and versions advance, but nothing consumes it yet.
        /// </summary>
        [Test]
        public async Task It_loads_normally()
        {
            var provider = CreateProvider(
                HandlerFor(
                    new Dictionary<string, object>
                    {
                        ["tenant-a"] = new[] { DataStoreJson(1, "A", TenantAPrimary) },
                    }
                ),
                new RecordingLogger<ConfigurationServiceDataStoreProvider>()
            );

            IList<DataStore> loaded = await provider.LoadDataStores("tenant-a");

            loaded.Should().ContainSingle();
            provider.GetById(1, "tenant-a").Should().NotBeNull();
        }
    }
}
