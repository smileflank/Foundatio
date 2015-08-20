﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Logging;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.ServiceProviders;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Jobs {
    public class JobTests : CaptureTests {
        public JobTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
            MinimumLogLevel = LogLevel.Trace;
            EnableLogging = false;
        }

        [Fact]
        public void CanRunJobs() {
            var job = new HelloWorldJob();
            Assert.Equal(0, job.RunCount);
            job.Run();
            Assert.Equal(1, job.RunCount);

            job.RunContinuous(iterationLimit: 2);
            Assert.Equal(3, job.RunCount);

            job.RunContinuous(token: new CancellationTokenSource(TimeSpan.FromMilliseconds(100)).Token);
            Assert.InRange(job.RunCount, 5, 12);

            var jobInstance = JobRunner.CreateJobInstance(typeof(HelloWorldJob).AssemblyQualifiedName);
            Assert.NotNull(jobInstance);
            Assert.Equal(0, ((HelloWorldJob)jobInstance).RunCount);
            Assert.Equal(JobResult.Success, jobInstance.Run());
            Assert.Equal(1, ((HelloWorldJob)jobInstance).RunCount);
        }

        [Fact]
        public async void CanRunMultipleInstances()
        {
            HelloWorldJob.GlobalRunCount = 0;

            var tokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await JobRunner.RunContinuousAsync(typeof(HelloWorldJob), null, 5, 1, tokenSource.Token);
            Assert.Equal(5, HelloWorldJob.GlobalRunCount);

            HelloWorldJob.GlobalRunCount = 0;

            tokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await JobRunner.RunContinuousAsync(typeof(HelloWorldJob), null, 5, 5, tokenSource.Token);
            Assert.Equal(25, HelloWorldJob.GlobalRunCount);
        }

        [Fact]
        public async void CanCancelContinuousJobs()
        {
            var job = new HelloWorldJob();
            var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            job.RunContinuous(TimeSpan.FromSeconds(1), 5, cancellationTokenSource.Token);
            Assert.Equal(1, job.RunCount);

            var jobs = new List<HelloWorldJob>(new[] {
                new HelloWorldJob(),
                new HelloWorldJob(),
                new HelloWorldJob()
            });

            var tokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var token = tokenSource.Token;

            await Task.WhenAll(jobs.Select(async j => await j.RunContinuousAsync(null, -1, token)));
        }

        [Fact]
        public void CanRunJobsWithLocks() {
            var job = new WithLockingJob();
            Assert.Equal(0, job.RunCount);
            job.Run();
            Assert.Equal(1, job.RunCount);

            job.RunContinuous(iterationLimit: 2);
            Assert.Equal(3, job.RunCount);

            Task.Run(() => job.Run());
            Task.Run(() => job.Run());
            Thread.Sleep(200);
            Assert.Equal(4, job.RunCount);
        }

        [Fact]
        public async void CanRunThrottledJobs() {
            var client = new InMemoryCacheClient();
            var jobs = new List<ThrottledJob>(new[] {
                new ThrottledJob(client),
                new ThrottledJob(client),
                new ThrottledJob(client)
            });

            var tokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Task.WhenAll(jobs.Select(
                async job => await job.RunContinuousAsync(cancellationToken: tokenSource.Token))
            );

            Assert.InRange(jobs.Sum(j => j.RunCount), 6, 14);
        }

        [Fact]
        public void CanBootstrapJobs() {
            ServiceProvider.SetServiceProvider(typeof(JobTests));
            Assert.NotNull(ServiceProvider.Current);
            Assert.Equal(ServiceProvider.Current.GetType(), typeof(MyBootstrappedServiceProvider));

            ServiceProvider.SetServiceProvider(typeof(MyBootstrappedServiceProvider));
            Assert.NotNull(ServiceProvider.Current);
            Assert.Equal(ServiceProvider.Current.GetType(), typeof(MyBootstrappedServiceProvider));

            var job = ServiceProvider.Current.GetService<WithDependencyJob>();
            Assert.NotNull(job);
            Assert.NotNull(job.Dependency);
            Assert.Equal(5, job.Dependency.MyProperty);

            var jobInstance = JobRunner.CreateJobInstance("Foundatio.Tests.Jobs.HelloWorldJob,Foundatio.Tests");
            Assert.NotNull(job);
            Assert.NotNull(job.Dependency);
            Assert.Equal(5, job.Dependency.MyProperty);

            ServiceProvider.SetServiceProvider("Foundatio.Tests.Jobs.MyBootstrappedServiceProvider,Foundatio.Tests", "Foundatio.Tests.Jobs.HelloWorldJob,Foundatio.Tests");
            jobInstance = JobRunner.CreateJobInstance("Foundatio.Tests.Jobs.HelloWorldJob,Foundatio.Tests");
            Assert.NotNull(job);
            Assert.NotNull(job.Dependency);
            Assert.Equal(5, job.Dependency.MyProperty);

            var result = jobInstance.Run();
            Assert.Equal(true, result.IsSuccess);
            Assert.True(jobInstance is HelloWorldJob);
        }

        [Fact]
        public void CanRunQueueJob() {
            const int workItemCount = 500;
            var metrics = new InMemoryMetricsClient();
            var queue = new InMemoryQueue<SampleQueueWorkItem>(0, TimeSpan.Zero, metrics: metrics);

            for (int i = 0; i < workItemCount; i++)
                queue.Enqueue(new SampleQueueWorkItem { Created = DateTime.Now, Path = "somepath" + i });

            var job = new SampleQueueJob(queue, metrics);
            job.RunUntilEmpty(new CancellationTokenSource(10000).Token);
            metrics.DisplayStats();

            Assert.Equal(0, queue.GetQueueCount());
        }
    }
}
