// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

[TestFixture]
public class Given_managed_job_enqueue_is_disabled
{
    private static ServiceProvider Services() =>
        JobServices.WithRefreshHandler(services =>
        {
            services.AddSingleton(JobClaimPolicy.ExcludeManagedDataStores);
            services.AddJobHandler<
                CreateHandler,
                CreateManagedDataStorePayload,
                CreateManagedDataStorePayloadValidator
            >(ManagedJobTypes.Create, 1);
            services.AddJobHandler<
                DeleteHandler,
                DeleteManagedDataStorePayload,
                DeleteManagedDataStorePayloadValidator
            >(ManagedJobTypes.Delete, 1);
        });

    public sealed class CreateHandler : IJobHandler<CreateManagedDataStorePayload>
    {
        public Task ExecuteAsync(
            JobExecutionContext context,
            CreateManagedDataStorePayload payload,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("Enqueue-only handler must never execute.");
    }

    public sealed class DeleteHandler : IJobHandler<DeleteManagedDataStorePayload>
    {
        public Task ExecuteAsync(
            JobExecutionContext context,
            DeleteManagedDataStorePayload payload,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("Enqueue-only handler must never execute.");
    }

    [TestCase(ManagedJobTypes.Create)]
    [TestCase(ManagedJobTypes.Delete)]
    public async Task It_refuses_only_managed_enqueue_and_retains_the_registration(string type)
    {
        using var services = Services();
        services.GetRequiredService<IJobHandlerRegistry>().TryGet(type, out _).Should().BeTrue();
        (
            await services
                .GetRequiredService<IJobEnqueuer>()
                .EnqueueAsync(new(type, 1, """{"manageId":1}"""), null, default)
        )
            .Should()
            .BeOfType<JobEnqueueResult.ManagementDisabled>();
        services.GetRequiredService<RecordingJobRepository>().Enqueued.Should().BeEmpty();
    }

    [TestCase("manageddatastore.create")]
    [TestCase("ManagedDataStore.Create ")]
    [TestCase("ManagedDataStore.Delete ")]
    [TestCase("Unrelated.Unknown")]
    public async Task It_keeps_normal_validation_for_other_types(string type)
    {
        using var services = Services();
        (await services.GetRequiredService<IJobEnqueuer>().EnqueueAsync(new(type, 1, "{}"), null, default))
            .Should()
            .BeOfType<JobEnqueueResult.FailureUnsupportedType>();
        services.GetRequiredService<RecordingJobRepository>().Enqueued.Should().BeEmpty();
    }

    [Test]
    public async Task It_allows_refresh_jobs_and_still_validates_their_payloads()
    {
        using var services = Services();
        var enqueuer = services.GetRequiredService<IJobEnqueuer>();
        (
            await enqueuer.EnqueueAsync(
                new(JobServices.RefreshJobType, 2, """{"dataStoreId":7,"dataStoreCode":"north-1"}"""),
                null,
                default
            )
        )
            .Should()
            .BeOfType<JobEnqueueResult.Success>();
        (await enqueuer.EnqueueAsync(new(JobServices.RefreshJobType, 2, "{}"), null, default))
            .Should()
            .BeOfType<JobEnqueueResult.FailurePayloadInvalid>();
        services.GetRequiredService<RecordingJobRepository>().Enqueued.Should().ContainSingle();
    }
}
