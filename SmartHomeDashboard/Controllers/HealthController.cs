using System;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;
using SmartHomeDashboard.Repositories;

namespace SmartHomeDashboard.Controllers
{
    /// <summary>
    /// Simple health endpoints:
    ///   GET /health  -> 200 OK      (process is up)
    ///   GET /ready   -> 200 READY   (storage initialized), or 503 NOT_READY
    /// Keep responses plain text so they are scrape-friendly.
    /// </summary>
    [ApiController]
    public class HealthController : ControllerBase
    {
        private readonly DeviceRepository _devices;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<HealthController> _logger;

        public HealthController(DeviceRepository devices, IWebHostEnvironment env, ILogger<HealthController> logger)
        {
            _devices = devices;
            _env = env;
            _logger = logger;
        }

        // Liveness: if the app can execute this action, we're alive.
        [HttpGet("/health")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public IActionResult Health()
        {
            var stamp = DateTimeOffset.UtcNow.ToString("u");
            return Content($"OK {stamp}", "text/plain");
        }

        // Readiness: make sure storage exists and repo can read data.
        [HttpGet("/ready")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public IActionResult Ready()
        {
            try
            {
                // Touch the repo (will also create/seed on first run).
                var devices = _devices.GetAll();

                // Verify App_Data directory is present on disk (publish/run folder).
                var dataDir = Path.Combine(_env.ContentRootPath, "App_Data");
                var devicesPath = Path.Combine(dataDir, "devices.json");
                var dirExists = Directory.Exists(dataDir);
                var fileExists = System.IO.File.Exists(devicesPath);

                if (dirExists && fileExists)
                {
                    return Content($"READY devices={devices.Count}", "text/plain");
                }

                _logger.LogWarning("Readiness check: dirExists={DirExists}, fileExists={FileExists}, path={Path}",
                    dirExists, fileExists, devicesPath);
                return StatusCode(503, "NOT_READY");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Readiness check failed");
                return StatusCode(503, "NOT_READY");
            }
        }
    }
}
