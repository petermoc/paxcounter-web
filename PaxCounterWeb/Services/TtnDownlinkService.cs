using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PaxCounterWeb.Models;

namespace PaxCounterWeb.Services;

public sealed class TtnDownlinkService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public TtnDownlinkService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<TtnDownlinkResult> TrySendAsync(Device device, byte[] payload, CancellationToken ct)
    {
        var baseUrl = _configuration["TtnDownlink:BaseUrl"];
        var applicationId = _configuration["TtnDownlink:ApplicationId"];
        var apiToken = _configuration["TtnDownlink:ApiToken"];

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(applicationId) || string.IsNullOrWhiteSpace(apiToken))
        {
            return new TtnDownlinkResult(false, "TTN downlink is not configured yet.");
        }

        if (string.IsNullOrWhiteSpace(device.DeviceId))
        {
            return new TtnDownlinkResult(false, "Device has no TTN device_id.");
        }

        var url = $"{baseUrl.TrimEnd('/')}/api/v3/as/applications/{Uri.EscapeDataString(applicationId)}/devices/{Uri.EscapeDataString(device.DeviceId)}/down/push";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

        var body = new
        {
            downlinks = new[]
            {
                new
                {
                    f_port = 2,
                    frm_payload = Convert.ToBase64String(payload),
                    priority = "NORMAL"
                }
            }
        };

        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (response.IsSuccessStatusCode)
            {
                return new TtnDownlinkResult(true, $"LoRa downlink accepted ({(int)response.StatusCode}).");
            }

            var shortBody = string.IsNullOrWhiteSpace(responseBody)
                ? $"HTTP {(int)response.StatusCode}"
                : $"HTTP {(int)response.StatusCode}: {responseBody}";
            return new TtnDownlinkResult(false, shortBody.Length > 240 ? shortBody[..240] : shortBody);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new TtnDownlinkResult(false, "TTN downlink timed out after 8s.");
        }
        catch (Exception ex)
        {
            return new TtnDownlinkResult(false, ex.Message);
        }
    }
}

public readonly record struct TtnDownlinkResult(bool Accepted, string Message);
