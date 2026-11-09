using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.IO;

namespace ForzaOverlay.App.Services;

public sealed record UpdateInfo(
    string Tag,
    string Version,
    string DownloadUrl,
    string ReleaseNotes,
    string AssetName);

public static class UpdateService
{
    public const string Owner = "FatFishNet";
    public const string Repo = "forza-fatfish-hud";

    public static string CurrentVersionText
    {
        get
        {
            string? text = typeof(UpdateService).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            return string.IsNullOrWhiteSpace(text) ? "1.0.0" : text;
        }
    }

    public static Version CurrentVersion
    {
        get
        {
            string text = CurrentVersionText;
            int plus = text.IndexOf('+', StringComparison.Ordinal);
            if (plus >= 0)
            {
                text = text[..plus];
            }

            return Version.TryParse(text, out Version? version) ? version : new Version(1, 0, 0);
        }
    }

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpClient client = CreateClient();
            using HttpResponseMessage response = await client.GetAsync(
                $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest",
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            string tag = root.TryGetProperty("tag_name", out JsonElement tagElement)
                ? tagElement.GetString() ?? string.Empty
                : string.Empty;
            string version = tag.TrimStart('v', 'V');
            string notes = root.TryGetProperty("body", out JsonElement bodyElement)
                ? bodyElement.GetString() ?? string.Empty
                : string.Empty;

            string? downloadUrl = null;
            string assetName = string.Empty;

            if (root.TryGetProperty("assets", out JsonElement assets))
            {
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out JsonElement nameElement)
                        ? nameElement.GetString()
                        : null;

                    if (name is not null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out JsonElement urlElement)
                            ? urlElement.GetString()
                            : null;
                        assetName = name;
                        break;
                    }
                }
            }

            if (downloadUrl is null || string.IsNullOrEmpty(version))
            {
                return null;
            }

            return new UpdateInfo(tag, version, downloadUrl, notes, assetName);
        }
        catch (Exception ex)
        {
            ForzaOverlay.Core.Logging.Diagnostics.Write("检查更新失败: " + ex.Message);
            return null;
        }
    }

    public static bool IsNewer(UpdateInfo info)
    {
        return Version.TryParse(info.Version, out Version? remote) && remote > CurrentVersion;
    }

    public static string GetTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ForzaFatFishHUD");
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        string targetPath = Path.Combine(GetTempDirectory(), info.AssetName);

        // 更新包较大（60MB+），下载超时给足 10 分钟。
        using HttpClient client = CreateClient(TimeSpan.FromMinutes(10));
        using HttpResponseMessage response = await client.GetAsync(
            info.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream output = File.Create(targetPath);

        byte[] buffer = new byte[81920];
        long received = 0;
        int read;

        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;

            if (total is > 0)
            {
                progress?.Report((double)received / total.Value);
            }
        }

        return targetPath;
    }

    private static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        HttpClient client = new()
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ForzaFatFishHUD-Updater");
        return client;
    }
}
