using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrchardCore.Data.Documents;
using OrchardCore.Documents.Options;
using OrchardCore.Environment.Shell.Scope;
using OrchardCore.Locking.Distributed;

namespace OrchardCore.Documents
{
    /// <summary>
    /// A <see cref="DocumentManager{TDocument}"/> using a multi level cache but without any persistent storage.
    /// </summary>
    public class VolatileDocumentManager<TDocument> : DocumentManager<TDocument>, IVolatileDocumentManager<TDocument> where TDocument : class, IDocument, new()
    {
        private readonly IDistributedLock _distributedLock;
        private readonly ILogger _logger;

        private delegate Task<TDocument> UpdateDelegate();
        private delegate Task AfterUpdateDelegate(TDocument document);

        public VolatileDocumentManager(
            IDistributedCache distributedCache,
            IDistributedLock distributedLock,
            IMemoryCache memoryCache,
            IOptionsMonitor<DocumentOptions> options,
            ILogger<DocumentManager<TDocument>> logger)
            : base(distributedCache, memoryCache, options, logger)
        {
            _isVolatile = true;
            _distributedLock = distributedLock;
            _logger = logger;
        }

        public async Task UpdateAtomicAsync(Func<Task<TDocument>> updateAsync, Func<TDocument, Task> afterUpdateAsync = null)
        {
            if (_isDistributed)
            {
                try
                {
                    _ = await _distributedCache.GetStringAsync(_options.CacheIdKey);
                }
                catch
                {
                    await DocumentStore.CancelAsync();

                    _logger.LogError("Can't update the '{DocumentName}' if not able to access the distributed cache", typeof(TDocument).Name);
                    
                    throw;
                }
            }

            var delegates = ShellScope.GetOrCreateFeature<UpdateDelegates>();
            if (delegates.UpdateDelegateAsync == null ||
                !delegates.UpdateDelegateAsync.GetInvocationList().Contains(updateAsync))
            {
                delegates.UpdateDelegateAsync += () => updateAsync();
            }

            if (afterUpdateAsync != null &&
                (delegates.AfterUpdateDelegateAsync == null ||
                !delegates.AfterUpdateDelegateAsync.GetInvocationList().Contains(afterUpdateAsync)))
            {
                delegates.AfterUpdateDelegateAsync += document => afterUpdateAsync(document);
            }

            DocumentStore.AfterCommitSuccess<TDocument>(async () =>
            {
                (var locker, var locked) = await _distributedLock.TryAcquireLockAsync(
                    _options.CacheKey + "_LOCK",
                    TimeSpan.FromMilliseconds(_options.LockTimeout),
                    TimeSpan.FromMilliseconds(_options.LockExpiration));

                if (!locked)
                {
                    return;
                }

                await using var acquiredLock = locker;

                TDocument document = null;
                foreach (var d in delegates.UpdateDelegateAsync.GetInvocationList())
                {
                    document = await ((UpdateDelegate)d)();
                }

                document.Identifier ??= IdGenerator.GenerateId();

                await SetInternalAsync(document);

                if (delegates.AfterUpdateDelegateAsync != null)
                {
                    foreach (var d in delegates.AfterUpdateDelegateAsync.GetInvocationList())
                    {
                        await ((AfterUpdateDelegate)d)(document);
                    }
                }
            });
        }

        private class UpdateDelegates
        {
            public UpdateDelegate UpdateDelegateAsync;
            public AfterUpdateDelegate AfterUpdateDelegateAsync;
        }
    }
}
