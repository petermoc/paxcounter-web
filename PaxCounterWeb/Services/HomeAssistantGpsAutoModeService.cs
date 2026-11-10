using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PaxCounterWeb.Services;

public sealed class HomeAssistantGpsAutoModeService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<HomeAssistantGpsAutoModeService> _logger;
    private readonly TrackerModeOverrideService _trackerModeOverrideService;
    private string? _lastAppliedMode;
    private string? _lastAppliedTrackerEntityId;
    private DateTimeOffset? _awayBoostUntilUtc;
    private DateTimeOffset? _lastAwayRefreshUtc;

    public HomeAssistantGpsAutoModeService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<HomeAssistantGpsAutoModeService> logger,
        TrackerModeOverrideService trackerModeOverrideService)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _trackerModeOverrideService = trackerModeOverrideService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncModeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Home Assistant GPS auto-mode sync failed.");
            }

            var pollSeconds = Math.Clamp(GetConfiguredInt("HomeAssistantGps:AutoModePollSeconds", 30), 10, 300);
            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
        }
    }

    private async Task SyncModeAsync(CancellationToken ct)
    {
        if (!GetConfiguredBool("HomeAssistantGps:AutoModeEnabled", true))
        {
            return;
        }

        var intervalEntityId = _configuration["HomeAssistantGps:IntervalEntityId"];
        if (string.IsNullOrWhiteSpace(intervalEntityId))
        {
            return;
        }

        var preferredTrackerEntityId = _configuration["HomeAssistantGps:AutoModeDeviceTrackerEntityId"] ?? "device_tracker.sm_a528b_star_telefon";
        var trackerCandidates = new[]
        {
            await GetTrackerSnapshotAsync("device_tracker.sm_a528b_star_telefon", ct),
            await GetTrackerSnapshotAsync("device_tracker.sm_s911b", ct)
        };

        var selectedTracker = SelectTracker(trackerCandidates, preferredTrackerEntityId);
        if (selectedTracker is null || string.IsNullOrWhiteSpace(selectedTracker.State))
        {
            return;
        }

        var trackerEntityId = selectedTracker.EntityId;
        var trackerState = selectedTracker.State!;
        var forceUrl = ResolveForceUrlForTracker(trackerEntityId);

        var homeStates = (_configuration["HomeAssistantGps:AutoModeHomeStates"] ?? "home,Paxcounter Zone")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var trackerReportsHome = homeStates.Any(state => string.Equals(state, trackerState, StringComparison.OrdinalIgnoreCase));
        var manualOverrideMode = _trackerModeOverrideService.GetMode();
        var desiredMode = manualOverrideMode ?? (trackerReportsHome ? "home" : "away");
        var desiredIsHome = string.Equals(desiredMode, "home", StringComparison.OrdinalIgnoreCase);
        var desiredInterval = desiredIsHome
            ? Math.Clamp(GetConfiguredInt("HomeAssistantGps:AutoModeHomeIntervalSeconds", 30), 5, 3600)
            : Math.Clamp(GetConfiguredInt("HomeAssistantGps:AutoModeAwayIntervalSeconds", 5), 5, 3600);

        var currentIntervalState = await GetStateAsync(intervalEntityId, ct);
        var currentInterval = int.TryParse(currentIntervalState, out var parsedInterval) ? parsedInterval : 0;

        if (currentInterval != desiredInterval)
        {
            await ApplyIntervalAsync(desiredInterval, !desiredIsHome, ct);
            _logger.LogInformation("GPS auto-mode changed HA interval to {Interval}s using tracker {TrackerEntityId} with state '{TrackerState}' ({Mode}).", desiredInterval, trackerEntityId, trackerState, desiredMode);
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var boostDurationSeconds = Math.Clamp(GetConfiguredInt("HomeAssistantGps:BoostDurationSeconds", 120), 15, 3600);
        var refreshPulseSeconds = Math.Clamp(GetConfiguredInt("HomeAssistantGps:RefreshPulseSeconds", 15), 5, 300);

        if (!desiredIsHome)
        {
            var modeChangedToAway = !string.Equals(_lastAppliedMode, desiredMode, StringComparison.OrdinalIgnoreCase);
            var trackerChanged = !string.Equals(_lastAppliedTrackerEntityId, trackerEntityId, StringComparison.OrdinalIgnoreCase);
            if (modeChangedToAway || trackerChanged)
            {
                _awayBoostUntilUtc = nowUtc.AddSeconds(boostDurationSeconds);
                _lastAwayRefreshUtc = null;
            }

            var shouldRefresh = modeChangedToAway
                || trackerChanged
                || _lastAwayRefreshUtc is null
                || nowUtc - _lastAwayRefreshUtc.Value >= TimeSpan.FromSeconds(refreshPulseSeconds)
                || (_awayBoostUntilUtc.HasValue && nowUtc <= _awayBoostUntilUtc.Value);

            if (shouldRefresh)
            {
                await SendBoostAsync(forceUrl, ct);
                _lastAwayRefreshUtc = nowUtc;
                _logger.LogInformation(
                    modeChangedToAway || trackerChanged
                        ? "GPS auto-mode triggered away boost for {TrackerEntityId} because tracker state is '{TrackerState}'."
                        : "GPS auto-mode refreshed away high-accuracy tracking for {TrackerEntityId} while tracker state remains '{TrackerState}'.",
                    trackerEntityId,
                    trackerState);
            }
        }
        else
        {
            if (!string.Equals(_lastAppliedMode, desiredMode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_lastAppliedTrackerEntityId, trackerEntityId, StringComparison.OrdinalIgnoreCase))
            {
                await SendHighAccuracyOffAsync(forceUrl, ct);
                _logger.LogInformation("GPS auto-mode turned off away high-accuracy tracking for {TrackerEntityId} because tracker state returned to '{TrackerState}'.", trackerEntityId, trackerState);
            }

            _awayBoostUntilUtc = null;
            _lastAwayRefreshUtc = null;
        }

        _lastAppliedMode = desiredMode;
        _lastAppliedTrackerEntityId = trackerEntityId;
    }

    private sealed record TrackerSnapshot(string EntityId, string? State, DateTimeOffset? LastUpdatedUtc);

    private async Task<TrackerSnapshot> GetTrackerSnapshotAsync(string entityId, CancellationToken ct)
    {
        var state = await GetStateAsync(entityId, ct);
        var lastUpdatedUtc = await GetLastUpdatedAsync(entityId, ct);
        return new TrackerSnapshot(entityId, state, lastUpdatedUtc);
    }

    private TrackerSnapshot? SelectTracker(IEnumerable<TrackerSnapshot> candidates, string preferredTrackerEntityId)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var staleThreshold = TimeSpan.FromMinutes(2);
        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.State))
            .OrderBy(candidate => candidate.LastUpdatedUtc is null || nowUtc - candidate.LastUpdatedUtc.Value > staleThreshold ? 1 : 0)
            .ThenBy(candidate => string.Equals(candidate.EntityId, preferredTrackerEntityId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(candidate => candidate.LastUpdatedUtc)
            .FirstOrDefault();
    }

    private string? ResolveForceUrlForTracker(string trackerEntityId)
    {
        var phoneKey = string.Equals(trackerEntityId, "device_tracker.sm_s911b", StringComparison.OrdinalIgnoreCase)
            ? "S23"
            : "A52";

        var configuredUrl = _configuration[$"HomeAssistantGps:PhoneForceUrls:{phoneKey}"];
        if (!string.IsNullOrWhiteSpace(configuredUrl))
        {
            return configuredUrl;
        }

        return _configuration["HomeAssistantGps:ForceUrl"];
    }

    private async Task<string?> GetStateAsync(string entityId, CancellationToken ct)
    {
        var statesBaseUrl = ResolveStatesBaseUrl();
        if (string.IsNullOrWhiteSpace(statesBaseUrl))
        {
            return null;
        }

        using var request = CreateRequest(HttpMethod.Get, $"{statesBaseUrl.TrimEnd('/')}/{entityId}");
        using var response = await CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("HA state read failed for {EntityId}: {StatusCode}", entityId, (int)response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return document.RootElement.TryGetProperty("state", out var stateElement)
            ? stateElement.GetString()
            : null;
    }

    private async Task<DateTimeOffset?> GetLastUpdatedAsync(string entityId, CancellationToken ct)
    {
        var statesBaseUrl = ResolveStatesBaseUrl();
        if (string.IsNullOrWhiteSpace(statesBaseUrl))
        {
            return null;
        }

        using var request = CreateRequest(HttpMethod.Get, $"{statesBaseUrl.TrimEnd('/')}/{entityId}");
        using var response = await CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("last_updated", out var lastUpdatedElement))
        {
            return null;
        }

        return DateTimeOffset.TryParse(lastUpdatedElement.GetString(), out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private async Task ApplyIntervalAsync(int seconds, bool highAccuracyEnabled, CancellationToken ct)
    {
        var intervalUrl = _configuration["HomeAssistantGps:IntervalUrl"];
        var intervalEntityId = _configuration["HomeAssistantGps:IntervalEntityId"];
        if (string.IsNullOrWhiteSpace(intervalUrl) || string.IsNullOrWhiteSpace(intervalEntityId))
        {
            return;
        }

        var resolvedOption = ResolveIntervalOption(seconds);
        var body = JsonSerializer.Serialize(new
        {
            entity_id = intervalEntityId,
            option = resolvedOption.ToString()
        });

        await SendJsonAsync(intervalUrl, body, ct);

        var highFrequencyUrl = highAccuracyEnabled
            ? _configuration["HomeAssistantGps:HighFrequencyUrl"]
            : _configuration["HomeAssistantGps:HighFrequencyOffUrl"];
        var highFrequencyEntityId = _configuration["HomeAssistantGps:HighFrequencyEntityId"];
        if (!string.IsNullOrWhiteSpace(highFrequencyUrl) && !string.IsNullOrWhiteSpace(highFrequencyEntityId))
        {
            var highFrequencyBody = JsonSerializer.Serialize(new { entity_id = highFrequencyEntityId });
            await SendJsonAsync(highFrequencyUrl, highFrequencyBody, ct);
        }
    }

    private async Task SendBoostAsync(string? forceUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(forceUrl))
        {
            return;
        }

        var highAccuracyOnBody = _configuration["HomeAssistantGps:HighAccuracyForceOnBody"] ?? JsonSerializer.Serialize(new
        {
            message = "command_high_accuracy_mode",
            data = new { command = "force_on" }
        });

        var highAccuracyIntervalBody = _configuration["HomeAssistantGps:HighAccuracyIntervalBody"] ?? JsonSerializer.Serialize(new
        {
            message = "command_high_accuracy_mode",
            data = new
            {
                command = "high_accuracy_set_update_interval",
                high_accuracy_update_interval = Math.Clamp(GetConfiguredInt("HomeAssistantGps:AutoModeAwayIntervalSeconds", 5), 5, 120)
            }
        });

        var refreshBody = _configuration["HomeAssistantGps:Body"] ?? JsonSerializer.Serialize(new { message = "request_location_update" });
        await SendJsonAsync(forceUrl, highAccuracyOnBody, ct);
        await Task.Delay(250, ct);
        await SendJsonAsync(forceUrl, highAccuracyIntervalBody, ct);
        await Task.Delay(250, ct);
        await SendJsonAsync(forceUrl, refreshBody, ct);
    }

    private async Task SendHighAccuracyOffAsync(string? forceUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(forceUrl))
        {
            return;
        }

        var highAccuracyOffBody = _configuration["HomeAssistantGps:HighAccuracyForceOffBody"] ?? JsonSerializer.Serialize(new
        {
            message = "command_high_accuracy_mode",
            data = new { command = "force_off" }
        });

        await SendJsonAsync(forceUrl, highAccuracyOffBody, ct);
    }

    private async Task SendJsonAsync(string url, string jsonBody, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, url);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var response = await CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("HA command failed for {Url}: {StatusCode} {Body}", url, (int)response.StatusCode, body);
        }
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        var bearer = _configuration["HomeAssistantGps:BearerToken"];
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return request;
    }

    private HttpClient CreateClient() => _httpClientFactory.CreateClient();

    private string? ResolveStatesBaseUrl()
    {
        var explicitUrl = _configuration["HomeAssistantGps:StatesBaseUrl"];
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            return explicitUrl;
        }

        var sourceUrl = _configuration["HomeAssistantGps:IntervalUrl"] ?? _configuration["HomeAssistantGps:ForceUrl"];
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        var marker = "/api/services/";
        var index = sourceUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0
            ? sourceUrl[..index] + "/api/states"
            : null;
    }

    private int ResolveIntervalOption(int requestedSeconds)
    {
        var configured = (_configuration["HomeAssistantGps:SupportedOptions"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var parsed) ? parsed : (int?)null)
            .Where(value => value.HasValue && value.Value > 0)
            .Select(value => value!.Value)
            .Distinct()
            .OrderBy(value => value)
            .ToList();

        if (configured.Count == 0)
        {
            configured = new List<int> { 5, 10, 20, 30 };
        }

        if (configured.Contains(requestedSeconds))
        {
            return requestedSeconds;
        }

        return configured.Where(value => value <= requestedSeconds).DefaultIfEmpty(configured.Max()).Max();
    }

    private int GetConfiguredInt(string key, int fallback)
        => int.TryParse(_configuration[key], out var parsed) ? parsed : fallback;

    private bool GetConfiguredBool(string key, bool fallback)
        => bool.TryParse(_configuration[key], out var parsed) ? parsed : fallback;
}
