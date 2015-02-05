﻿using System;
using Foundatio.Caching;
using Foundatio.Utility;
using NLog.Fluent;

namespace Foundatio.Lock {
    public class CacheLockProvider : ILockProvider {
        private readonly ICacheClient _cacheClient;

        public CacheLockProvider(ICacheClient cacheClient) {
            _cacheClient = cacheClient;
        }

        public IDisposable AcquireLock(string name, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null) {
            Log.Trace().Message("AcquireLock: {0}", name).Write();
            if (!acquireTimeout.HasValue)
                acquireTimeout = TimeSpan.FromMinutes(1);
            string cacheKey = GetCacheKey(name);

            Run.UntilTrue(() => {
                Log.Trace().Message("Checking to see if lock exists: {0}", name).Write();
                var lockValue = _cacheClient.Get<object>(cacheKey);
                Log.Trace().Message("Lock: {0} Value: {1}", name, lockValue ?? "<null>").Write();
                if (lockValue != null)
                    return false;

                Log.Trace().Message("Lock doesn't exist: {0}", name).Write();

                if (lockTimeout.HasValue && lockTimeout.Value == TimeSpan.Zero)
                    return _cacheClient.Add(cacheKey, DateTime.Now);

                return _cacheClient.Add(cacheKey, DateTime.Now, lockTimeout ?? TimeSpan.FromMinutes(20));
            }, acquireTimeout, TimeSpan.FromMilliseconds(50));

            Log.Trace().Message("Returning lock: {0}", name).Write();
            return new DisposableLock(name, this);
        }

        public bool IsLocked(string name) {
            string cacheKey = GetCacheKey(name);
            return _cacheClient.Get<object>(cacheKey) != null;
        }

        public void ReleaseLock(string name) {
            Log.Trace().Message("ReleaseLock: {0}", name).Write();
            _cacheClient.Remove(GetCacheKey(name));
        }

        private string GetCacheKey(string name) {
            return String.Concat("lock:", name);
        }
    }
}