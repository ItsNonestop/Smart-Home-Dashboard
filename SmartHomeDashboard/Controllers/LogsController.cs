using Microsoft.AspNetCore.Mvc;
using SmartHomeDashboard.Repositories;

namespace SmartHomeDashboard.Controllers
{
    /// <summary>
    /// Full logs page controller.
    /// Shows a larger tail of logs (newest first). Keep it fast and simple for now.
    /// </summary>
    public class LogsController : Controller
    {
        private readonly LogsRepository _logs;

        public LogsController(LogsRepository logs)
        {
            _logs = logs;
        }

        // GET /Logs or /Logs?take=500
        public IActionResult Index(int? take)
        {
            // Reasonable defaults/limits to keep things snappy.
            var count = Math.Clamp(take ?? 500, 50, 1000);

            var entries = _logs.GetTail(count); // newest first
            ViewData["Title"] = "Logs";
            ViewBag.Take = count;

            return View(entries);
        }
    }
}
