﻿using Lyra.Authorizer.Decentralize;
using Lyra.Client.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using Orleans;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LyraNodesBot
{
    class Program
    {
        static void Main(string[] args)
        {
            var monitor = new NodesMonitor();
            monitor.Start();

            var host = CreateHost();
            host.Start();
            var client = (ClusterClientHostedService)host.Services.GetService<IHostedService>();




            Console.ReadLine();

            monitor.Stop();
        }

        //private static async Task AttachStream(IClusterClient client)
        //{
        //    var room = client.GetGrain<ILyraGossip>(GossipConstants.LyraGossipStreamId);
        //    var streamId = await stream.OnNextAsync(new ChatMsg("System", $"{nickname} joins the chat '{this.GetPrimaryKeyString()}' ..."));
        //    var stream = client.GetStreamProvider(GossipConstants.LyraGossipStreamProvider)
        //        .GetStream<ChatMsg>(streamId, GossipConstants.LyraGossipStreamNameSpace);
        //    //subscribe to the stream to receiver furthur messages sent to the chatroom
        //    await stream.SubscribeAsync(new StreamObserver(client.ServiceProvider.GetService<ILoggerFactory>()
        //        .CreateLogger($"{joinedChannel} channel")));
        //}

        private static IHost CreateHost()
        {
            return new HostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<ClusterClientHostedService>();
                    services.AddSingleton<IHostedService>(_ => _.GetService<ClusterClientHostedService>());
                    services.AddSingleton(_ => _.GetService<ClusterClientHostedService>().Client);

                    //services.AddHostedService<DAGClientHostedService>();

                    services.Configure<ConsoleLifetimeOptions>(options =>
                    {
                        options.SuppressStatusMessages = true;
                    });
                })
                //.ConfigureLogging(builder =>
                //{
                //    builder.AddConsole();
                //})
                .Build();
        }
    }
}