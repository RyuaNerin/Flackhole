using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using Newtonsoft.Json;

namespace Flackhole
{
    internal class LatestRealease
    {
        [JsonProperty("tag_name")]
        public string TagName { get; set; }

        [JsonProperty("html_url")]
        public string HtmlUrl { get; set; }

        [JsonProperty("assets")]
        public Asset[] Assets { get; set; }

        [JsonObject]
        public class Asset
        {
            [JsonProperty("browser_download_url")]
            public string BrowserDownloadUrl { get; set; }
        }
    }

    internal static class LastRelease
    {
        public static LatestRealease CheckNewVersion()
        {
            var version = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().ManifestModule.FullyQualifiedName).ProductVersion;

            if (version.StartsWith("0"))
                return null;

            try
            {
                LatestRealease last;

                var req = WebRequest.CreateHttp("https://api.github.com/repos/RyuaNerin/Flackhole/releases/latest");
                req.Timeout = 5000;
                req.UserAgent = "Flackhole";
                using (var res = req.GetResponse())
                {
                    var json = new JsonSerializer();

                    using (var rStream = res.GetResponseStream())
                    using (var sReader = new StreamReader(rStream))
                    using (var jReader = new JsonTextReader(sReader))
                    {
                        last = json.Deserialize<LatestRealease>(jReader);
                    }
                }

                return new Version(last.TagName) > new Version(version) ? last : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
