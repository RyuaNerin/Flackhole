using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Shell;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace Flackhole
{
    internal partial class MainWindow : Window
    {
        private const int Workers = 8;
        private const int MaxTries = 3;
        private const double ApiRateRemain = 0.5; // API 제한의 몇 % 를 남길 것인가

        public struct FavObject
        {
            [JsonProperty("like")]
            public LikeObject Like { get; set; }

            public struct LikeObject
            {
                [JsonProperty("tweetId")]
                public string TweetId { get; set; }

                [JsonProperty("fullText")]
                public string FullText { get; set; }

                [JsonProperty("expandedUrl")]
                public string ExpandedUrl { get; set; }
            }

            private static readonly Regex regInvaild =
                new Regex(
                    "[" +
                    Regex.Escape(
                        new string(Path.GetInvalidFileNameChars()) +
                        new string(Path.GetInvalidPathChars()) +
                        Path.AltDirectorySeparatorChar.ToString() + 
                        Path.DirectorySeparatorChar.ToString() +
                        Path.VolumeSeparatorChar.ToString() +
                        Path.PathSeparator.ToString()
                    ) +
                    "]",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled);

            public string FileName => 
                regInvaild.Replace($"{this.Like.TweetId} {this.Like.FullText.Substring(0, Math.Min(this.Like.FullText.Length, 50))}", "").Trim().Trim('.', ' ').Trim();
        }

        private readonly AutoResetEvent m_limitLock = new AutoResetEvent(true);
        private readonly ManualResetEventSlim m_limit = new ManualResetEventSlim(true);

        private string m_baseDirectory;
        private string m_failDirectory;

        private TwitterClient m_twitterClient;

        private readonly List<FavObject> m_favList = new List<FavObject>();
        private int m_favCount;
        private DateTime m_startTime;

        private int m_currentWorkers = Workers; // interlocked

        public MainWindow()
        {
            this.InitializeComponent();

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 100;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            Application.Current.Shutdown();
        }

        private bool m_shown;
        protected override async void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            if (this.m_shown)
                return;

            this.m_shown = true;

            this.taskBarItemInfo.ProgressState = TaskbarItemProgressState.Paused;

            //////////////////////////////////////////////////

            var obj = await Task.Factory.StartNew(LastRelease.CheckNewVersion);
            if (obj != null)
            {
                MessageBox.Show(this, "새 버전이 출시되었습니다.\n\n정상적인 작동을 위해 업데이트해주세요!", "Flackhole (관심글 청소기)", MessageBoxButton.OK, MessageBoxImage.Information);

                try
                {
                    Process.Start(new ProcessStartInfo { FileName = obj.HtmlUrl, UseShellExecute = true }).Dispose();
                }
                catch
                {
                }
                this.Close();
                return;
            }

            var form = new Login
            {
                Owner = this
            };
            if (!form.ShowDialog() ?? false)
            {
                this.Close();
                return;
            }

            this.m_twitterClient = form.TwitterClient;

            //////////////////////////////////////////////////

            var dfd = new OpenFileDialog()
            {
                CheckFileExists = true,
                Filter = "like.js|like.js",
            };
            if (!dfd.ShowDialog() ?? false)
            {
                this.Close();
                return;
            }

            var r = await Task.Factory.StartNew(() =>
            {
                try
                {
                    using (var fs = File.OpenRead(dfd.FileName))
                    using (var rd = new StreamReader(fs))
                    {
                        while (rd.Read() != '=') ;

                        App.JsonSerializer.Populate(rd, this.m_favList);
                    }

                    return true;
                }
                catch
                {
                    return false;
                }
            });

            if (!r)
            {
                MessageBox.Show(this, "파일을 읽지 못하였습니다.", "Flackhole (관심글 청소기)", MessageBoxButton.OK, MessageBoxImage.Error);
                this.Close();
                return;
            }

            this.m_favCount = this.m_favList.Count;

            //////////////////////////////////////////////////

            var sfd = new System.Windows.Forms.FolderBrowserDialog();
            if (sfd.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            this.m_baseDirectory = Path.Combine(@"\\?\" + sfd.SelectedPath, $"Flackhole {this.m_twitterClient.ScreenName}");

            int i = 2;
            while (Directory.Exists(this.m_baseDirectory))
                this.m_baseDirectory = Path.Combine(@"\\?\" + sfd.SelectedPath, $"Flackhole {this.m_twitterClient.ScreenName} ({i++})");

            this.m_failDirectory = Path.Combine(this.m_baseDirectory, "[작업오류]");

            try
            {
                Directory.CreateDirectory(this.m_baseDirectory);
            }
            catch (Exception)
            {
                MessageBox.Show(this, "저장할 폴더를 생성하지 못하였습니다.", "Flackhole (관심글 청소기)", MessageBoxButton.OK, MessageBoxImage.Error);
                this.Close();
                return;
            }

            //////////////////////////////////////////////////

            this.taskBarItemInfo.ProgressState = TaskbarItemProgressState.Normal;

            this.m_startTime = DateTime.Now;

            this.ctlProgress.Value = 0;
            this.ctlProgress.Maximum = this.m_favList.Count;
            this.UpdateProgress(null, null);

            _ = Task.Factory.StartNew(() =>
              {
                  while (true)
                  {
                      Thread.Sleep(TimeSpan.FromSeconds(1));

                      var dt = DateTime.Now - this.m_startTime;
                      var str = string.Format("작업 시간 : {0:0}시간 {1:0}분 {2:0}초", dt.Hours, dt.Minutes, dt.Seconds);

                      this.Dispatcher.Invoke(() => this.ctlTime.Text = str);
                  }
              });

            await Task.Factory.StartNew(() => this.m_favList.Sort((a, b) => a.Like.TweetId.CompareTo(b.Like.TweetId)));

            this.ctlStatus.Text = "작동중";
            for (i = 0; i < Workers; i++)
            {
                new Thread(this.Runner)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Lowest,
                }.Start();
                //_ = Task.Run(this.Runner);
            }
        }

        private readonly object m_updateLock = new object();
        private long m_destroySuccess = 0;
        private int m_savedCount = 0;
        private int m_savedCountSuccess = 0;
        private void UpdateProgress(bool? noErrorDestroy, bool? noErrorSave)
        {
            lock (this.m_updateLock)
            {
                var count = this.m_favCount;
                lock (this.m_favList)
                    count -= this.m_favList.Count;

                var p = (double)count / this.m_favCount;

                if (noErrorDestroy.HasValue)
                {
                    if (noErrorDestroy ?? false)
                        this.m_destroySuccess++;
                }

                if (noErrorSave.HasValue)
                {
                    this.m_savedCount++;
                    if (noErrorSave ?? false)
                        this.m_savedCountSuccess++;

                }

                this.Dispatcher.Invoke(() =>
                {
                    this.ctlProgress.Value = count;
                    this.ctlProgressVal.Text = string.Format(
                        "[{0:##0.0} %] {1:#,##0} / {2:#,##0}",
                        p * 100,
                        count,
                        this.m_favCount
                    );

                    this.ctlDetailSucc.Text = string.Format(
                        "삭제 성공 : {0:#,##0} / {1:#,##0} ({2:##0.0} %)",
                        this.m_destroySuccess,
                        this.m_favCount,
                        (float)this.m_destroySuccess / this.m_favCount * 100);

                    this.ctlDetailFail.Text = string.Format(
                        "삭제 실패 : {0:#,##0} / {1:#,##0} ({2:##0.0} %)",
                        count - this.m_destroySuccess,
                        this.m_favCount,
                        (float)(count - this.m_destroySuccess) / this.m_favCount * 100);

                    this.ctlSaveSucc.Text = string.Format(
                        "저장 성공 : {0:#,##0} / {1:#,##0} ({2:##0.0} %)",
                        this.m_savedCountSuccess,
                        this.m_savedCount,
                        (float)this.m_savedCountSuccess / this.m_savedCount * 100);

                    this.ctlSaveFail.Text = string.Format(
                        "저장 실패 : {0:#,##0} / {1:#,##0} ({2:##0.0} %)",
                        this.m_savedCount - this.m_savedCountSuccess,
                        this.m_savedCount,
                        (float)(this.m_savedCount - this.m_savedCountSuccess) / this.m_savedCount * 100);

                    this.taskBarItemInfo.ProgressValue = p;
                });
            }
        }

        private readonly static string[] IECStr = { "B", "KiB", "MiB", "GiB", "TiB" };
        private readonly static object UpdateFilesLock = new object();
        private long m_savedCapacity = 0;
        private int m_savedFiles = 0;
        private void UpdateSavedCapacity(int size)
        {
            lock (UpdateFilesLock)
            {
                this.m_savedCapacity += size;

                var bytes = (float)this.m_savedCapacity;
                var files = ++this.m_savedFiles;

                var i = 0;
                while (bytes > 1000 && i++ < 5)
                    bytes /= 1024;

                this.Dispatcher.Invoke(() => this.ctlSaveCapacity.Text = string.Format("저장 용량 : {0:##0.0} {1} ({2:#,##0} 파일)", bytes, IECStr[i], files));
            }
        }

        private static readonly Regex regTCo = new Regex(@"https?://t\.co/([^ ]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private void Runner()
        {
            bool? downloadSucceed;
            while (true)
            {
                FavObject fav;

                lock (this.m_favList)
                {
                    if (this.m_favList.Count == 0)
                        break;

                    fav = this.m_favList[this.m_favList.Count - 1];
                    this.m_favList.RemoveAt(this.m_favList.Count - 1);
                }

                downloadSucceed = null;
                if (!string.IsNullOrWhiteSpace(fav.Like.FullText) && regTCo.IsMatch(fav.Like.FullText))
                {
                    downloadSucceed = this.Download(fav);
                }

                this.Remove(fav.Like.TweetId, downloadSucceed);
            }

            this.WorkerExit();
        }

        private static readonly DateTime ForTimeStamp = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private bool Download(FavObject fav)
        {
            StatusObject status = null;
            bool succ = false;

            for (int i = 0; i < MaxTries && !succ; i++)
            {
                this.m_limit.Wait();

                var req = this.m_twitterClient.CreateReqeust("GET", $"https://api.twitter.com/1.1/statuses/show.json?tweet_mode=extended&id={fav.Like.TweetId}");

                HttpWebResponse res = null;
                try
                {
                    res = req.GetResponse() as HttpWebResponse;
                }
                catch (WebException ex)
                {
                    res = ex.Response as HttpWebResponse;
                }
                catch
                {
                }

                if (res == null)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(10));
                    continue;
                }
                else
                {
                    using (res)
                    {
                        switch (res.StatusCode)
                        {
                            case HttpStatusCode.OK:
                                succ = true;

                                using (var stream = res.GetResponseStream())
                                using (var streamReader = new StreamReader(stream, Encoding.UTF8))
                                {
                                    status = (StatusObject)App.JsonSerializer.Deserialize(streamReader, typeof(StatusObject));
                                }
                                break;

                            case HttpStatusCode.Unauthorized:
                            case HttpStatusCode.Forbidden:
                            case HttpStatusCode.NotFound:
                                succ = true;
                                break;
                        }

                        if (status != null)
                            break;

                        var waitForLimit = res.StatusCode == (HttpStatusCode)429;
                        if (!waitForLimit)
                        {
                            if (int.TryParse(res.Headers.Get("x-rate-limit-limit"), out int limit) && 
                                int.TryParse(res.Headers.Get("x-rate-limit-remaining"), out int remaining))
                            {
                                waitForLimit = remaining < (int)(limit * ApiRateRemain);
                            }
                        }

                        if (waitForLimit)
                        {
                            if (this.m_limitLock.WaitOne(0))
                            {
                                this.m_limit.Reset();

                                this.Dispatcher.Invoke(() => this.taskBarItemInfo.ProgressState = TaskbarItemProgressState.Paused);

                                try
                                {
                                    DateTime dstTime;
                                    if (int.TryParse(res.Headers.Get("x-rate-limit-reset"), out int reset))
                                        dstTime = ForTimeStamp.AddSeconds(reset);
                                    else
                                        dstTime = DateTime.Now + TimeSpan.FromMinutes(5);

                                    if (dstTime > DateTime.UtcNow)
                                    {
                                        using (var m = new ManualResetEventSlim(true))
                                        {
                                            var task = Task.Factory.StartNew(() =>
                                            {
                                                while (m.IsSet)
                                                {
                                                    var dt = dstTime - DateTime.UtcNow;

                                                    this.Dispatcher.Invoke(() => this.ctlStatus.Text = string.Format("다운로드 대기중 {0:00}:{1:00}", dt.Minutes, dt.Seconds));
                                                }
                                            });

                                            Thread.Sleep(dstTime - DateTime.UtcNow);
                                            m.Reset();
                                            task.Wait();
                                        }
                                    }

                                    this.Dispatcher.Invoke(() => {
                                        this.taskBarItemInfo.ProgressState = TaskbarItemProgressState.Normal;
                                        this.ctlStatus.Text = "작동중";
                                    });
                                }
                                catch
                                {
                                }

                                this.m_limit.Set();
                            }
                            else
                            {
                                this.m_limitLock.WaitOne();
                            }

                            this.m_limitLock.Set();
                        }
                    }
                }
            }

            if (succ && status?.ExtendedEntities.Media?.Length > 0)
            {
                var dir = Path.Combine(this.m_baseDirectory, status.User.ScreenName, fav.FileName);
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch
                {
                }

                var f = Parallel.For(
                    0,
                    status.ExtendedEntities.Media.Length,
                    i =>
                    {
                        var downloaded = false;

                        var pathFile = Path.Combine(dir, $"{fav.Like.TweetId} {(i + 1)}");

                        string uri = null;
                        if (status.ExtendedEntities.Media[i].VideoInfo.Variants?.Length > 0)
                        {
                            if (status.ExtendedEntities.Media[i].VideoInfo.Variants.Length == 0)
                            {
                                uri = status.ExtendedEntities.Media[i].VideoInfo.Variants[0].Url;
                            }
                            else
                            {
                                try
                                {
                                    uri = status.ExtendedEntities.Media[i].VideoInfo.Variants?.Where(e => e.Bitrate != 0).Aggregate((a, b) => a.Bitrate > b.Bitrate ? a : b).Url;
                                }
                                catch
                                {
                                }
                            }
                        }
                        if (string.IsNullOrWhiteSpace(uri))
                            uri = status.ExtendedEntities.Media[i].MediaUrlHttps + ":orig";

                        long downloadedSize = 0;
                        for (int k = 0; k < MaxTries && !downloaded; k++)
                        {
                            try
                            {
                                var req = WebRequest.CreateHttp(uri);
                                req.Method = "GET";
                                req.UserAgent = "Flackhole";
                                req.Timeout = 5000;

                                var res = req.GetResponse() as HttpWebResponse;
                                using (res)
                                {
                                    downloadedSize = res.ContentLength;

                                    using (var stream = res.GetResponseStream())
                                    {
                                        pathFile = Path.ChangeExtension(pathFile, Path.GetExtension(status.ExtendedEntities.Media[i].MediaUrlHttps));
                                        using (var fs = File.Create(pathFile))
                                        {
                                            stream.CopyTo(fs);
                                            fs.Flush();

                                            downloaded = true;
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                try
                                {
                                    File.Delete(pathFile);
                                }
                                catch (Exception)
                                {
                                }
                            }
                        }

                        if (downloaded)
                        {
                            this.UpdateSavedCapacity((int)downloadedSize);
                        }
                        else
                        {
                            pathFile = Path.ChangeExtension(pathFile, ".url");
                            try
                            {
                                File.WriteAllText(pathFile + ".url", $"[InternetShortcut]\r\nURL={(!string.IsNullOrWhiteSpace(uri) ? uri : fav.Like.ExpandedUrl)}");
                            }
                            catch
                            {
                            }
                        }

                        try
                        {
                            File.SetCreationTime(pathFile, status.CreatedAt);
                            File.SetLastWriteTime(pathFile, status.CreatedAt);
                        }
                        catch
                        {
                        }
                    });

                try
                {
                    Directory.SetCreationTime(dir, status.CreatedAt);
                    Directory.SetLastWriteTime(dir, status.CreatedAt);
                }
                catch
                {
                }
            }
            else
            {
                try
                {
                    Directory.CreateDirectory(this.m_failDirectory);

                    File.WriteAllText(
                        Path.Combine(this.m_failDirectory, fav.FileName + ".url"),
                        $"[InternetShortcut]\r\nURL={fav.Like.ExpandedUrl}");
                }
                catch
                {
                }
            }

            return succ;
        }

        private void Remove(string id, bool? downloadSucceed)
        {
            var req = this.m_twitterClient.CreateReqeust("POST", "https://api.twitter.com/1.1/favorites/destroy.json");
            req.ContentType = "application/x-www-form-urlencoded; charset=UTF-8";

            var buff = Encoding.UTF8.GetBytes($"tweet_mode=extended&id={id}");
            req.GetRequestStream().Write(buff, 0, buff.Length);

            try
            {
                using (var res = req.GetResponse())
                {
                    using (var stream = res.GetResponseStream())
                        stream.CopyTo(Stream.Null);

                    this.UpdateProgress(true, downloadSucceed);
                }
            }
            catch
            {
                this.UpdateProgress(false, downloadSucceed);
            }
        }

        private void WorkerExit()
        {
            if (Interlocked.Decrement(ref this.m_currentWorkers) == 0)
            {
                this.Dispatcher.Invoke(
                    () =>
                    {
                        this.taskBarItemInfo.ProgressState = TaskbarItemProgressState.None;

                        MessageBox.Show(this, "작업이 완료되었습니다.", "Flackhole (관심글 청소기)", MessageBoxButton.OK, MessageBoxImage.Information);
                        this.Close();
                    });
            }
        }

        private void ctlCopyRight_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "https://ryuar.in/", UseShellExecute = true }).Dispose();
            }
            catch
            {
            }
        }
    }
}
