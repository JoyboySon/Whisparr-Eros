using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using NLog;
using NzbDrone.Common.Cloud;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Analytics;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using Semver;

namespace NzbDrone.Core.Update
{
    public class GithubUpdatePackageProvider : IUpdatePackageProvider
    {
        private readonly IPlatformInfo _platformInfo;
        private readonly IAnalyticsService _analyticsService;
        private readonly IConfigFileProvider _configFileProvider;
        private readonly IMainDatabase _mainDatabase;
        private readonly IHttpClient _httpClient;
        private readonly IWhisparrCloudRequestBuilder _cloudRequestBuilder;
        private readonly Logger _logger;

        public GithubUpdatePackageProvider(
            IHttpClient httpClient,
            IAnalyticsService analyticsService,
            IPlatformInfo platformInfo,
            IMainDatabase mainDatabase,
            IConfigFileProvider configFileProvider,
            IWhisparrCloudRequestBuilder cloudRequestBuilder)
        {
            _platformInfo = platformInfo;
            _analyticsService = analyticsService;
            _configFileProvider = configFileProvider;
            _httpClient = httpClient;
            _mainDatabase = mainDatabase;
            _cloudRequestBuilder = cloudRequestBuilder;
            _logger = NzbDrone.Common.Instrumentation.NzbDroneLogger.GetLogger(this);
        }

        public UpdatePackage GetLatestUpdate(string branch, Version currentVersion)
        {
            _logger.Info($"Checking for latest update (branch: {branch}, currentVersion: {currentVersion})");
            var updates = GetRecentUpdates(branch, currentVersion);
            var latest = updates?.OrderByDescending(u => u.Version).FirstOrDefault();
            if (latest != null)
            {
                _logger.Info($"Latest update found: {latest.Version} ({latest.FileName})");
            }
            else
            {
                _logger.Warn("No update found from GitHub releases.");
            }

            return latest;
        }

        public List<UpdatePackage> GetRecentUpdates(string branch, Version currentVersion, Version previousVersion = null)
        {
            var ownerRepo = _configFileProvider.GithubOwnerRepo;
            _logger.Info($"Fetching recent updates from GitHub releases (branch: {branch}, currentVersion: {currentVersion}, previousVersion: {previousVersion})");
            var builder = _cloudRequestBuilder.GithubReleases.Create();
            builder.SetSegment("githubownerrepo", ownerRepo);
            builder.AddQueryParam("per_page", "5");

            var request = builder.Build();
            _logger.Debug($"Requesting: {request.Url}");

            var response = _httpClient.Get(request);
            _logger.Debug($"GitHub API response: {response.StatusCode}, {response.Content?.Length ?? 0} bytes");

            var releases = JsonSerializer.Deserialize<List<GithubRelease>>(response.Content);
            var os = OsInfo.Os.ToString().ToLowerInvariant();
            var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

            var packages = new List<UpdatePackage>();
            foreach (var release in releases)
            {
                // Filter release assetsby OS and architecture
                var asset = release.assets.FirstOrDefault(a =>
                    a.name.Contains(os, StringComparison.OrdinalIgnoreCase) &&
                    a.name.Contains(arch, StringComparison.OrdinalIgnoreCase));
                if (asset == null)
                {
                    _logger.Debug($"No asset found for release {release.tag_name} matching OS '{os}' and arch '{arch}'");
                    continue;
                }

                _logger.Trace($"Found update: {release.tag_name} - {asset.name}");
                var tag = release.tag_name.TrimStart('v');
                if (!SemVersion.TryParse(tag, SemVersionStyles.Any, out var version))
                {
                    _logger.Warn($"Could not parse semver from tag '{release.tag_name}' (parsed: '{tag}'). Skipping this release.");
                    continue;
                }

                packages.Add(new UpdatePackage
                {
                    Version = version,
                    ReleaseDate = release.published_at,
                    FileName = asset.name,
                    Url = asset.browser_download_url,
                    Changes = new UpdateChanges { New = new List<string> { release.body } },
                    Hash = asset.digest,
                    Branch = branch
                });
            }

            _logger.Debug($"Total updates found: {packages.Count}");
            return packages;
        }

        private class GithubRelease
        {
            public string tag_name { get; set; }
            public string body { get; set; }
            public DateTime published_at { get; set; }
            public List<GithubAsset> assets { get; set; }
        }

        private class GithubAsset
        {
            public string name { get; set; }
            public string digest { get; set; }
            public string browser_download_url { get; set; }
        }
    }
}
