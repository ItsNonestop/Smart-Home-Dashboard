using System.Net.NetworkInformation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartHomeDashboard.Models;
using SmartHomeDashboard.Repositories;

namespace SmartHomeDashboard.Services
{
    /// <summary>
    /// Background monitor that periodically checks device reachability and updates status.
    /// v1: ICMP ping only. Also emits log entries to LogsRepository when status changes.
    /// </summary>
    public class DeviceMonitorService : BackgroundService
    {
        private readonly ILogger<DeviceMonitorService> _logger;
        private readonly DeviceRepository _repo;
        private readonly LogsRepository _logs;

        // Simple tunables (can move to appsettings later)
        private readonly TimeSpan _interval = TimeSpan.FromSeconds(30);
        private readonly int _pingTimeoutMs = 1000;

        public DeviceMonitorService(
            ILogger<DeviceMonitorService> logger,
            DeviceRepository repo,
            LogsRepository logs)
        {
            _logger = logger;
            _repo = repo;
            _logs = logs;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("DeviceMonitorService started.");

            // Small startup delay so the app can finish warming up
            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); } catch { /* ignored */ }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var snapshot = _repo.GetAll();
                    if (snapshot.Count > 0)
                    {
                        foreach (var d in snapshot)
                        {
                            if (stoppingToken.IsCancellationRequested) break;

                            // Skip disabled devices entirely
                            if (!d.Enabled) continue;

                            var previous = d.Status;

                            var (reachable, error) = await CheckReachableAsync(d.IpAddress, _pingTimeoutMs, stoppingToken);

                            // Compute new status
                            var newStatus = reachable ? DeviceStatus.Online : DeviceStatus.Offline;

                            // Update model
                            d.Status = newStatus;
                            if (reachable)
                            {
                                d.LastSeenUtc = DateTimeOffset.UtcNow;
                            }

                            // Persist to devices.json
                            _repo.Upsert(d);

                            // If status changed, append a concise log entry
                            if (newStatus != previous)
                            {
                                var msg = $"{d.Name} is {newStatus} ({d.IpAddress})";
                                _logs.Append(new LogsRepository.LogEntry
                                {
                                    Level = "Info",
                                    Source = "Monitor",
                                    Action = "StatusChanged",
                                    Actor = "system",
                                    DeviceId = d.Id,
                                    DeviceName = d.Name,
                                    Message = msg,
                                    Details = new Dictionary<string, string>
                                    {
                                        ["oldStatus"] = previous.ToString(),
                                        ["newStatus"] = newStatus.ToString(),
                                        ["ip"] = d.IpAddress ?? "",
                                        ["vlan"] = d.Vlan ?? ""
                                    }
                                });
                            }

                            if (error is not null)
                                _logger.LogDebug("Ping error for {Name} ({Ip}): {Err}", d.Name, d.IpAddress, error.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Device monitoring iteration failed.");
                }

                try { await Task.Delay(_interval, stoppingToken); } catch { /* cancellation or timer error */ }
            }

            _logger.LogInformation("DeviceMonitorService stopping.");
        }

        private static async Task<(bool ok, Exception? error)> CheckReachableAsync(string? ip, int timeoutMs, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(ip)) return (false, null);

            try
            {
                using var ping = new Ping();
#if NET8_0_OR_GREATER
                var reply = await ping.SendPingAsync(ip, timeoutMs);
#else
                var reply = await Task.Run(() => ping.Send(ip, timeoutMs), ct);
#endif
                return (reply.Status == IPStatus.Success, null);
            }
            catch (Exception ex)
            {
                // Firewalls or invalid IPs can throw; treat as unreachable and report the error upstream.
                return (false, ex);
            }
        }
    }
}
