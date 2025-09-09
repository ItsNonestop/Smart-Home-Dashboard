using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using SmartHomeDashboard.Models.Options;
using SmartHomeDashboard.Repositories;

namespace SmartHomeDashboard.Controllers
{
    [AutoValidateAntiforgeryToken]
    public class SettingsController : Controller
    {
        private readonly AppSettingsStore _store;
        private readonly LogsRepository _logs;

        public SettingsController(AppSettingsStore store, LogsRepository logs)
        {
            _store = store;
            _logs = logs;
        }

        [HttpGet]
        public IActionResult Index()
        {
            var options = _store.GetMonitor();
            var model = ToViewModel(options);
            ViewData["Title"] = "Settings";
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Index(SettingsViewModel model)
        {
            if (!TryParsePortsCsv(model.TcpPortsCsv, out var ports, out var csvError))
                ModelState.AddModelError(nameof(SettingsViewModel.TcpPortsCsv), csvError!);

            if (!ModelState.IsValid)
            {
                ViewData["Title"] = "Settings";
                return View(model);
            }

            var options = ToOptions(model, ports);
            if (!_store.TrySaveMonitor(options, out var changes, out var errorMessage))
            {
                ModelState.AddModelError(string.Empty, errorMessage ?? "Failed to save settings.");
                ViewData["Title"] = "Settings";
                return View(model);
            }

            var details = changes?.ToDictionary(
                kv => kv.Key,
                kv => JsonSerializer.Serialize(new { old = kv.Value.OldValue, @new = kv.Value.NewValue })
            );

            _logs.Append(new LogsRepository.LogEntry
            {
                Level = "Info",
                Source = "UserAction",
                Action = "SettingsChanged",
                Actor = "system",
                Message = "Monitor settings updated",
                Details = details
            });

            TempData["ToastSuccess"] = "Settings saved.";
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public IActionResult DownloadLogsJson()
        {
            var entries = _logs.GetAll();
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            var bytes = Encoding.UTF8.GetBytes(json);
            var fileName = $"logs-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-utc.json";
            return File(bytes, "application/json", fileName);
        }

        [HttpGet]
        public IActionResult DownloadLogsExcel()
        {
            var entries = _logs.GetAll(); // newest first

            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Logs");

            // -------- Columns (mirrors JSON + user friendly) --------
            // 1 Timestamp (UTC)
            // 2 Level
            // 3 Source
            // 4 Action
            // 5 Actor
            // 6 Device Name
            // 7 Device ID
            // 8 Status (explicit or inferred, UPPERCASE)
            // 9 IP
            // 10 VLAN
            // 11 Message
            // 12 Details (multiline: key = value)

            var headers = new[]
            {
                "Timestamp (UTC)","Level","Source","Action","Actor",
                "Device Name","Device ID","Status","IP","VLAN","Message","Details"
            };

            for (int c = 0; c < headers.Length; c++)
                ws.Cell(1, c + 1).Value = headers[c];

            var header = ws.Range(1, 1, 1, headers.Length);
            header.Style.Font.Bold = true;
            header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF8"); // soft blue header
            header.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            header.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

            int row = 2;
            foreach (var e in entries)
            {
                // Extract common values
                var status = ExtractStatus(e);
                var statusUpper = string.IsNullOrWhiteSpace(status) ? "-" : status.ToUpperInvariant();
                var ip = e.Details != null && e.Details.TryGetValue("ip", out var ipVal) ? ipVal : "";
                var vlan = e.Details != null && e.Details.TryGetValue("vlan", out var vlanVal) ? vlanVal : "";

                ws.Cell(row, 1).Value = e.TimestampUtc.UtcDateTime;
                ws.Cell(row, 1).Style.DateFormat.Format = "yyyy-mm-dd HH:mm:ss";

                ws.Cell(row, 2).Value = e.Level ?? "";
                ws.Cell(row, 3).Value = e.Source ?? "";
                ws.Cell(row, 4).Value = e.Action ?? "";
                ws.Cell(row, 5).Value = e.Actor ?? "system";
                ws.Cell(row, 6).Value = e.DeviceName ?? "";
                ws.Cell(row, 7).Value = e.DeviceId?.ToString() ?? "";
                ws.Cell(row, 8).Value = statusUpper;
                ws.Cell(row, 9).Value = ip;
                ws.Cell(row, 10).Value = vlan;
                ws.Cell(row, 11).Value = e.Message ?? "";

                // Multiline details (better readability with WrapText)
                string detailsStr = "";
                if (e.Details is not null && e.Details.Count > 0)
                    detailsStr = string.Join(Environment.NewLine, e.Details.Select(kv => $"{kv.Key} = {kv.Value}"));
                ws.Cell(row, 12).Value = detailsStr;

                // Alternating row fill for readability
                if (row % 2 == 0)
                    ws.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#F7F9FC");

                // Cell borders
                ws.Range(row, 1, row, headers.Length).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                ws.Range(row, 1, row, headers.Length).Style.Border.InsideBorder = XLBorderStyleValues.Thin;

                // -------- Visual cues (color coding & emphasis) --------

                // Level emphasis (Error/Warning/Success)
                var levelCell = ws.Cell(row, 2);
                var level = (e.Level ?? "").Trim().ToLowerInvariant();
                levelCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                levelCell.Style.Font.Bold = true;
                if (level == "error")
                {
                    levelCell.Style.Font.FontColor = XLColor.FromHtml("#DC2626");
                    levelCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF2F2");
                }
                else if (level == "warning" || level == "warn")
                {
                    levelCell.Style.Font.FontColor = XLColor.FromHtml("#D97706");
                    levelCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF3C7");
                }
                else if (level == "success")
                {
                    levelCell.Style.Font.FontColor = XLColor.FromHtml("#16A34A");
                    levelCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#ECFDF5");
                }

                // Source emphasis (UserAction blue, Monitor slate, System purple)
                var sourceCell = ws.Cell(row, 3);
                var src = (e.Source ?? "").Trim().ToLowerInvariant();
                sourceCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                sourceCell.Style.Font.Bold = true;
                if (src == "useraction")
                    sourceCell.Style.Font.FontColor = XLColor.FromHtml("#1D4ED8"); // blue
                else if (src == "monitor")
                    sourceCell.Style.Font.FontColor = XLColor.FromHtml("#334155"); // slate
                else
                    sourceCell.Style.Font.FontColor = XLColor.FromHtml("#7C3AED"); // purple for system/other

                // Action emphasis
                var actionCell = ws.Cell(row, 4);
                var act = (e.Action ?? "").Trim().ToLowerInvariant();
                actionCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                actionCell.Style.Font.Bold = true;
                switch (act)
                {
                    case "devicedeleted":
                        actionCell.Style.Font.FontColor = XLColor.FromHtml("#DC2626");
                        actionCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF2F2");
                        break;
                    case "deviceedited":
                        actionCell.Style.Font.FontColor = XLColor.FromHtml("#2563EB");
                        actionCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#EFF6FF");
                        break;
                    case "devicecreated":
                        actionCell.Style.Font.FontColor = XLColor.FromHtml("#16A34A");
                        actionCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#ECFDF5");
                        break;
                    case "toggleenabled":
                        actionCell.Style.Font.FontColor = XLColor.FromHtml("#7C3AED");
                        actionCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F5F3FF");
                        break;
                    case "statuschanged":
                        actionCell.Style.Font.FontColor = XLColor.FromHtml("#334155");
                        break;
                }

                // Status formatting (UPPERCASE + color + subtle fill)
                var statusCell = ws.Cell(row, 8);
                statusCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                var s = statusUpper;
                if (s == "ONLINE")
                {
                    statusCell.Style.Font.Bold = true;
                    statusCell.Style.Font.FontColor = XLColor.FromHtml("#16A34A"); // green
                    statusCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#ECFDF5"); // soft green
                }
                else if (s == "OFFLINE")
                {
                    statusCell.Style.Font.Bold = true;
                    statusCell.Style.Font.FontColor = XLColor.FromHtml("#DC2626"); // red
                    statusCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF2F2"); // soft red
                }

                row++;
            }

            // Borders around whole used range
            if (row > 2)
            {
                var used = ws.Range(1, 1, row - 1, headers.Length);
                used.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                used.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            }

            // Column sizing & alignment
            ws.Column(1).Width = 22;
            ws.Column(2).Width = 10;
            ws.Column(3).Width = 12;
            ws.Column(4).Width = 16;
            ws.Column(5).Width = 16;
            ws.Column(6).Width = 22;
            ws.Column(7).Width = 42;
            ws.Column(8).Width = 10;
            ws.Column(9).Width = 16;
            ws.Column(10).Width = 16;
            ws.Column(11).Width = 60;
            ws.Column(12).Width = 60;

            // Wrap text where useful
            ws.Columns(11, 12).Style.Alignment.WrapText = true;
            ws.Columns(11, 12).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;

            // Freeze header and enable AutoFilter
            ws.SheetView.FreezeRows(1);
            ws.Range(1, 1, Math.Max(1, row - 1), headers.Length).SetAutoFilter();

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            stream.Position = 0;

            var fileName = $"logs-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-utc.xlsx";
            const string excelMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            return File(stream.ToArray(), excelMime, fileName);
        }

        private static string ExtractStatus(LogsRepository.LogEntry e)
        {
            // Prefer explicit "newStatus" from monitor entries
            if (string.Equals(e.Action, "StatusChanged", StringComparison.OrdinalIgnoreCase)
                && e.Details is not null
                && e.Details.TryGetValue("newStatus", out var ns)
                && !string.IsNullOrWhiteSpace(ns))
            {
                return ns.ToLowerInvariant();
            }

            // Fall back: look for online/offline in message
            var msg = e.Message?.ToLowerInvariant() ?? string.Empty;
            if (msg.Contains(" online")) return "online";
            if (msg.Contains(" offline")) return "offline";
            return "-";
        }

        // ----------- helpers -----------

        private static SettingsViewModel ToViewModel(MonitorOptions src) => new()
        {
            PollIntervalSeconds = src.PollIntervalSeconds,
            PingTimeoutMs = src.PingTimeoutMs,
            TcpFallbackEnabled = src.TcpFallbackEnabled,
            TcpPortsCsv = src.TcpPorts is null || src.TcpPorts.Count == 0
                ? string.Empty
                : string.Join(",", src.TcpPorts)
        };

        private static MonitorOptions ToOptions(SettingsViewModel vm, List<int> ports) => new()
        {
            PollIntervalSeconds = vm.PollIntervalSeconds,
            PingTimeoutMs = vm.PingTimeoutMs,
            TcpFallbackEnabled = vm.TcpFallbackEnabled,
            TcpPorts = ports
        };

        private static bool TryParsePortsCsv(string? csv, out List<int> ports, out string? error)
        {
            ports = new List<int>();
            error = null;
            if (string.IsNullOrWhiteSpace(csv))
                return true;

            foreach (var segment in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var token = segment.Trim();
                if (int.TryParse(token, out var port))
                {
                    ports.Add(port);
                }
                else
                {
                    error = $"Invalid port \"{token}\".";
                    return false;
                }
            }

            return true;
        }
    }

    public class SettingsViewModel
    {
        public int PollIntervalSeconds { get; set; }
        public int PingTimeoutMs { get; set; }
        public bool TcpFallbackEnabled { get; set; }
        public string? TcpPortsCsv { get; set; }
    }
}
