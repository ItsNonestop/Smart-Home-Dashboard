using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartHomeDashboard.Models;
using SmartHomeDashboard.Models.Options;
using SmartHomeDashboard.Repositories;
using System.Linq;

namespace SmartHomeDashboard.Services
{
    /// <summary>
    /// Background monitor that periodically checks device reachability and updates status.
    /// Reads settings on each loop and applies changes without restarting the app.
    /// </summary>
    public class DeviceMonitorService : BackgroundService
    {
        private readonly ILogger<DeviceMonitorService> _logger;
        private readonly DeviceRepository _repo;
        private readonly LogsRepository _logs;
        private readonly AppSettingsStore _settingsStore;

        private MonitorOptions _lastApplied;

        public DeviceMonitorService(
            ILogger<DeviceMonitorService> logger,
            DeviceRepository repo,
            LogsRepository logs,
            AppSettingsStore settingsStore)
        {
            _logger = logger;
            _repo = repo;
            _logs = logs;
            _settingsStore = settingsStore;
            try
            {
                _lastApplied = _settingsStore.GetMonitor();
            }
            catch
            {
                _lastApplied = new MonitorOptions();
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("DeviceMonitorService started.");

            // Small startup delay so the app can finish warming up
            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); } catch { }

            while (!stoppingToken.IsCancellationRequested)
            {
                var options = _lastApplied;
                try
                {
                    var current = _settingsStore.GetMonitor();
                    var diff = BuildDiff(_lastApplied, current);
                    if (diff.Count > 0)
                    {
                        var diffObj = diff.ToDictionary(kv => kv.Key, kv => new { old = kv.Value.Old, @new = kv.Value.New });
                        _logs.Append(new LogsRepository.LogEntry
                        {
                            Level = "Info",
                            Source = "System",
                            Action = "SettingsApplied",
                            Actor = "system",
                            Message = "Monitor settings applied",
                            Details = new Dictionary<string, string>
                            {
                                ["changes"] = JsonSerializer.Serialize(diffObj)
                            }
                        });
                        _lastApplied = current;
                    }
                    options = _lastApplied;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read monitor settings; continuing with last applied values.");
                }

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

                            var (reachable, error) = await CheckReachableAsync(
                                d.IpAddress,
                                options.PingTimeoutMs,
                                options.TcpFallbackEnabled,
                                options.TcpPorts ?? new List<int>(),
                                stoppingToken);

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
                                _logger.LogDebug("Ping/TCP error for {Name} ({Ip}): {Err}", d.Name, d.IpAddress, error.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Device monitoring iteration failed.");
                }

                try { await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken); } catch { }
            }

            _logger.LogInformation("DeviceMonitorService stopping.");
        }

        private static async Task<(bool ok, Exception? error)> CheckReachableAsync(
            string? ip,
            int timeoutMs,
            bool tcpFallback,
            List<int> tcpPorts,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(ip)) return (false, null);

            Exception? pingError = null;
            try
            {
                using var ping = new Ping();
#if NET8_0_OR_GREATER
                var reply = await ping.SendPingAsync(ip, timeoutMs);
#else
                var reply = await Task.Run(() => ping.Send(ip, timeoutMs), ct);
#endif
                if (reply.Status == IPStatus.Success)
                    return (true, null);
            }
            catch (Exception ex)
            {
                pingError = ex;
            }

            if (tcpFallback && tcpPorts.Count > 0)
            {
                foreach (var port in tcpPorts)
                {
                    try
                    {
                        using var client = new TcpClient();
                        var connectTask = client.ConnectAsync(ip, port);
                        var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs, ct));
                        if (completed == connectTask && client.Connected)
                            return (true, null);
                    }
                    catch
                    {
                        // ignore and try next port
                    }
                }
            }

            return (false, pingError);
        }

        private static Dictionary<string, (object? Old, object? New)> BuildDiff(MonitorOptions oldOpts, MonitorOptions newOpts)
        {
            var diff = new Dictionary<string, (object? Old, object? New)>();

            if (oldOpts.PollIntervalSeconds != newOpts.PollIntervalSeconds)
                diff["PollIntervalSeconds"] = (oldOpts.PollIntervalSeconds, newOpts.PollIntervalSeconds);
            if (oldOpts.PingTimeoutMs != newOpts.PingTimeoutMs)
                diff["PingTimeoutMs"] = (oldOpts.PingTimeoutMs, newOpts.PingTimeoutMs);
            if (oldOpts.TcpFallbackEnabled != newOpts.TcpFallbackEnabled)
                diff["TcpFallbackEnabled"] = (oldOpts.TcpFallbackEnabled, newOpts.TcpFallbackEnabled);

            var oldPorts = oldOpts.TcpPorts ?? new List<int>();
            var newPorts = newOpts.TcpPorts ?? new List<int>();
            if (!oldPorts.SequenceEqual(newPorts))
                diff["TcpPorts"] = (
                    oldPorts.Count == 0 ? null : oldPorts.ToArray(),
                    newPorts.Count == 0 ? null : newPorts.ToArray());

            return diff;
        }
    }
}
