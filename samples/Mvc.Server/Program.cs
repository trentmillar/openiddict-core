using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;

namespace Mvc.Server
{
    public static class Program
    {
        public static void Main(string[] args) =>
            BuildWebHost(args).Run();

        private static IWebHost BuildWebHost(string[] args) {
            var host = WebHost.CreateDefaultBuilder(args);
            host = host.UseStartup<Startup>();
            var webHost = host.Build();
            return webHost;
        }
    }
}

