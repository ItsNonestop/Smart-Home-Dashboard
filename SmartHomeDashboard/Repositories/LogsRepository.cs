using System.Text.Json;

namespace SmartHomeDashboard.Repositories
{
    /// <summary>
    /// Append-only JSON log store at App_Data/logs.json with simple capping/rotation.
    /// Thread-safe (single-process) via a private lock. Keeps memory + IO small.
    /// </summary>
    public class LogsRepository
    {
        private readonly ILogger<LogsRepository> _logger;
        private readonly string _dataDir;
        private readonly string _logsFilePath;

        // Keep a reasonable cap; dashboard shows a small tail, Logs page can show a larger slice.
        private const int MaxEntries = 5000;

        private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
        private readonly object _sync = new();
        private List<LogEntry> _cache = new();

        public LogsRepository(IWebHostEnvironment env, ILogger<LogsRepository> logger)
        {
            _logger = logger;
            _dataDir = Path.Combine(env.ContentRootPath, "App_Data");
            _logsFilePath = Path.Combine(_dataDir, "logs.json");

            EnsureStorage();
            LoadOrInit();
        }

        /// <summary>Append one entry (timestamp/ids are auto-filled if missing). Persists to disk.</summary>
        public void Append(LogEntry entry)
        {
            if (entry is null) throw new ArgumentNullException(nameof(entry));

            lock (_sync)
            {
                Normalize(entry);
                _cache.Add(entry);
                EnforceCap_NoAlloc();
                Persist();
            }
        }

        /// <summary>Append many entries at once (fills timestamp/ids as needed). Persists once.</summary>
        public void AppendRange(IEnumerable<LogEntry> entries)
        {
            if (entries is null) throw new ArgumentNullException(nameof(entries));

            lock (_sync)
            {
                foreach (var e in entries) Normalize(e);
                _cache.AddRange(entries);
                EnforceCap_NoAlloc();
                Persist();
            }
        }

        /// <summary>Return the latest N entries (newest first).</summary>
        public IReadOnlyList<LogEntry> GetTail(int count)
        {
            lock (_sync)
            {
                if (count <= 0 || _cache.Count == 0) return Array.Empty<LogEntry>();
                var take = Math.Min(count, _cache.Count);
                // Latest first without reallocating entire list
                return _cache.Skip(_cache.Count - take).OrderByDescending(e => e.TimestampUtc).Select(Clone).ToList();
            }
        }

        /// <summary>Return a snapshot of all entries (ordered newest first). Use sparingly.</summary>
        public IReadOnlyList<LogEntry> GetAll()
        {
            lock (_sync)
            {
                return _cache.OrderByDescending(e => e.TimestampUtc).Select(Clone).ToList();
            }
        }

        // ------------------- internals -------------------

        private void EnsureStorage()
        {
            try
            {
                if (!Directory.Exists(_dataDir)) Directory.CreateDirectory(_dataDir);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ensure App_Data at {Dir}", _dataDir);
                throw;
            }
        }

        private void LoadOrInit()
        {
            try
            {
                if (File.Exists(_logsFilePath))
                {
                    var json = File.ReadAllText(_logsFilePath);
                    var data = string.IsNullOrWhiteSpace(json)
                        ? null
                        : JsonSerializer.Deserialize<List<LogEntry>>(json, _jsonOptions);
                    _cache = data ?? new List<LogEntry>();
                }
                else
                {
                    _cache = new List<LogEntry>();
                    Persist(); // create empty file
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load logs from {Path}. Starting empty.", _logsFilePath);
                _cache = new List<LogEntry>();
            }
        }

        private void Persist()
        {
            try
            {
                var json = JsonSerializer.Serialize(_cache, _jsonOptions);
                var tmp = Path.GetTempFileName();
                File.WriteAllText(tmp, json);
                File.Copy(tmp, _logsFilePath, overwrite: true);
                File.Delete(tmp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist logs to {Path}", _logsFilePath);
            }
        }

        private static void Normalize(LogEntry e)
        {
            if (e.Id == Guid.Empty) e.Id = Guid.NewGuid();
            if (e.TimestampUtc == default) e.TimestampUtc = DateTimeOffset.UtcNow;

            e.Level = string.IsNullOrWhiteSpace(e.Level) ? "Info" : e.Level.Trim();
            e.Source = string.IsNullOrWhiteSpace(e.Source) ? "System" : e.Source.Trim();
            e.Action = string.IsNullOrWhiteSpace(e.Action) ? "Log" : e.Action.Trim();
            e.Actor = string.IsNullOrWhiteSpace(e.Actor) ? "system" : e.Actor.Trim();

            e.Message ??= string.Empty;
            e.DeviceName ??= string.Empty;

            // Coerce Details to a small dictionary for extensibility
            e.Details ??= new Dictionary<string, string>();
        }

        private void EnforceCap_NoAlloc()
        {
            // Drop oldest items if we exceed cap. Safe to mutate in place.
            var overflow = _cache.Count - MaxEntries;
            if (overflow > 0)
            {
                _cache.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc)); // oldest first
                _cache.RemoveRange(0, overflow);
            }
        }

        private static LogEntry Clone(LogEntry e) => new()
        {
            Id = e.Id,
            TimestampUtc = e.TimestampUtc,
            Level = e.Level,
            Source = e.Source,
            Action = e.Action,
            Actor = e.Actor,
            Message = e.Message,
            DeviceId = e.DeviceId,
            DeviceName = e.DeviceName,
            Details = e.Details is null ? null : new Dictionary<string, string>(e.Details)
        };

        // ------------------- LogEntry model -------------------

        /// <summary>
        /// Minimal, extensible log entry. One-line summary in Message; optional Details for the full page.
        /// </summary>
        public class LogEntry
        {
            public Guid Id { get; set; }
            public DateTimeOffset TimestampUtc { get; set; }

            // Info/Warning/Error (free-form; keep it readable)
            public string Level { get; set; } = "Info";

            // Where it came from: Monitor, UserAction, System, etc.
            public string Source { get; set; } = "System";

            // What happened: StatusChanged, DeviceCreated, DeviceEdited, DeviceDeleted, ToggleEnabled, SettingsChanged
            public string Action { get; set; } = "Log";

            // Who did it: "system" (monitor/service) or a future user name/id
            public string Actor { get; set; } = "system";

            // Optional device context
            public Guid? DeviceId { get; set; }
            public string? DeviceName { get; set; }

            // Short human text shown in lists
            public string Message { get; set; } = string.Empty;

            // Optional key/value details for the full logs page (expandable view later)
            public Dictionary<string, string>? Details { get; set; }
        }
    }
}
