using Microsoft.EntityFrameworkCore;
using PaxCounterWeb.Data.PaxCounterWeb.Data;
using PaxCounterWeb.Models;

namespace PaxCounterWeb.Services;

public sealed class DeviceCommandService
{
    private readonly AppDbContext _db;

    public DeviceCommandService(AppDbContext db)
    {
        _db = db;
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

        var payload = new byte[] { 0x0A, (byte)sendCycle };
        var payloadBase64 = Convert.ToBase64String(payload);
        var payloadHex = Convert.ToHexString(payload);

        var existingPending = await _db.PendingDeviceCommands
            .Where(x => x.DeviceEntityId == device.Id && x.CommandName == "set_sendcycle" && x.ConsumedAtUtc == null)
            .ToListAsync(ct);

        if (existingPending.Count > 0)
        {
            _db.PendingDeviceCommands.RemoveRange(existingPending);
        }

        var command = new PendingDeviceCommand
        {
            DeviceEntityId = device.Id,
            CommandName = "set_sendcycle",
            PayloadBase64 = payloadBase64,
            PayloadHex = payloadHex,
            RequestedSeconds = intervalSeconds,
            RequestedTransport = "LoRa+WiFi",
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.PendingDeviceCommands.Add(command);
        await SqliteWriteHelper.SaveChangesWithRetryAsync(_db, ct);

        return new DeviceCommandQueueResult(true, $"Queued {intervalSeconds}s for {device.DeviceId}. Delivery now continues in the background over LoRa and WiFi.");
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
}

public readonly record struct DeviceCommandQueueResult(bool Success, string Message);

public readonly record struct DeviceCommandBulkResult(bool Success, int QueuedCount, string Message);
