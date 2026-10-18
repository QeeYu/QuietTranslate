using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QuietTranslate;

// Shared by selection, manual input and service tests; never retains text or raw credentials.
internal static class TranslationRequests
{
    private sealed class Slot
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int Users;
        internal DateTimeOffset NextAllowed;
    }
    private static readonly Dictionary<string, Slot> slots = new();

    internal sealed class Turn : IDisposable
    {
        private readonly string scope;
        private readonly Slot slot;
        private bool disposed;
        private Turn(string scope, Slot slot) { this.scope = scope; this.slot = slot; }
        internal void BackOff(ServiceException error)
        {
            if (error.Failure is not (ServiceFailure.RateLimit or ServiceFailure.Busy)) return;
            var delay = error.RetryAfter ?? TimeSpan.FromSeconds(error.Failure == ServiceFailure.Busy ? 3 : 2);
            // Only the next explicit user request waits. Never silently resend billable work.
            var now = DateTimeOffset.UtcNow;
            var until = delay <= TimeSpan.Zero ? now : delay >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
            lock (slots) { if (until > slot.NextAllowed) slot.NextAllowed = until; }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; slot.Gate.Release(); Release(scope, slot);
        }
        internal static async Task<Turn> EnterAsync(string scope, CancellationToken ct)
        {
            Slot slot;
            lock (slots)
            {
                foreach (var pair in slots.Where(p => p.Value.Users == 0 && p.Value.NextAllowed <= DateTimeOffset.UtcNow).ToArray())
                { slots.Remove(pair.Key); pair.Value.Gate.Dispose(); }
                if (!slots.TryGetValue(scope, out slot!))
                {
                    // ponytail: keep at most 64 idle cooldowns; old idle scopes may lose backoff after many key changes.
                    if (slots.Count >= 64)
                    {
                        var oldest = slots.Where(p => p.Value.Users == 0).OrderBy(p => p.Value.NextAllowed).FirstOrDefault();
                        if (oldest.Value != null) { slots.Remove(oldest.Key); oldest.Value.Gate.Dispose(); }
                    }
                    slots[scope] = slot = new Slot();
                }
                slot.Users++;
            }
            bool acquired = false;
            try
            {
                await slot.Gate.WaitAsync(ct).ConfigureAwait(false); acquired = true;
                var delay = slot.NextAllowed - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return new Turn(scope, slot);
            }
            catch { if (acquired) slot.Gate.Release(); Release(scope, slot); throw; }
        }
    }
    private static void Release(string scope, Slot slot)
    {
        lock (slots)
        {
            slot.Users--;
            if (slot.Users == 0 && slot.NextAllowed <= DateTimeOffset.UtcNow)
            { slots.Remove(scope); slot.Gate.Dispose(); }
        }
    }
    internal static Task<Turn> EnterAsync(Uri endpoint, string key, CancellationToken ct) =>
        Turn.EnterAsync(ApiSupport.Hash(endpoint.AbsoluteUri + "\0" + key), ct);
}
