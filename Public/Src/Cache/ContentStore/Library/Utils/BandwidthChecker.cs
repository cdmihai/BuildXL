﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.Host.Configuration;

namespace BuildXL.Cache.ContentStore.Utils
{
    /// <summary>
    /// Checks that a copy has a minimum bandwidth, and cancells copies otherwise.
    /// </summary>
    public class BandwidthChecker
    {
        private const double BytesInMb = 1024 * 1024;

        private readonly HistoricalBandwidthLimitSource _historicalBandwidthLimitSource;
        private readonly IBandwidthLimitSource _bandwidthLimitSource;
        private readonly Configuration _config;

        /// <nodoc />
        public BandwidthChecker(Configuration config)
        {
            _config = config;
            _bandwidthLimitSource = config.MinimumBandwidthMbPerSec == null
                ? (IBandwidthLimitSource)new HistoricalBandwidthLimitSource(config.HistoricalBandwidthRecordsStored)
                : new ConstantBandwidthLimit(config.MinimumBandwidthMbPerSec.Value);
            _historicalBandwidthLimitSource = _bandwidthLimitSource as HistoricalBandwidthLimitSource;
        }

        /// <summary>
        /// Checks that a copy has a minimum bandwidth, and cancells it otherwise.
        /// </summary>
        /// <param name="context">The context of the operation.</param>
        /// <param name="copyTaskFactory">Function that will trigger the copy.</param>
        /// <param name="destinationStream">Stream into which the copy is being made. Used to meassure bandwidth.</param>
        public async Task<CopyFileResult> CheckBandwidthAtIntervalAsync(OperationContext context, Func<CancellationToken, Task<CopyFileResult>> copyTaskFactory, Stream destinationStream)
        {
            if (_historicalBandwidthLimitSource != null)
            {
                var startPosition = destinationStream.Position;
                var timer = Stopwatch.StartNew();

                try
                {
                    return await impl();
                }
                finally
                {
                    timer.Stop();
                    var endPosition = destinationStream.Position;

                    // Bandwidth checker expects speed in MiB/s, so convert it.
                    var bytesCopied = endPosition - startPosition;
                    var speed = bytesCopied / timer.Elapsed.TotalSeconds / (1024 * 1024);
                    _historicalBandwidthLimitSource.AddBandwidthRecord(speed);
                }
            }
            else
            {
                return await impl();
            }

            async Task<CopyFileResult> impl()
            {
                // This method should not fail with exceptions because the resulting task may be left unobserved causing an application to crash
                // (given that the app is configured to fail on unobserved task exceptions).
                var minimumSpeedInMbPerSec = _bandwidthLimitSource.GetMinimumSpeedInMbPerSec() * _config.BandwidthLimitMultiplier;
                minimumSpeedInMbPerSec = Math.Min(minimumSpeedInMbPerSec, _config.MaxBandwidthLimit);

                long previousPosition = 0;
                var copyCompleted = false;
                using var copyCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Token);
                var copyTask = copyTaskFactory(copyCancellation.Token);

                try
                {
                    while (!copyCompleted)
                    {
                        // Wait some time for bytes to be copied
                        var firstCompletedTask = await Task.WhenAny(copyTask,
                            Task.Delay(_config.BandwidthCheckInterval, context.Token));

                        copyCompleted = firstCompletedTask == copyTask;
                        if (copyCompleted)
                        {
                            return await copyTask;
                        }
                        else if (context.Token.IsCancellationRequested)
                        {
                            context.Token.ThrowIfCancellationRequested();
                        }

                        // Copy is not completed and operation has not been canceled, perform
                        // bandwidth check 
                        try
                        {
                            var position = destinationStream.Position;

                            var receivedMiB = (position - previousPosition) / BytesInMb;
                            var currentSpeed = receivedMiB / _config.BandwidthCheckInterval.TotalSeconds;
                            if (currentSpeed == 0 || currentSpeed < minimumSpeedInMbPerSec)
                            {
                                return new CopyFileResult(CopyFileResult.ResultCode.CopyBandwidthTimeoutError, $"Average speed was {currentSpeed}MiB/s - under {minimumSpeedInMbPerSec}MiB/s requirement. Aborting copy with {position} copied");
                            }

                            previousPosition = position;
                        }
                        catch (ObjectDisposedException)
                        {
                            // If the check task races with the copy completing, it might attempt to check the position of a disposed stream.
                            // Don't bother logging because the copy completed successfully.
                        }
                    }

                    return await copyTask;
                }
                finally
                {
                    if (!copyCompleted)
                    {
                        // Ensure that we signal the copy to cancel
                        copyCancellation.Cancel();
                        traceCopyTaskFailures(copyTask);
                    }
                }

                void traceCopyTaskFailures(Task task)
                {
                    // When the operation is cancelled, it is possible for the copy operation to fail.
                    // In this case we still want to trace the failure (but just with the debug severity and not with the error),
                    // but we should exclude ObjectDisposedException completely.
                    // That's why we don't use task.FireAndForget but tracing inside the task's continuation.
                    copyTask.ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            if (!(t.Exception?.InnerException is ObjectDisposedException))
                            {
                                context.TraceDebug($"Checked copy failed. {t.Exception}");
                            }
                        }
                    });
                }
            }
        }

        /// <nodoc />
        public class Configuration
        {
            /// <nodoc />
            public Configuration(TimeSpan bandwidthCheckInterval, double? minimumBandwidthMbPerSec, double? maxBandwidthLimit, double? bandwidthLimitMultiplier, int? historicalBandwidthRecordsStored)
            {
                BandwidthCheckInterval = bandwidthCheckInterval;
                MinimumBandwidthMbPerSec = minimumBandwidthMbPerSec;
                MaxBandwidthLimit = maxBandwidthLimit ?? double.MaxValue;
                BandwidthLimitMultiplier = bandwidthLimitMultiplier ?? 1;
                HistoricalBandwidthRecordsStored = historicalBandwidthRecordsStored ?? 64;

                Contract.Assert(MaxBandwidthLimit > 0);
                Contract.Assert(BandwidthLimitMultiplier > 0);
                Contract.Assert(HistoricalBandwidthRecordsStored > 0);
            }

            /// <nodoc />
            public static readonly Configuration Default = new Configuration(TimeSpan.FromSeconds(30), null, null, null, null);

            /// <nodoc />
            public static readonly Configuration Disabled = new Configuration(TimeSpan.FromMilliseconds(int.MaxValue), minimumBandwidthMbPerSec: 0, null, null, null);

            /// <nodoc />
            public static Configuration FromDistributedContentSettings(DistributedContentSettings dcs)
            {
                if (!dcs.IsBandwidthCheckEnabled)
                {
                    return Disabled;
                }

                return new Configuration(
                    TimeSpan.FromSeconds(dcs.BandwidthCheckIntervalSeconds),
                    dcs.MinimumSpeedInMbPerSec,
                    dcs.MaxBandwidthLimit,
                    dcs.BandwidthLimitMultiplier,
                    dcs.HistoricalBandwidthRecordsStored);
            }

            /// <nodoc />
            public TimeSpan BandwidthCheckInterval { get; }

            /// <nodoc />
            public double? MinimumBandwidthMbPerSec { get; }

            /// <nodoc />
            public double MaxBandwidthLimit { get; }

            /// <nodoc />
            public double BandwidthLimitMultiplier { get; }

            /// <nodoc />
            public int HistoricalBandwidthRecordsStored { get; }
        }
    }

    /// <nodoc />
    public class BandwidthTooLowException : Exception
    {
        /// <nodoc />
        public BandwidthTooLowException(string message)
            : base(message)
        {
        }
    }
}