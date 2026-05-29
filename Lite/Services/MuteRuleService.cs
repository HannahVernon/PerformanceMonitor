using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Notifications;

namespace PerformanceMonitorLite.Services
{
    /// <summary>
    /// Manages alert mute rules with DuckDB persistence behind <see cref="IMuteRuleStore"/>.
    /// Rules are cached in memory for fast matching and synced to the store on changes.
    /// Thread-safe: all in-memory operations are protected by _lock.
    /// Lite persists *then* mutates the cache (a persist failure skips the cache add);
    /// this ordering is unchanged in E2.
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

        public async Task LoadAsync()
        {
            var rules = await _store.LoadAllAsync();

            lock (_lock)
            {
                _rules = rules.ToList();
            }

            /* Purge expired rules on startup */
            await PurgeExpiredRulesAsync();
        }

        public async Task AddRuleAsync(MuteRule rule)
        {
            try
            {
                await _store.InsertAsync(rule);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to persist new mute rule to DuckDB — rule will not be saved: {ex.Message}");
                return;
            }

            lock (_lock)
            {
                _rules.Add(rule);
            }
        }

        public async Task RemoveRuleAsync(string ruleId)
        {
            try
            {
                await _store.DeleteAsync(ruleId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to delete mute rule from DuckDB: {ex.Message}");
            }

            lock (_lock)
            {
                _rules.RemoveAll(r => r.Id == ruleId);
            }
        }

        public async Task UpdateRuleAsync(MuteRule updated)
        {
            try
            {
                await _store.UpdateAsync(updated);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to update mute rule in DuckDB: {ex.Message}");
            }

            lock (_lock)
            {
                var index = _rules.FindIndex(r => r.Id == updated.Id);
                if (index >= 0)
                    _rules[index] = updated;
            }
        }

        public async Task SetRuleEnabledAsync(string ruleId, bool enabled)
        {
            try
            {
                await _store.SetEnabledAsync(ruleId, enabled);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to update mute rule enabled state in DuckDB: {ex.Message}");
            }

            lock (_lock)
            {
                var rule = _rules.FirstOrDefault(r => r.Id == ruleId);
                if (rule != null) rule.Enabled = enabled;
            }
        }

        public async Task<int> PurgeExpiredRulesAsync()
        {
            List<string> expiredIds;
            lock (_lock)
            {
                expiredIds = _rules.Where(r => r.IsExpired).Select(r => r.Id).ToList();
                if (expiredIds.Count == 0) return 0;
            }

            try
            {
                await _store.DeleteExpiredAsync(expiredIds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to purge expired mute rules from DuckDB: {ex.Message}");
                return 0;
            }

            lock (_lock)
            {
                _rules.RemoveAll(r => expiredIds.Contains(r.Id));
            }

            return expiredIds.Count;
        }
    }
}
