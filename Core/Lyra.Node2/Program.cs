using System.Net;
using System.Threading.Tasks;
using Lyra.Authorizer.Decentralize;
using Lyra.Core.API;
using Lyra.Node2.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans;
using Microsoft.Extensions.Configuration;
using System.IO;
using System;
using System.Threading;
using Lyra.Core.Utils;
using System.Diagnostics;
using Lyra.Authorizer.Services;

namespace Lyra.Node2
{
    public class Program
    {
        static CancellationTokenSource _cancel;
        public static void Main(string[] args)
        {
            Console.WriteLine("Waiting for debugger to attach");
            while (!Debugger.IsAttached)
            {
                Thread.Sleep(100);
            }
            Console.WriteLine("Debugger attached");

            using (var host = CreateHostBuilder(args).Build())
            {
                _cancel = new CancellationTokenSource();
                host.StartAsync().Wait();
                Console.ReadLine();
            }
        }

        // Additional configuration is required to successfully run gRPC on macOS.
        // For instructions on how to configure Kestrel and gRPC clients on macOS, visit https://go.microsoft.com/fwlink/?linkid=2099682
        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseSystemd()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                })
                .UseOrleans((cntx, siloBuilder) =>
                {
                    siloBuilder
                    //.UseLocalhostClustering()
                    //.UseAdoNetClustering(options =>
                    //{
                    //    options.Invariant = "System.Data.SqlClient";
                    //    options.ConnectionString = "Data Source=ZION;Initial Catalog=Orleans;Persist Security Info=True;User ID=orleans;Password=orleans";
                    //})
                    .UseZooKeeperClustering((options) =>
                    {
                        options.ConnectionString = OrleansSettings.AppSetting["ZooKeeperClusteringSilo:ConnectionString"];
                    })
                    .Configure<ClusterOptions>(options =>
                    {
                        options.ClusterId = OrleansSettings.AppSetting["Cluster:ClusterId"];
                        options.ServiceId = OrleansSettings.AppSetting["Cluster:ServiceId"];
                    })
                    .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Parse(OrleansSettings.AppSetting["EndPoint:AdvertisedIPAddress"]))
                    .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(ApiService).Assembly).WithReferences())
                    //.AddAdoNetGrainStorage("OrleansStorage", options =>
                    //{
                    //    options.Invariant = "System.Data.SqlClient";
                    //    options.ConnectionString = "Data Source=ZION;Initial Catalog=Orleans;Persist Security Info=True;User ID=orleans;Password=orleans";
                    //})
                    .AddSimpleMessageStreamProvider(LyraGossipConstants.LyraGossipStreamProvider)
                    .AddMemoryGrainStorage("PubSubStore")
                    .UseDashboard(options => {
                        options.Port = 8080;
                    })
                    .AddStartupTask((sp, token) =>
                    {
                        //var sh = (SiloHandle)sp.GetRequiredService(typeof(SiloHandle));
                        SiloHandle.TheSilo = siloBuilder;
                        return Task.CompletedTask;
                    });
                })
                .ConfigureServices(services =>
                {
                    //services.AddTransient(typeof(SiloHandle));
                    //services.AddHostedService<NodeService>();
                    //services.AddSingleton<INodeAPI, ApiService>();
                });
    }
}
