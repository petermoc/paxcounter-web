using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PaxCounterWeb.Models;

namespace PaxCounterWeb.Services;

public sealed class HomeAssistantGpsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly UiFeatureSettingsService _uiFeatureSettingsService;

    public HomeAssistantGpsService(HttpClient httpClient, IConfiguration configuration, UiFeatureSettingsService uiFeatureSettingsService)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _uiFeatureSettingsService = uiFeatureSettingsService;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_configuration["HomeAssistantGps:ForceUrl"]);

    public async Task<(bool Success, string Message)> ForceRefreshAsync(CancellationToken ct)
    {
        var forceUrl = ResolveForceUrl();
        var method = _configuration["HomeAssistantGps:Method"] ?? "POST";
        var contentType = _configuration["HomeAssistantGps:ContentType"] ?? "application/json";
        var boostDurationSeconds = Math.Clamp(GetConfiguredInt("HomeAssistantGps:BoostDurationSeconds", 120), 15, 600);
        var refreshPulseSeconds = Math.Clamp(GetConfiguredInt("HomeAssistantGps:RefreshPulseSeconds", 15), 5, 120);
        var refreshCycles = Math.Max(1, (int)Math.Ceiling(boostDurationSeconds / (double)refreshPulseSeconds));
        var highAccuracyIntervalSeconds = Math.Clamp(GetConfiguredInt("HomeAssistantGps:HighAccuracyUpdateIntervalSeconds", 5), 5, 120);

        var setupSteps = new (string Label, string Body)[]
        {
            ("High accuracy on", _configuration["HomeAssistantGps:HighAccuracyForceOnBody"] ?? JsonSerializer.Serialize(new
            {
                message = "command_high_accuracy_mode",
                data = new
                {
                    command = "force_on"
                }
            })),
            ("High accuracy interval", _configuration["HomeAssistantGps:HighAccuracyIntervalBody"] ?? JsonSerializer.Serialize(new
            {
                message = "command_high_accuracy_mode",
                data = new
                {
                    command = "high_accuracy_set_update_interval",
                    high_accuracy_update_interval = highAccuracyIntervalSeconds
                }
            }))
        };

        foreach (var step in setupSteps)
        {
            var result = await SendConfiguredRequestAsync(forceUrl, method, contentType, step.Body, ct, step.Label);
            if (!result.Success)
            {
                return result;
            }

            await Task.Delay(250, ct);
        }

        var intervalSync = await ApplyIntervalAsync(highAccuracyIntervalSeconds, ct);
        if (!intervalSync.Success)
        {
            return intervalSync;
        }

        for (var cycle = 0; cycle < refreshCycles; cycle++)
        {
            var sensorRefresh = await SendConfiguredRequestAsync(forceUrl, method, contentType,
                _configuration["HomeAssistantGps:UpdateSensorsBody"] ?? JsonSerializer.Serialize(new { message = "command_update_sensors" }),
                ct,
                $"Sensor refresh {cycle + 1}/{refreshCycles}");
            if (!sensorRefresh.Success)
            {
                return sensorRefresh;
            }

            await Task.Delay(250, ct);

            var locationRefresh = await SendConfiguredRequestAsync(forceUrl, method, contentType,
                _configuration["HomeAssistantGps:Body"] ?? JsonSerializer.Serialize(new { message = "request_location_update" }),
                ct,
                $"Location refresh {cycle + 1}/{refreshCycles}");
            if (!locationRefresh.Success)
            {
                return locationRefresh;
            }

            if (cycle < refreshCycles - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(refreshPulseSeconds), ct);
            }
        }

        return (true, $"High-accuracy GPS boost is active for about {boostDurationSeconds}s with {refreshPulseSeconds}s refresh pulses.");
    }

    public async Task<(bool Success, string Message)> ApplyIntervalAsync(int seconds, CancellationToken ct, bool highAccuracyEnabled = true)
    {
        var intervalUrl = _configuration["HomeAssistantGps:IntervalUrl"];
        var intervalEntityId = _configuration["HomeAssistantGps:IntervalEntityId"];
        if (string.IsNullOrWhiteSpace(intervalUrl) || string.IsNullOrWhiteSpace(intervalEntityId))
        {
            return (false, "Phone GPS interval sync is not configured yet.");
        }

        var resolvedOption = ResolveIntervalOption(seconds);
        var intervalBody = JsonSerializer.Serialize(new
        {
            entity_id = intervalEntityId,
            option = resolvedOption.ToString()
        });

        var intervalResult = await SendConfiguredRequestAsync(
            intervalUrl,
            "POST",
            "application/json",
            intervalBody,
            ct,
            "GPS interval sync");

        if (!intervalResult.Success)
        {
            return intervalResult;
        }

        var highFrequencyUrl = highAccuracyEnabled
            ? _configuration["HomeAssistantGps:HighFrequencyUrl"]
            : _configuration["HomeAssistantGps:HighFrequencyOffUrl"];
        var highFrequencyEntityId = _configuration["HomeAssistantGps:HighFrequencyEntityId"];
        if (string.IsNullOrWhiteSpace(highFrequencyUrl) || string.IsNullOrWhiteSpace(highFrequencyEntityId))
        {
            return (true, BuildIntervalSyncMessage(seconds, resolvedOption));
        }

        var highFrequencyBody = JsonSerializer.Serialize(new
        {
            entity_id = highFrequencyEntityId
        });

        var highFrequencyResult = await SendConfiguredRequestAsync(
            highFrequencyUrl,
            "POST",
            "application/json",
            highFrequencyBody,
            ct,
            "GPS high-frequency toggle");

        if (!highFrequencyResult.Success)
        {
            return highFrequencyResult;
        }

        return (true, BuildIntervalSyncMessage(seconds, resolvedOption));
    }

    public async Task<(bool Success, string Message)> DisableHighAccuracyAsync(CancellationToken ct)
    {
        var forceUrl = ResolveForceUrl();
        if (string.IsNullOrWhiteSpace(forceUrl))
        {
            return (false, "Force GPS is not configured yet.");
        }

        return await SendConfiguredRequestAsync(
            forceUrl,
            _configuration["HomeAssistantGps:Method"] ?? "POST",
            _configuration["HomeAssistantGps:ContentType"] ?? "application/json",
            _configuration["HomeAssistantGps:HighAccuracyForceOffBody"] ?? JsonSerializer.Serialize(new
            {
                message = "command_high_accuracy_mode",
                data = new { command = "force_off" }
            }),
            ct,
            "High accuracy off");
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

    private static string BuildIntervalSyncMessage(int requestedSeconds, int appliedSeconds)
    {
        if (requestedSeconds == appliedSeconds)
        {
            return $"Phone GPS interval synced to {appliedSeconds}s.";
        }

        return $"Phone GPS interval synced to {appliedSeconds}s (closest available option for requested {requestedSeconds}s).";
    }

    private int GetConfiguredInt(string key, int fallback)
    {
        return int.TryParse(_configuration[key], out var parsed) ? parsed : fallback;
    }

    private async Task<(bool Success, string Message)> SendConfiguredRequestAsync(string? url, string methodText, string contentType, string body, CancellationToken ct, string label)
    {
        var bearer = _configuration["HomeAssistantGps:BearerToken"];

        if (string.IsNullOrWhiteSpace(url))
        {
            return (false, $"{label} is not configured yet in appsettings.json.");
        }

        var method = string.Equals(methodText, "GET", StringComparison.OrdinalIgnoreCase)
            ? HttpMethod.Get
            : HttpMethod.Post;

        using var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (method != HttpMethod.Get)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var historyTimeoutSeconds = Math.Clamp(GetConfiguredInt("HomeAssistantGps:HistoryTimeoutSeconds", 45), 10, 120);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(historyTimeoutSeconds));

        try
        {
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (response.IsSuccessStatusCode)
            {
                return (true, string.IsNullOrWhiteSpace(responseBody)
                    ? $"{label} request sent."
                    : $"{label} request sent ({(int)response.StatusCode}).");
            }

            var shortBody = string.IsNullOrWhiteSpace(responseBody)
                ? $"HTTP {(int)response.StatusCode}"
                : $"HTTP {(int)response.StatusCode}: {responseBody}";
            return (false, shortBody.Length > 240 ? shortBody[..240] : shortBody);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, $"{label} timed out after 10s.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public Task<HomeAssistantTrackerStatus> GetTrackerStatusAsync(CancellationToken ct)
    {
        var entityId = _configuration["HomeAssistantGps:AutoModeDeviceTrackerEntityId"] ?? "device_tracker.sm_s911b";
        return GetTrackerStatusAsync(entityId, ct);
    }

    public async Task<HomeAssistantTrackerStatus> GetTrackerStatusAsync(string entityId, CancellationToken ct)
    {
        var statesBaseUrl = _configuration["HomeAssistantGps:StatesBaseUrl"]?.TrimEnd('/');
        var bearer = _configuration["HomeAssistantGps:BearerToken"];

        if (string.IsNullOrWhiteSpace(statesBaseUrl))
        {
            return new HomeAssistantTrackerStatus(false, entityId, "-", null, null, null, null, "StatesBaseUrl is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{statesBaseUrl}/{entityId}");
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new HomeAssistantTrackerStatus(true, entityId, "error", null, null, null, null, $"HTTP {(int)response.StatusCode}: {body}");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var state = root.TryGetProperty("state", out var stateEl) ? stateEl.GetString() ?? "-" : "-";
            DateTimeOffset? lastUpdated = null;
            if (root.TryGetProperty("last_updated", out var updEl) && DateTimeOffset.TryParse(updEl.GetString(), out var parsed))
            {
                lastUpdated = parsed.ToUniversalTime();
            }

            double? lat = null;
            double? lon = null;
            int? acc = null;
            if (root.TryGetProperty("attributes", out var attrs))
            {
                if (attrs.TryGetProperty("latitude", out var latEl) && latEl.TryGetDouble(out var latVal)) lat = latVal;
                if (attrs.TryGetProperty("longitude", out var lonEl) && lonEl.TryGetDouble(out var lonVal)) lon = lonVal;
                if (attrs.TryGetProperty("gps_accuracy", out var accEl) && accEl.TryGetInt32(out var accVal)) acc = accVal;
            }

            return new HomeAssistantTrackerStatus(true, entityId, state, lastUpdated, lat, lon, acc, string.Empty);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HomeAssistantTrackerStatus(true, entityId, "timeout", null, null, null, null, "Tracker status request timed out.");
        }
        catch (Exception ex)
        {
            return new HomeAssistantTrackerStatus(true, entityId, "error", null, null, null, null, ex.Message);
        }
    }

    public async Task<List<HomeAssistantTrackerHistoryPoint>> GetTrackerHistoryAsync(string entityId, DateTimeOffset sinceUtc, DateTimeOffset untilUtc, CancellationToken ct)
    {
        var historyBaseUrl = ResolveHistoryBaseUrl();
        var bearer = _configuration["HomeAssistantGps:BearerToken"];

        if (string.IsNullOrWhiteSpace(historyBaseUrl) || string.IsNullOrWhiteSpace(entityId) || untilUtc <= sinceUtc)
        {
            return new List<HomeAssistantTrackerHistoryPoint>();
        }

        var start = Uri.EscapeDataString(sinceUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        var end = Uri.EscapeDataString(untilUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        var url = $"{historyBaseUrl}/{start}?filter_entity_id={Uri.EscapeDataString(entityId)}&end_time={end}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var historyTimeoutSeconds = Math.Clamp(GetConfiguredInt("HomeAssistantGps:HistoryTimeoutSeconds", 45), 10, 120);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(historyTimeoutSeconds));

        try
        {
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new List<HomeAssistantTrackerHistoryPoint>();
            }

            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new List<HomeAssistantTrackerHistoryPoint>();
            }

            var rows = new List<HomeAssistantTrackerHistoryPoint>();
            foreach (var series in doc.RootElement.EnumerateArray())
            {
                if (series.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in series.EnumerateArray())
                {
                    if (!TryParseTrackerHistoryPoint(item, entityId, out var point))
                    {
                        continue;
                    }

                    if (point.TimestampUtc < sinceUtc || point.TimestampUtc > untilUtc)
                    {
                        continue;
                    }

                    rows.Add(point);
                }
            }

            return rows
                .OrderBy(p => p.TimestampUtc)
                .GroupBy(p => $"{p.TimestampUtc:O}|{p.Latitude:F6}|{p.Longitude:F6}|{p.SourceTopic}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }
        catch
        {
            return new List<HomeAssistantTrackerHistoryPoint>();
        }
    }

    public async Task<string> ResolveGpsSourceTopicAsync(GpsMessage gps, string defaultTopic, CancellationToken ct)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var staleThreshold = TimeSpan.FromMinutes(10);
        var selectedPhone = _uiFeatureSettingsService.GetSelectedGpsPhone();
        var preferredPhone = string.Equals(selectedPhone, "S23", StringComparison.OrdinalIgnoreCase) ? "S23" : "A52";

        var a52 = await GetTrackerStatusAsync("device_tracker.sm_a528b_star_telefon", ct);
        var s23 = await GetTrackerStatusAsync("device_tracker.sm_s911b", ct);

        var candidates = new List<(string Topic, string Phone, double DistanceMeters, int Accuracy, bool Preferred, bool Plausible, bool Stable)>();

        AddCandidate(a52, "phone/gps/a52", "A52");
        AddCandidate(s23, "phone/gps/s23", "S23");

        if (candidates.Count == 0)
        {
            return ResolveFallbackGpsTopic(defaultTopic, preferredPhone);
        }

        return candidates
            .OrderByDescending(c => c.Preferred && c.Plausible)
            .ThenBy(c => PhoneRank(c.Phone, preferredPhone))
            .ThenByDescending(c => c.Plausible)
            .ThenByDescending(c => c.Stable)
            .ThenBy(c => c.DistanceMeters)
            .ThenBy(c => c.Accuracy)
            .Select(c => c.Topic)
            .First();

        void AddCandidate(HomeAssistantTrackerStatus status, string topic, string phone)
        {
            if (!status.IsConfigured || status.IsStale(staleThreshold, nowUtc) || !status.Latitude.HasValue || !status.Longitude.HasValue)
            {
                return;
            }

            var distance = HaversineMeters(gps.lat, gps.lon, status.Latitude.Value, status.Longitude.Value);
            var accuracy = Math.Max(1, status.Accuracy ?? 9999);
            var preferred = string.Equals(phone, preferredPhone, StringComparison.OrdinalIgnoreCase);
            var plausible = accuracy <= 250 || distance <= 1500;
            var stable = accuracy <= 60 && distance <= 300;

            if (!plausible && accuracy > 600 && distance > 5000)
            {
                return;
            }

            candidates.Add((topic, phone, distance, accuracy, preferred, plausible, stable));
        }

        static int PhoneRank(string phone, string preferredPhone)
        {
            if (string.Equals(phone, preferredPhone, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return string.Equals(preferredPhone, "A52", StringComparison.OrdinalIgnoreCase)
                ? (string.Equals(phone, "S23", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                : (string.Equals(phone, "A52", StringComparison.OrdinalIgnoreCase) ? 1 : 2);
        }
    }

    private string? ResolveHistoryBaseUrl()
    {
        var configured = _configuration["HomeAssistantGps:HistoryBaseUrl"]?.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var statesBaseUrl = _configuration["HomeAssistantGps:StatesBaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(statesBaseUrl))
        {
            return null;
        }

        const string suffix = "/api/states";
        if (statesBaseUrl.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return statesBaseUrl[..^suffix.Length] + "/api/history/period";
        }

        return null;
    }

    private static bool TryParseTrackerHistoryPoint(JsonElement item, string entityId, out HomeAssistantTrackerHistoryPoint point)
    {
        point = default!;

        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!item.TryGetProperty("last_updated", out var updatedEl) || !DateTimeOffset.TryParse(updatedEl.GetString(), out var updated))
        {
            return false;
        }

        if (!item.TryGetProperty("attributes", out var attrs))
        {
            return false;
        }

        if (!attrs.TryGetProperty("latitude", out var latEl) || !latEl.TryGetDouble(out var lat))
        {
            return false;
        }

        if (!attrs.TryGetProperty("longitude", out var lonEl) || !lonEl.TryGetDouble(out var lon))
        {
            return false;
        }

        int? acc = null;
        if (attrs.TryGetProperty("gps_accuracy", out var accEl) && accEl.TryGetInt32(out var parsedAcc))
        {
            acc = parsedAcc;
        }

        point = new HomeAssistantTrackerHistoryPoint(
            updated.ToUniversalTime(),
            lat,
            lon,
            acc,
            $"ha:{entityId}");
        return true;
    }

    private static string ResolveFallbackGpsTopic(string defaultTopic, string preferredPhone)
    {
        if (!string.Equals(defaultTopic, "phone/gps", StringComparison.OrdinalIgnoreCase))
        {
            return defaultTopic;
        }

        return string.Equals(preferredPhone, "S23", StringComparison.OrdinalIgnoreCase)
            ? "phone/gps/s23"
            : "phone/gps/a52";
    }

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000;
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    private string? ResolveForceUrl()
    {
        var selectedPhone = _uiFeatureSettingsService.GetSelectedGpsPhone();
        var configuredUrl = _configuration[$"HomeAssistantGps:PhoneForceUrls:{selectedPhone}"];
        if (!string.IsNullOrWhiteSpace(configuredUrl))
        {
            return configuredUrl;
        }

        var baseUrl = _configuration["HomeAssistantGps:ForceUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return baseUrl;
        }

        var selectedService = ResolvePhoneNotifyService(selectedPhone);
        if (string.IsNullOrWhiteSpace(selectedService))
        {
            return baseUrl;
        }

        var knownServices = new[]
        {
            "mobile_app_sm_s911b",
            "mobile_app_a52s",
            "mobile_app_a52",
            "mobile_app_sm_a528b"
        };

        foreach (var service in knownServices)
        {
            if (baseUrl.Contains(service, StringComparison.OrdinalIgnoreCase))
            {
                return baseUrl.Replace(service, selectedService, StringComparison.OrdinalIgnoreCase);
            }
        }

        return baseUrl;
    }

    private string ResolvePhoneNotifyService(string selectedPhone)
    {
        var configuredService = _configuration[$"HomeAssistantGps:PhoneNotifyServices:{selectedPhone}"];
        if (!string.IsNullOrWhiteSpace(configuredService))
        {
            return configuredService;
        }

        return string.Equals(selectedPhone, "S23", StringComparison.OrdinalIgnoreCase)
            ? "mobile_app_sm_s911b"
            : "mobile_app_sm_a528b";
    }
}


public sealed record HomeAssistantTrackerStatus(
    bool IsConfigured,
    string EntityId,
    string State,
    DateTimeOffset? LastUpdatedUtc,
    double? Latitude,
    double? Longitude,
    int? Accuracy,
    string Message)
{
    public bool IsStale(TimeSpan threshold, DateTimeOffset nowUtc)
        => !LastUpdatedUtc.HasValue || nowUtc - LastUpdatedUtc.Value > threshold;
}

public sealed record HomeAssistantTrackerHistoryPoint(DateTimeOffset TimestampUtc, double Latitude, double Longitude, int? Accuracy, string SourceTopic);
