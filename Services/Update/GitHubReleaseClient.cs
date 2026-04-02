using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using VRCosme.Services;

namespace VRCosme.Services.Update;

public sealed class GitHubReleaseInfo
{
    public required string TagName { get; init; }
    public required string Body { get; init; }
    public required string HtmlUrl { get; init; }
    public required bool Prerelease { get; init; }
    public required bool Draft { get; init; }
    public required IReadOnlyList<GitHubReleaseAsset> Assets { get; init; }
}

public sealed class GitHubReleaseAsset
{
    public required string Name { get; init; }
    public required string BrowserDownloadUrl { get; init; }
}

public static class GitHubReleaseClient
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/natuki53/VRCosme/releases/latest";
    private const string ReleasesUrl = "https://api.github.com/repos/natuki53/VRCosme/releases?per_page=20";
    private static readonly HttpClient Client = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<GitHubReleaseInfo?> GetLatestReleaseAsync(
        bool includePrerelease = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var dto = includePrerelease
                ? await GetLatestIncludingPrereleaseAsync(cancellationToken)
                : await GetLatestStableAsync(cancellationToken);
            if (dto == null)
                return null;

            var assets = (dto.Assets ?? [])
                .Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl))
                .Select(a => new GitHubReleaseAsset
                {
                    Name = a.Name!,
                    BrowserDownloadUrl = a.BrowserDownloadUrl!
                })
                .ToList();

            return new GitHubReleaseInfo
            {
                TagName = dto.TagName ?? "",
                Body = dto.Body ?? "",
                HtmlUrl = dto.HtmlUrl ?? "",
                Prerelease = dto.Prerelease,
                Draft = dto.Draft,
                Assets = assets
            };
        }
        catch (Exception ex)
        {
            LogService.Error("更新チェック失敗: GitHub Releases 取得に失敗", ex);
            return null;
        }
    }

    private static async Task<ReleaseDto?> GetLatestStableAsync(CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(LatestReleaseUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LogService.Error($"更新チェック失敗: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<ReleaseDto>(stream, JsonOptions, cancellationToken);
    }

    private static async Task<ReleaseDto?> GetLatestIncludingPrereleaseAsync(CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(ReleasesUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LogService.Error($"更新チェック失敗: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var dtos = await JsonSerializer.DeserializeAsync<List<ReleaseDto>>(stream, JsonOptions, cancellationToken);
        if (dtos == null || dtos.Count == 0)
            return null;

        var latestPrerelease = dtos.FirstOrDefault(r => r is { Draft: false, Prerelease: true });
        if (latestPrerelease != null)
            return latestPrerelease;

        return dtos.FirstOrDefault(r => r is { Draft: false, Prerelease: false });
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VRCosme/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("assets")] public List<AssetDto>? Assets { get; set; }
    }

    private sealed class AssetDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
