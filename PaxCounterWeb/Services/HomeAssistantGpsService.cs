using System.Net.Http.Headers;
using System.Text;

namespace PaxCounterWeb.Services;

public sealed class HomeAssistantGpsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public HomeAssistantGpsService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_configuration["HomeAssistantGps:ForceUrl"]);

    public async Task<(bool Success, string Message)> ForceRefreshAsync(CancellationToken ct)
    {
        var url = _configuration["HomeAssistantGps:ForceUrl"];
        var bearer = _configuration["HomeAssistantGps:BearerToken"];
        var methodText = _configuration["HomeAssistantGps:Method"] ?? "POST";
        var contentType = _configuration["HomeAssistantGps:ContentType"] ?? "application/json";
        var body = _configuration["HomeAssistantGps:Body"] ?? string.Empty;

        if (string.IsNullOrWhiteSpace(url))
        {
            return (false, "Force GPS is not configured yet in appsettings.json.");
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
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (response.IsSuccessStatusCode)
            {
                return (true, string.IsNullOrWhiteSpace(responseBody)
                    ? "Force GPS request sent."
                    : $"Force GPS request sent ({(int)response.StatusCode}).");
            }

            var shortBody = string.IsNullOrWhiteSpace(responseBody)
                ? $"HTTP {(int)response.StatusCode}"
                : $"HTTP {(int)response.StatusCode}: {responseBody}";
            return (false, shortBody.Length > 240 ? shortBody[..240] : shortBody);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "Force GPS timed out after 10s.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
