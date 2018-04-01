﻿namespace ServerConsole.Commands.Menus
{
    using System;
    using System.IO;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using AaronLuna.Common.Console;
    using AaronLuna.Common.Console.Menu;
    using AaronLuna.Common.Extensions;
    using AaronLuna.Common.IO;
    using AaronLuna.Common.Logging;
    using AaronLuna.Common.Network;
    using AaronLuna.Common.Result;

    using ServerCommands;

    using TplSockets;

    using CompositeCommands;

    class MainMenu : SelectionMenuLoop
    {
        readonly AppState _state;
        readonly Logger _log = new Logger(typeof(MainMenu));

        public MainMenu(AppState state)
        {
            _log.Info("Begin: Instantiate MainMenu");

            ReturnToParent = true;
            ItemText = "Main menu";
            MenuText = "\nMenu for TPL socket server:";
            Options = new List<ICommand>();

            var selectServerCommand = new SelectRemoteServerMenu(state);
            var selectActionCommand = new SelectServerActionMenu(state);
            var changeSettingsCommand = new ChangeSettingsMenu(state);
            var shutdownCommand = new ShutdownServerCommand();

            Options.Add(selectServerCommand);
            Options.Add(selectActionCommand);
            Options.Add(changeSettingsCommand);
            Options.Add(shutdownCommand);

            _state = state;
            _state.Server.EventOccurred += HandleServerEvent;
            _state.Server.FileTransferProgress += HandleFileTransferProgress;

            _log.Info("Complete: Instantiate MainMenu");
        }

        public new async Task<Result> ExecuteAsync()
        {
            _log.Info("Begin: MainMenu.ExecuteAsync");

            var exit = false;
            Result result = null;

            while (!exit)
            {
                _log.Info("Re-enter while loop: MainMenu.ExecuteAsync");

                //_state.WaitingForUserInput = false;
                var userSelection = 0;
                while (userSelection == 0)
                {
                    MenuFunctions.DisplayMenu(MenuText, Options);
                    var input = Console.ReadLine();

                    var validationResult = MenuFunctions.ValidateUserInput(input, OptionCount);
                    if (validationResult.Failure)
                    {
                        _log.Error($"Error: {validationResult.Error} (MainMenu.ExecuteAsync)");
                        Console.WriteLine(validationResult.Error);
                        continue;
                    }

                    userSelection = validationResult.Value;
                }

                var selectedOption = Options[userSelection - 1];
                result = await selectedOption.ExecuteAsync();
                exit = selectedOption.ReturnToParent;

                if (result.Success) continue;
                Console.WriteLine($"{Environment.NewLine}Error: {result.Error}");

                if (result.Error.Contains(ConsoleStatic.NoClientSelectedError))
                {
                    Console.WriteLine("Press Enter to return to main menu.");
                    Console.ReadLine();
                    continue;
                }

                _log.Error($"Error: {result.Error} (MainMenu.ExecuteAsync)");
                exit = ConsoleStatic.PromptUserYesOrNo("Exit program?");

                if (exit)
                {
                    _log.Info("Exit while loop: MainMenu.ExecuteAsync");
                }
            }

            _log.Info("Complete: MainMenu.ExecuteAsync");
            return result;
        }

        void HandleServerEvent(object sender, ServerEvent serverEvent)
        {
            _log.Info(serverEvent.ToString());
            DisplayServerEvent(serverEvent);
            ProcessServerEvent(serverEvent).GetAwaiter().GetResult();

            //if (!_state.WaitingForUserInput) return;

            //_state.SignalDispayMenu.WaitOne();
            //_state.WaitingForUserInput = false;
            //MenuFunctions.DisplayMenu(MenuText, Options);
        }

        void HandleFileTransferProgress(object sender, ServerEvent serverEvent)
        {
            _state.Progress.BytesReceived = serverEvent.TotalFileBytesReceived;
            _state.Progress.Report(serverEvent.PercentComplete);
        }

        private void DisplayServerEvent(ServerEvent serverEvent)
        {
            string fileCount;

            switch (serverEvent.EventType)
            {
                case EventType.ReceivedOutboundFileTransferRequest:
                    Console.WriteLine("\nReceived Outbound File Transfer Request");
                    Console.WriteLine($"File Requested:\t\t{serverEvent.FileName}\nFile Size:\t\t{serverEvent.FileSizeString}\nRemote Endpoint:\t{serverEvent.RemoteServerIpAddress}:{serverEvent.RemoteServerPortNumber}\nTarget Directory:\t{serverEvent.RemoteFolder}");
                    break;

                case EventType.ReceivedInboundFileTransferRequest:
                    Console.WriteLine($"\nIncoming file transfer from {serverEvent.RemoteServerIpAddress}:{serverEvent.RemoteServerPortNumber}:");
                    Console.WriteLine($"File Name:\t{serverEvent.FileName}\nFile Size:\t{serverEvent.FileSizeString}\nSave To:\t{serverEvent.LocalFolder}");
                    break;

                case EventType.SendNotificationNoFilesToDownloadStarted:
                    Console.WriteLine($"\nClient ({serverEvent.RemoteServerIpAddress}:{serverEvent.RemoteServerPortNumber}) requested list of files available to download, but transfer folder is empty");
                    break;

                case EventType.ReceivedNotificationNoFilesToDownload:
                    Console.WriteLine("\nClient has no files available for download.");
                    break;

                case EventType.SendFileTransferRejectedStarted:
                    Console.WriteLine("\nA file with the same name already exists in the download folder, please rename or remove this file in order to proceed");
                    break;

                case EventType.SendFileBytesStarted:
                    Console.WriteLine("\nSending file to client...");
                    break;

                //case EventType.SendFileTransferCanceledStarted:
                //case EventType.ReceiveFileTransferCanceledComplete:
                //    Console.WriteLine("File transfer successfully canceled");
                //    break;

                case EventType.ReceiveConfirmationMessageComplete:
                    Console.WriteLine("Client confirmed file transfer completed successfully");
                    break;

                case EventType.RequestFileListStarted:
                    Console.WriteLine($"Sending request for list of available files to {serverEvent.RemoteServerIpAddress}:{serverEvent.RemoteServerPortNumber}...");
                    break;

                case EventType.ReceivedFileListRequest:
                    Console.WriteLine($"\nReceived request for list of available files from {serverEvent.RemoteServerIpAddress}:{serverEvent.RemoteServerPortNumber}...");
                    break;

                case EventType.SendFileListStarted:

                    fileCount = serverEvent.FileInfoList.Count == 1
                        ? $"{serverEvent.FileInfoList.Count} file in list"
                        : $"{serverEvent.FileInfoList.Count} files in list";

                    Console.WriteLine($"Sending list of files to {serverEvent.RemoteServerIpAddress}:{serverEvent.RemoteServerPortNumber} ({fileCount})");
                    break;

                case EventType.ReceivedFileList:

                    fileCount = serverEvent.FileInfoList.Count == 1
                        ? $"{serverEvent.FileInfoList.Count} file in list"
                        : $"{serverEvent.FileInfoList.Count} files in list";

                    Console.WriteLine($"Received list of files from {serverEvent.RemoteServerIpAddress}:{serverEvent.RemoteServerPortNumber} ({fileCount})\n");
                    break;

                case EventType.RequestPublicIpAddressStarted:
                    Console.WriteLine($"\nSending request for public IP address to {serverEvent.RemoteServerIpAddress}:{serverEvent.RemoteServerPortNumber}...");
                    break;

                case EventType.ReceivedPublicIpAddressRequest:
                    Console.WriteLine($"\nReceived request for public IP address from {serverEvent.RemoteServerIpAddress}:{serverEvent.RemoteServerPortNumber}...");
                    break;

                case EventType.SendTransferFolderPathStarted:
                case EventType.SendPublicIpAddressStarted:
                    Console.WriteLine("Sent");
                    break;

                case EventType.ReceivedTransferFolderPath:
                case EventType.ReceivedPublicIpAddress:
                    Console.Write("Success");
                    break;

                case EventType.RequestTransferFolderPathStarted:
                    Console.WriteLine($"Sending request for transfer folder path to {serverEvent.RemoteServerIpAddress}:{serverEvent.RemoteServerPortNumber}...");
                    break;

                case EventType.ReceivedTransferFolderPathRequest:
                    Console.WriteLine($"\nReceived request for transfer folder path from {serverEvent.RemoteServerIpAddress}:{serverEvent.RemoteServerPortNumber}...");
                    break;

                case EventType.ShutdownListenSocketCompletedWithoutError:
                    Console.WriteLine("Server has been successfully shutdown, press Enter to exit program\n");
                    break;

                case EventType.ShutdownListenSocketCompletedWithError:
                    Console.WriteLine($"Error occurred while attempting to shutdown listening socket:{Environment.NewLine}{serverEvent.ErrorMessage}");
                    break;

                case EventType.ErrorOccurred:
                    Console.WriteLine($"Error: {serverEvent.ErrorMessage}");
                    break;
            }
        }

        async Task ProcessServerEvent(ServerEvent serverEvent)
        {
            switch (serverEvent.EventType)
            {
                case EventType.ServerIsListening:
                    _state.WaitingForServerToBeginAcceptingConnections = false;
                    return;

                //case EventType.ConnectionAccepted:
                //    await SaveNewClient(serverEvent.RemoteServerIpAddress, serverEvent.RemoteServerPortNumber).ConfigureAwait(false);
                //    return;

                //case EventType.ReceivedTransferFolderPathRequest:
                //    _state.IgnoreIncomingConnections = true;
                //    break;

                //case EventType.SendPublicIpAddressStarted:
                //    _state.IgnoreIncomingConnections = false;
                //    await ProcessUnknownHostsAsync();
                //    break;

                case EventType.SendPublicIpAddressComplete:
                    await Task.Delay(100);
                    var clientIp = _state.ClientSessionIpAddress;
                    var clientPort = _state.ClientServerPort;
                    SaveNewClient(clientIp, clientPort);
                    break;

                case EventType.ReceivedTextMessage:
                    HandleReadTextMessageComplete(serverEvent);
                    break;

                case EventType.ReceivedInboundFileTransferRequest:
                    _state.ClientInfo.SessionIpAddress = Network.ParseSingleIPv4Address(serverEvent.RemoteServerIpAddress).Value;
                    _state.ClientInfo.Port = serverEvent.RemoteServerPortNumber;
                    _state.DownloadFileName = serverEvent.FileName;
                    _state.RetryCounter = 0;
                    return;

                case EventType.ReceiveFileBytesStarted:
                    HandleReceiveFileBytesStarted(serverEvent);
                    return;

                case EventType.ReceiveFileBytesComplete:
                    HandleReceiveFileBytesComplete(serverEvent);
                    break;

                case EventType.ReceiveConfirmationMessageComplete:
                    _state.WaitingForConfirmationMessage = false;
                    break;

                case EventType.ReceivedFileList:
                    _state.WaitingForFileListResponse = false;
                    _state.FileInfoList = serverEvent.FileInfoList;
                    return;

                case EventType.SendFileTransferStalledComplete:
                    HandleFileTransferStalled();
                    break;

                case EventType.SendFileTransferRejectedComplete:
                    _state.SignalExitRetryDownloadLogic.WaitOne();
                    break;

                case EventType.ClientRejectedFileTransfer:
                    _state.WaitingForConfirmationMessage = false;
                    _state.FileTransferRejected = true;
                    break;

                case EventType.FileTransferStalled:
                    _state.WaitingForConfirmationMessage = false;
                    _state.FileTransferCanceled = true;
                    break;

                case EventType.ReceivedNotificationNoFilesToDownload:
                    _state.WaitingForFileListResponse = false;
                    _state.NoFilesAvailableForDownload = true;
                    break;

                case EventType.ErrorOccurred:
                    _state.ErrorOccurred = true;
                    break;

                default:
                    return;
            }
        }

        //async Task ProcessUnknownHostsAsync()
        //{
        //    if (_state.UnknownHosts.Count <= 0) return;

        //    var hostCount = _state.UnknownHosts.Count;
        //    foreach (var i in Enumerable.Range(0, hostCount))
        //    {
        //        var startEvent = new ServerEvent
        //        {
        //            EventType = EventType.ProcessUnknownHostStarted,
        //            TotalUnknownHosts = hostCount,
        //            UnknownHostsProcessed = i
        //        };

        //        _log.Info(startEvent.ToString());

        //        await SaveNewClient(
        //            _state.UnknownHosts[i].ConnectionInfo.SessionIpString,
        //            _state.UnknownHosts[i].ConnectionInfo.Port).ConfigureAwait(false);

        //        var completeEvent = new ServerEvent
        //        {
        //            EventType = EventType.ProcessUnkownHostComplete,
        //            TotalUnknownHosts = hostCount,
        //            UnknownHostsProcessed = i + 1
        //        };

        //        _log.Info(completeEvent.ToString());
        //    }
        //}

        void HandleReadTextMessageComplete(ServerEvent serverEvent)
        {
            var textIp = Network.ParseSingleIPv4Address(serverEvent.RemoteServerIpAddress).Value;
            var textPort = serverEvent.RemoteServerPortNumber;
            _state.TextMessageEndPoint = new IPEndPoint(textIp, textPort);

            Console.WriteLine("Presss enter to return to main menu");
            Console.ReadLine();
            Console.WriteLine("Returning to main menu...");

            //Console.Clear();
            //Console.WriteLine($"\n{serverEvent.RemoteServerIpAddress}:{serverEvent.RemoteServerPortNumber} says:");
            //Console.WriteLine(serverEvent.TextMessage);

            //if (ConsoleStatic.PromptUserYesOrNo($"{Environment.NewLine}Reply to {textIp}:{textPort}?"))
            //{
            //    var message = Console.ReadLine();

            //    var sendMessageResult =
            //        await _state.Server.SendTextMessageAsync(
            //            message,
            //            textIp.ToString(),
            //            textPort,
            //            _state.MyLocalIpAddress,
            //            _state.MyServerPort,
            //            new CancellationToken()).ConfigureAwait(false);

            //    if (sendMessageResult.Failure)
            //    {
            //        Console.WriteLine(sendMessageResult.Error);
            //    }
            //}

            //_state.WaitingForUserInput = true;
            //_state.SignalDispayMenu.Set();
        }

        void HandleReceiveFileBytesStarted(ServerEvent serverEvent)
        {
            //_statusChecker = new StatusChecker(1000);
            //_statusChecker.NoActivityEvent += HandleStalledFileTransfer;

            _state.Progress = new ConsoleProgressBar
            {
                FileSizeInBytes = serverEvent.FileSizeInBytes,
                NumberOfBlocks = 15,
                StartBracket = "|",
                EndBracket = "|",
                CompletedBlock = "|",
                UncompletedBlock = "-",
                DisplayAnimation = false,
                DisplayLastRxTime = true,
                FileStalledInterval = TimeSpan.FromSeconds(10)
            };

            _state.Progress.FileTransferStalled += HandleStalledFileTransfer;
            _state.ProgressBarInstantiated = true;
            Console.WriteLine(Environment.NewLine);
        }

        void HandleReceiveFileBytesComplete(ServerEvent serverEvent)
        {
            _state.Progress.BytesReceived = serverEvent.FileSizeInBytes;
            _state.Progress.Report(1);
            Task.Delay(ConsoleStatic.OneHalfSecondInMilliseconds).GetAwaiter().GetResult();

            _state.Progress.Dispose();
            _state.ProgressBarInstantiated = false;

            Console.WriteLine($"\n\nTransfer Start Time:\t\t{serverEvent.FileTransferStartTime.ToLongTimeString()}");
            Console.WriteLine($"Transfer Complete Time:\t\t{serverEvent.FileTransferCompleteTime.ToLongTimeString()}");
            Console.WriteLine($"Elapsed Time:\t\t\t{serverEvent.FileTransferElapsedTimeString}");
            Console.WriteLine($"Transfer Rate:\t\t\t{serverEvent.FileTransferRate}");

            _state.WaitingForDownloadToComplete = false;
            _state.RetryCounter = 0;
            _state.SignalExitRetryDownloadLogic.Set();
        }

        void HandleStalledFileTransfer(object sender, ProgressEventArgs eventArgs)
        {
            _state.FileStalledInfo = eventArgs;
            _state.FileTransferStalled = true;
            _state.WaitingForDownloadToComplete = false;

            _state.Progress.Dispose();
            _state.ProgressBarInstantiated = false;

            var notifyStalledResult =
                NotifyClientThatFileTransferHasStalled(
                    _state.ClientInfo.SessionIpAddress.ToString(),
                    _state.ClientInfo.Port,
                    new CancellationToken());

            if (notifyStalledResult.Failure)
            {
                Console.WriteLine(notifyStalledResult.Error);
            }

            if (_state.RetryCounter >= _state.Settings.MaxDownloadAttempts)
            {
                var maxRetriesReached =
                    "Maximum # of attempts to complete stalled file transfer reached or exceeded " +
                    $"({_state.Settings.MaxDownloadAttempts} failed attempts for \"{_state.DownloadFileName}\")";

                Console.WriteLine(maxRetriesReached);

                var folder = _state.Settings.TransferFolderPath;
                var filePath1 = $"{folder}{Path.DirectorySeparatorChar}{_state.DownloadFileName}";
                //var filePath2 = Path.Combine(folder, _state.DownloadFileName);

                FileHelper.DeleteFileIfAlreadyExists(filePath1);

                _state.SignalExitRetryDownloadLogic.Set();
                return;
            }

            var userPrompt = $"Try again to download file \"{_state.DownloadFileName}\" from {_state.ClientInfo.SessionIpAddress}:{_state.ClientInfo.Port}?";
            if (ConsoleStatic.PromptUserYesOrNo(userPrompt))
            {
                _state.RetryCounter++;
                _state.Server.RetryCanceledFileTransfer(
                    _state.ClientSessionIpAddress,
                    _state.ClientServerPort).GetAwaiter().GetResult();
            }
        }

        Result NotifyClientThatFileTransferHasStalled(
            string ipAddress,
            int port,
            CancellationToken token)
        {
            var notifyCientResult =
                _state.Server.SendNotificationFileTransferStalledAsync().GetAwaiter().GetResult();

            return notifyCientResult.Failure
                ? Result.Fail($"\nError occurred when notifying client that file transfer data is no longer being received:\n{notifyCientResult.Error}")
                : Result.Fail("File transfer canceled, data is no longer being received from client");

        }

        private void HandleFileTransferStalled()
        {
            var sinceLastActivity = DateTime.Now - _state.FileStalledInfo.LastDataReceived;
            Console.WriteLine($"\n\nFile transfer has stalled, {sinceLastActivity.ToFormattedString()} elapsed since last data received");
        }

        void SaveNewClient(string clientIpAddress, int clientPort)
        {
            var newClient = new RemoteServer(clientIpAddress, clientPort);

            //if (_state.IgnoreIncomingConnections)
            //{dt
            //    _state.UnknownHosts.Add(newClient);
            //    return      Result.Ok();
            //}
            var requestServerInfoCommand = new RequestAdditionalInfoFromRemoteServerCommand(_state, newClient);
            var requestServerInfoResult = requestServerInfoCommand.ExecuteAsync().GetAwaiter().GetResult();

            if (requestServerInfoResult.Failure)
            {
                Console.WriteLine(requestServerInfoResult.Error);
            }

            _state.ClientInfo = newClient.ConnectionInfo;
            _state.ClientTransferFolderPath = newClient.TransferFolder;
        }
    }
}
 