﻿using System.Collections.Generic;
using System.Linq;

namespace TplSocketServer
{
    using AaronLuna.Common.IO;
    using AaronLuna.Common.Result;

    using System;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    public class TplSocketServer
    {
        const string ConfirmationMessage = "handshake";
        const int OneSecondInMilliseconds = 1000;

        readonly int _maxConnections;
        readonly int _bufferSize;

        readonly int _connectTimeoutMs;
        readonly int _receiveTimeoutMs;
        readonly int _sendTimeoutMs;
        
        readonly string _transferFolderPath;

        string _localIpAddress;
        int _localPort;

        Socket _listenSocket;
        Socket _transferSocket;

        public TplSocketServer()
        {
            _transferFolderPath = $"{Directory.GetCurrentDirectory()}{Path.DirectorySeparatorChar}transfer";

            _maxConnections = 5;
            _bufferSize = 1024 * 8;
            _connectTimeoutMs = 5000;
            _receiveTimeoutMs = 5000;
            _sendTimeoutMs = 5000;

            _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }

        public TplSocketServer(ServerSettings serverSettings)
        {
            _transferFolderPath = serverSettings.TransferFolderPath;

            _maxConnections = serverSettings.SocketSettings.MaxNumberOfConections;
            _bufferSize = serverSettings.SocketSettings.BufferSize;
            _connectTimeoutMs = serverSettings.SocketSettings.ConnectTimeoutMs;
            _receiveTimeoutMs = serverSettings.SocketSettings.ReceiveTimeoutMs;
            _sendTimeoutMs = serverSettings.SocketSettings.SendTimeoutMs;

            _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }
        
        public event ServerEventDelegate EventOccurred;

        public async Task<Result> HandleIncomingConnectionsAsync(IPAddress ipAddress, int localPort, CancellationToken token)
        {
            _localIpAddress = ipAddress.ToString();
            _localPort = localPort;

            return (await Task.Factory.StartNew(() => Listen(ipAddress, localPort), token).ConfigureAwait(false))
                .OnSuccess(() => WaitForConnectionsAsync(token));
        }

        private Result Listen(IPAddress ipAddress, int localPort)
        { 
            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ListenOnLocalPortStarted });
            
            var ipEndPoint = new IPEndPoint(ipAddress, localPort);            
            try
            {
                _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listenSocket.Bind(ipEndPoint);
                _listenSocket.Listen(_maxConnections);
            }
            catch (SocketException ex)
            {
                return Result.Fail($"{ex.Message} ({ex.GetType()} raised in method TplSocketServer.Listen)");
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ListenOnLocalPortCompleted });

            return Result.Ok();
        }

        private async Task<Result> WaitForConnectionsAsync(CancellationToken token)
        {
            // Main loop. Server handles incoming connections until encountering an error
            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    token.ThrowIfCancellationRequested();
                }

                var acceptConnectionResult = await AcceptNextConnection(token).ConfigureAwait(false);

                if (acceptConnectionResult.Success)
                {
                    var requestResult = await ProcessRequestAsync(token).ConfigureAwait(false);

                    if (requestResult.Failure)
                    {
                        EventOccurred?.Invoke(new ServerEventInfo
                        {
                            EventType = ServerEventType.ErrorOccurred,
                            ErrorMessage = requestResult.Error
                        });

                        return requestResult;
                    }
                }
            }
        }

        private async Task<Result> AcceptNextConnection(CancellationToken token)
        {
            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ConnectionAttemptStarted });

            var acceptResult = await _listenSocket.AcceptTaskAsync().ConfigureAwait(false);
            if (acceptResult.Failure)
            {
                return acceptResult;
            }

            if (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }

            _transferSocket = acceptResult.Value;

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ConnectionAttemptCompleted });

            return Result.Ok();
        }

        private async Task<Result> ProcessRequestAsync(CancellationToken token)
        {
            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.DetermineTransferTypeStarted });
            
            if (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }

            var buffer = new byte[_bufferSize];

            var determineTransferTypeResult = await DetermineTransferTypeAsync(buffer).ConfigureAwait(false);
            if (determineTransferTypeResult.Failure)
            {
                return determineTransferTypeResult;
            }

            var transferType = determineTransferTypeResult.Value;
            switch (transferType)
            {
                case RequestType.TextMessage:

                    EventOccurred?.Invoke(
                        new ServerEventInfo
                        {
                            EventType = ServerEventType.DetermineTransferTypeCompleted,
                            RequestType = RequestType.TextMessage
                        });

                    return ReceiveTextMessage(buffer, token);

                case RequestType.InboundFileTransfer:

                    EventOccurred?.Invoke(
                        new ServerEventInfo
                        {
                            EventType = ServerEventType.DetermineTransferTypeCompleted,
                            RequestType = RequestType.InboundFileTransfer
                        });

                    return await InboundFileTransferAsync(buffer, token).ConfigureAwait(false);

                case RequestType.OutboundFileTransfer:

                    EventOccurred?.Invoke(
                        new ServerEventInfo
                        {
                            EventType = ServerEventType.DetermineTransferTypeCompleted,
                            RequestType = RequestType.OutboundFileTransfer
                        });

                    return await OutboundFileTransferAsync(buffer, token).ConfigureAwait(false);

                case RequestType.GetFileList:

                    EventOccurred?.Invoke(
                        new ServerEventInfo
                        {
                            EventType = ServerEventType.DetermineTransferTypeCompleted,
                            RequestType = RequestType.GetFileList
                        });

                    return await SendFileList(buffer, token).ConfigureAwait(false);

                case RequestType.ReceiveFileList:

                    EventOccurred?.Invoke(
                        new ServerEventInfo
                        {
                            EventType = ServerEventType.DetermineTransferTypeCompleted,
                            RequestType = RequestType.ReceiveFileList
                        });

                    return ReceiveFileList(buffer, token);

                default:

                    var error = "Unable to determine transfer type, value of " + "'" + transferType + "' is invalid.";
                    return Result.Fail(error);
            }
        }

        private async Task<Result<RequestType>> DetermineTransferTypeAsync(byte[] buffer)
        {
            Result<int> receiveResult;
            int bytesReceived;

            try
            {
                receiveResult = 
                    await _transferSocket.ReceiveWithTimeoutAsync(buffer, 0, _bufferSize, 0, _receiveTimeoutMs)
                        .ConfigureAwait(false);

                bytesReceived = receiveResult.Value;
            }
            catch (SocketException ex)
            {
                return Result.Fail<RequestType>($"{ex.Message} ({ex.GetType()} raised in method TplSocketServer.DetermineTransferTypeAsync)");
            }
            catch (TimeoutException ex)
            {
                return Result.Fail<RequestType>($"{ex.Message} ({ex.GetType()} raised in method TplSocketServer.DetermineTransferTypeAsync)");
            }

            if (receiveResult.Failure)
            {
                return Result.Fail<RequestType>(receiveResult.Error);
            }

            if (bytesReceived == 0)
            {
                return Result.Fail<RequestType>("Error reading request from client, no data was received");
            }

            var transferType = MessageUnwrapper.DetermineTransferType(buffer).ToString();
            var transferTypeEnum = (RequestType)Enum.Parse(typeof(RequestType), transferType);

            return Result.Ok(transferTypeEnum);
        }

        private Result ReceiveTextMessage(byte[] buffer, CancellationToken token)
        {
            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ReceiveTextMessageStarted });

            var(message, 
                remoteIpAddress, 
                remotePortNumber) = MessageUnwrapper.ReadTextMessageRequest(buffer);

            if (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }

            EventOccurred?.Invoke(
                new ServerEventInfo
                {
                    EventType = ServerEventType.ReceiveTextMessageCompleted,
                    TextMessage = message,
                    RemoteServerIpAddress = remoteIpAddress,
                    RemoteServerPortNumber = remotePortNumber
                });

            return Result.Ok();
        }

        private async Task<Result> InboundFileTransferAsync(byte[] buffer, CancellationToken token)
        {
            EventOccurred?.Invoke(
                new ServerEventInfo { EventType = ServerEventType.ReceiveInboundFileTransferInfoStarted });

            var(localFilePath, 
                fileSizeBytes) = MessageUnwrapper.ReadInboundFileTransferRequest(buffer);

            if (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }

            var deleteFileResult = FileHelper.DeleteFileIfAlreadyExists(localFilePath);
            if (deleteFileResult.Failure)
            {
                return deleteFileResult;
            }

            EventOccurred?.Invoke(
                new ServerEventInfo
                {
                    EventType = ServerEventType.ReceiveInboundFileTransferInfoCompleted,
                    LocalFolder = Path.GetDirectoryName(localFilePath),
                    FileName = Path.GetFileName(localFilePath),
                    FileSizeInBytes = fileSizeBytes
                });

            var startTime = DateTime.Now;

            EventOccurred?.Invoke(new ServerEventInfo
            {
                EventType = ServerEventType.ReceiveFileBytesStarted,
                FileTransferStartTime = startTime            
            });

            var receiveFileResult = 
                await ReceiveFileAsync(localFilePath, fileSizeBytes, buffer, 0, _bufferSize, 0, _receiveTimeoutMs, token)
                    .ConfigureAwait(false);

            if (receiveFileResult.Failure)
            {
                return receiveFileResult;
            }

            EventOccurred?.Invoke(new ServerEventInfo
            {
                EventType = ServerEventType.ReceiveFileBytesCompleted,
                FileTransferStartTime = startTime,
                FileTransferCompleteTime = DateTime.Now,
                FileSizeInBytes = fileSizeBytes
            });

            //TODO: This is a hack to separate the file read and handshake steps to ensure that all data is read correcty by the client server. I know how to fix by keeping track of the buffer between steps.
            await Task.Delay(OneSecondInMilliseconds);

            EventOccurred?.Invoke(
                new ServerEventInfo
                {
                    EventType = ServerEventType.SendConfirmationMessageStarted,
                    ConfirmationMessage = ConfirmationMessage
                });

            var confirmationMessageData = Encoding.ASCII.GetBytes(ConfirmationMessage);

            var sendConfirmatinMessageResult = 
                await _transferSocket.SendWithTimeoutAsync(confirmationMessageData, 0, confirmationMessageData.Length, 0, _sendTimeoutMs)
                    .ConfigureAwait(false);

            if (sendConfirmatinMessageResult.Failure)
            {
                return sendConfirmatinMessageResult;
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.SendConfirmationMessageCompleted });

            return Result.Ok();
        }

        private async Task<Result> ReceiveFileAsync(
            string localFilePath,
            long fileSizeInBytes,
            byte[] buffer,
            int offset,
            int size,
            SocketFlags socketFlags,
            int receiveTimout,
            CancellationToken token)
        {
            long totalBytesReceived = 0;
            float percentComplete = 0;
            int receiveCount = 0;

            // Read file bytes from transfer socket until 
            //      1. the entire file has been received OR 
            //      2. Data is no longer being received OR
            //      3, Transfer is cancelled by server receiving the file
            while (true)
            {
                if (totalBytesReceived == fileSizeInBytes)
                {
                    percentComplete = 1;
                    EventOccurred?.Invoke(
                        new ServerEventInfo
                        {
                            EventType = ServerEventType.FileTransferProgress,
                            PercentComplete = percentComplete
                        });

                    return Result.Ok();
                }

                if (token.IsCancellationRequested)
                {
                    token.ThrowIfCancellationRequested();
                }

                int bytesReceived;
                try
                {
                    var receiveBytesResult = await _transferSocket
                                                 .ReceiveWithTimeoutAsync(
                                                     buffer,
                                                     offset,
                                                     size,
                                                     socketFlags,
                                                     receiveTimout).ConfigureAwait(false);

                    bytesReceived = receiveBytesResult.Value;
                }
                catch (SocketException ex)
                {
                    return Result.Fail($"{ex.Message} ({ex.GetType()} raised in method TplSocketServer.ReceiveFileAsync)");
                }
                catch (TimeoutException ex)
                {
                    return Result.Fail($"{ex.Message} ({ex.GetType()} raised in method TplSocketServer.ReceiveFileAsync)");
                }

                if (bytesReceived == 0)
                {
                    return Result.Fail("Socket is no longer receiving data, must abort file transfer");
                }

                totalBytesReceived += bytesReceived;
                var writeBytesResult = FileHelper.WriteBytesToFile(localFilePath, buffer, bytesReceived);

                if (writeBytesResult.Failure)
                {
                    return writeBytesResult;
                }

                // THese two lines and the event raised below are useful when debugging socket errors
                receiveCount++;
                long bytesRemaining = fileSizeInBytes - totalBytesReceived;

                //EventOccurred?.Invoke(new ServerEventInfo
                //{
                //    EventType = ServerEventType.ReceivedDataFromSocket,
                //    ReceiveBytesCount = receiveBytesCount,
                //    CurrentBytesReceivedFromSocket = bytesReceived,
                //    TotalBytesReceivedFromSocket = totalBytesReceived,
                //    FileSizeInBytes = fileSizeInBytes,
                //    BytesRemainingInFile = bytesRemaining
                //});

                var checkPercentComplete = totalBytesReceived / (float)fileSizeInBytes;
                var changeSinceLastUpdate = checkPercentComplete - percentComplete;

                // Report progress only if at least 1% of file has been received since the last update
                if (changeSinceLastUpdate > (float).01)
                {
                    percentComplete = checkPercentComplete;
                    EventOccurred?.Invoke(
                        new ServerEventInfo
                        {
                            EventType = ServerEventType.FileTransferProgress,
                            PercentComplete = percentComplete
                        });
                }
            }           
        }

        private async Task<Result> OutboundFileTransferAsync(byte[] buffer, CancellationToken token)
        {
            EventOccurred?.Invoke(
                new ServerEventInfo { EventType = ServerEventType.ReceiveOutboundFileTransferInfoStarted });

            var(requestedFilePath, 
                remoteServerIpAddress, 
                remoteServerPort, 
                remoteFolderPath) = MessageUnwrapper.ReadOutboundFileTransferRequest(buffer);

            EventOccurred?.Invoke(
                new ServerEventInfo
                {
                    EventType = ServerEventType.ReceiveOutboundFileTransferInfoCompleted,
                    LocalFolder = Path.GetDirectoryName(requestedFilePath),
                    FileName = Path.GetFileName(requestedFilePath),
                    FileSizeInBytes = new FileInfo(requestedFilePath).Length,
                    RemoteFolder = remoteFolderPath,
                    RemoteServerIpAddress = remoteServerIpAddress,
                    RemoteServerPortNumber = remoteServerPort
                });

            if (!File.Exists(requestedFilePath))
            {
                return Result.Fail("File does not exist: " + requestedFilePath);
            }

            if (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }

            return await
                SendFileAsync(remoteServerIpAddress, remoteServerPort, requestedFilePath, remoteFolderPath, token)
                    .ConfigureAwait(false);
        }

        private async Task<Result> SendFileList(byte[] buffer, CancellationToken token)
        {
            EventOccurred?.Invoke(new ServerEventInfo{ EventType = ServerEventType.ReceiveFileListRequestStarted});

            (string remoteIpAddress, 
                int remotePortNumber) = MessageUnwrapper.ReadFileListRequest(buffer);

            List<string> listOfFiles;
            try
            {
                listOfFiles = Directory.GetFiles(_transferFolderPath).ToList();
            }
            catch (IOException ex)
            {
                return Result.Fail($"{ex.Message} ({ex.GetType()})");
            }

            if (!listOfFiles.Any())
            {
                var message = $"The requested folder is empty ({_transferFolderPath})";

                var sendResult = await SendTextMessageAsync(
                        message, 
                        remoteIpAddress, 
                        remotePortNumber, 
                        _localIpAddress,
                        _localPort, 
                        token)
                        .ConfigureAwait(false);

                return Result.Fail(sendResult.Success 
                    ? message 
                    : sendResult.Error);
            }

            if (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }

            var fileInfoList = new List<(string, long)>();
            foreach (var file in listOfFiles)
            {
                var fileSize = new FileInfo(file).Length;
                fileInfoList.Add((file, fileSize));
            }

            EventOccurred?.Invoke(
                new ServerEventInfo
                {
                    EventType = ServerEventType.ReceiveFileListRequestCompleted,
                    RemoteServerIpAddress = remoteIpAddress,
                    RemoteServerPortNumber = remotePortNumber
                });

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ConnectToRemoteServerStarted });

            var transferSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            var connectResult =
                await transferSocket.ConnectWithTimeoutAsync(remoteIpAddress, remotePortNumber, _connectTimeoutMs)
                    .ConfigureAwait(false);

            if (connectResult.Failure)
            {
                return connectResult;
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ConnectToRemoteServerCompleted });

            EventOccurred?.Invoke(new ServerEventInfo
            {
                EventType = ServerEventType.SendFileListResponseStarted,
                RemoteServerIpAddress = remoteIpAddress,
                RemoteServerPortNumber = remotePortNumber,
                FileInfoList = fileInfoList
            });

            var fileListData =
                MessageWrapper.ConstructFileListResponse(
                    fileInfoList,
                    '*',
                    '?',
                    remoteIpAddress,
                    remotePortNumber);

            var sendRequest =
                await transferSocket.SendWithTimeoutAsync(fileListData, 0, fileListData.Length, 0, _sendTimeoutMs)
                    .ConfigureAwait(false);

            if (sendRequest.Failure)
            {
                return sendRequest;
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.SendFileListResponseCompleted });

            transferSocket.Shutdown(SocketShutdown.Both);
            transferSocket.Close();

            return Result.Ok();
        }

        private Result ReceiveFileList(byte[] buffer, CancellationToken token)
        {
            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ReceiveFileListResponseStarted });

            var(remoteServerIp, 
                remoteServerPort,
                fileInfoList) = MessageUnwrapper.ReadFileListResponse(buffer);

            if (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }

            EventOccurred?.Invoke(
                new ServerEventInfo
                {
                    EventType = ServerEventType.ReceiveFileListResponseCompleted,
                    RemoteServerIpAddress = remoteServerIp,
                    RemoteServerPortNumber = remoteServerPort,
                    FileInfoList = fileInfoList
                });

            return Result.Ok();
        }

        public async Task<Result> SendTextMessageAsync(
            string message, 
            string remoteServerIpAddress, 
            int remoteServerPort, 
            string localIpAddress, 
            int localPort, 
            CancellationToken token)
        {
            if (string.IsNullOrEmpty(message))
            {
                return Result.Fail("Message is null or empty string.");
            }

            if (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }

            var transferSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ConnectToRemoteServerStarted });

            var connectResult =
                await transferSocket.ConnectWithTimeoutAsync(remoteServerIpAddress, remoteServerPort, _connectTimeoutMs)
                    .ConfigureAwait(false);

            if (connectResult.Failure)
            {
                return connectResult;
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ConnectToRemoteServerCompleted });
            EventOccurred?.Invoke(
                new ServerEventInfo
                {
                    EventType = ServerEventType.SendTextMessageStarted,
                    TextMessage = message,
                    RemoteServerIpAddress = remoteServerIpAddress,
                    RemoteServerPortNumber = remoteServerPort
                });

            var messageWrapperAndData = MessageWrapper.ConstuctTextMessageRequest(message, localIpAddress, localPort);

            var sendMessageResult = 
                await transferSocket.SendWithTimeoutAsync(messageWrapperAndData, 0, messageWrapperAndData.Length, 0, _sendTimeoutMs)
                    .ConfigureAwait(false);

            if (sendMessageResult.Failure)
            {
                return sendMessageResult;
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.SendTextMessageCompleted });

            transferSocket.Shutdown(SocketShutdown.Both);
            transferSocket.Close();

            return Result.Ok();
        }
        
        public async Task<Result> SendFileAsync(string remoteServerIpAddress, int remoteServerPort, string localFilePath, string remoteFolderPath, CancellationToken token)
        {
            if (!File.Exists(localFilePath))
            {
                return Result.Fail("File does not exist: " + localFilePath);
            }

            if (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }

            var transferSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ConnectToRemoteServerStarted });

            var connectResult =
                await transferSocket.ConnectWithTimeoutAsync(remoteServerIpAddress, remoteServerPort, _connectTimeoutMs)
                    .ConfigureAwait(false);

            if (connectResult.Failure)
            {
                return connectResult;
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ConnectToRemoteServerCompleted });

            var fileSizeBytes = new FileInfo(localFilePath).Length;

            var messageWrapper = 
                MessageWrapper.ConstructOutboundFileTransferRequest(localFilePath, fileSizeBytes, remoteFolderPath);

            EventOccurred?.Invoke(
                new ServerEventInfo
                    {
                        EventType = ServerEventType.SendOutboundFileTransferInfoStarted,
                        LocalFolder = Path.GetDirectoryName(localFilePath),
                        FileName = Path.GetFileName(localFilePath),
                        FileSizeInBytes = fileSizeBytes,
                        RemoteServerIpAddress = remoteServerIpAddress,
                        RemoteServerPortNumber = remoteServerPort,
                        RemoteFolder = remoteFolderPath
                    });

            var sendRequest = 
                await transferSocket.SendWithTimeoutAsync(messageWrapper, 0, messageWrapper.Length, 0, _sendTimeoutMs)
                    .ConfigureAwait(false);

            if (sendRequest.Failure)
            {
                return sendRequest;
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.SendOutboundFileTransferInfoCompleted });

            //TODO: This is a hack to separate the transfer request and file transfer steps to ensure that all data is read correcty by the client server. I know how to fix by keeping track of the buffer between steps.
            await Task.Delay(OneSecondInMilliseconds);

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.SendFileBytesStarted });

            var sendFileResult = await transferSocket.SendFileAsync(localFilePath).ConfigureAwait(false);
            if (sendFileResult.Failure)
            {
                return sendFileResult;
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.SendFileBytesCompleted });

            var receiveConfirationMessageResult = await ReceiveConfirmationAsync(transferSocket).ConfigureAwait(false);
            if (receiveConfirationMessageResult.Failure)
            {
                return receiveConfirationMessageResult;
            }
            
            transferSocket.Shutdown(SocketShutdown.Both);
            transferSocket.Close();

            return Result.Ok();
        }

        async Task<Result> ReceiveConfirmationAsync(Socket transferSocket)
        {
            var buffer = new byte[_bufferSize];
            Result<int> receiveMessageResult;
            int bytesReceived;

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ReceiveConfirmationMessageStarted });

            try
            {
                receiveMessageResult = await transferSocket.ReceiveAsync(buffer, 0, _bufferSize, 0).ConfigureAwait(false);
                bytesReceived = receiveMessageResult.Value;
            }
            catch (SocketException ex)
            {
                return Result.Fail($"{ex.Message} ({ex.GetType()} raised in method TplSocketServer.ReceiveConfirmationAsync)");
            }
            catch (TimeoutException ex)
            {
                return Result.Fail($"{ex.Message} ({ex.GetType()} raised in method TplSocketServer.ReceiveConfirmationAsync)");
            }

            if (receiveMessageResult.Failure || bytesReceived == 0)
            {
                return Result.Fail("Error receiving confirmation message from remote server");
            }

            var confirmationMessage = Encoding.ASCII.GetString(buffer, 0, bytesReceived);

            if (confirmationMessage != ConfirmationMessage)
            {
                return Result.Fail($"Confirmation message doesn't match:\n\tExpected:\t{ConfirmationMessage}\n\tActual:\t{confirmationMessage}");
            }

            EventOccurred?.Invoke(
                new ServerEventInfo
                {
                    EventType = ServerEventType.ReceiveConfirmationMessageCompleted,
                    ConfirmationMessage = confirmationMessage
                });

            return Result.Ok();
        }

        public async Task<Result> GetFileAsync(
            string remoteServerIpAddress,
            int remoteServerPort,
            string remoteFilePath,
            string localIpAddress,
            int localPort,
            string localFolderPath,
            CancellationToken token)
        {
            if (!_listenSocket.IsBound)
            {
                return Result.Fail("Server's listening port is unbound, cannot accept inbound file transfers");
            }

            if (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }

            var transferSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ConnectToRemoteServerStarted });

            var connectResult = 
                await transferSocket.ConnectWithTimeoutAsync(remoteServerIpAddress, remoteServerPort, _connectTimeoutMs)
                    .ConfigureAwait(false);

            if (connectResult.Failure)
            {
                return connectResult;
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ConnectToRemoteServerCompleted });

            var messageHeaderData = 
                MessageWrapper.ConstructInboundFileTransferRequest(
                    remoteFilePath,
                    localIpAddress,
                    localPort,
                    localFolderPath);

            EventOccurred?.Invoke(
                new ServerEventInfo
                    {
                        EventType = ServerEventType.SendInboundFileTransferInfoStarted,
                        RemoteServerIpAddress = remoteServerIpAddress,
                        RemoteServerPortNumber = remoteServerPort,
                        RemoteFolder = Path.GetDirectoryName(remoteFilePath),
                        FileName = Path.GetFileName(remoteFilePath),
                        LocalFolder = localFolderPath,
                    });

            var requestResult = 
                await transferSocket.SendWithTimeoutAsync(messageHeaderData, 0, messageHeaderData.Length, 0, _sendTimeoutMs)
                    .ConfigureAwait(false);

            if (requestResult.Failure)
            {
                return requestResult;
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.SendInboundFileTransferInfoCompleted });

            transferSocket.Shutdown(SocketShutdown.Both);
            transferSocket.Close();
            
            return Result.Ok();
        }

        public async Task<Result> RequestFileListAsync(
            string remoteServerIpAddress,
            int remoteServerPort,
            string localIpAddress,
            int localPort,
            CancellationToken token)
        {
            if (!_listenSocket.IsBound)
            {
                return Result.Fail("Server's listening port is unbound, cannot accept inbound file transfers");
            }

            if (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }

            var transferSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ConnectToRemoteServerStarted });

            var connectResult =
                await transferSocket.ConnectWithTimeoutAsync(remoteServerIpAddress, remoteServerPort, _connectTimeoutMs)
                    .ConfigureAwait(false);

            if (connectResult.Failure)
            {
                return connectResult;
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ConnectToRemoteServerCompleted });

            var requestData =
                MessageWrapper.ConstructFileListRequest(localIpAddress, localPort);

            EventOccurred?.Invoke(
                new ServerEventInfo
                {
                    EventType = ServerEventType.SendFileListRequestStarted,
                    RemoteServerIpAddress = localIpAddress,
                    RemoteServerPortNumber = localPort
                });

            var requestResult =
                await transferSocket.SendWithTimeoutAsync(requestData, 0, requestData.Length, 0, _sendTimeoutMs)
                    .ConfigureAwait(false);

            if (requestResult.Failure)
            {
                return requestResult;
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.SendFileListRequestCompleted });

            transferSocket.Shutdown(SocketShutdown.Both);
            transferSocket.Close();

            return Result.Ok();
        }

        public Result CloseListenSocket()
        {
            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ShutdownListenSocketStarted });

            try
            {
                _listenSocket.Shutdown(SocketShutdown.Both);
                _listenSocket.Close();
            }
            catch (SocketException ex)
            {
                EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ShutdownListenSocketCompleted });
                return Result.Ok($"{ex.Message} ({ex.GetType()} raised in method TplSocketServer.CloseListenSocket)");
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ShutdownListenSocketCompleted });
            return Result.Ok();
        }
    }
}