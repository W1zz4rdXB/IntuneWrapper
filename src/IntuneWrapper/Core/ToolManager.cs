using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace IntuneWrapper.Core
{
    public sealed class ToolRelease
    {
        public string Tag { get; set; }
        public Version Version { get; set; }
        public string DownloadUrl { get; set; }
        public string HtmlUrl { get; set; }
    }

    public sealed class ToolUpdateCheck
    {
        public ToolRelease Release { get; set; }
        public bool UpdateAvailable { get; set; }

        /// <summary>Temp copy of the latest release; the caller installs or deletes it.</summary>
        public string DownloadedPath { get; set; }
    }

    public sealed class ToolDownloadException : Exception
    {
        public ToolDownloadException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }

    /// <summary>Locates, downloads and updates IntuneWinAppUtil.exe (SPEC 4.2).</summary>
    public static class ToolManager
    {
        public const string ToolFileName = "IntuneWinAppUtil.exe";
        public const string Repo = "microsoft/Microsoft-Win32-Content-Prep-Tool";
        public const string RepoUrl = "https://github.com/" + Repo;
        public const string ReleasesUrl = RepoUrl + "/releases";
        private const string LatestReleaseApi = "https://api.github.com/repos/" + Repo + "/releases/latest";

        private static readonly Lazy<HttpClient> Http = new Lazy<HttpClient>(() =>
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("IntuneWrapper/" + AppInfo.Version);
            return client;
        });

        public static bool IsPresent(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

        /// <summary>The tool's file version, or null if it cannot be read.</summary>
        public static Version GetLocalVersion(string path)
        {
            if (!IsPresent(path)) return null;
            try
            {
                FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(path);
                return ParseVersion(fvi.FileVersion) ?? ParseVersion(fvi.ProductVersion);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Parses "v1.8.7", "1.8.7.0" etc. into a three-part version.</summary>
        public static Version ParseVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            Match m = Regex.Match(text, @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return null;
            return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        }

        /// <summary>
        /// FR-8. The tool's file version (e.g. 6.2509.50) is unrelated to the GitHub release tag
        /// (e.g. v1.8.7), so versions cannot be compared. Instead the latest release's exe is
        /// downloaded to a temp file (about 60 KB) and compared by SHA-256 with the local copy.
        /// </summary>
        public static async Task<ToolUpdateCheck> CheckForUpdateAsync(string localPath, string tempFolder,
            CancellationToken cancellationToken)
        {
            ToolRelease release = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            string temp = Path.Combine(tempFolder, "IntuneWinAppUtil-" + Guid.NewGuid().ToString("N") + ".exe");
            await DownloadAsync(release, temp, null, cancellationToken).ConfigureAwait(false);
            bool different = !IsPresent(localPath) ||
                             !string.Equals(InstallerInspector.ComputeSha256(localPath), InstallerInspector.ComputeSha256(temp),
                                 StringComparison.OrdinalIgnoreCase);
            return new ToolUpdateCheck { Release = release, UpdateAvailable = different, DownloadedPath = temp };
        }

        /// <summary>Copies a downloaded tool into place, e.g. after <see cref="CheckForUpdateAsync"/>.</summary>
        public static void Install(string downloadedPath, string destination)
        {
            ValidateExecutable(downloadedPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(downloadedPath, destination, true);
        }

        public static async Task<ToolRelease> GetLatestReleaseAsync(CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                response = await Http.Value.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                throw new ToolDownloadException("Could not reach GitHub. Check the network connection or proxy and try again.", ex);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
                {
                    throw new ToolDownloadException("GitHub's rate limit was reached. Try again later" +
                                                    RateLimitReset(response) + ".");
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw new ToolDownloadException("GitHub returned " + (int)response.StatusCode + " " +
                                                    response.ReasonPhrase + " while looking up the latest release.");
                }
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseRelease(json);
            }
        }

        internal static ToolRelease ParseRelease(string json)
        {
            GitHubRelease release;
            try
            {
                release = Json.Deserialize<GitHubRelease>(json);
            }
            catch (Exception ex)
            {
                throw new ToolDownloadException("GitHub's release information could not be read.", ex);
            }
            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            {
                throw new ToolDownloadException("No release was found for " + Repo + ".");
            }

            // Prefer an attached asset; Microsoft currently commits the exe to the repo instead,
            // so fall back to the file at the release tag.
            GitHubAsset asset = (release.Assets ?? new GitHubAsset[0])
                .FirstOrDefault(a => string.Equals(a.Name, ToolFileName, StringComparison.OrdinalIgnoreCase));
            string url = asset != null
                ? asset.DownloadUrl
                : "https://raw.githubusercontent.com/" + Repo + "/" + Uri.EscapeDataString(release.TagName) + "/" + ToolFileName;

            return new ToolRelease
            {
                Tag = release.TagName,
                Version = ParseVersion(release.TagName),
                DownloadUrl = url,
                HtmlUrl = release.HtmlUrl ?? ReleasesUrl,
            };
        }

        /// <summary>
        /// Downloads the tool to <paramref name="destination"/>. Progress reports (bytes, total or -1).
        /// The file is written to a temp name and validated before replacing an existing copy.
        /// </summary>
        public static async Task DownloadAsync(ToolRelease release, string destination, IProgress<Tuple<long, long>> progress,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temp = destination + ".download";
            try
            {
                HttpResponseMessage response;
                try
                {
                    response = await Http.Value.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    throw new ToolDownloadException("Could not reach GitHub to download the tool. Check the network connection.", ex);
                }

                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        throw new ToolDownloadException("Release " + release.Tag + " does not contain " + ToolFileName + ".");
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new ToolDownloadException("Download failed: " + (int)response.StatusCode + " " + response.ReasonPhrase + ".");
                    }
                    long total = response.Content.Headers.ContentLength ?? -1;
                    using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        long done = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                            done += read;
                            progress?.Report(Tuple.Create(done, total));
                        }
                    }
                }

                ValidateExecutable(temp);
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(temp, destination);
                Logger.Info("Downloaded " + ToolFileName + " " + release.Tag + " to " + destination);
            }
            finally
            {
                try
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch
                {
                    // Ignore.
                }
            }
        }

        internal static void ValidateExecutable(string path)
        {
            var info = new FileInfo(path);
            bool ok = info.Exists && info.Length > 10 * 1024;
            if (ok)
            {
                using (var fs = info.OpenRead())
                {
                    ok = fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
                }
            }
            if (!ok)
            {
                throw new ToolDownloadException("The downloaded file is not a valid executable. The download may have been " +
                                                "blocked by a proxy.");
            }
        }

        private static string RateLimitReset(HttpResponseMessage response)
        {
            System.Collections.Generic.IEnumerable<string> values;
            long epoch;
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out values)
                && long.TryParse(values.FirstOrDefault(), out epoch))
            {
                DateTime reset = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(epoch).ToLocalTime();
                return " (after " + reset.ToString("HH:mm") + ")";
            }
            return "";
        }

        [DataContract]
        internal sealed class GitHubRelease
        {
            [DataMember(Name = "tag_name")] public string TagName { get; set; }
            [DataMember(Name = "html_url")] public string HtmlUrl { get; set; }
            [DataMember(Name = "assets")] public GitHubAsset[] Assets { get; set; }
        }

        [DataContract]
        internal sealed class GitHubAsset
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "browser_download_url")] public string DownloadUrl { get; set; }
        }
    }
}
