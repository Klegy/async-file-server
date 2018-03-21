﻿namespace ServerConsole.Commands.ServerCommands
{
    using System;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using AaronLuna.Common.Console.Menu;
    using AaronLuna.Common.Result;

    using TplSocketServer;

    class RequestTransferFolderPathFromRemoteServerCommand : ICommand<string>
    {
        const string ConnectionRefusedAdvice =
            "\nPlease verify that the port number on the client server is properly opened, this could entail modifying firewall or port forwarding settings depending on the operating system.";

        readonly AppState _state;
        readonly IPAddress _clientIp;
        readonly int _clientPort;

        public RequestTransferFolderPathFromRemoteServerCommand(AppState state, IPAddress clientIp, int clientPort)
        {
            _state = state;
            _state.Server.EventOccurred += HandleServerEvent;

            _clientIp = clientIp;
            _clientPort = clientPort;

            ReturnToParent = false;
            ItemText = "Request transfer folder path";
        }

        public string ItemText { get; set; }
        public bool ReturnToParent { get; set; }

        public async Task<CommandResult<string>> ExecuteAsync()
        {
            _state.WaitingForTransferFolderResponse = true;
            _state.ClientResponseIsStalled = false;
            _state.ClientTransferFolderPath = string.Empty;

            var sendFolderRequestResult =
                await _state.Server.RequestTransferFolderPathAsync(
                        _clientIp.ToString(),
                        _clientPort,
                        _state.MyInfo.LocalIpAddress.ToString(),
                        _state.MyInfo.Port,
                        new CancellationToken())
                    .ConfigureAwait(false);

            if (sendFolderRequestResult.Failure)
            {
                var userHint = string.Empty;
                if (sendFolderRequestResult.Error.Contains("Connection refused"))
                {
                    userHint = ConnectionRefusedAdvice;
                }

                var result =  Result.Fail<string>($"{sendFolderRequestResult.Error}{userHint}");
                return new CommandResult<string>
                {
                    ReturnToParent = ReturnToParent,
                    Result = result
                };
            }

            var twoSecondTimer = new Timer(HandleTimeout, true, 2000, Timeout.Infinite);

            while (_state.WaitingForTransferFolderResponse)
            {
                if (_state.ClientResponseIsStalled)
                {
                    twoSecondTimer.Dispose();
                    throw new TimeoutException();
                }
            }
            
            var anotherResult = Result.Ok(_state.ClientTransferFolderPath);
            return new CommandResult<string>
            {
                ReturnToParent = ReturnToParent,
                Result = anotherResult
            };
        }

        void HandleTimeout(object state)
        {
            _state.ClientResponseIsStalled = true;
        }

        void HandleServerEvent(object sender, ServerEvent eventInfo)
        {
            switch (eventInfo.EventType)
            {
                case EventType.ReadTransferFolderResponseComplete:
                    _state.WaitingForTransferFolderResponse = false;
                    _state.ClientTransferFolderPath = eventInfo.RemoteFolder;
                    break;
            }
        }
    }
}