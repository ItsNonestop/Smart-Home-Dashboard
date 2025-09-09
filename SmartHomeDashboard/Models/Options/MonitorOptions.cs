using System.ComponentModel.DataAnnotations;

namespace SmartHomeDashboard.Models.Options
{
    /// <summary>
    /// Strongly-typed settings bound from the "Monitor" section in appsettings.json.
    /// Read-only for now (shown on the Settings page), but includes validation
    /// attributes to support future editable settings.
    /// </summary>
    public class MonitorOptions
    {
        /// <summary>How often the background monitor runs.</summary>
        [Range(5, 3600, ErrorMessage = "Poll interval must be between 5 and 3600 seconds.")]
        public int PollIntervalSeconds { get; set; } = 30;

        /// <summary>ICMP ping timeout per device.</summary>
        [Range(100, 10000, ErrorMessage = "Ping timeout must be between 100 and 10000 ms.")]
        public int PingTimeoutMs { get; set; } = 750;

        /// <summary>Try TCP ports if ICMP is blocked.</summary>
        public bool TcpFallbackEnabled { get; set; } = false;

        /// <summary>Ports to try (only used when TcpFallbackEnabled is true).</summary>
        [MinLength(1, ErrorMessage = "Provide at least one TCP port.")]
        public List<int> TcpPorts { get; set; } = new();
    }
}
