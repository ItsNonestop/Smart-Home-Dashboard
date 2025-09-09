using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SmartHomeDashboard.Models;
using SmartHomeDashboard.Repositories;

namespace SmartHomeDashboard.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly DeviceRepository _devices;

        public HomeController(ILogger<HomeController> logger, DeviceRepository devices)
        {
            _logger = logger;
            _devices = devices;
        }

        // Dashboard — load devices from JSON via the repository
        public IActionResult Index()
        {
            var all = _devices.GetAll(); // snapshot copy
            return View(all);
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
