using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using SmartHomeDashboard.Models;

namespace SmartHomeDashboard.Repositories
{
    /// <summary>
    /// Super-simple JSON persistence for devices in App_Data/devices.json.
    /// Registered as a Singleton; internal operations are guarded by a lock.
    /// </summary>
    public class DeviceRepository
    {
        private readonly ILogger<DeviceRepository> _logger;
        private readonly string _dataDir;
        private readonly string _devicesFilePath;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true
        };

        // In-memory cache guarded by _sync for all read/write operations.
        private readonly object _sync = new();
        private List<Device> _cache = new();

        public DeviceRepository(IWebHostEnvironment env, ILogger<DeviceRepository> logger)
        {
            _logger = logger;
            _dataDir = Path.Combine(env.ContentRootPath, "App_Data");
            _devicesFilePath = Path.Combine(_dataDir, "devices.json");

            EnsureStorage();
            LoadOrSeed();
        }

        /// <summary>Returns a snapshot copy of all devices.</summary>
        public IReadOnlyList<Device> GetAll()
        {
            lock (_sync)
            {
                // Return a defensive copy so callers can't mutate our cache directly.
                return _cache.Select(Clone).ToList();
            }
        }

        /// <summary>
        /// Upserts a device by Id. If Id is empty or not found, a new device is added.
        /// Returns the persisted copy.
        /// </summary>
        public Device Upsert(Device device)
        {
            if (device is null) throw new ArgumentNullException(nameof(device));

            lock (_sync)
            {
                if (device.Id == Guid.Empty) device.Id = Guid.NewGuid();

                var existing = _cache.FirstOrDefault(d => d.Id == device.Id);
                if (existing is null)
                {
                    _cache.Add(Clone(device));
                }
                else
                {
                    existing.Name = device.Name;
                    existing.IpAddress = device.IpAddress;
                    existing.Vlan = device.Vlan;
                    existing.Status = device.Status;
                    existing.LastSeenUtc = device.LastSeenUtc;
                    existing.Enabled = device.Enabled;
                }

                Persist();
                return Clone(_cache.First(d => d.Id == device.Id));
            }
        }

        /// <summary>Replaces the entire device list (used by admin import or bulk save).</summary>
        public void SaveAll(IEnumerable<Device> devices)
        {
            if (devices is null) throw new ArgumentNullException(nameof(devices));
            lock (_sync)
            {
                _cache = devices.Select(Clone).ToList();
                Persist();
            }
        }

        /// <summary>Removes a device by Id. Returns true if removed.</summary>
        public bool Remove(Guid id)
        {
            lock (_sync)
            {
                var removed = _cache.RemoveAll(d => d.Id == id) > 0;
                if (removed) Persist();
                return removed;
            }
        }

        /// <summary>Toggles the Enabled flag for a device. Returns the new Enabled value, or null if not found.</summary>
        public bool? ToggleEnabled(Guid id)
        {
            lock (_sync)
            {
                var dev = _cache.FirstOrDefault(d => d.Id == id);
                if (dev is null) return null;
                dev.Enabled = !dev.Enabled;
                Persist();
                return dev.Enabled;
            }
        }

        // --- Internal helpers --------------------------------------------------

        private void EnsureStorage()
        {
            try
            {
                if (!Directory.Exists(_dataDir))
                    Directory.CreateDirectory(_dataDir);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ensure storage directory at {DataDir}", _dataDir);
                throw;
            }
        }

        private void LoadOrSeed()
        {
            try
            {
                if (File.Exists(_devicesFilePath))
                {
                    var json = File.ReadAllText(_devicesFilePath);
                    var data = string.IsNullOrWhiteSpace(json)
                        ? null
                        : JsonSerializer.Deserialize<List<Device>>(json, _jsonOptions);

                    _cache = data ?? new List<Device>();
                }
                else
                {
                    _cache = SeedDefaults();
                    Persist(); // Create the file with seeds
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load devices from {Path}. Starting with empty list.", _devicesFilePath);
                _cache = new List<Device>();
            }
        }

        private void Persist()
        {
            try
            {
                var json = JsonSerializer.Serialize(_cache, _jsonOptions);
                var tmp = Path.GetTempFileName();
                File.WriteAllText(tmp, json);
                File.Copy(tmp, _devicesFilePath, overwrite: true);
                File.Delete(tmp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist devices to {Path}", _devicesFilePath);
                // We don't rethrow to avoid crashing the app on a transient IO failure.
            }
        }

        private static List<Device> SeedDefaults()
        {
            var now = DateTimeOffset.UtcNow;
            return new List<Device>
            {
                new Device
                {
                    Name = "Camera 1",
                    IpAddress = "192.168.1.10",
                    Vlan = "IoT_VLAN_1",
                    Status = DeviceStatus.Online,
                    LastSeenUtc = now.AddMinutes(-2),
                    Enabled = true
                },
                new Device
                {
                    Name = "Smart Light",
                    IpAddress = "192.168.1.11",
                    Vlan = "IoT_VLAN_1",
                    Status = DeviceStatus.Offline,
                    LastSeenUtc = now.AddMinutes(-8),
                    Enabled = true
                },
                new Device
                {
                    Name = "Thermostat",
                    IpAddress = "192.168.1.12",
                    Vlan = "IoT_VLAN_2",
                    Status = DeviceStatus.Online,
                    LastSeenUtc = now.AddSeconds(-10),
                    Enabled = true
                }
            };
        }

        private static Device Clone(Device d) => new()
        {
            Id = d.Id,
            Name = d.Name,
            IpAddress = d.IpAddress,
            Vlan = d.Vlan,
            Status = d.Status,
            LastSeenUtc = d.LastSeenUtc,
            Enabled = d.Enabled
        };
    }
}
