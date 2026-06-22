using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.ShopifyPlugin.Services;

public sealed class AsyncDuplicateLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> LockAsync(string key, CancellationToken ct = default)
    {
        var sem = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        return new Releaser(this, key, sem);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly AsyncDuplicateLock _parent;
        private readonly string _key;
        private readonly SemaphoreSlim _sem;
        public Releaser(AsyncDuplicateLock parent, string key, SemaphoreSlim sem)
            => (_parent, _key, _sem) = (parent, key, sem);

        public void Dispose() => _sem.Release();
    }
}