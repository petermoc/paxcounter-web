using Microsoft.EntityFrameworkCore;
using PaxCounterWeb.Data.PaxCounterWeb.Data;

namespace PaxCounterWeb.Services;

public sealed class PendingCommandDispatcherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PendingCommandDispatcherService> _logger;

    public PendingCommandDispatcherService(IServiceScopeFactory scopeFactory, ILogger<PendingCommandDispatcherService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchPendingCommandsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pending command dispatcher loop failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task DispatchPendingCommandsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ttnDownlink = scope.ServiceProvider.GetRequiredService<TtnDownlinkService>();

        var pending = await db.PendingDeviceCommands
            .Include(x => x.Device)
            .Where(x => x.LoRaAttemptedAtUtc == null)
            .OrderBy(x => x.CreatedAtUtc)
            .Take(20)
            .ToListAsync(ct);

        foreach (var command in pending)
        {
            var payload = Convert.FromBase64String(command.PayloadBase64);
            var result = await ttnDownlink.TrySendAsync(command.Device, payload, ct);
            command.LoRaAttemptedAtUtc = DateTime.UtcNow;
            command.LoRaAccepted = result.Accepted;
            command.LoRaStatus = result.Message;
            await SqliteWriteHelper.SaveChangesWithRetryAsync(db, ct);
            _logger.LogInformation("Command {CommandId} for {DeviceId}: {Status}", command.Id, command.Device.DeviceId, result.Message);
        }
    }
}
