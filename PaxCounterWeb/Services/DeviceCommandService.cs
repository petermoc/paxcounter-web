using Microsoft.EntityFrameworkCore;
using PaxCounterWeb.Data.PaxCounterWeb.Data;
using PaxCounterWeb.Models;

namespace PaxCounterWeb.Services;

public sealed class DeviceCommandService
{
    private readonly AppDbContext _db;
    private readonly PhoneGpsService _phoneGpsService;
    private readonly UiFeatureSettingsService _uiFeatureSettingsService;

    public DeviceCommandService(AppDbContext db, PhoneGpsService phoneGpsService, UiFeatureSettingsService uiFeatureSettingsService)
    {
        _db = db;
        _phoneGpsService = phoneGpsService;
        _uiFeatureSettingsService = uiFeatureSettingsService;
    }

    public async Task<DeviceCommandQueueResult> QueueSendCycleAsync(Device device, int intervalSeconds, CancellationToken ct)
    {
        if (intervalSeconds < 10 || intervalSeconds > 600 || intervalSeconds % 2 != 0)
        {
            return new DeviceCommandQueueResult(false, "Interval must be an even value between 10s and 600s.");
        }

        var sendCycle = intervalSeconds / 2;
        if (sendCycle > byte.MaxValue)
        {
            return new DeviceCommandQueueResult(false, "Requested interval is too large for pax firmware.");
        }

        var result = await QueueCommandAsync(device, "set_sendcycle", new byte[] { 0x0A, (byte)sendCycle }, intervalSeconds, ct);
        if (!result.Success)
        {
            return result;
        }

        return await AppendGpsSyncMessageAsync(result, intervalSeconds, ct);
    }

    public async Task<DeviceCommandBulkResult> QueueSendCycleForAllAsync(int intervalSeconds, CancellationToken ct)
    {
        var devices = await _db.Devices
            .Where(d => !d.IsHidden && !string.IsNullOrWhiteSpace(d.DeviceId))
            .OrderBy(d => d.DeviceId)
            .ToListAsync(ct);

        var queued = 0;
        foreach (var device in devices)
        {
            var result = await QueueSendCycleAsync(device, intervalSeconds, ct);
            if (result.Success)
            {
                queued++;
            }
        }

        return new DeviceCommandBulkResult(
            queued > 0,
            queued,
            $"Queued {intervalSeconds}s for {queued} pax device(s). Background delivery is now running.");
    }

    public async Task<DeviceCommandQueueResult> QueueRssiLimitAsync(Device device, int rssiLimit, CancellationToken ct)
    {
        if (rssiLimit > 0 || rssiLimit < -128)
        {
            return new DeviceCommandQueueResult(false, "RLIM must be 0 (off) or between -1 and -128 dBm.");
        }

        var encoded = Math.Abs(rssiLimit);
        var label = rssiLimit == 0 ? 0 : rssiLimit;
        return await QueueCommandAsync(device, "set_rssi", new byte[] { 0x01, (byte)encoded }, label, ct);
    }

    public async Task<DeviceCommandBulkResult> QueueRssiLimitForAllAsync(int rssiLimit, CancellationToken ct)
    {
        var devices = await _db.Devices
            .Where(d => !d.IsHidden && !string.IsNullOrWhiteSpace(d.DeviceId))
            .OrderBy(d => d.DeviceId)
            .ToListAsync(ct);

        var queued = 0;
        foreach (var device in devices)
        {
            var result = await QueueRssiLimitAsync(device, rssiLimit, ct);
            if (result.Success)
            {
                queued++;
            }
        }

        return new DeviceCommandBulkResult(
            queued > 0,
            queued,
            $"Queued RLIM {(rssiLimit == 0 ? "off" : rssiLimit + " dBm")} for {queued} pax device(s).");
    }

    public async Task<DeviceCommandQueueResult> QueueBleScanIntervalAsync(Device device, int intervalMilliseconds, CancellationToken ct)
    {
        if (intervalMilliseconds < 10 || intervalMilliseconds > 2550 || intervalMilliseconds % 10 != 0)
        {
            return new DeviceCommandQueueResult(false, "BLE scan interval must be between 10 ms and 2550 ms in 10 ms steps.");
        }

        var encoded = intervalMilliseconds / 10;
        return await QueueCommandAsync(device, "set_ble_scan_interval", new byte[] { 0x0C, (byte)encoded }, intervalMilliseconds, ct);
    }

    public async Task<DeviceCommandBulkResult> QueueBleScanIntervalForAllAsync(int intervalMilliseconds, CancellationToken ct)
    {
        var devices = await _db.Devices
            .Where(d => !d.IsHidden && !string.IsNullOrWhiteSpace(d.DeviceId))
            .OrderBy(d => d.DeviceId)
            .ToListAsync(ct);

        var queued = 0;
        foreach (var device in devices)
        {
            var result = await QueueBleScanIntervalAsync(device, intervalMilliseconds, ct);
            if (result.Success)
            {
                queued++;
            }
        }

        return new DeviceCommandBulkResult(
            queued > 0,
            queued,
            $"Queued BLE scan {intervalMilliseconds} ms for {queued} pax device(s).");
    }

    public async Task<DeviceCommandQueueResult> CancelPendingAsync(int deviceEntityId, CancellationToken ct)
    {
        var pending = await _db.PendingDeviceCommands
            .Where(x => x.DeviceEntityId == deviceEntityId && x.ConsumedAtUtc == null)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return new DeviceCommandQueueResult(false, "No pending command to cancel for this pax.");
        }

        _db.PendingDeviceCommands.RemoveRange(pending);
        await SqliteWriteHelper.SaveChangesWithRetryAsync(_db, ct);
        return new DeviceCommandQueueResult(true, "Pending command canceled for this pax.");
    }

    public async Task<PendingDeviceCommand?> TryConsumePendingCommandAsync(Device device, CancellationToken ct)
    {
        var pending = await _db.PendingDeviceCommands
            .Where(x => x.DeviceEntityId == device.Id && x.ConsumedAtUtc == null)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (pending == null)
        {
            return null;
        }

        pending.ConsumedAtUtc = DateTime.UtcNow;
        pending.WifiDispatchCount += 1;
        await SqliteWriteHelper.SaveChangesWithRetryAsync(_db, ct);
        return pending;
    }

    private async Task<DeviceCommandQueueResult> QueueCommandAsync(Device device, string commandName, byte[] payload, int requestedValue, CancellationToken ct)
    {
        var payloadBase64 = Convert.ToBase64String(payload);
        var payloadHex = Convert.ToHexString(payload);

        var existingPending = await _db.PendingDeviceCommands
            .Where(x => x.DeviceEntityId == device.Id && x.CommandName == commandName && x.ConsumedAtUtc == null)
            .ToListAsync(ct);

        if (existingPending.Count > 0)
        {
            _db.PendingDeviceCommands.RemoveRange(existingPending);
        }

        var command = new PendingDeviceCommand
        {
            DeviceEntityId = device.Id,
            CommandName = commandName,
            PayloadBase64 = payloadBase64,
            PayloadHex = payloadHex,
            RequestedSeconds = requestedValue,
            RequestedTransport = "LoRa+WiFi",
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.PendingDeviceCommands.Add(command);
        await SqliteWriteHelper.SaveChangesWithRetryAsync(_db, ct);

        var label = commandName switch
        {
            "set_rssi" => requestedValue == 0 ? "RLIM off" : $"RLIM {requestedValue} dBm",
            "set_ble_scan_interval" => $"BLE scan {requestedValue} ms",
            _ => $"{requestedValue}s"
        };

        return new DeviceCommandQueueResult(true, $"Queued {label} for {device.DeviceId}. Delivery now continues in the background over LoRa and WiFi.");
    }

    private async Task<DeviceCommandQueueResult> AppendGpsSyncMessageAsync(DeviceCommandQueueResult baseResult, int intervalSeconds, CancellationToken ct)
    {
        if (!baseResult.Success || !_uiFeatureSettingsService.GetGpsIntervalSyncEnabled())
        {
            return baseResult;
        }

        var syncResult = await _phoneGpsService.ApplyIntervalAsync(intervalSeconds, ct);
        if (!syncResult.Success)
        {
            return new DeviceCommandQueueResult(true, $"{baseResult.Message} Phone GPS sync note: {syncResult.Message}");
        }

        return new DeviceCommandQueueResult(true, $"{baseResult.Message} Phone GPS interval queued for its next HTTPS check.");
    }
}

public readonly record struct DeviceCommandQueueResult(bool Success, string Message);

public readonly record struct DeviceCommandBulkResult(bool Success, int QueuedCount, string Message);
