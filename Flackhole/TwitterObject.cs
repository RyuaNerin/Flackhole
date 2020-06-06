using System;
using Newtonsoft.Json;

namespace Flackhole
{
    internal struct UserObject
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("screen_name")]
        public string ScreenName { get; set; }
    }

    internal struct StatusObject
    {
        [JsonProperty("id_str")]
        public string IdStr { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("user")]
        public UserObject User { get; set; }

        public struct UserObject
        {
            [JsonProperty("screen_name")]
            public string ScreenName { get; set; }
        }

        [JsonProperty("extended_entities")]
        public EntitiesObject ExtendedEntities { get; set; }

        public struct EntitiesObject
        {
            [JsonProperty("media")]
            public MediaObject[] Media { get; set; }

            public struct MediaObject
            {
                [JsonProperty("media_url_https")]
                public string MediaUrlHttps { get; set; }

                [JsonProperty("video_info")]
                public VideoInfoObject VideoInfo { get; set; }

                public struct VideoInfoObject
                {
                    [JsonProperty("variants")]
                    public VariantsObject[] Variants { get; set; }

                    public struct VariantsObject
                    {
                        [JsonProperty("bitrate")]
                        public int Bitrate { get; set; }

                        [JsonProperty("content_type")]
                        public string ContentType { get; set; }

                        [JsonProperty("url")]
                        public string Url { get; set; }
                    }
                }
            }
        }
    }
}
