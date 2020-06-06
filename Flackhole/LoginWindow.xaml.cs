using System;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Navigation;

namespace Flackhole
{
    internal partial class Login : Window
    {
        static Login()
        {
            NativeMethods.SetCookieSupressBehavior();
        }

        private SHDocVw.WebBrowser m_webBrowserSh;

        public Login()
        {
            this.InitializeComponent();
        }

        public TwitterClient TwitterClient { get; private set; }

        private bool m_shown;
        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            if (this.m_shown)
                return;

            this.m_shown = true;

            NativeMethods.SetCookieSupressBehavior();

            try
            {
                this.m_webBrowserSh = (SHDocVw.WebBrowser)this.ctlWebBrowser.GetType().InvokeMember("ActiveXInstance", BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.NonPublic, null, this.ctlWebBrowser, new object[] { });

                this.m_webBrowserSh.Resizable = false;
                this.m_webBrowserSh.Silent = true;
                this.m_webBrowserSh.StatusBar = false;
                this.m_webBrowserSh.TheaterMode = false;
                this.m_webBrowserSh.Offline = false;
                this.m_webBrowserSh.MenuBar = false;
                this.m_webBrowserSh.RegisterAsBrowser = false;
                this.m_webBrowserSh.RegisterAsDropTarget = false;
                this.m_webBrowserSh.AddressBar = false;
            }
            catch
            {
            }

            this.ctlWebBrowser.Navigate("https://mobile.twitter.com/login?redirect_after_login=https%3A%2F%2Ftwitter.com%2F");

            this.ctlWebBrowser.LoadCompleted += this.CtlWebBrowser_LoadCompleted;
        }

        private readonly object m_loginLock = new object();
        private void CtlWebBrowser_LoadCompleted(object sender, NavigationEventArgs e)
        {
            if (!e.Uri.Host.EndsWith("twitter.com"))
            {
                this.ctlWebBrowser.Navigate("https://mobile.twitter.com/login?redirect_after_login=https%3A%2F%2Ftwitter.com%2F");
                return;
            }

            if (e.Uri.AbsolutePath == "/")
            {
                Task.Factory.StartNew(() =>
                {
                    lock (this.m_loginLock)
                    {
                        try
                        {
                            var cookie = NativeMethods.GetCookies(TwitterClient.TwitterUri).GetCookieHeader(TwitterClient.TwitterUri);
                            this.TwitterClient = new TwitterClient(cookie);

                            if (this.TwitterClient.VerifyCredentials())
                            {
                                this.Dispatcher.Invoke(() =>
                                {
                                    this.DialogResult = true;
                                    this.Close();
                                });

                                return;
                            }
                        }
                        catch
                        {
                        }
                    }
                });
            }
        }

        private static class NativeMethods
        {
            [DllImport("wininet.dll", CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool InternetSetOption(
                IntPtr hInternet,
                int dwOption,
                IntPtr lpBuffer,
                int dwBufferLength);

            [DllImport("wininet.dll", CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool InternetGetCookieEx(
                string url,
                string cookieName,
                StringBuilder cookieData,
                ref int size,
                int dwFlags,
                IntPtr lpReserved);

            private const int INTERNET_COOKIE_HTTPONLY = 0x00002000;
            private const int INTERNET_OPTION_SUPPRESS_BEHAVIOR = 81;

            public static void SetCookieSupressBehavior()
            {
                var optionPtr = IntPtr.Zero;
                try
                {
                    optionPtr = Marshal.AllocHGlobal(4);
                    Marshal.WriteInt32(optionPtr, 3);

                    InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SUPPRESS_BEHAVIOR, optionPtr, 4);
                }
                finally
                {
                    if (optionPtr != IntPtr.Zero)
                        Marshal.FreeHGlobal(optionPtr);
                }
            }

            public static CookieContainer GetCookies(Uri uri)
            {
                var cc = new CookieContainer();

                GetCookies(uri, cc, 0);
                GetCookies(uri, cc, INTERNET_COOKIE_HTTPONLY);

                return cc;
            }
            private static void GetCookies(Uri uri, CookieContainer cc, int option)
            {
                int datasize = 4096;
                var cookieData = new StringBuilder(datasize);
                if (!InternetGetCookieEx(uri.ToString(), null, cookieData, ref datasize, option, IntPtr.Zero))
                {
                    if (datasize < 0)
                        return;

                    cookieData.Clear();
                    cookieData.Capacity = datasize;

                    if (!InternetGetCookieEx(uri.ToString(), null, cookieData, ref datasize, option, IntPtr.Zero))
                        return;
                }

                if (cookieData.Length > 0)
                    cc.SetCookies(uri, cookieData.ToString().Replace(';', ','));
            }
        }
    }
}
