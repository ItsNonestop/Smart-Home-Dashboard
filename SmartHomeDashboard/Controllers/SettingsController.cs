using System.Linq;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SmartHomeDashboard.Models.Options;
using SmartHomeDashboard.Repositories;

namespace SmartHomeDashboard.Controllers
{
    /// <summary>
    /// Settings (read-only for now) + log downloads (JSON & Excel).
    /// Includes a POST scaffold ready for future editable settings with validation.
    /// </summary>
    [AutoValidateAntiforgeryToken]
    public class SettingsController : Controller
    {
        private readonly IOptions<MonitorOptions> _monitorOptions;
        private readonly LogsRepository _logs;

        public SettingsController(IOptions<MonitorOptions> monitorOptions, LogsRepository logs)
        {
            _monitorOptions = monitorOptions;
            _logs = logs;
        }

        // GET: /Settings
        [HttpGet]
        public IActionResult Index()
        {
            var model = _monitorOptions.Value; // read-only
            ViewData["Title"] = "Settings";
            return View(model);
        }

        // POST: /Settings (framework only – does NOT persist yet)
        [HttpPost]
        public IActionResult Index(MonitorOptions input)
        {
            if (!ModelState.IsValid)
            {
                ViewData["Title"] = "Settings";
                TempData["Toast"] = "Validation errors — changes not saved.";
                return View(input);
            }

            TempData["Toast"] = "Editing is disabled in this build. No changes were saved.";
            ViewData["Title"] = "Settings";
            return View(_monitorOptions.Value);
        }

        // GET: /Settings/DownloadLogsJson
        [HttpGet]
        public IActionResult DownloadLogsJson()
        {
            var entries = _logs.GetAll(); // newest first
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });

            var bytes = Encoding.UTF8.GetBytes(json);
            var fileName = $"logs-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-utc.json";
            return File(bytes, "application/json", fileName);
        }

        // GET: /Settings/DownloadLogsExcel
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

                // Action emphasis (DeviceDeleted red, DeviceEdited blue, DeviceCreated green, ToggleEnabled purple, StatusChanged slate)
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
            ws.Column(1).Width = 22; // timestamp
            ws.Column(2).Width = 10; // level
            ws.Column(3).Width = 12; // source
            ws.Column(4).Width = 16; // action
            ws.Column(5).Width = 16; // actor
            ws.Column(6).Width = 22; // device name
            ws.Column(7).Width = 42; // device id
            ws.Column(8).Width = 10; // status
            ws.Column(9).Width = 16; // ip
            ws.Column(10).Width = 16; // vlan
            ws.Column(11).Width = 60; // message
            ws.Column(12).Width = 60; // details

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
    }
}
