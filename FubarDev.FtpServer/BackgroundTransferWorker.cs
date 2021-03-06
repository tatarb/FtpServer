﻿//-----------------------------------------------------------------------
// <copyright file="BackgroundTransferWorker.cs" company="Fubar Development Junker">
//     Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>
// <author>Mark Junker</author>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FubarDev.FtpServer.FileSystem;

namespace FubarDev.FtpServer
{
    internal class BackgroundTransferWorker : IDisposable
    {
        private readonly ManualResetEvent _event = new ManualResetEvent(false);

        private readonly IFtpLog _log;

        private CancellationTokenSource _cancellationTokenSource;

        private bool _disposedValue;

        public BackgroundTransferWorker(FtpServer server)
        {
            _log = server?.LogManager?.CreateLog(GetType());
            Queue = new BackgroundTransferQueue(_event);
        }

        public BackgroundTransferQueue Queue { get; }

        public bool HasData { get; private set; }

        internal BackgroundTransferEntry CurrentEntry { get; private set; }

        public Task Start(CancellationTokenSource cts)
        {
            if (_cancellationTokenSource != null)
                throw new InvalidOperationException("Background transfer worker already started");
            _cancellationTokenSource = cts;
            return Task.Run(() => ProcessQueue(cts.Token), cts.Token);
        }

        public void Enqueue(BackgroundTransferEntry entry)
        {
            lock (Queue)
            {
                Queue.Enqueue(entry);
            }
        }

        public IReadOnlyCollection<Tuple<string, BackgroundTransferStatus>> GetStates()
        {
            var result = new List<Tuple<string, BackgroundTransferStatus>>();
            lock (Queue)
            {
                var current = CurrentEntry;
                if (current != null)
                {
                    result.Add(Tuple.Create(current.BackgroundTransfer.TransferId, current.Status));
                }
                result.AddRange(
                    Queue.GetEntries()
                        .Select(entry => Tuple.Create(entry.BackgroundTransfer.TransferId, entry.Status)));
            }
            return result;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _cancellationTokenSource?.Cancel();
                }

                _disposedValue = true;
            }
        }

        private BackgroundTransferEntry GetNextEntry()
        {
            lock (Queue)
            {
                var item = Queue.Dequeue();
                CurrentEntry = item;
                return item;
            }
        }

        private void ProcessQueue(CancellationToken cancellationToken)
        {
            var handles = new[]
            {
                cancellationToken.WaitHandle, _event
            };
            _log?.Debug("Starting background transfer worker.");
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var handleIndex = WaitHandle.WaitAny(handles);
                    if (handleIndex == 0)
                        break;

                    HasData = true;

                    try
                    {
                        BackgroundTransferEntry backgroundTransferEntry;
                        while ((backgroundTransferEntry = GetNextEntry()) != null)
                        {
                            Debug.Assert(backgroundTransferEntry != null, "backgroundTransferEntry must not be null (internal error)");
                            var log = backgroundTransferEntry.Log;
                            var backgroundTransfer = backgroundTransferEntry.BackgroundTransfer;
                            try
                            {
                                var bt = backgroundTransfer;
                                log?.Info("Starting background transfer {0}", bt.TransferId);
                                backgroundTransferEntry.Status = BackgroundTransferStatus.Transferring;
                                var task = bt.Start(cancellationToken);
                                var cancelledTask = task
                                    .ContinueWith(
                                        t =>
                                        {
                                            // Nothing to do
                                            log?.Warn("Background transfer {0} cancelled", bt.TransferId);
                                        },
                                        TaskContinuationOptions.OnlyOnCanceled);
                                var faultedTask = task
                                    .ContinueWith(
                                        t =>
                                        {
                                            log?.Error(t.Exception, "Background transfer {0} faulted", bt.TransferId);
                                        },
                                        TaskContinuationOptions.OnlyOnFaulted);
                                var completedTask = task
                                    .ContinueWith(
                                        t =>
                                        {
                                            // Nothing to do
                                            log?.Info("Completed background transfer {0}", bt.TransferId);
                                        },
                                        TaskContinuationOptions.NotOnCanceled);

                                try
                                {
                                    Task.WaitAll(cancelledTask, faultedTask, completedTask);
                                }
                                catch (AggregateException ex) when (ex.InnerExceptions.All(x => x is TaskCanceledException))
                                {
                                    // Ignore AggregateException when it only contains TaskCancelledException
                                }

                                log?.Trace("Background transfer {0} finished", bt.TransferId);
                            }
                            catch (Exception ex)
                            {
                                log?.Error(ex, "Error during execution of background transfer {0}", backgroundTransfer.TransferId);
                            }
                            finally
                            {
                                backgroundTransfer.Dispose();
                            }

                            backgroundTransferEntry.Status = BackgroundTransferStatus.Finished;
                            CurrentEntry = null;
                        }
                    }
                    finally
                    {
                        HasData = false;
                    }
                }
                _log?.Info("Cancellation requested - stopping background transfer worker.");
            }
            finally
            {
                _log?.Debug("Background transfer worker stopped.");
                Queue.Dispose();
            }
        }
    }
}
