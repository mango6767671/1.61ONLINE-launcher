using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OnlineLauncher;

public sealed class ApiException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public ApiException(HttpStatusCode statusCode, string message) : base(message) => StatusCode = statusCode;
}

public sealed record ServiceHealth(bool IsReady, string Message);

public sealed class CommunityApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;

    public CommunityApiClient(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute, out var uri))
            throw new ArgumentException("The community API URL is invalid.", nameof(baseUrl));

        _http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("1.61ONLINE-Launcher/0.2");
    }

    public async Task<ServiceHealth> CheckServiceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var ready = await _http.GetAsync("health/ready", cancellationToken);
            if (!ready.IsSuccessStatusCode)
                return new ServiceHealth(false, "API reachable, database not ready");

            using var bootstrap = await _http.GetAsync("v1/client/bootstrap", cancellationToken);
            if (!bootstrap.IsSuccessStatusCode)
                return new ServiceHealth(false, "API responds, but the client contract is unavailable");

            return new ServiceHealth(true, "Community API and database ready");
        }
        catch (TaskCanceledException)
        {
            return new ServiceHealth(false, "Connection timed out");
        }
        catch (HttpRequestException)
        {
            return new ServiceHealth(false, "Can't reach this API URL");
        }
    }

    public Task<JsonDocument> GetJsonAsync(string path, string? accessToken = null, CancellationToken cancellationToken = default) =>
        SendJsonAsync(HttpMethod.Get, path, null, accessToken, cancellationToken);

    public Task<JsonDocument> PostJsonAsync(string path, object body, string? accessToken = null, CancellationToken cancellationToken = default) =>
        SendJsonAsync(HttpMethod.Post, path, body, accessToken, cancellationToken);

    public Task<JsonDocument> PatchJsonAsync(string path, object body, string? accessToken = null, CancellationToken cancellationToken = default) =>
        SendJsonAsync(HttpMethod.Patch, path, body, accessToken, cancellationToken);
    public Task<JsonDocument> PutJsonAsync(string path, object body, string? accessToken = null, CancellationToken cancellationToken = default) =>
        SendJsonAsync(HttpMethod.Put, path, body, accessToken, cancellationToken);

    public Task<JsonDocument> DeleteJsonAsync(string path, string? accessToken = null, CancellationToken cancellationToken = default) =>
        SendJsonAsync(HttpMethod.Delete, path, null, accessToken, cancellationToken);

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, object? body, string? accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new ApiException(response.StatusCode, FriendlyError(response.StatusCode, content));

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(content) ? "{}" : content);
    }

    private static string FriendlyError(HttpStatusCode statusCode, string content)
    {
        string? code = null;
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("error", out var error)) code = error.GetString();
        }
        catch (JsonException) { }

        return code switch
        {
            "invalid_credentials" => "Email or password is incorrect.",
            "account_already_exists" => "An account already exists with that email.",
            "account_suspended" => "This community account is suspended.",
            "refresh_token_expired" or "invalid_refresh_token" or "refresh_token_reuse_detected" => "Your session expired. Please sign in again.",
            "invalid_request" or "invalid_query" => "Some entered information is invalid. Check the fields and try again.",
            "player_not_found" => "That player could not be found.",
            "already_friends" or "friend_request_already_pending" or "incoming_friend_request_exists" => "A friend relationship or request already exists.",
            "session_full" => "That session is full.",
            "already_in_session" => "You are already in a community session. Leave it before joining another.",
            "session_not_found" => "That session is no longer available.",
            _ when statusCode == HttpStatusCode.Unauthorized => "You need to sign in again.",
            _ when statusCode == HttpStatusCode.Forbidden => "This action is not permitted for this account.",
            _ when statusCode == HttpStatusCode.Conflict => "The request conflicts with the current account or session state.",
            _ when (int)statusCode >= 500 => "The community service had a server error. Try again shortly.",
            _ => $"The community API rejected the request (HTTP {(int)statusCode})."
        };
    }

    public void Dispose() => _http.Dispose();
}
