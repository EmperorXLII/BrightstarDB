﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BrightstarDB.Dto;
using BrightstarDB.Model;
#if PORTABLE
using BrightstarDB.Portable.Compatibility;
#elif WINDOWS_PHONE
using BrightstarDB.Mobile.Compatibility;
#else
using System.Collections.Concurrent;
#endif
using BrightstarDB.Query;
using BrightstarDB.Storage;
using BrightstarDB.Storage.Persistence;
using VDS.RDF.Query;

namespace BrightstarDB.Server
{

    ///<summary>
    /// Called by the store worker after the thread has been successfully terminated.
    ///</summary>
    internal delegate void ShutdownContinuation(); 

    /// <summary>
    /// This can be considered a logical store. A logical store is comprised of 2 physical stores. A read store and a write store.
    /// Read requests coming in to the store worker are dispatched to the readStore. The readStore pulls data in from the disk as required, until 
    /// it reaches some predefined levels at which point the cache is flushed.
    /// Update transactions coming to the logical store are put in a queue. The logical store has a worker thread that processes each transaction
    /// one at time. All updates are made against the writeStore. When a transaction completes the read store instance is invalidated so that the 
    /// next read request to arrive forces the data to be re-read from disk.
    /// </summary>
    internal class StoreWorker
    {
        // write store
        private IStore _writeStore;

        // read store
        private IStore _readStore;

        // store name
        private readonly string _storeName;

        // store directory location
        private readonly string _baseLocation;

        // store location
        private readonly string _storeLocation;

        // job queue
        private readonly ConcurrentQueue<Job> _jobs;

        private readonly ITransactionLog _transactionLog;

        private readonly IStoreStatisticsLog _storeStatisticsLog;

        private StatsMonitor _statsMonitor;

        // todo: might need to make this persistent or clear it out now and again.
        private readonly ConcurrentDictionary<string, JobExecutionStatus> _jobExecutionStatus;

        private bool _shutdownRequested;
        private bool _completeRemainingJobs;
        private readonly ManualResetEvent _shutdownCompleted;

        /// <summary>
        /// Event fired after a successful job execution but before the job status is updated to completed
        /// </summary>
        internal event JobCompletedDelegate JobCompleted;

        /// <summary>
        /// Called after we shutdown all jobs and close all file handles.
        /// </summary>
        private ShutdownContinuation _shutdownContinuation;

        private List<WeakReference> _invalidatedReadStores;
 
        /// <summary>
        /// Creates a new server core 
        /// </summary>
        /// <param name="baseLocation">Path to the stores directory</param>
        /// <param name="storeName">Name of store</param>
        public StoreWorker(string baseLocation, string storeName)
        {
            _baseLocation = baseLocation;
            _storeName = storeName;
            _storeLocation = Path.Combine(baseLocation, storeName);
            Logging.LogInfo("StoreWorker created with location {0}", _storeLocation);
            _jobs = new ConcurrentQueue<Job>();
            _jobExecutionStatus = new ConcurrentDictionary<string, JobExecutionStatus>();
            _storeManager = StoreManagerFactory.GetStoreManager();
            _transactionLog = _storeManager.GetTransactionLog(_storeLocation);
            _storeStatisticsLog = _storeManager.GetStatisticsLog(_storeLocation);
            _statsMonitor = new StatsMonitor();
            InitializeStatsMonitor();
            _shutdownCompleted = new ManualResetEvent(false);
            _invalidatedReadStores = new List<WeakReference>();
        }

        /// <summary>
        /// Starts the thread that processes the jobs queue.
        /// </summary>
        public void Start()
        {
            
            ThreadPool.QueueUserWorkItem(ProcessJobs);
        }

        public ITransactionLog TransactionLog
        {
            get { return _transactionLog; }
        }

        public IStoreStatisticsLog StoreStatistics
        {
            get { return _storeStatisticsLog; }
        }

        private void ProcessJobs(object state)
        {
            Logging.LogInfo("Process Jobs Started");

            while (!_shutdownRequested)
            {
                try
                {
                    if (_shutdownRequested && !_completeRemainingJobs) break;
                    Job job;
                    _jobs.TryDequeue(out job);
                    if (job != null)
                    {
                        Logging.LogInfo("Job found {0}", job.JobId);

                        // process job
                        JobExecutionStatus jobExecutionStatus;
                        if (_jobExecutionStatus.TryGetValue(job.JobId.ToString(), out jobExecutionStatus))
                        {

                            try
                            {
                                jobExecutionStatus.Information = "Job Started";
                                jobExecutionStatus.Started = DateTime.UtcNow;
                                jobExecutionStatus.JobStatus = JobStatus.Started;

                                var st = DateTime.UtcNow;
                                job.Run();
                                var et = DateTime.UtcNow;
#if NETSTANDARD16
                                Logging.LogInfo("Job completed in {0}", et.Subtract(st).TotalMilliseconds);
#else
                                Logging.LogInfo("Job completed in {0} : Current memory usage : {1}",et.Subtract(st).TotalMilliseconds, System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 );
#endif
                                jobExecutionStatus.Information = "Job Completed";
                                jobExecutionStatus.Ended = DateTime.UtcNow;
                                jobExecutionStatus.JobStatus = JobStatus.CompletedOk;
                                jobExecutionStatus.WaitEvent.Set();
                            }
                            catch (Exception ex)
                            {
                                Logging.LogError(BrightstarEventId.JobProcessingError,
                                                 "Error Processing Transaction {0}",
                                                 ex);
                                jobExecutionStatus.Information = job.ErrorMessage ?? "Job Error";
                                jobExecutionStatus.Ended = DateTime.UtcNow;
                                jobExecutionStatus.ExceptionDetail = GetExceptionDetail(ex);
                                jobExecutionStatus.JobStatus = JobStatus.TransactionError;
                                jobExecutionStatus.WaitEvent.Set();
                            }
                            finally
                            {
                                if (JobCompleted!= null)
                                {
                                    JobCompleted(this, new JobCompletedEventArgs(_storeName, job));
                                }
                            }
                        }
                    }
                    _shutdownCompleted.WaitOne(1);
                }
                catch (Exception ex)
                {
                    Logging.LogError(BrightstarEventId.JobProcessingError,
                                     "Unexpected exception caught in ProcessJobs loop: {0}", ex);
                }
            }

            ReleaseResources();
            if (_shutdownContinuation != null)
            {
                try
                {
                    _shutdownContinuation();
                }
                catch (Exception ex)
                {
                    Logging.LogError(BrightstarEventId.JobProcessingError,
                                     "Unexpected exception caught in processing Shutdown continuation: {0}", ex);
                }
            }
        }

        private static ExceptionDetailObject GetExceptionDetail(Exception ex)
        {
            if (ex is PreconditionFailedException)
            {
                var pfe = ex as PreconditionFailedException;
                return pfe.AsExceptionDetailObject();
            }
            return new ExceptionDetailObject(ex);
        }

        public IEnumerable<JobExecutionStatus> GetJobs()
        {
            return _jobExecutionStatus.Values.OrderByDescending(x => x.Queued);
        }

        public JobExecutionStatus GetJobStatus(string jobId)
        {
            JobExecutionStatus status;
            if (_jobExecutionStatus.TryGetValue(jobId, out status))
            {
                return status;
            }

            return null;
        }

        /// <summary>
        /// This is used to ensure there is no race condition when returning
        /// the readstore when commits are occurring.
        /// </summary>
        private readonly object _readStoreLock = new object();

        private readonly IStoreManager _storeManager;

        internal void InvalidateReadStore()
        {
            lock(_readStoreLock)
            {
                // KA: Don't close the read store at this point
                // as export jobs or queries may still be using it
                // instead the store should dispose of any resources
                // when it is garbage collected.
                // This fixes an issue found in ClientTests.TestExportWhileWriting
                //if (_readStore != null)
                //{
                //    _readStore.Close();
                //}

                _invalidatedReadStores.Add(new WeakReference(_readStore));
                _readStore = null;
            }
        }

        internal IStore ReadStore
        {
            get
            {
                lock (_readStoreLock)
                {
                    return _readStore ?? (_readStore = _storeManager.OpenStore(_storeLocation, true));
                }
            }
        }

        private readonly object _writeStoreLock = new object();
        internal IStore WriteStore
        {
            get
            {
                lock (_writeStoreLock)
                {
                    return _writeStore ?? (_writeStore = _storeManager.OpenStore(_storeLocation));
                }
            }
        }

        public BrightstarSparqlResultsType Query(ulong commitPointId, SparqlQuery query, ISerializationFormat targetFormat, Stream resultsStream, string[] defaultGraphUris)
        {
            // Not supported by read/write store so no handling for ReadWriteStoreModifiedException required
            Logging.LogDebug("CommitPointId={0}, Query={1}", commitPointId, query);
            using (var readStore = _storeManager.OpenStore(_storeLocation, commitPointId))
            {
                return readStore.ExecuteSparqlQuery(query, targetFormat, resultsStream, defaultGraphUris);
            }
        }

        public BrightstarSparqlResultsType Query(SparqlQuery query, ISerializationFormat targetFormat, Stream resultsStream, string[] defaultGraphUris )
        {
            Logging.LogDebug("Query {0}", query);
            try
            {
                return ReadStore.ExecuteSparqlQuery(query, targetFormat, resultsStream, defaultGraphUris);
            }
            catch (ReadWriteStoreModifiedException)
            {
                Logging.LogDebug("Read/Write store was concurrently modified. Attempting a retry");
                InvalidateReadStore();
                return Query(query, targetFormat, resultsStream, defaultGraphUris);
            }
        }

        public void QueueJob(Job job, bool incrementTransactionCount = true)
        {
            Logging.LogDebug("Queueing Job Id {0}", job.JobId);
            bool queuedJob = false;
            while (!queuedJob)
            {
                if (
                    _jobExecutionStatus.TryAdd(
                        job.JobId.ToString(),
                        new JobExecutionStatus
                            {
                                JobId = job.JobId,
                                JobStatus = JobStatus.Pending,
                                Queued = DateTime.UtcNow,
                                Label = job.Label,
                                WaitEvent = new AutoResetEvent(false)
                            }))
                {
                    _jobs.Enqueue(job);
                    queuedJob = true;
                    Logging.LogDebug("Queued Job Id {0}", job.JobId);
                    _statsMonitor.OnJobScheduled(incrementTransactionCount);
                }
            }
        }


        /// <summary>
        /// Queue a txn job.
        /// </summary>
        /// <param name="preconditions">The triples that must be present for txn to succeed</param>
        /// <param name="notExistsPreconditions">The triples that must not be present for txn to succeed</param>
        /// <param name="deletePatterns"></param>
        /// <param name="insertData"></param>
        /// <param name="defaultGraphUri"></param>
        /// <param name="format"></param>
        /// <param name="jobLabel"></param>
        /// <returns></returns>
        public Guid ProcessTransaction(string preconditions, string notExistsPreconditions, string deletePatterns, string insertData, string defaultGraphUri, string format, string jobLabel= null)
        {
            Logging.LogDebug("ProcessTransaction");
            var jobId = Guid.NewGuid();
            var job = new GuardedUpdateTransaction(jobId, jobLabel, this, preconditions, notExistsPreconditions,
                                                   deletePatterns, insertData, defaultGraphUri);
            QueueJob(job);
            return jobId;
        }

        public Guid Import(string contentFileName, string graphUri, RdfFormat importFormat = null, string jobLabel = null)
        {
            Logging.LogDebug("Import {0}, {1}, {2}", contentFileName, graphUri, importFormat);
            var jobId = Guid.NewGuid();
            var job = new ImportJob(jobId, jobLabel, this, contentFileName, importFormat, graphUri);
            QueueJob(job);
            return jobId;
        }

        public Guid Export(string fileName, string graphUri, RdfFormat exportFormat, string jobLabel = null)
        {
            Logging.LogDebug("Export {0}, {1}, {2}", fileName, graphUri, exportFormat.DefaultExtension);
            var jobId = Guid.NewGuid();
            var exportJob = new ExportJob(jobId, jobLabel, this, fileName, graphUri, exportFormat);
            _jobExecutionStatus.TryAdd(jobId.ToString(),
                                       new JobExecutionStatus
                                           {
                                               JobId = jobId,
                                               JobStatus = JobStatus.Started,
                                               Queued = DateTime.UtcNow,
                                               Started = DateTime.UtcNow,
                                               Label = jobLabel,
                                               WaitEvent = new AutoResetEvent(false)
                                           });
            exportJob.Run((id, ex) =>
                              {
                                  JobExecutionStatus jobExecutionStatus;
                                  if (_jobExecutionStatus.TryGetValue(id.ToString(), out jobExecutionStatus))
                                  {
                                      jobExecutionStatus.Information = "Export failed";
                                      jobExecutionStatus.ExceptionDetail = GetExceptionDetail(ex);
                                      jobExecutionStatus.JobStatus = JobStatus.TransactionError;
                                      jobExecutionStatus.Ended = DateTime.UtcNow;
                                      jobExecutionStatus.WaitEvent.Set();
                                  }
                              },
                          id =>
                              {
                                  JobExecutionStatus jobExecutionStatus;
                                  if (_jobExecutionStatus.TryGetValue(id.ToString(), out jobExecutionStatus))
                                  {
                                      jobExecutionStatus.Information = "Export completed";
                                      jobExecutionStatus.JobStatus = JobStatus.CompletedOk;
                                      jobExecutionStatus.Ended = DateTime.UtcNow;
                                      jobExecutionStatus.WaitEvent.Set();
                                  }
                              });
            return jobId;
        }

        public Guid UpdateStatistics(string jobLabel = null)
        {
            Logging.LogDebug("UpdateStatistics");
            var jobId = Guid.NewGuid();
            var job = new UpdateStatsJob(jobId, jobLabel, this);
            QueueJob(job);
            return jobId;
        }

        public Guid QueueSnapshotJob(string destinationStoreName, PersistenceType persistenceType, ulong commitPointId = StoreConstants.NullUlong, string jobLabel = null)
        {
            Logging.LogDebug("QueueSnapshotJob {0}, {1}", destinationStoreName, commitPointId);
            var jobId = Guid.NewGuid();
            var snapshotJob = new SnapshotJob(jobId, jobLabel, this, destinationStoreName, persistenceType, commitPointId);
            QueueJob(snapshotJob, false);
            return jobId;
        }

        internal void CreateSnapshot(string destinationStoreName, PersistenceType persistenceType, ulong commitPointId)
        {
            _storeManager.CreateSnapshot(_storeLocation, Path.Combine(_baseLocation, destinationStoreName), persistenceType, commitPointId);
        }

        public IEnumerable<Triple> GetResourceStatements(string resourceUri)
        {
            Logging.LogDebug("GetResourceStatements {0}", resourceUri);
            try
            {
                return ReadStore.GetResourceStatements(resourceUri);
            }
            catch (ReadWriteStoreModifiedException)
            {
                Logging.LogDebug("Read/Write store was concurrently modified. Attempting retry.");
                InvalidateReadStore();
                return GetResourceStatements(resourceUri);
            }
        }

        private void ReleaseResources()
        {
            lock (this)
            {
                // close read and write stores
                if (_readStore != null)
                {
                    _readStore.Close();
                    _readStore.Dispose();
                    _readStore = null;
                }

                foreach (var invalidatedReadStoreReference in _invalidatedReadStores)
                {
                    if (invalidatedReadStoreReference.IsAlive)
                    {
                        var readStore = invalidatedReadStoreReference.Target as IStore;
                        if (readStore != null)
                        {
                            readStore.Dispose();
                        }
                    }
                }
                _invalidatedReadStores.Clear();

                if (_writeStore != null)
                {
                    _writeStore.Close();
                    _writeStore.Dispose();
                    _writeStore = null;
                }
            }
        }

        public void Shutdown(bool completeJobs, ShutdownContinuation c = null)
        {
            _shutdownContinuation = c;
            _shutdownRequested = true;
            _completeRemainingJobs = completeJobs;
            ReleaseResources();
        }

        public void Consolidate(Guid jobId)
        {
            ReleaseResources();
            _writeStore = null;
            _readStore = null;
            WriteStore.Consolidate(jobId);
            ReleaseResources();
            _writeStore = null;
            _readStore = null;    
        }

        private void InitializeStatsMonitor()
        {
            if (Configuration.StatsUpdateTimespan > 0 || Configuration.StatsUpdateTransactionCount > 0)
            {
                var lastStats = _storeStatisticsLog.GetStatistics().FirstOrDefault();
                CommitPoint lastCommitPoint;
                using (var readStore = _storeManager.OpenStore(_storeLocation, true))
                {
                    lastCommitPoint = readStore.GetCommitPoints().FirstOrDefault();
                }
                _statsMonitor.Initialize(lastStats, lastCommitPoint == null ? 0 : lastCommitPoint.CommitNumber,
                                         () => UpdateStatistics());
            }
        }

        /// <summary>
        /// Preload index and resource pages for this store
        /// </summary>
        /// <param name="pageCacheRatio">The fractional amount of the number of available cache pages to use in the preload</param>
        public void WarmupStore(decimal pageCacheRatio)
        {
            if (pageCacheRatio > 1.0m) pageCacheRatio = 1.0m;
#if PORTABLE || WINDOWS_PHONE
            var pagesToPreload = (int)Math.Floor(PageCache.Instance.FreePages * (float)pageCacheRatio);
#else
            var pagesToPreload = (int)Math.Floor(PageCache.Instance.FreePages*pageCacheRatio);
#endif
            ReadStore.WarmupPageCache(pagesToPreload);
        }

        /// <summary>
        /// Get the identifiers of the named graphs in the store
        /// </summary>
        /// <returns>An enumeration of the identifiers of the named graphs in the store</returns>
        public IEnumerable<string> ListNamedGraphs()
        {
            return ReadStore.GetGraphUris().Where(x => x != Constants.DefaultGraphUri);
        }
    }
}
