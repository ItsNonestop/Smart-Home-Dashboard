using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using SmartHomeDashboard.Models.Options;

namespace SmartHomeDashboard.Repositories
{
    /// <summary>
    /// JSON-backed store for editable application settings.
    /// Currently only stores Monitor options in App_Data/settings.json.
    /// Thread-safe via a private lock and in-memory cache.
    /// </summary>
    public class AppSettingsStore
    {
        private readonly string _dataDir;
        private readonly string _settingsFilePath;
        private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
        private readonly object _sync = new();
        private MonitorOptions _monitor = new();

        public AppSettingsStore(IOptions<MonitorOptions> defaults, IWebHostEnvironment env)
        {
            if (defaults is null) throw new ArgumentNullException(nameof(defaults));
            if (env is null) throw new ArgumentNullException(nameof(env));

            _dataDir = Path.Combine(env.ContentRootPath, "App_Data");
            _settingsFilePath = Path.Combine(_dataDir, "settings.json");

            EnsureStorage();
            LoadOrSeed(defaults.Value);
        }

        /// <summary>Return a deep copy of current monitor settings.</summary>
        public MonitorOptions GetMonitor()
        {
            lock (_sync)
            {
                return Clone(_monitor);
            }
        }

        /// <summary>
        /// Validate and persist monitor settings.
        /// Returns true on success with a diff of changes; false sets errorMessage.
        /// </summary>
        public bool TrySaveMonitor(MonitorOptions input,
            out Dictionary<string, (string? OldValue, string? NewValue)>? changes,
            out string? errorMessage)
        {
            changes = null;
            errorMessage = null;

            var context = new ValidationContext(input);
            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(input, context, results, true))
            {
                errorMessage = string.Join("; ", results.Select(r => r.ErrorMessage));
                return false;
            }

            lock (_sync)
            {
                var diff = BuildDiff(_monitor, input);
                var next = Clone(input);
                try
                {
                    Persist(next);
                    _monitor = next;
                    changes = diff;
                    return true;
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                    return false;
                }
            }
        }

        // ------------------- internals -------------------

        private void EnsureStorage()
        {
            if (!Directory.Exists(_dataDir)) Directory.CreateDirectory(_dataDir);
        }

        private void LoadOrSeed(MonitorOptions defaults)
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    var dto = string.IsNullOrWhiteSpace(json)
                        ? null
                        : JsonSerializer.Deserialize<Root>(json, _jsonOptions);
                    _monitor = dto?.Monitor ?? Clone(defaults);
                }
                else
                {
                    _monitor = Clone(defaults);
                    Persist(_monitor); // create file with defaults
                }
            }
            catch
            {
                _monitor = Clone(defaults);
            }
        }

        private void Persist(MonitorOptions monitor)
        {
            var dto = new Root { Monitor = monitor };
            var json = JsonSerializer.Serialize(dto, _jsonOptions);
            var tmp = Path.GetTempFileName();
            File.WriteAllText(tmp, json);
            File.Copy(tmp, _settingsFilePath, overwrite: true);
            File.Delete(tmp);
        }

        private static MonitorOptions Clone(MonitorOptions src) => new()
        {
            PollIntervalSeconds = src.PollIntervalSeconds,
            PingTimeoutMs = src.PingTimeoutMs,
            TcpFallbackEnabled = src.TcpFallbackEnabled,
            TcpPorts = src.TcpPorts is null ? new List<int>() : new List<int>(src.TcpPorts)
        };

        private static Dictionary<string, (string? OldValue, string? NewValue)> BuildDiff(
            MonitorOptions current, MonitorOptions input)
        {
            var diff = new Dictionary<string, (string?, string?)>();

            if (current.PollIntervalSeconds != input.PollIntervalSeconds)
                diff["PollIntervalSeconds"] = (current.PollIntervalSeconds.ToString(), input.PollIntervalSeconds.ToString());

            if (current.PingTimeoutMs != input.PingTimeoutMs)
                diff["PingTimeoutMs"] = (current.PingTimeoutMs.ToString(), input.PingTimeoutMs.ToString());

            if (current.TcpFallbackEnabled != input.TcpFallbackEnabled)
                diff["TcpFallbackEnabled"] = (current.TcpFallbackEnabled.ToString(), input.TcpFallbackEnabled.ToString());

            var currentPorts = current.TcpPorts ?? new List<int>();
            var inputPorts = input.TcpPorts ?? new List<int>();
            if (!currentPorts.SequenceEqual(inputPorts))
                diff["TcpPorts"] = (FormatPorts(currentPorts), FormatPorts(inputPorts));

            return diff;
        }

        private static string? FormatPorts(List<int> ports)
        {
            return ports.Count == 0 ? null : string.Join(",", ports);
        }

        private class Root
        {
            public MonitorOptions Monitor { get; set; } = new();
        }
    }
}

