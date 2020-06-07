using System;
using System.Collections.Generic;
using System.ComponentModel;
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
        private const string MutexNamePrefix = "Flackhole-Mutex-";

        private const int WorkerDestory = 4;
        private const int WorkerDownloader = 8;

        private const int LookupIdCount = 100; // Lookup 할 때 한번에 보낼 id 수
        private const int LookupMaxTries = 3;
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
                regInvaild.Replace($"{this.Like.TweetId} {this.Like.FullText.Substring(0, Math.Min(this.Like.FullText.Length, 50))}", "_").Trim().Trim('.', ' ').Trim();
        }
        public struct FavObject2
        {
            public FavObject FavObject { get; set; }
            public StatusObject Status { get; set; }
        }

        private string m_baseDirectory;
        private string m_failDirectory;

        private TwitterClient m_twitterClient;

        private readonly Stack<string> m_todoDestory = new Stack<string>(); // Fav.StatusId, 뒤에서부터 작업
        private readonly Stack<FavObject2> m_todoDownload = new Stack<FavObject2>(); // 다운로드할 거
        private readonly Stack<FavObject> m_todoLookup = new Stack<FavObject>();

        private int m_favCount;
        private DateTime m_startTime;

        private long m_workerDestory; // interlocked, 스레드 돌리기 전에 설정
        private long m_workerDownloader; // interlocked
        private long m_workerLookup;

        private Mutex m_mutex;

        public MainWindow()
        {
            this.InitializeComponent();

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 100;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            if (Interlocked.Read(ref this.m_workerDestory) != 0)
            {
                var r = MessageBox.Show(this, "정말 종료하시겠어요?\n\n다음에 실행할 때는 처음부터 다시 진행해야해요.", "Flackhole (관심글 청소기)", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

                if (r != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            this.m_mutex?.Dispose();

            Application.Current.Shutdown();
        }

        private static readonly Regex regTCo = new Regex(@"https?://t\.co/([^ ]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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

            this.m_mutex = new Mutex(true, MutexNamePrefix + form.TwitterClient.Id, out var muxteIsCreatedNew);
            if (!muxteIsCreatedNew)
            {
                MessageBox.Show(this, "이 계정은 이미 작업중입니다!\n\n로그인 한 아이디 : " + form.TwitterClient.ScreenName, "Flackhole (관심글 청소기)", MessageBoxButton.OK, MessageBoxImage.Error);
                this.Close();
                return;
            }
                
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

            var lst = new List<FavObject>();
            var r = await Task.Factory.StartNew(() =>
            {
                try
                {
                    using (var fs = File.OpenRead(dfd.FileName))
                    using (var rd = new StreamReader(fs))
                    {
                        while (rd.Read() != '=') ;

                        App.JsonSerializer.Populate(rd, lst);
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

            this.m_favCount = lst.Count;

            //////////////////////////////////////////////////

            var msgResult = MessageBox.Show(
                this,
                "삭제하기 전에 사진과 동영상을 저장할까요?",
                "Flackhole (관심글 청소기)",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);

            if (msgResult != MessageBoxResult.Yes && msgResult != MessageBoxResult.No)
            {
                this.Close();
                return;
            }

            var withDownload = msgResult == MessageBoxResult.Yes;

            if (!withDownload)
            {
                this.ctlSaveCapacity.TextDecorations = TextDecorations.Strikethrough;
                this.ctlSaveSucc.TextDecorations = TextDecorations.Strikethrough;
                this.ctlSaveFail.TextDecorations = TextDecorations.Strikethrough;
            }

            if (withDownload)
            {
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
            }

            //////////////////////////////////////////////////

            this.taskBarItemInfo.ProgressState = TaskbarItemProgressState.Normal;

            this.m_startTime = DateTime.Now;

            this.ctlProgress.Value = 0;
            this.ctlProgress.Maximum = lst.Count;
            this.UpdateDefaultStyle();

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

            await Task.Factory.StartNew(() =>
            {
                lst.Sort((a, b) => a.Like.TweetId.CompareTo(b.Like.TweetId));

                if (withDownload)
                {
                    foreach (var fav in lst)
                    {
                        if (!string.IsNullOrWhiteSpace(fav.Like.FullText) && regTCo.IsMatch(fav.Like.FullText))
                            this.m_todoLookup.Push(fav);
                        else
                            this.m_todoDestory.Push(fav.Like.TweetId);
                    }
                }
                else
                {
                    foreach (var fav in lst)
                        this.m_todoDestory.Push(fav.Like.TweetId);
                }
            });

            this.ctlStatus.Text = "작동중";

            this.m_workerDestory = WorkerDestory;

            if (withDownload)
            {
                this.m_workerDownloader = WorkerDownloader;
                this.m_workerLookup = 1;
            }

            for (var i = 0; i < WorkerDestory; i++)
            {
                new Thread(this.ThreadDestroyer)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Lowest,
                }.Start();
            }

            if (withDownload)
            {
                for (var i = 0; i < WorkerDownloader; i++)
                {
                    new Thread(this.ThreadDownloader)
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.Lowest,
                    }.Start();
                }

                new Thread(this.ThreadLookup)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Lowest,
                }.Start();
            }
        }

        private void ThreadDestroyer()
        {
            string statusId;

            while (true)
            {
                lock (this.m_todoDestory)
                {
                    if (this.m_todoDestory.Count == 0)
                        break;

                    while (this.m_todoDestory.Count == 0)
                    {
                        if (Interlocked.Read(ref this.m_workerDownloader) == 0)
                        {
                            if (Interlocked.Decrement(ref this.m_workerDestory) == 0)
                            {
                                this.Dispatcher.Invoke(
                                    () =>
                                    {
                                        this.taskBarItemInfo.ProgressState = TaskbarItemProgressState.None;

                                        MessageBox.Show(this, "작업이 완료되었습니다.", "Flackhole (관심글 청소기)", MessageBoxButton.OK, MessageBoxImage.Information);
                                        this.Close();
                                    });
                            }

                            return;
                        }

                        Monitor.Wait(this.m_todoDownload);
                    }

                    statusId = this.m_todoDestory.Pop();
                }

                this.Remove(statusId);
            }
        }
        private void ThreadDownloader()
        {
            FavObject2 fav2;

            while (true)
            {
                lock (this.m_todoDownload)
                {
                    while (this.m_todoDownload.Count == 0)
                    {
                        if (Interlocked.Read(ref this.m_workerLookup) == 0)
                        {
                            Interlocked.Decrement(ref this.m_workerDownloader);

                            lock (this.m_todoDestory)
                            {
                                Monitor.PulseAll(this.m_todoDestory);
                            }
                            return;
                        }

                        Monitor.Wait(this.m_todoDownload);
                    }

                    fav2 = this.m_todoDownload.Pop();
                }

                this.Download(fav2);

                lock (this.m_todoDestory)
                {
                    this.m_todoDestory.Push(fav2.FavObject.Like.TweetId);

                    Monitor.Pulse(this.m_todoDestory);
                }
            }

        }
        private void ThreadLookup()
        {
            var idTry = new Dictionary<string, int>();
            var id = new Dictionary<string, FavObject>();

            var lst = new List<StatusObject>(LookupIdCount);

            while (true)
            {
                while (idTry.Count < LookupIdCount & this.m_todoLookup.Count > 0)
                {
                    var fav = this.m_todoLookup.Pop();
                    idTry[fav.Like.TweetId] = 0;
                    id[fav.Like.TweetId] = fav;
                }

                if (idTry.Count == 0)
                {
                    Interlocked.Exchange(ref this.m_workerLookup, 0);

                    lock (this.m_todoDownload)
                    {
                        Monitor.PulseAll(this.m_todoDownload);
                    }

                    return;
                }

                Lookup();

                lock (this.m_todoDownload)
                {
                    foreach (var k in idTry.Keys.ToArray())
                    {
                        if (++idTry[k] >= LookupMaxTries)
                        {
                            this.m_todoDownload.Push( new FavObject2 { FavObject = id[k], });

                            idTry.Remove(k);
                            id.Remove(k);
                        }
                    }

                    if (this.m_todoDownload.Count > 0)
                        Monitor.PulseAll(this.m_todoDownload);
                }
            }

            void Lookup()
            {
                var req = this.m_twitterClient.CreateReqeust("POST", $"https://api.twitter.com/1.1/statuses/lookup.json");

                try
                {
                    using (var sw = new StreamWriter(req.GetRequestStream()))
                    {
                        sw.Write("tweet_mode=extended&id=");

                        var first = true;
                        foreach (var k in idTry.Keys)
                        {
                            if (first)
                                first = false;
                            else
                            {
                                sw.Write(',');
                                first = false;
                            }
                            sw.Write(k);
                        }

                        sw.Flush();
                    }
                }
                catch
                {
                    Wait(DateTime.UtcNow + TimeSpan.FromSeconds(30));
                    return;
                }

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
                    return;
                }

                using (res)
                {
                    if (res.StatusCode == HttpStatusCode.OK)
                    {
                        using (var stream = res.GetResponseStream())
                        using (var streamReader = new StreamReader(stream, Encoding.UTF8))
                        {
                            lst.Clear();
                            App.JsonSerializer.Populate(streamReader, lst);

                            lock (this.m_todoDownload)
                            {
                                foreach (var status in lst)
                                {
                                    this.m_todoDownload.Push(
                                        new FavObject2
                                        {
                                            FavObject = id[status.IdStr],
                                            Status = status,
                                        });

                                    idTry.Remove(status.IdStr);
                                    id.Remove(status.IdStr);
                                }

                                Monitor.PulseAll(this.m_todoDownload);
                            }
                        }
                    }

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
                        if (int.TryParse(res.Headers.Get("x-rate-limit-reset"), out int reset))
                            Wait(ForTimeStamp.AddSeconds(reset));
                        else
                            Wait(DateTime.Now + TimeSpan.FromMinutes(5));
                    }
                }
            }

            void Wait(DateTime dstTime)
            {
                if (dstTime <= DateTime.UtcNow)
                    return;

                this.Dispatcher.Invoke(() => this.taskBarItemInfo.ProgressState = TaskbarItemProgressState.Paused);

                try
                {
                    using (var m = new ManualResetEventSlim(true))
                    {
                        var task = Task.Factory.StartNew(() =>
                        {
                            while (m.IsSet)
                            {
                                var dt = dstTime - DateTime.UtcNow;

                                this.Dispatcher.Invoke(() => this.ctlStatus.Text = string.Format("트윗 조회 대기중 {0:00}:{1:00}", dt.Minutes, dt.Seconds));
                            }
                        });

                        Thread.Sleep(Math.Max((dstTime - DateTime.UtcNow).Milliseconds, 100));
                        m.Reset();
                        task.Wait();
                    }
                }
                catch
                {
                }

                this.Dispatcher.Invoke(() =>
                {
                    this.taskBarItemInfo.ProgressState = TaskbarItemProgressState.Normal;
                    this.ctlStatus.Text = "작동중";
                });
            }
        }

        private static readonly DateTime ForTimeStamp = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private void Download(FavObject2 fav)
        {
            if (fav.Status.ExtendedEntities.Media?.Length > 0)
            {
                var dir = Path.Combine(this.m_baseDirectory, fav.Status.User.ScreenName, fav.FavObject.FileName);
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch
                {
                }

                var f = Parallel.For(
                    0,
                    fav.Status.ExtendedEntities.Media.Length,
                    i =>
                    {
                        var downloaded = false;

                        var pathFile = Path.Combine(dir, $"{fav.FavObject.Like.TweetId} {(i + 1)}");

                        string rawUri = null;
                        Uri uri = null;
                        if (fav.Status.ExtendedEntities.Media[i].VideoInfo.Variants?.Length > 0)
                        {
                            try
                            {
                                if (fav.Status.ExtendedEntities.Media[i].VideoInfo.Variants.Length == 1)
                                {
                                    rawUri = fav.Status.ExtendedEntities.Media[i].VideoInfo.Variants[0].Url;
                                    uri = new Uri(rawUri);
                                }
                                else
                                {
                                    rawUri = fav.Status.ExtendedEntities.Media[i].VideoInfo.Variants?.Where(e => e.Bitrate != 0).Aggregate((a, b) => a.Bitrate > b.Bitrate ? a : b).Url;
                                    uri = new Uri(rawUri);
                                }
                            }
                            catch
                            {
                            }
                        }
                        if (uri == null)
                        {
                            try
                            {
                                rawUri = fav.Status.ExtendedEntities.Media[i].MediaUrlHttps;
                                uri = new Uri(rawUri + ":orig");
                            }
                            catch
                            {
                            }
                        }

                        long downloadedSize = 0;

                        if (uri != null)
                        {
                            for (int k = 0; k < LookupMaxTries && !downloaded; k++)
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
                                            pathFile = Path.ChangeExtension(pathFile, Path.GetExtension(rawUri));
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
                                File.WriteAllText(pathFile + ".url", $"[InternetShortcut]\r\nURL={(uri?.ToString() ?? fav.FavObject.Like.ExpandedUrl)}");
                            }
                            catch
                            {
                            }
                        }

                        try
                        {
                            File.SetCreationTime(pathFile, fav.Status.CreatedAt);
                            File.SetLastWriteTime(pathFile, fav.Status.CreatedAt);
                        }
                        catch
                        {
                        }
                    });

                try
                {
                    Directory.SetCreationTime(dir, fav.Status.CreatedAt);
                    Directory.SetLastWriteTime(dir, fav.Status.CreatedAt);
                }
                catch
                {
                }

                this.UpdateDownloadStat(true);
            }
            else
            {
                try
                {
                    Directory.CreateDirectory(this.m_failDirectory);

                    File.WriteAllText(
                        Path.Combine(this.m_failDirectory, fav.FavObject.FileName + ".url"),
                        $"[InternetShortcut]\r\nURL={fav.FavObject.Like.ExpandedUrl}");
                }
                catch
                {
                }

                this.UpdateDownloadStat(false);
            }
        }

        private void Remove(string id)
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

                    this.UpdateDestoryStat(true);
                }
            }
            catch
            {
                this.UpdateDestoryStat(false);
            }
        }

        private readonly object m_updateProgressStat = new object();
        private int m_favDestroyed;
        private int m_favDestoryedSuccess = 0;
        private void UpdateDestoryStat(bool? success)
        {
            lock (this.m_updateProgressStat)
            {
                var p = (double)this.m_favDestroyed / this.m_favCount;

                if (success.HasValue)
                {
                    this.m_favDestroyed++;

                    if (success ?? false)
                        this.m_favDestoryedSuccess++;
                }

                this.Dispatcher.Invoke(() =>
                {
                    this.ctlProgress.Value = this.m_favDestroyed;
                    this.ctlProgressVal.Text = string.Format(
                        "[{0:##0.0} %] {1:#,##0} / {2:#,##0}",
                        p * 100,
                        this.m_favDestroyed,
                        this.m_favCount
                    );

                    this.ctlDetailSucc.Text = string.Format(
                        "삭제 성공 : {0:#,##0} / {1:#,##0} ({2:##0.0} %)",
                        this.m_favDestoryedSuccess,
                        this.m_favCount,
                        (float)this.m_favDestoryedSuccess / this.m_favCount * 100);

                    this.ctlDetailFail.Text = string.Format(
                        "삭제 실패 : {0:#,##0} / {1:#,##0} ({2:##0.0} %)",
                        this.m_favDestroyed - this.m_favDestoryedSuccess,
                        this.m_favCount,
                        (float)(this.m_favDestroyed - this.m_favDestoryedSuccess) / this.m_favCount * 100);

                    this.taskBarItemInfo.ProgressValue = p;
                });
            }
        }

        private readonly object m_updateDownloadStat = new object();
        private int m_savedCount = 0;
        private int m_savedCountSuccess = 0;
        private void UpdateDownloadStat(bool success)
        {
            lock (this.m_updateDownloadStat)
            {
                this.m_savedCount++;
                if (success)
                    this.m_savedCountSuccess++;

                this.Dispatcher.Invoke(() =>
                {
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
