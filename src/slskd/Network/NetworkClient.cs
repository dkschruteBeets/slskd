﻿// <copyright file="NetworkClient.cs" company="slskd Team">
//     Copyright (c) slskd Team. All rights reserved.
//
//     This program is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published
//     by the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//
//     You should have received a copy of the GNU Affero General Public License
//     along with this program.  If not, see https://www.gnu.org/licenses/.
// </copyright>

using Microsoft.Extensions.Options;

namespace slskd.Network
{
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.SignalR.Client;
    using Microsoft.Data.Sqlite;
    using Serilog;
    using slskd.Cryptography;
    using slskd.Shares;

    public interface INetworkClient
    {
        Task StartAsync(CancellationToken cancellationToken = default);
        Task StopAsync(CancellationToken cancellationToken = default);
    }

    public class NetworkClient : INetworkClient
    {
        public NetworkClient(
            IShareService shareService,
            IOptionsMonitor<Options> optionsMonitor,
            IHttpClientFactory httpClientFactory)
        {
            Shares = shareService;

            HttpClientFactory = httpClientFactory;

            OptionsMonitor = optionsMonitor;
            OptionsMonitor.OnChange(options => Configure(options));

            Configure(OptionsMonitor.CurrentValue);
        }

        private IShareService Shares { get; }
        private SemaphoreSlim SyncRoot { get; } = new SemaphoreSlim(1, 1);
        private string LastOptionsHash { get; set; }
        private IOptionsMonitor<Options> OptionsMonitor { get; }
        private IHttpClientFactory HttpClientFactory { get; }
        private HttpClient HttpClient { get; set; }
        private HubConnection HubConnection { get; set; }
        private bool StartRequested { get; set; }
        private CancellationTokenSource StartCancellationTokenSource { get; set; }
        private ILogger Log { get; } = Serilog.Log.ForContext<NetworkClient>();
        private TaskCompletionSource LoggedIn { get; set; }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (OptionsMonitor.CurrentValue.Network.OperationMode.ToEnum<NetworkOperationMode>() != NetworkOperationMode.Agent && !OptionsMonitor.CurrentValue.Flags.DualNetworkMode)
            {
                throw new InvalidOperationException($"Network client can only be started when operation mode is {NetworkOperationMode.Agent}");
            }

            StartCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            LoggedIn = new TaskCompletionSource();
            StartRequested = true;

            Log.Information("Attempting to connect to the network controller {Address}", OptionsMonitor.CurrentValue.Network.Controller.Address);

            // retry indefinitely
            await Retry.Do(
                task: () => HubConnection.StartAsync(StartCancellationTokenSource.Token),
                isRetryable: (attempts, ex) => true,
                onFailure: (attempts, ex) => Log.Warning("Failed attempt #{Attempts} to connect to network controller: {Message}", attempts, ex.Message),
                maxAttempts: int.MaxValue,
                maxDelayInMilliseconds: 30000,
                StartCancellationTokenSource.Token);

            Log.Information("Network controller connection established. Awaiting authentication...");

            await LoggedIn.Task;

            Log.Information("Authenticated. Uploading shares...");

            try
            {
                await UploadSharesAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, ex.Message);
                Log.Error("Disconnecting from the network controller");
                await StopAsync();
                throw;
            }

            Log.Information("Shares uploaded. Ready to upload files.");
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            StartRequested = false;

            if (HubConnection != null)
            {
                await HubConnection.StopAsync(cancellationToken);

                Log.Information("Network controller connection disconnected");
            }
        }

        public async Task UploadSharesAsync()
        {
            var temp = Path.Combine(Path.GetTempPath(), Program.AppName, $"share_backup_{Path.GetRandomFileName()}.db");

            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Program.AppName));

            Log.Debug("Backing up shares to {Filename}", temp);
            await Shares.DumpAsync(temp);
            Log.Debug("Share backup successful");

            Log.Debug("Requesting share upload token...");
            var token = await HubConnection.InvokeAsync<Guid>(nameof(NetworkHub.GetShareUploadToken));
            Log.Debug("Share upload token {Token}", token);

            var stream = new FileStream(temp, FileMode.Open, FileAccess.Read);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v0/network/shares/{token}");
            using var content = new MultipartFormDataContent
            {
                { new StringContent(Shares.LocalHost.Shares.ToJson()), "shares" },
                { new StreamContent(stream), "database", "shares" },
            };

            request.Content = content;

            Log.Information("Beginning upload of shares");
            var response = await HttpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                throw new NetworkException($"Failed to upload shares to network controller: {response.StatusCode}");
            }

            Log.Information("Upload shares succeeded");
        }

        private async Task HandleFileRequest(string filename, Guid id)
        {
            Log.Information("Network controller requested file {Filename} with ID {Id}", filename, id);

            try
            {
                var (_, localFilename) = await Shares.ResolveFileAsync(filename);

                var localFileInfo = new FileInfo(localFilename);

                if (!localFileInfo.Exists)
                {
                    Shares.RequestScan();
                    throw new NotFoundException($"The file '{localFilename}' could not be located on disk. A share scan should be performed.");
                }

                using var stream = new FileStream(localFileInfo.FullName, FileMode.Open, FileAccess.Read);
                using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v0/network/files/{id}");
                using var content = new MultipartFormDataContent
                {
                    { new StreamContent(stream), "file", filename },
                };

                request.Content = content;

                Log.Information("Beginning upload of file {Filename} with ID {Id}", filename, id);
                var response = await HttpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Error("Upload of file {Filename} with ID {Id} failed: {StatusCode}", response.StatusCode);
                }
                else
                {
                    Log.Information("Upload of file {Filename} with ID {Id} succeeded.", filename);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to handle file request: {Message}", ex.Message);

                // report the failure to the controller. this avoids a failure due to timeout.
                await HubConnection.InvokeAsync(nameof(NetworkHub.NotifyUploadFailed), id);
            }
        }

        private async Task HandleFileInfoRequest(string filename, Guid id)
        {
            Log.Information("Network controller requested file info for {Filename} with ID {Id}", filename, id);

            try
            {
                var (_, localFilename) = await Shares.ResolveFileAsync(filename);

                var localFileInfo = new FileInfo(localFilename);

                await HubConnection.InvokeAsync(nameof(NetworkHub.ReturnFileInfo), id, localFileInfo.Exists, localFileInfo.Length);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to handle file info request: {Message}", ex.Message);
                await HubConnection.InvokeAsync(nameof(NetworkHub.ReturnFileInfo), id, false, 0);
            }
        }

        private async Task HandleAuthenticationChallenge(string challengeToken)
        {
            Log.Debug("Network controller sent an authentication challenge");

            try
            {
                var options = OptionsMonitor.CurrentValue;

                var agent = options.InstanceName;
                var key = options.Network.Controller.Secret.FromBase62();
                var tokenBytes = challengeToken.FromBase62();

                var response = Aes.Encrypt(tokenBytes, key).ToBase62();

                Log.Debug("Logging in...");
                var success = await HubConnection.InvokeAsync<bool>(nameof(NetworkHub.Login), agent, response);

                if (!success)
                {
                    await HubConnection.StopAsync();
                    Log.Error("Network controller authentication failed. Check configuration.");
                }

                Log.Debug("Login succeeded.");
                LoggedIn?.TrySetResult();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to handle authentication challenge: {Message}", ex.Message);
            }
        }

        private Task HubConnection_Closed(Exception arg)
        {
            Log.Warning("Network controller connection closed: {Message}", arg.Message);
            return Task.CompletedTask;
        }

        private Task HubConnection_Reconnecting(Exception arg)
        {
            Log.Warning("Network controller connection reconnecting: {Message}", arg.Message);
            return Task.CompletedTask;
        }

        private Task HubConnection_Reconnected(string arg)
        {
            Log.Warning("Network controller connection reconnected");
            return Task.CompletedTask;
        }

        private void Configure(Options options)
        {
            if (options.Network.OperationMode.ToEnum<NetworkOperationMode>() != NetworkOperationMode.Agent && !options.Flags.DualNetworkMode)
            {
                return;
            }

            SyncRoot.Wait();

            try
            {
                var optionsHash = Compute.Sha1Hash(options.Network.Controller.ToJson());

                if (optionsHash == LastOptionsHash)
                {
                    return;
                }

                Log.Debug("Network options changed. Reconfiguring...");

                // if the client is attempting a connection, cancel it
                // it's going to be dropped when we create a new instance, but we need
                // the retry loop around connection to stop.
                StartCancellationTokenSource?.Cancel();

                HttpClient = HttpClientFactory.CreateClient();
                HttpClient.BaseAddress = new(options.Network.Controller.Address);

                HubConnection = new HubConnectionBuilder()
                    .WithUrl($"{options.Network.Controller.Address}/hub/agents")
                    .WithAutomaticReconnect(new[]
                    {
                        TimeSpan.FromSeconds(0),
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(3),
                        TimeSpan.FromSeconds(10),
                        TimeSpan.FromSeconds(30),
                    })
                    .Build();

                HubConnection.Reconnected += HubConnection_Reconnected;
                HubConnection.Reconnecting += HubConnection_Reconnecting;
                HubConnection.Closed += HubConnection_Closed;

                HubConnection.On<string, Guid>(NetworkHubMethods.RequestFile, HandleFileRequest);
                HubConnection.On<string, Guid>(NetworkHubMethods.RequestFileInfo, HandleFileInfoRequest);
                HubConnection.On<string>(NetworkHubMethods.AuthenticationChallenge, HandleAuthenticationChallenge);

                LastOptionsHash = optionsHash;

                Log.Debug("Network options reconfigured");

                // if start was requested (if StartAsync() was called externally), restart
                // after re-configuration
                if (StartRequested)
                {
                    Log.Information("Reconnecting the network controller connection...");
                    _ = StartAsync();
                }
            }
            finally
            {
                SyncRoot.Release();
            }
        }
    }
}
