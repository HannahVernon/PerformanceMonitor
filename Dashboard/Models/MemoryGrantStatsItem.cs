using System;

namespace PerformanceMonitorDashboard.Models
{
    public class MemoryGrantStatsItem
    {
        public long CollectionId { get; set; }
        public DateTime CollectionTime { get; set; }
        public short ResourceSemaphoreId { get; set; }
        public int PoolId { get; set; }

        // Memory metrics
        public decimal? TargetMemoryMb { get; set; }
        public decimal? MaxTargetMemoryMb { get; set; }
        public decimal? TotalMemoryMb { get; set; }
        public decimal? AvailableMemoryMb { get; set; }
        public decimal? GrantedMemoryMb { get; set; }
        public decimal? UsedMemoryMb { get; set; }

        // Counts
        public int? GranteeCount { get; set; }
        public int? WaiterCount { get; set; }
        public long? TimeoutErrorCount { get; set; }
        public long? ForcedGrantCount { get; set; }

        // Pressure warnings
        public bool? AvailableMemoryPressureWarning { get; set; }
        public bool? WaiterCountWarning { get; set; }
        public bool? TimeoutErrorWarning { get; set; }
        public bool? ForcedGrantWarning { get; set; }

        // Computed helpers
        public decimal? GrantedPercentage => TargetMemoryMb > 0
            ? GrantedMemoryMb * 100.0m / TargetMemoryMb
            : null;
        public decimal? UsedPercentage => GrantedMemoryMb > 0
            ? UsedMemoryMb * 100.0m / GrantedMemoryMb
            : null;
    }
}
