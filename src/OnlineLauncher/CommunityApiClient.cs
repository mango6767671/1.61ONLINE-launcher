using System.Net.Http;
namespace OnlineLauncher;
public sealed class CommunityApiClient : IDisposable
{
 private readonly HttpClient _http;
 public CommunityApiClient(){var url=Environment.GetEnvironmentVariable("ONLINE_LAUNCHER_API") ?? "http://127.0.0.1:3000/";_http=new HttpClient{BaseAddress=new Uri(url.EndsWith('/')?url:url+"/"),Timeout=TimeSpan.FromSeconds(4)};}
 public async Task<bool> CheckBootstrapAsync(CancellationToken token=default){try{using var response=await _http.GetAsync("v1/client/bootstrap",token);return response.IsSuccessStatusCode;}catch(HttpRequestException){return false;}catch(TaskCanceledException){return false;}}
 public void Dispose()=>_http.Dispose();
}