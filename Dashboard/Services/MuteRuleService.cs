using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Notifications;

namespace PerformanceMonitorDashboard.Services
{
    /// <summary>
    /// Manages alert mute rules with JSON persistence behind <see cref="IMuteRuleStore"/>.
    /// Thread-safe: all in-memory operations are protected by _lock.
    /// <para>
    /// E2 keeps Dashboard's cache-then-save ordering (mutate the in-memory cache,
    /// then persist) and its synchronous public API — the store does synchronous
    /// I/O and returns completed tasks, and the constructor's load is synchronous so
    /// no startup-ordering change is needed. Converging to persist-then-cache /
    /// async is deferred to E3b.
    /// </para>
    /// </summary>
    public class MuteRuleService
    {
        private readonly IMuteRuleStore _store;
        private readonly ILogger<MuteRuleService> _logger;
        private readonly object _lock = new object();
        private List<MuteRule> _rules = new();

        public MuteRuleService(IMuteRuleStore store, ILogger<MuteRuleService> logger)
        {
            _store = store;
            _logger = logger;

            /* The store loads its set synchronously in its own constructor; LoadAllAsync
               returns that already-loaded set (completes synchronously). */
            _rules = _store.LoadAllAsync().GetAwaiter().GetResult().ToList();
            PurgeExpiredRules();
        }

        public bool IsAlertMuted(AlertMuteContext context)
        {
            lock (_lock)
            {
                return _rules.Any(r => r.Matches(context));
            }
        }

        public List<MuteRule> GetRules()
        {
            lock (_lock)
            {
                return _rules.ToList();
            }
        }

        public List<MuteRule> GetActiveRules()
        {
            lock (_lock)
            {
                return _rules.Where(r => r.Enabled && !r.IsExpired).ToList();
            }
        }

        public void AddRule(MuteRule rule)
        {
            lock (_lock)
            {
                _rules.Add(rule);
                _store.InsertAsync(rule).GetAwaiter().GetResult();
            }
        }

        public void RemoveRule(string ruleId)
        {
            lock (_lock)
            {
                _rules.RemoveAll(r => r.Id == ruleId);
                _store.DeleteAsync(ruleId).GetAwaiter().GetResult();
            }
        }

        public void UpdateRule(MuteRule updated)
        {
            lock (_lock)
            {
                var index = _rules.FindIndex(r => r.Id == updated.Id);
                if (index >= 0)
                {
                    _rules[index] = updated;
                    _store.UpdateAsync(updated).GetAwaiter().GetResult();
                }
            }
        }

        public void SetRuleEnabled(string ruleId, bool enabled)
        {
            lock (_lock)
            {
                var rule = _rules.FirstOrDefault(r => r.Id == ruleId);
                if (rule != null)
                {
                    rule.Enabled = enabled;
                    _store.SetEnabledAsync(ruleId, enabled).GetAwaiter().GetResult();
                }
            }
        }

        /// <summary>
        /// Removes all expired rules from the list.
        /// </summary>
        public int PurgeExpiredRules()
        {
            lock (_lock)
            {
                var expiredIds = _rules.Where(r => r.IsExpired).Select(r => r.Id).ToList();
                int removed = _rules.RemoveAll(r => r.IsExpired);
                if (removed > 0) _store.DeleteExpiredAsync(expiredIds).GetAwaiter().GetResult();
                return removed;
            }
        }
    }
}
