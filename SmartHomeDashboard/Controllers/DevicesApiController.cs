using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using SmartHomeDashboard.Models;
using SmartHomeDashboard.Repositories;

namespace SmartHomeDashboard.Controllers
{
    /// <summary>
    /// Lightweight snapshot endpoint for the dashboard to poll.
    /// Uses an ETag so the client can send If-None-Match and get 304 Not Modified
    /// (zero body) when nothing changed — minimal bandwidth and CPU.
    ///
    /// GET /api/devices
    ///   200 OK   + JSON body + ETag: W/"..."
    ///   304 Not Modified  (when If-None-Match matches current ETag)
    /// </summary>
    [ApiController]
    [Route("api/devices")]
    [Produces("application/json")]
    public class DevicesApiController : ControllerBase
    {
        private readonly DeviceRepository _repo;

        public DevicesApiController(DeviceRepository repo)
        {
            _repo = repo;
        }

        [HttpGet]
        public IActionResult Get()
        {
            var devices = _repo.GetAll();

            // Compute a stable weak ETag from the device snapshot (only fields that affect UI)
            var etag = ComputeWeakEtag(devices);

            // If client already has same version, return 304 (no body)
            var inm = Request.Headers.IfNoneMatch.ToString();
            if (!string.IsNullOrWhiteSpace(inm))
            {
                // If-None-Match may contain multiple values; do a simple contains check.
                if (inm.Split(',').Select(s => s.Trim()).Any(v => string.Equals(v, etag, StringComparison.Ordinal)))
                {
                    Response.Headers.ETag = etag;
                    Response.Headers.CacheControl = "no-cache";
                    return StatusCode(304);
                }
            }

            // Prepare a small DTO payload (kept simple and readable)
            var payload = devices
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new DeviceDto
                {
                    Id = d.Id,
                    Name = d.Name,
                    IpAddress = d.IpAddress,
                    Vlan = d.Vlan,
                    Status = d.Status.ToString(),
                    Enabled = d.Enabled,
                    LastSeenUtc = d.LastSeenUtc
                })
                .ToList();

            Response.Headers.ETag = etag;
            Response.Headers.CacheControl = "no-cache";
            return Ok(payload);
        }

        // Optional HEAD for quick checks (returns headers incl. ETag)
        [HttpHead]
        public IActionResult Head()
        {
            var etag = ComputeWeakEtag(_repo.GetAll());
            Response.Headers.ETag = etag;
            Response.Headers.CacheControl = "no-cache";
            return Ok();
        }

        private static string ComputeWeakEtag(IReadOnlyList<Device> devices)
        {
            // Build a compact string capturing the UI-relevant state of the list
            // Sort by Id to ensure stable ordering for hashing.
            var sb = new StringBuilder();
            foreach (var d in devices.OrderBy(x => x.Id))
            {
                sb.Append(d.Id).Append('|')
                  .Append(d.Name).Append('|')
                  .Append(d.IpAddress).Append('|')
                  .Append(d.Vlan).Append('|')
                  .Append((int)d.Status).Append('|')
                  .Append(d.Enabled ? '1' : '0').Append('|')
                  .Append(d.LastSeenUtc?.ToUnixTimeSeconds() ?? 0L)
                  .Append('\n');
            }

            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var hash = sha.ComputeHash(bytes);
            var b64 = Convert.ToBase64String(hash);     // short & HTTP-safe
            return $"W/\"{b64}\"";                       // weak ETag
        }

        // DTO kept minimal for polling
        private sealed class DeviceDto
        {
            public Guid Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public string IpAddress { get; set; } = string.Empty;
            public string Vlan { get; set; } = string.Empty;
            public string Status { get; set; } = "Unknown";
            public bool Enabled { get; set; }
            public DateTimeOffset? LastSeenUtc { get; set; }
        }
    }
}
