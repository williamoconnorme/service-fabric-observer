﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Fabric;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.TelemetryLib
{
    /// <summary>
    /// Helper class to execute fabric client operations with retry.
    /// </summary>
    public static class FabricClientRetryHelper
    {
        private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Helper method to execute given function with defaultFabricClientRetryErrors and default Operation Timeout.
        /// </summary>
        /// <param name="function">Action to be performed.</param>
        /// <param name="cancellationToken">Cancellation token for Async operation.</param>
        /// <returns>Task object.</returns>
        public static async Task<T> ExecuteFabricActionWithRetryAsync<T>(Func<Task<T>> function, CancellationToken cancellationToken)
        {
            return await ExecuteFabricActionWithRetryAsync(
                          function,
                          new FabricClientRetryErrors(),
                          DefaultOperationTimeout,
                          cancellationToken).ConfigureAwait(true);
        }

        /// <summary>
        /// Helper method to execute given function with given user FabricClientRetryErrors and given Operation Timeout.
        /// </summary>
        /// <param name="function">Action to be performed.</param>
        /// <param name="errors">Fabric Client Errors that can be retired.</param>
        /// <param name="operationTimeout">Timeout for the operation.</param>
        /// <param name="cancellationToken">Cancellation token for Async operation.</param>
        /// <returns>Task object.</returns>
        public static async Task<T> ExecuteFabricActionWithRetryAsync<T>(
                                        Func<Task<T>> function,
                                        FabricClientRetryErrors errors,
                                        TimeSpan operationTimeout,
                                        CancellationToken cancellationToken)
        {
            bool needToWait = false;
            var watch = new Stopwatch();
            watch.Start();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (needToWait)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(true);
                }

                try
                {
                    return await function().ConfigureAwait(true);
                }
                catch (Exception e)
                {
                    if (!HandleException(e, errors, out bool retryElseSuccess))
                    {
                        throw;
                    }

                    if (retryElseSuccess)
                    {
                        if (watch.Elapsed > operationTimeout)
                        {
                            throw;
                        }

                        needToWait = true;

                        continue;
                    }

                    return default;
                }
            }
        }

        private static bool HandleException(Exception e, FabricClientRetryErrors errors, out bool retryElseSuccess)
        {
            var fabricException = e as FabricException;

            if (errors.RetryableExceptions.Contains(e.GetType()))
            {
                retryElseSuccess = true /*retry*/;
                return true;
            }

            if (fabricException != null && errors.RetryableFabricErrorCodes.Contains(fabricException.ErrorCode))
            {
                retryElseSuccess = true /*retry*/;
                return true;
            }

            if (errors.RetrySuccessExceptions.Contains(e.GetType()))
            {
                retryElseSuccess = false /*success*/;
                return true;
            }

            if (fabricException != null
                && errors.RetrySuccessFabricErrorCodes.Contains(fabricException.ErrorCode))
            {
                retryElseSuccess = false /*success*/;
                return true;
            }

            if (e.GetType() == typeof(FabricTransientException))
            {
                retryElseSuccess = true /*retry*/;
                return true;
            }

            if (fabricException?.InnerException != null)
            {
                if (fabricException.InnerException is COMException ex && errors.InternalRetrySuccessFabricErrorCodes.Contains((uint)ex.ErrorCode))
                {
                    retryElseSuccess = false /*success*/;
                    return true;
                }
            }

            retryElseSuccess = false;
            return false;
        }
    }
}