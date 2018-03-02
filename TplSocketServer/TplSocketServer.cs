﻿using System.Collections.Generic;
using System.Linq;
using AaronLuna.Common.Network;

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
        const string FileAlreadyExists = "A file with the same name already exists in the download folder, please rename or remove this file in order to proceed.";
        const string EmptyTransferFolderErrorMessage = "Currently there are no files available in transfer folder";

        const int OneSecondInMilliseconds = 1000;

        readonly int _maxConnections;
        readonly int _bufferSize;

        readonly int _connectTimeoutMs;
        readonly int _receiveTimeoutMs;
        readonly int _sendTimeoutMs;
        
        readonly string _transferFolderPath;

        readonly string _localIpAddress;
        int _localPort;

        Socket _listenSocket;
        Socket _transferSocket;

        public TplSocketServer()
        {
            _transferFolderPath = $"{Directory.GetCurrentDirectory()}{Path.DirectorySeparatorChar}transfer";

            _maxConnections = 5;
            _bufferSize = 1024;
            _connectTimeoutMs = 5000;
            _receiveTimeoutMs = 5000;
            _sendTimeoutMs = 5000;

            _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }

        public TplSocketServer(AppSettings appSettings, IPAddress localIpAddress)
        {
            _localIpAddress = localIpAddress.ToString();
            _transferFolderPath = appSettings.TransferFolderPath;

            _maxConnections = appSettings.SocketSettings.MaxNumberOfConections;
            _bufferSize = appSettings.SocketSettings.BufferSize;
            _connectTimeoutMs = appSettings.SocketSettings.ConnectTimeoutMs;
            _receiveTimeoutMs = appSettings.SocketSettings.ReceiveTimeoutMs;
            _sendTimeoutMs = appSettings.SocketSettings.SendTimeoutMs;

            _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }

        public string LocalEndPoint => _listenSocket.IsBound
            ? _listenSocket.LocalEndPoint.ToString()
            : string.Empty;

        public event ServerEventDelegate EventOccurred;

        public async Task<Result> HandleIncomingConnectionsAsync(int localPort, CancellationToken token)
        {
            _localPort = localPort;

            return (await Task.Factory.StartNew(() => Listen(localPort), token).ConfigureAwait(false))
                .OnSuccess(() => WaitForConnectionsAsync(token));
        }

        private Result Listen(int localPort)
        { 
            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ListenOnLocalPortStarted });
            
            var ipEndPoint = new IPEndPoint(IPAddress.Any, localPort);            
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
                if (acceptConnectionResult.Failure)
                {
                    EventOccurred?.Invoke(new ServerEventInfo
                    {
                        EventType = ServerEventType.ErrorOccurred,
                        ErrorMessage = acceptConnectionResult.Error
                    });

                    return acceptConnectionResult;
                }

                var requestResult = await ProcessRequestAsync(token).ConfigureAwait(false);
                if (requestResult.Success)
                {
                    _transferSocket.Shutdown(SocketShutdown.Both);
                    _transferSocket.Close();
                    continue;
                }

                EventOccurred?.Invoke(new ServerEventInfo
                {
                    EventType = ServerEventType.ErrorOccurred,
                    ErrorMessage = requestResult.Error
                });

                return requestResult;
            }
        }

        private async Task<Result> AcceptNextConnection(CancellationToken token)
        {
            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.AcceptConnectionAttemptStarted });

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

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.AcceptConnectionAttemptCompleted });
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

                case RequestType.TransferFolderPathRequest:

                    EventOccurred?.Invoke(
                        new ServerEventInfo
                        {
                            EventType = ServerEventType.DetermineTransferTypeCompleted,
                            RequestType = RequestType.TransferFolderPathRequest
                        });

                    return await SendTransferFolderResponseAsync(buffer, token).ConfigureAwait(false);

                case RequestType.TransferFolderPathResponse:

                    EventOccurred?.Invoke(
                        new ServerEventInfo
                        {
                            EventType = ServerEventType.DetermineTransferTypeCompleted,
                            RequestType = RequestType.TransferFolderPathResponse
                        });

                    return ReceiveTransferFolderResponse(buffer, token);
                    
                case RequestType.PublicIpAddressRequest:

                    EventOccurred?.Invoke(
                        new ServerEventInfo
                        {
                            EventType = ServerEventType.DetermineTransferTypeCompleted,
                            RequestType = RequestType.PublicIpAddressRequest
                        });

                    return await SendPublicIpAddress(buffer, token).ConfigureAwait(false);

                case RequestType.PublicIpAddressResponse:

                    EventOccurred?.Invoke(
                        new ServerEventInfo
                        {
                            EventType = ServerEventType.DetermineTransferTypeCompleted,
                            RequestType = RequestType.PublicIpAddressResponse
                        });

                    return ReceivePublicIpAddress(buffer, token);

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
                remotePortNumber) = MessageUnwrapper.ReadTextMessage(buffer);

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
                fileSizeBytes,
                remoteIpAddress,
                remotePort) = MessageUnwrapper.ReadInboundFileTransferRequest(buffer);

            if (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }

            EventOccurred?.Invoke(
                new ServerEventInfo
                {
                    EventType = ServerEventType.ReceiveInboundFileTransferInfoCompleted,
                    LocalFolder = Path.GetDirectoryName(localFilePath),
                    FileName = Path.GetFileName(localFilePath),
                    FileSizeInBytes = fileSizeBytes,
                    RemoteServerIpAddress = remoteIpAddress,
                    RemoteServerPortNumber = remotePort
                });

            if (File.Exists(localFilePath))
            {
                var message = $"{FileAlreadyExists} ({localFilePath})";

                var sendResult = await SendTextMessageAsync(
                        message,
                        remoteIpAddress,
                        remotePort,
                        _localIpAddress,
                        _localPort,
                        token)
                    .ConfigureAwait(false);
                
                return sendResult.Success ? Result.Ok() : sendResult;
            }

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
            var receiveCount = 0;

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
                var bytesRemaining = fileSizeInBytes - totalBytesReceived;

                EventOccurred?.Invoke(new ServerEventInfo
                {
                    EventType = ServerEventType.ReceivedDataFromSocket,
                    ReceiveBytesCount = receiveCount,
                    CurrentBytesReceivedFromSocket = bytesReceived,
                    TotalBytesReceivedFromSocket = totalBytesReceived,
                    FileSizeInBytes = fileSizeInBytes,
                    BytesRemainingInFile = bytesRemaining
                });

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

            (string requestorIpAddress, 
                int requestorPortNumber,
                string targetFolderPath) = MessageUnwrapper.ReadFileListRequest(buffer);

            EventOccurred?.Invoke(
                new ServerEventInfo
                {
                    EventType = ServerEventType.ReceiveFileListRequestCompleted,
                    RemoteServerIpAddress = requestorIpAddress,
                    RemoteServerPortNumber = requestorPortNumber,
                    RemoteFolder = targetFolderPath
                });

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
                EventOccurred?.Invoke(new ServerEventInfo
                {
                    EventType = ServerEventType.SendFileListResponseStarted,
                    RemoteServerIpAddress = _localIpAddress,
                    RemoteServerPortNumber = _localPort,
                    FileInfoList = new List<(string, long)>(),
                    LocalFolder = targetFolderPath,
                });

                var message = $"{EmptyTransferFolderErrorMessage}: {_transferFolderPath}";

                var sendResult = await SendTextMessageAsync(
                        message, 
                        requestorIpAddress, 
                        requestorPortNumber, 
                        _localIpAddress,
                        _localPort, 
                        token)
                        .ConfigureAwait(false);

                EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.SendFileListResponseCompleted });

                return sendResult.Success ? Result.Ok() : sendResult;
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
            
            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ConnectToRemoteServerStarted });

            var transferSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            var connectResult =
                await transferSocket.ConnectWithTimeoutAsync(requestorIpAddress, requestorPortNumber, _connectTimeoutMs)
                    .ConfigureAwait(false);

            if (connectResult.Failure)
            {
                return connectResult;
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ConnectToRemoteServerCompleted });

            EventOccurred?.Invoke(new ServerEventInfo
            {
                EventType = ServerEventType.SendFileListResponseStarted,
                RemoteServerIpAddress = _localIpAddress,
                RemoteServerPortNumber = _localPort,
                FileInfoList = fileInfoList,
                LocalFolder = targetFolderPath,
            });

            var fileListData =
                MessageWrapper.ConstructFileListResponse(
                    fileInfoList,
                    "*",
                    "|",
                    _localIpAddress,
                    _localPort,
                    requestorIpAddress,
                    requestorPortNumber,
                    targetFolderPath);

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
                localIp,
                localPort,
                transferFolder,
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
                    LocalIpAddress = localIp,
                    LocalPortNumber = localPort,
                    LocalFolder = transferFolder,
                    FileInfoList = fileInfoList
                });

            return Result.Ok();
        }

        private async Task<Result> SendTransferFolderResponseAsync(byte[] buffer, CancellationToken token)
        {
            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ReceiveTransferFolderRequestStarted });

            (string requestorIpAddress,
                int requestorPortNumber) = MessageUnwrapper.ReadTransferFolderRequest(buffer);

            EventOccurred?.Invoke(
                new ServerEventInfo
                {
                    EventType = ServerEventType.ReceiveTransferFolderRequestCompleted,
                    RemoteServerIpAddress = requestorIpAddress,
                    RemoteServerPortNumber = requestorPortNumber
                });

            if (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }
            
            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ConnectToRemoteServerStarted });

            var transferSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            var connectResult =
                await transferSocket.ConnectWithTimeoutAsync(requestorIpAddress, requestorPortNumber, _connectTimeoutMs)
                    .ConfigureAwait(false);

            if (connectResult.Failure)
            {
                return connectResult;
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ConnectToRemoteServerCompleted });

            EventOccurred?.Invoke(new ServerEventInfo
            {
                EventType = ServerEventType.SendTransferFolderResponseStarted,
                RemoteServerIpAddress = requestorIpAddress,
                RemoteServerPortNumber = requestorPortNumber,
                LocalFolder = _transferFolderPath
            });

            var transferFolderData =
                MessageWrapper.ConstructTransferFolderResponse(
                    _localIpAddress,
                    _localPort,
                    _transferFolderPath);

            var sendRequest =
                await transferSocket.SendWithTimeoutAsync(transferFolderData, 0, transferFolderData.Length, 0, _sendTimeoutMs)
                    .ConfigureAwait(false);

            if (sendRequest.Failure)
            {
                return sendRequest;
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.SendTransferFolderRequestCompleted });

            transferSocket.Shutdown(SocketShutdown.Both);
            transferSocket.Close();

            return Result.Ok();
        }

        private Result ReceiveTransferFolderResponse(byte[] buffer, CancellationToken token)
        {
            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ReceiveTransferFolderResponseStarted });

            var (remoteServerIp,
                remoteServerPort,
                transferFolder) = MessageUnwrapper.ReadTransferFolderResponse(buffer);

            if (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }

            EventOccurred?.Invoke(
                new ServerEventInfo
                {
                    EventType = ServerEventType.ReceiveTransferFolderResponseCompleted,
                    RemoteServerIpAddress = remoteServerIp,
                    RemoteServerPortNumber = remoteServerPort,
                    RemoteFolder = transferFolder
                });

            return Result.Ok();
        }

        private async Task<Result> SendPublicIpAddress(byte[] buffer, CancellationToken token)
        {
            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ReceiveTransferFolderRequestStarted });

            (string requestorIpAddress,
                int requestorPortNumber) = MessageUnwrapper.ReadPublicIpAddressRequest(buffer);

            EventOccurred?.Invoke(
                new ServerEventInfo
                {
                    EventType = ServerEventType.ReceivePublicIpRequestCompleted,
                    RemoteServerIpAddress = requestorIpAddress,
                    RemoteServerPortNumber = requestorPortNumber
                });

            if (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ConnectToRemoteServerStarted });

            var transferSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            var connectResult =
                await transferSocket.ConnectWithTimeoutAsync(requestorIpAddress, requestorPortNumber, _connectTimeoutMs)
                    .ConfigureAwait(false);

            if (connectResult.Failure)
            {
                return connectResult;
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ConnectToRemoteServerCompleted });

            var publicIp = IPAddress.None.ToString();
            var publicIpResult = await IpAddressHelper.GetPublicIPv4AddressAsync().ConfigureAwait(false);
            if (publicIpResult.Success)
            {
                publicIp = publicIpResult.Value.ToString();
            }

            EventOccurred?.Invoke(new ServerEventInfo
            {
                EventType = ServerEventType.SendPublicIpResponseStarted,
                RemoteServerIpAddress = _localIpAddress,
                RemoteServerPortNumber = _localPort,
                PublicIpAddress = publicIp
            });

            var publicIpData =
                MessageWrapper.ConstructPublicIpAddressResponse(
                    _localIpAddress,
                    _localPort,
                    publicIp);

            var sendRequest =
                await transferSocket.SendWithTimeoutAsync(publicIpData, 0, publicIpData.Length, 0, _sendTimeoutMs)
                    .ConfigureAwait(false);

            if (sendRequest.Failure)
            {
                return sendRequest;
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.SendPublicIpResponseCompleted });

            transferSocket.Shutdown(SocketShutdown.Both);
            transferSocket.Close();

            return Result.Ok();
        }

        private Result ReceivePublicIpAddress(byte[] buffer, CancellationToken token)
        {
            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.ReceivePublicIpResponseStarted });

            var (remoteServerIp,
                remoteServerPort,
                publicIpAddress) = MessageUnwrapper.ReadPublicIpAddressResponse(buffer);

            if (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }

            EventOccurred?.Invoke(
                new ServerEventInfo
                {
                    EventType = ServerEventType.ReceivePublicIpResponseCompleted,
                    RemoteServerIpAddress = remoteServerIp,
                    RemoteServerPortNumber = remoteServerPort,
                    PublicIpAddress = publicIpAddress
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
                MessageWrapper.ConstructOutboundFileTransferRequest(localFilePath, fileSizeBytes, _localIpAddress, _localPort, remoteFolderPath);

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
            string targetFolder,
            CancellationToken token)
        {
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
                MessageWrapper.ConstructFileListRequest(localIpAddress, localPort, targetFolder);

            EventOccurred?.Invoke(
                new ServerEventInfo
                {
                    EventType = ServerEventType.SendFileListRequestStarted,
                    RemoteServerIpAddress = remoteServerIpAddress,
                    RemoteServerPortNumber = remoteServerPort,
                    RemoteFolder = targetFolder
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

        public async Task<Result> RequestTransferFolderPath(
            string remoteServerIpAddress,
            int remoteServerPort,
            string localIpAddress,
            int localPort,
            CancellationToken token)
        {
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
                MessageWrapper.ConstructTransferFolderRequest(localIpAddress, localPort);

            EventOccurred?.Invoke(
                new ServerEventInfo
                {
                    EventType = ServerEventType.SendTransferFolderRequestStarted,
                    RemoteServerIpAddress = remoteServerIpAddress,
                    RemoteServerPortNumber = remoteServerPort
                });

            var requestResult =
                await transferSocket.SendWithTimeoutAsync(requestData, 0, requestData.Length, 0, _sendTimeoutMs)
                    .ConfigureAwait(false);

            if (requestResult.Failure)
            {
                return requestResult;
            }

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.SendTransferFolderRequestCompleted });

            transferSocket.Shutdown(SocketShutdown.Both);
            transferSocket.Close();

            return Result.Ok();
        }

        public async Task<Result> RequestPublicIp(
            string remoteServerIpAddress,
            int remoteServerPort,
            string localIpAddress,
            int localPort,
            CancellationToken token)
        {
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
                MessageWrapper.ConstructPublicIpAddressRequest(localIpAddress, localPort);

            EventOccurred?.Invoke(
                new ServerEventInfo
                {
                    EventType = ServerEventType.SendPublicIpRequestStarted,
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

            EventOccurred?.Invoke(new ServerEventInfo { EventType = ServerEventType.SendPublicIpRequestCompleted });

            transferSocket.Shutdown(SocketShutdown.Both);
            transferSocket.Close();

            return Result.Ok();
        }

        public void RemoveAllSubscribers()
        {
            EventOccurred = null;
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