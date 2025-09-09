using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using SmartHomeDashboard.Repositories;

namespace SmartHomeDashboard.Controllers
{
    /// <summary>
    /// Lightweight logs endpoint for polling with minimal bandwidth.
    /// Supports ETag so the dashboard can send If-None-Match and get 304 when unchanged.
    ///
    /// GET /api/logs?count=50
    ///   200 OK + JSON body + ETag: W/"..."
    ///   304 Not Modified (no body) when If-None-Match matches current ETag
    /// </summary>
    [ApiController]
    [Route("api/logs")]
    [Produces("application/json")]
    public class LogsApiController : ControllerBase
    {
        private readonly LogsRepository _logs;

        // Dashboard will typically request a small tail; full page can ask for more later.
        private const int DefaultCount = 50;
        private const int MaxCount = 500;

        public LogsApiController(LogsRepository logs)
        {
            _logs = logs;
        }

        [HttpGet]
        public IActionResult Get([FromQuery] int? count = null)
        {
            var take = Math.Clamp(count ?? DefaultCount, 1, MaxCount);
            var tail = _logs.GetTail(take);

            var etag = ComputeWeakEtag(tail);

            // If client already has same version, return 304 (no body)
            var inm = Request.Headers.IfNoneMatch.ToString();
            if (!string.IsNullOrWhiteSpace(inm))
            {
                if (inm.Split(',').Select(s => s.Trim()).Any(v => string.Equals(v, etag, StringComparison.Ordinal)))
                {
                    Response.Headers.ETag = etag;
                    Response.Headers.CacheControl = "no-cache";
                    return StatusCode(304);
                }
            }

            var payload = tail.Select(e => new LogDto
            {
                Id = e.Id,
                TimestampUtc = e.TimestampUtc,
                Level = e.Level,
                Source = e.Source,
                Action = e.Action,
                Actor = e.Actor,
                DeviceId = e.DeviceId,
                DeviceName = e.DeviceName,
                Message = e.Message,
                Details = e.Details
            }).ToList();

            Response.Headers.ETag = etag;
            Response.Headers.CacheControl = "no-cache";
            return Ok(payload);
        }

        // HEAD returns headers (incl. ETag) without a body.
        [HttpHead]
        public IActionResult Head([FromQuery] int? count = null)
        {
            var take = Math.Clamp(count ?? DefaultCount, 1, MaxCount);
            var tail = _logs.GetTail(take);
            var etag = ComputeWeakEtag(tail);
            Response.Headers.ETag = etag;
            Response.Headers.CacheControl = "no-cache";
            return Ok();
        }

        private static string ComputeWeakEtag(IReadOnlyList<LogsRepository.LogEntry> list)
        {
            // Hash only the tail content that affects the UI; newest-first order is fine.
            var sb = new StringBuilder();
            sb.Append("n=").Append(list.Count).Append('\n');
            foreach (var e in list)
            {
                sb.Append(e.Id).Append('|')
                  .Append(e.TimestampUtc.ToUnixTimeSeconds()).Append('|')
                  .Append(e.Level).Append('|')
                  .Append(e.Source).Append('|')
                  .Append(e.Action).Append('|')
                  .Append(e.Actor).Append('|')
                  .Append(e.DeviceId?.ToString() ?? "").Append('|')
                  .Append(e.Message)
                  .Append('\n');
            }
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            var b64 = Convert.ToBase64String(hash);
            return $"W/\"{b64}\"";
        }

        private sealed class LogDto
        {
            public Guid Id { get; set; }
            public DateTimeOffset TimestampUtc { get; set; }
            public string Level { get; set; } = "Info";
            public string Source { get; set; } = "System";
            public string Action { get; set; } = "Log";
            public string Actor { get; set; } = "system";
            public Guid? DeviceId { get; set; }
            public string? DeviceName { get; set; }
            public string Message { get; set; } = "";
            public Dictionary<string, string>? Details { get; set; }
        }
    }
}
