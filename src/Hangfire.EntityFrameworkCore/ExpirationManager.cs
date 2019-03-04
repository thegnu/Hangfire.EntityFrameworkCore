﻿using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using Hangfire.EntityFrameworkCore.Properties;
using Hangfire.Logging;
using Hangfire.Server;
using Hangfire.Storage;

namespace Hangfire.EntityFrameworkCore
{
#pragma warning disable 618
    internal class ExpirationManager : IServerComponent
#pragma warning restore 618
    {
        private const int BatchSize = 1000;
        private const string LockKey = "locks:expirationmanager";
        private readonly ILog _logger = LogProvider.For<ExpirationManager>();
        private readonly EFCoreStorage _storage;

        public ExpirationManager(EFCoreStorage storage)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        }

        public void Execute(CancellationToken cancellationToken)
        {
            RemoveExpired<HangfireCounter>();
            RemoveExpired<HangfireHash>();
            RemoveExpired<HangfireList>();
            RemoveExpired<HangfireSet>();
            RemoveExpired<HangfireJob>();
            cancellationToken.WaitHandle.WaitOne(_storage.JobExpirationCheckInterval);
        }

        private void RemoveExpired<T>()
            where T : class, IExpirable
        {
            var type = typeof(T);
            _logger.DebugFormat(CoreStrings.ExpirationManagerRemoveExpiredStarting, type.Name);

            UseLock(() =>
            {
                while(0 != _storage.UseContext(context =>
                {
                    var set = context.Set<T>();
                    var entitiesToRemove = set.
                        Where(x => x.ExpireAt < DateTime.UtcNow).
                        Take(BatchSize);
                    set.RemoveRange(entitiesToRemove);
                    return context.SaveChanges();
                }));
            });

            _logger.TraceFormat(CoreStrings.ExpirationManagerRemoveExpiredCompleted, type.Name);
        }

        private void UseLock(Action action)
        {
            var lockTimeout = _storage.DistributedLockTimeout;
            using (var connection = _storage.GetConnection())
            {
                try
                {
                    using (var @lock = connection.AcquireDistributedLock(LockKey, lockTimeout))
                        action.Invoke();
                }
                catch (DistributedLockTimeoutException exception)
                when (exception.Resource == LockKey)
                {
                    _logger.Log(LogLevel.Debug, () =>
                        string.Format(
                            CultureInfo.InvariantCulture,
                            CoreStrings.ExpirationManagerUseLockFailed,
                            LockKey,
                            lockTimeout.TotalSeconds,
                            _storage.JobExpirationCheckInterval.TotalSeconds),
                        exception);
                }
            }
        }
    }
}
