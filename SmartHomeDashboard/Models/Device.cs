using System;

namespace SmartHomeDashboard.Models
{
    /// <summary>
    /// Basic device status for the dashboard.
    /// </summary>
    public enum DeviceStatus
    {
        Unknown = 0,
        Online = 1,
        Offline = 2
    }

    /// <summary>
    /// Core device model used across MVC (views, controllers, persistence).
    /// Keep this simple and readable.
    /// </summary>
    public class Device
    {
        /// <summary>Stable unique ID used for edits/deletes/toggles.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Display name shown on the card (e.g., "Camera 1").</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>IPv4/IPv6 address as entered by the user.</summary>
        public string IpAddress { get; set; } = string.Empty;

        /// <summary>Logical VLAN name / tag, free-form.</summary>
        public string Vlan { get; set; } = string.Empty;

        /// <summary>Current reachability reported by the monitor.</summary>
        public DeviceStatus Status { get; set; } = DeviceStatus.Unknown;

        /// <summary>UTC timestamp of the last successful heartbeat/ping.</summary>
        public DateTimeOffset? LastSeenUtc { get; set; }

        /// <summary>Disabled devices are dimmed and skipped by the monitor.</summary>
        public bool Enabled { get; set; } = true;

        // --- Convenience read-only helpers for views (no logic heavy lifting) ---

        /// <summary>True when Status == Online.</summary>
        public bool IsOnline => Status == DeviceStatus.Online;

        /// <summary>Quick label for "Last seen" in UI; safe to call when LastSeenUtc is null.</summary>
        public string LastSeenLabel =>
            LastSeenUtc is null ? "never" : $"{FormatRelativeTime(LastSeenUtc.Value, DateTimeOffset.UtcNow)}";

        private static string FormatRelativeTime(DateTimeOffset when, DateTimeOffset now)
        {
            // Tiny humanizer to avoid bringing in libraries; good enough for the dashboard.
            var delta = now - when;
            if (delta.TotalSeconds < 30) return "just now";
            if (delta.TotalMinutes < 2) return "1 min ago";
            if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} min ago";
            if (delta.TotalHours < 2) return "1 hour ago";
            if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} hours ago";
            if (delta.TotalDays < 2) return "yesterday";
            return $"{(int)delta.TotalDays} days ago";
        }
    }
}
