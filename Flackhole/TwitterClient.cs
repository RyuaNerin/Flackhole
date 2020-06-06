using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Flackhole
{
    internal class TwitterClient
    {
        public static readonly Uri TwitterUri = new Uri("https://twitter.com/");

        public TwitterClient(string cookie)
        {
            this.Cookie = cookie;
        }

        public string Cookie { get; }

        public long Id { get; private set; }
        public string ScreenName { get; private set; }

        public HttpWebRequest CreateReqeust(string method, string uri)
            => this.CreateReqeust(method, new Uri(uri));

        private string m_xCsrfToken = null;
        public HttpWebRequest CreateReqeust(string method, Uri uri)
        {
            if (this.m_xCsrfToken == null)
            {
                try
                {
                    this.m_xCsrfToken = Regex.Match(this.Cookie, "ct0=([^;]+)", RegexOptions.IgnoreCase).Groups[1].Value;
                }
                catch
                {
                    this.m_xCsrfToken = null;
                }
            }

            var req = WebRequest.CreateHttp(uri);
            req.Method = method;
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/83.0.4103.61 Safari/537.36";

            if (method == "POST")
                req.ContentType = "application/x-www-form-urlencoded";

            req.Headers.Set("Cookie", this.Cookie);
            req.Headers.Set("X-Csrf-Token", this.m_xCsrfToken);
            req.Headers.Set("Authorization", "Bearer AAAAAAAAAAAAAAAAAAAAAF7aAAAAAAAASCiRjWvh7R5wxaKkFp7MM%2BhYBqM%3DbQ0JPmjU9F6ZoMhDfI4uTNAaQuTDm2uO9x3WFVr2xBZ2nhjdP0");
            req.Headers.Set("X-Twitter-Auth-Type", "OAuth2Session");
            req.Headers.Set("X-Twitter-Client-Version", "Twitter-TweetDeck-blackbird-chrome/4.0.190115122859 web/");

            return req;
        }

        public bool VerifyCredentials()
        {
            var req = this.CreateReqeust("GET", "https://api.twitter.com/1.1/account/verify_credentials.json");

            UserObject user;

            try
            {
                using (var res = req.GetResponse() as HttpWebResponse)
                {
                    if (res.StatusCode != HttpStatusCode.OK)
                        return false;

                    using (var stream = res.GetResponseStream())
                    using (var streamReader = new StreamReader(stream, Encoding.UTF8))
                    {
                        user = (UserObject)App.JsonSerializer.Deserialize(streamReader, typeof(UserObject));

                        this.ScreenName = user.ScreenName;
                        this.Id = user.Id;

                        return !string.IsNullOrWhiteSpace(user.ScreenName) && user.Id != 0; 
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
