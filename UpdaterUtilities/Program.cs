using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace UpdaterUtilities
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddHostedService<Worker>();
                    services.AddSingleton<IConfiguration>(hostContext.Configuration);
                })
                .UseWindowsService(options =>
                {
                    options.ServiceName = "SystemUpdateHelper";
                });
    }
}