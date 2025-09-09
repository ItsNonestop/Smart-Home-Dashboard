using System;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SmartHomeDashboard.Models;
using SmartHomeDashboard.Repositories;

namespace SmartHomeDashboard.Controllers
{
    /// <summary>
    /// Device management: Create, Edit, Delete, ToggleEnabled
    /// + Emits user-action logs to LogsRepository (Actor is "system" for now; user accounts later).
    /// </summary>
    [AutoValidateAntiforgeryToken]
    public class DevicesController : Controller
    {
        private readonly ILogger<DevicesController> _logger;
        private readonly DeviceRepository _repo;
        private readonly LogsRepository _logs;

        public DevicesController(
            ILogger<DevicesController> logger,
            DeviceRepository repo,
            LogsRepository logs)
        {
            _logger = logger;
            _repo = repo;
            _logs = logs;
        }

        // --------------------- CREATE ---------------------

        // GET: /Devices/Create
        [HttpGet]
        public IActionResult Create()
        {
            var model = new Device { Enabled = true };
            return View(model);
        }

        // POST: /Devices/Create
        [HttpPost]
        public IActionResult Create([Bind("Name,IpAddress,Vlan,Enabled")] Device input)
        {
            ValidateBasic(input);
            ValidateDuplicateIp(input.IpAddress, excludeId: null);

            if (!ModelState.IsValid)
                return View(input);

            var toSave = new Device
            {
                Name = input.Name.Trim(),
                IpAddress = input.IpAddress.Trim(),
                Vlan = input.Vlan?.Trim() ?? string.Empty,
                Status = DeviceStatus.Unknown,
                LastSeenUtc = null,
                Enabled = input.Enabled
            };

            var saved = _repo.Upsert(toSave);

            // Log user action: created device
            _logs.Append(new LogsRepository.LogEntry
            {
                Level = "Info",
                Source = "UserAction",
                Action = "DeviceCreated",
                Actor = "system", // replace with real user later
                DeviceId = saved.Id,
                DeviceName = saved.Name,
                Message = $"Added device \"{saved.Name}\" ({saved.IpAddress})",
                Details = new Dictionary<string, string>
                {
                    ["ip"] = saved.IpAddress,
                    ["vlan"] = saved.Vlan,
                    ["enabled"] = saved.Enabled ? "true" : "false"
                }
            });

            TempData["Toast"] = $"Device \"{saved.Name}\" added.";
            return RedirectToAction("Index", "Home");
        }

        // --------------------- EDIT ----------------------

        // GET: /Devices/Edit/{id}
        [HttpGet]
        public IActionResult Edit(Guid id)
        {
            var device = _repo.GetAll().FirstOrDefault(d => d.Id == id);
            if (device is null)
            {
                TempData["Toast"] = "That device no longer exists.";
                return RedirectToAction("Index", "Home");
            }
            return View(device);
        }

        // POST: /Devices/Edit
        [HttpPost]
        public IActionResult Edit([Bind("Id,Name,IpAddress,Vlan,Enabled")] Device input)
        {
            var existing = _repo.GetAll().FirstOrDefault(d => d.Id == input.Id);
            if (existing is null)
            {
                TempData["Toast"] = "That device no longer exists.";
                return RedirectToAction("Index", "Home");
            }

            ValidateBasic(input);
            ValidateDuplicateIp(input.IpAddress, excludeId: input.Id);

            if (!ModelState.IsValid)
                return View(input);

            // Track changes for the log
            var changed = new List<string>();
            var details = new Dictionary<string, string>();

            if (!string.Equals(existing.Name, input.Name?.Trim(), StringComparison.Ordinal))
            {
                details["old.Name"] = existing.Name ?? "";
                details["new.Name"] = input.Name?.Trim() ?? "";
                changed.Add("Name");
            }
            if (!string.Equals(existing.IpAddress, input.IpAddress?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                details["old.IpAddress"] = existing.IpAddress ?? "";
                details["new.IpAddress"] = input.IpAddress?.Trim() ?? "";
                changed.Add("IP");
            }
            if (!string.Equals(existing.Vlan, input.Vlan?.Trim(), StringComparison.Ordinal))
            {
                details["old.Vlan"] = existing.Vlan ?? "";
                details["new.Vlan"] = input.Vlan?.Trim() ?? "";
                changed.Add("VLAN");
            }
            if (existing.Enabled != input.Enabled)
            {
                details["old.Enabled"] = existing.Enabled ? "true" : "false";
                details["new.Enabled"] = input.Enabled ? "true" : "false";
                changed.Add("Enabled");
            }

            // Apply edits
            existing.Name = input.Name?.Trim() ?? "";
            existing.IpAddress = input.IpAddress?.Trim() ?? "";
            existing.Vlan = input.Vlan?.Trim() ?? "";
            existing.Enabled = input.Enabled;

            var saved = _repo.Upsert(existing);

            // Log only if something changed
            if (changed.Count > 0)
            {
                _logs.Append(new LogsRepository.LogEntry
                {
                    Level = "Info",
                    Source = "UserAction",
                    Action = "DeviceEdited",
                    Actor = "system",
                    DeviceId = saved.Id,
                    DeviceName = saved.Name,
                    Message = $"Edited device \"{saved.Name}\" (changed: {string.Join(", ", changed)})",
                    Details = details
                });
            }

            TempData["Toast"] = $"Saved changes to \"{saved.Name}\".";
            return RedirectToAction("Index", "Home");
        }

        // --------------------- DELETE --------------------

        // POST: /Devices/Delete/{id}
        [HttpPost]
        public IActionResult Delete(Guid id)
        {
            // Get a copy before removal so we can log meaningful details
            var before = _repo.GetAll().FirstOrDefault(d => d.Id == id);

            var removed = _repo.Remove(id);

            if (removed)
            {
                _logs.Append(new LogsRepository.LogEntry
                {
                    Level = "Info",
                    Source = "UserAction",
                    Action = "DeviceDeleted",
                    Actor = "system",
                    DeviceId = before?.Id,
                    DeviceName = before?.Name,
                    Message = $"Deleted device \"{before?.Name ?? id.ToString()}\"",
                    Details = new Dictionary<string, string>
                    {
                        ["ip"] = before?.IpAddress ?? "",
                        ["vlan"] = before?.Vlan ?? ""
                    }
                });
            }

            TempData["Toast"] = removed ? "Device deleted." : "Device was not found.";
            return RedirectToAction("Index", "Home");
        }

        // ----------------- TOGGLE ENABLED ----------------

        // POST: /Devices/ToggleEnabled/{id}
        [HttpPost]
        public IActionResult ToggleEnabled(Guid id)
        {
            var dev = _repo.GetAll().FirstOrDefault(d => d.Id == id);
            var oldEnabled = dev?.Enabled;

            var enabled = _repo.ToggleEnabled(id);
            if (enabled is null)
            {
                TempData["Toast"] = "Device was not found.";
                return RedirectToAction("Index", "Home");
            }

            // Log toggle action
            _logs.Append(new LogsRepository.LogEntry
            {
                Level = "Info",
                Source = "UserAction",
                Action = "ToggleEnabled",
                Actor = "system",
                DeviceId = dev?.Id,
                DeviceName = dev?.Name,
                Message = $"{(enabled.Value ? "Enabled" : "Disabled")} device \"{dev?.Name ?? id.ToString()}\"",
                Details = new Dictionary<string, string>
                {
                    ["old.Enabled"] = (oldEnabled ?? !enabled.Value) ? "true" : "false",
                    ["new.Enabled"] = enabled.Value ? "true" : "false"
                }
            });

            TempData["Toast"] = enabled.Value ? "Device enabled." : "Device disabled.";
            return RedirectToAction("Index", "Home");
        }

        // -------------------- Helpers --------------------

        private void ValidateBasic(Device d)
        {
            if (string.IsNullOrWhiteSpace(d.Name))
                ModelState.AddModelError(nameof(Device.Name), "Name is required.");

            if (string.IsNullOrWhiteSpace(d.IpAddress))
            {
                ModelState.AddModelError(nameof(Device.IpAddress), "IP address is required.");
            }
            else
            {
                var ip = d.IpAddress.Trim();
                if (!IPAddress.TryParse(ip, out _))
                    ModelState.AddModelError(nameof(Device.IpAddress), "Enter a valid IPv4 or IPv6 address.");
            }
            // VLAN is optional.
        }

        private void ValidateDuplicateIp(string? ipCandidate, Guid? excludeId)
        {
            var ip = ipCandidate?.Trim();
            if (string.IsNullOrWhiteSpace(ip)) return;

            var exists = _repo.GetAll()
                              .Any(d =>
                                   (!excludeId.HasValue || d.Id != excludeId.Value) &&
                                   string.Equals(d.IpAddress?.Trim(), ip, StringComparison.OrdinalIgnoreCase));

            if (exists)
                ModelState.AddModelError(nameof(Device.IpAddress), "Another device already uses this IP address.");
        }
    }
}
