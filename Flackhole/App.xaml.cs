using System.Windows;
using Newtonsoft.Json;

namespace Flackhole
{
    public partial class App : Application
    {
        public static readonly JsonSerializer JsonSerializer = new JsonSerializer
        {
            DateFormatString = "ddd MMM dd HH:mm:ss +ffff yyyy",
            Formatting = Formatting.None,
            StringEscapeHandling = StringEscapeHandling.EscapeNonAscii,
        };
    }
}
