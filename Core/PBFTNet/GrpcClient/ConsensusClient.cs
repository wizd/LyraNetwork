﻿using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Communication;
using Grpc.Core;
using Grpc.Net.Client;

namespace GrpcClient
{
    public class ConsensusClient
    {
        const int PORT = 4505;
        GrpcClient _client;
        string _accountId;

        public event EventHandler<ResponseMessage> OnMessage;
        public event EventHandler<string> OnShutdown;

        public void Start(string nodeAddress, string accountId)
        {
            Console.WriteLine("GrpcClient started.");

            _accountId = accountId;

            var httpClientHandler = new HttpClientHandler();
            // Return `true` to allow certificates that are untrusted/invalid
            httpClientHandler.ServerCertificateCustomValidationCallback = (a, b, c, d) => true;
            var httpClient = new HttpClient(httpClientHandler);

            var channelCredentials = new SslCredentials(File.ReadAllText(@"Certs\certificate.crt"));
            //var channel = new Channel($"{nodeAddress}:{PORT}", channelCredentials);
            var channel = GrpcChannel.ForAddress($"https://{nodeAddress}:{PORT}", new GrpcChannelOptions { HttpClient = httpClient });

            var nl = Environment.NewLine;
            var orgTextColor = Console.ForegroundColor;

            _client = new GrpcClient(accountId);

            _ = Task.Run(async () =>
            {
                await _client.Do(
                    channel,
                    () =>
                    {
                        Console.Write($"Connected to server.{nl}ClientId = ");
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.Write($"{_client.ClientId}");
                        Console.ForegroundColor = orgTextColor;
                        Console.WriteLine($".{nl}Enter string message to server.{nl}" +
                            $"You will get response if your message will contain question mark '?'.{nl}" +
                            $"Enter empty message to quit.{nl}");
                    },
                    (resp) => { OnMessage(this, resp); },
                    () =>
                    {
                        Console.WriteLine("Shutting down...");
                        OnShutdown?.Invoke(this, _accountId);
                    }
                );
            });
        }

        public void SendMessage(object o)
        {
            if(_client == null)
                OnShutdown?.Invoke(this, _accountId);
            else
                _client.SendObject(o);
        }
    }
}