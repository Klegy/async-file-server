﻿namespace TplSockets
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using AaronLuna.Common.IO;
    using AaronLuna.Common.Logging;
    using AaronLuna.Common.Network;
    using AaronLuna.Common.Result;

    public class TplSocketServer
    {
        const string ConfirmationMessage = "handshake";
        const string NotInitializedMessage =
            "Server is unitialized and cannot handle incoming connections";

        int _initialized;
        int _idle;
        int _messageId;
        int _remoteServerAcceptedFileTransfer;
        int _remoteServerRejectedFileTransfer;
        int _transferInProgress;
        int _inboundFileTransferIsStalled;
        int _outboundFileTransferIsStalled;
        int _shutdownInitiated;
        int _shutdownComplete;
        int _outboundFileTransferMessageId;
        int _inboundFileTransferMessageId;
        int _retryStalledFileTransfer;
        readonly Logger _log = new Logger(typeof(TplSocketServer));
        readonly List<ServerEvent> _eventLog;
        readonly ServerState _state;
        readonly Socket _listenSocket;
        Socket _transferSocket;
        Socket _clientSocket;
        IPAddress _ipAddressLastConnection;
        CancellationToken _token;
        static readonly object File = new object();

        public bool Initialized
        {
            get => (Interlocked.CompareExchange(ref _initialized, 1, 1) == 1);
            set
            {
                if (value) Interlocked.CompareExchange(ref _initialized, 1, 0);
                else Interlocked.CompareExchange(ref _initialized, 0, 1);
            }
        }

        public bool IsListening
        {
            get => (Interlocked.CompareExchange(ref _shutdownComplete, 1, 1) == 1);
            set
            {
                if (value) Interlocked.CompareExchange(ref _shutdownComplete, 1, 0);
                else Interlocked.CompareExchange(ref _shutdownComplete, 0, 1);
            }
        }

        public bool IsIdle
        {
            get => Interlocked.CompareExchange(ref _idle, 1, 1) == 1;
            set
            {
                if (value) Interlocked.CompareExchange(ref _idle, 1, 0);
                else Interlocked.CompareExchange(ref _idle, 0, 1);
            }
        }

        bool RemoteServerAcceptedFileTransfer
        {
            get => Interlocked.CompareExchange(ref _remoteServerAcceptedFileTransfer, 1, 1) == 1;
            set
            {
                if (value) Interlocked.CompareExchange(ref _remoteServerAcceptedFileTransfer, 1, 0);
                else Interlocked.CompareExchange(ref _remoteServerAcceptedFileTransfer, 0, 1);
            }
        }

        bool RemoteServerRejectedFileTransfer
        {
            get => Interlocked.CompareExchange(ref _remoteServerRejectedFileTransfer, 1, 1) == 1;
            set
            {
                if (value) Interlocked.CompareExchange(ref _remoteServerRejectedFileTransfer, 1, 0);
                else Interlocked.CompareExchange(ref _remoteServerRejectedFileTransfer, 0, 1);
            }
        }

        bool TransferInProgress
        {
            get => Interlocked.CompareExchange(ref _transferInProgress, 1, 1) == 1;
            set
            {
                if (value) Interlocked.CompareExchange(ref _transferInProgress, 1, 0);
                else Interlocked.CompareExchange(ref _transferInProgress, 0, 1);
            }
        }

        bool InboundFileTransferIsStalled
        {
            get => Interlocked.CompareExchange(ref _inboundFileTransferIsStalled, 1, 1) == 1;
            set
            {
                if (value) Interlocked.CompareExchange(ref _inboundFileTransferIsStalled, 1, 0);
                else Interlocked.CompareExchange(ref _inboundFileTransferIsStalled, 0, 1);
            }
        }

        bool OutboundFileTransferIsStalled
        {
            get => Interlocked.CompareExchange(ref _outboundFileTransferIsStalled, 1, 1) == 1;
            set
            {
                if (value) Interlocked.CompareExchange(ref _outboundFileTransferIsStalled, 1, 0);
                else Interlocked.CompareExchange(ref _outboundFileTransferIsStalled, 0, 1);
            }
        }

        bool RetryPreviousFileTransfer
        {
            get => Interlocked.CompareExchange(ref _retryStalledFileTransfer, 1, 1) == 1;
            set
            {
                if (value) Interlocked.CompareExchange(ref _retryStalledFileTransfer, 1, 0);
                else Interlocked.CompareExchange(ref _retryStalledFileTransfer, 0, 1);
            }
        }

        bool ShutdownInitiated
        {
            get => Interlocked.CompareExchange(ref _shutdownInitiated, 1, 1) == 1;
            set
            {
                if (value) Interlocked.CompareExchange(ref _shutdownInitiated, 1, 0);
                else Interlocked.CompareExchange(ref _shutdownInitiated, 0, 1);
            }
        }

        public TplSocketServer()
        {
            Initialized = false;
            IsListening = false;
            IsIdle = true;
            InboundFileTransferIsStalled = false;
            OutboundFileTransferIsStalled = false;
            RetryPreviousFileTransfer = false;
            ShutdownInitiated = false;
            Queue = new List<Message>();
            Archive = new List<Message>();

            SocketSettings = new SocketSettings
            {
                ListenBacklogSize = 5,
                BufferSize = 1024,
                ConnectTimeoutMs = 5000,
                ReceiveTimeoutMs = 5000,
                SendTimeoutMs = 5000
            };

            Info = new ServerInfo()
            {
                TransferFolder = GetDefaultTransferFolder()
            };

            RemoteServerInfo = new ServerInfo();
            RemoteServerFileList = new List<(string filePath, long fileSize)>();
            
            _state = new ServerState();
            _messageId = 1;
            _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _transferSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _eventLog = new List<ServerEvent>();
        }

        public List<Message> Queue { get; set; }
        public List<Message> Archive { get; set; }
        public bool QueueIsEmpty => Queue.Count == 0;

        public float TransferUpdateInterval { get; set; }
        public SocketSettings SocketSettings { get; set; }
        public int ListenBacklogSize => SocketSettings.ListenBacklogSize;
        public int BufferSize => SocketSettings.BufferSize;
        public int ConnectTimeoutMs => SocketSettings.ConnectTimeoutMs;
        public int ReceiveTimeoutMs => SocketSettings.ReceiveTimeoutMs;
        public int SendTimeoutMs => SocketSettings.SendTimeoutMs;

        public PlatformID OperatingSystem => Environment.OSVersion.Platform;
        public ServerInfo Info { get; set; }

        public string MyTransferFolderPath
        {
            get => Info.TransferFolder;
            set => Info.TransferFolder = value;
        }

        public IPAddress MyLocalIpAddress => Info.LocalIpAddress;
        public IPAddress MyPublicIpAddress => Info.PublicIpAddress;
        public int MyServerPort => Info.Port;

        public ServerInfo RemoteServerInfo { get; set; }

        public string RemoteServerTransferFolderPath
        {
            get => RemoteServerInfo.TransferFolder;
            set => RemoteServerInfo.TransferFolder = value;
        }

        public IPAddress RemoteServerSessionIpAddress => RemoteServerInfo.SessionIpAddress;
        public IPAddress RemoteServerLocalIpAddress => RemoteServerInfo.LocalIpAddress;
        public IPAddress RemoteServerPublicIpAddress => RemoteServerInfo.PublicIpAddress;
        public int RemoteServerPort => RemoteServerInfo.Port;

        public List<(string filePath, long fileSize)> RemoteServerFileList { get; set; }

        public bool FileTransferAccepted => RemoteServerAcceptedFileTransfer;
        public bool FileTransferRejected => RemoteServerRejectedFileTransfer;
        public bool FileTransferInProgress => TransferInProgress;
        public bool FileTransferCanceled => OutboundFileTransferIsStalled;
        public bool FileTransferStalled => InboundFileTransferIsStalled;
        public bool RetryStalledFileTransfer => RetryPreviousFileTransfer;

        public string RemoteFilePath
        {
            get => _state.RemoteFilePath;
            set => _state.RemoteFilePath = value;
        }

        public string OutgoingFilePath
        {
            get => _state.OutgoingFilePath;
            set => _state.OutgoingFilePath = value;
        }

        public string IncomingFilePath
        {
            get => _state.IncomingFilePath;
            set => _state.IncomingFilePath = value;
        }

        public event EventHandler<ServerEvent> EventOccurred;
        public event EventHandler<ServerEvent> SocketEventOccurred;
        public event EventHandler<ServerEvent> FileTransferProgress;

        static string GetDefaultTransferFolder()
        {
            var defaultPath = $"{Directory.GetCurrentDirectory()}{Path.DirectorySeparatorChar}transfer";

            if (!Directory.Exists(defaultPath))
            {
                Directory.CreateDirectory(defaultPath);
            }

            return defaultPath;
        }

        public async Task InitializeAsync(string cidrIp, int port)
        {
            if (Initialized) return;
            
            var getLocalIpResult = NetworkUtilities.GetLocalIPv4Address(cidrIp);

            var localIp = getLocalIpResult.Success
                ? getLocalIpResult.Value
                : IPAddress.Loopback;
            
            var getPublicIResult =
                await NetworkUtilities.GetPublicIPv4AddressAsync().ConfigureAwait(false);

            var publicIp = getPublicIResult.Success
                ? getPublicIResult.Value
                : IPAddress.None;

            Info = new ServerInfo
            {
                Port = port,
                LocalIpAddress = localIp,
                PublicIpAddress = publicIp
            };

            if (getLocalIpResult.Success)
            {
                Info.SessionIpAddress = localIp;
            }
            else if (getPublicIResult.Success)
            {
                Info.SessionIpAddress = publicIp;
            }

            Initialized = true;
        }

        public async Task<Result> RunAsync(CancellationToken token)
        {
            if (!Initialized)
            {
                return Result.Fail(NotInitializedMessage);
            }

            _token = token;
            Logger.Start("server.log");

            var listenResult = Listen(MyServerPort);
            if (listenResult.Failure)
            {
                return listenResult;
            }

            IsListening = true;
            var runServerResult = await HandleIncomingConnectionsAsync().ConfigureAwait(false);

            IsListening = false;
            ShutdownListenSocket();

            EventOccurred?.Invoke(this,
             new ServerEvent { EventType = EventType.ServerStoppedListening });

            return runServerResult;
        }

        Result Listen(int localPort)
        {
            var ipEndPoint = new IPEndPoint(IPAddress.Any, localPort);
            try
            {
                _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listenSocket.Bind(ipEndPoint);
                _listenSocket.Listen(ListenBacklogSize);
            }
            catch (SocketException ex)
            {
                _log.Error("Error raised in method Listen", ex);
                return Result.Fail($"{ex.Message} ({ex.GetType()} raised in method TplSocketServer.Listen)");
            }

            EventOccurred?.Invoke(this,
                new ServerEvent
                {
                    EventType = EventType.ServerStartedListening,
                    LocalPortNumber = MyServerPort
                });

            return Result.Ok();
        }

        async Task<Result> HandleIncomingConnectionsAsync()
        {
            // Main loop. Server handles incoming connections until shutdown command is received
            // or an error is encountered
            while (true)
            {
                var acceptResult = await _listenSocket.AcceptTaskAsync(_token).ConfigureAwait(false);
                if (acceptResult.Failure)
                {
                    return acceptResult;
                }

                _clientSocket = acceptResult.Value;
                if (!(_clientSocket.RemoteEndPoint is IPEndPoint clientEndPoint))
                {
                    return Result.Fail("Error occurred casting _state._clientSocket.RemoteEndPoint as IPEndPoint");
                }

                _ipAddressLastConnection = clientEndPoint.Address;
                EventOccurred?.Invoke(this,
                    new ServerEvent
                    {
                        EventType = EventType.ConnectionAccepted,
                        RemoteServerIpAddress = _ipAddressLastConnection
                    });

                var receivedMessage = await ReceiveMessageFromClientAsync().ConfigureAwait(false);
                if (_token.IsCancellationRequested || ShutdownInitiated)
                {
                    return Result.Ok();
                }

                if (receivedMessage.Success) continue;

                EventOccurred?.Invoke(this,
                    new ServerEvent
                    {
                        EventType = EventType.ErrorOccurred,
                        ErrorMessage = receivedMessage.Error
                    });
            }
        }

        async Task<Result> ReceiveMessageFromClientAsync()
        {
            _state.Buffer = new byte[BufferSize];
            _state.UnreadBytes = new List<byte>();

            EventOccurred?.Invoke(this,
                new ServerEvent
                {
                    EventType = EventType.ReceiveMessageFromClientStarted,
                    RemoteServerIpAddress = _ipAddressLastConnection
                });

            var messageLengthResult = await DetermineMessageLengthAsync().ConfigureAwait(false);
            if (messageLengthResult.Failure)
            {
                return Result.Fail<Message>(messageLengthResult.Error);
            }

            var messageLength = messageLengthResult.Value;

            var receiveMessageResult = await ReceiveAllMessageBytesAsync(messageLength).ConfigureAwait(false);
            if (receiveMessageResult.Failure)
            {
                return Result.Fail<Message>(receiveMessageResult.Error);
            }

            var messageData = receiveMessageResult.Value;
            var messageTypeData = MessageUnwrapper.ReadInt32(messageData).ToString();
            var messageType = (MessageType)Enum.Parse(typeof(MessageType), messageTypeData);
            var message = new Message
            {
                Data = messageData,
                Type = messageType,
                RemoteServerIp = _ipAddressLastConnection,
                Id = _messageId,
                Timestamp = DateTime.Now
            };

            Queue.Add(message);
            _messageId++;

            var messageReceivedEvent = new ServerEvent
            {
                EventType = EventType.ReceiveMessageFromClientComplete,
                RemoteServerIpAddress = _ipAddressLastConnection,
                Message = message
            };

            EventOccurred?.Invoke(this, messageReceivedEvent);
            if (!message.MustBeProcessedImmediately()) return Result.Ok();

            IsIdle = false;
            Queue.Remove(message);
            var result = await ProcessRequestAsync(message);

            message.EventLog = _eventLog.Select(e => e).Where(e => e.MessageId == message.Id).ToList();
            Archive.Add(message);

            IsIdle = true;
            return result;
        }

        async Task<Result<int>> ReadFromSocketAsync()
        {
            Result<int> receiveResult;
            try
            {
                receiveResult =
                    await _clientSocket.ReceiveWithTimeoutAsync(
                        _state.Buffer,
                        0,
                        BufferSize,
                        SocketFlags.None,
                        ReceiveTimeoutMs)
                        .ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                _log.Error("Error raised in method DetermineTransferTypeAsync", ex);
                return Result.Fail<int>(
                    $"{ex.Message} ({ex.GetType()} raised in method TplSocketServer.DetermineTransferTypeAsync)");
            }
            catch (TimeoutException ex)
            {
                _log.Error("Error raised in method DetermineTransferTypeAsync", ex);
                return Result.Fail<int>(
                    $"{ex.Message} ({ex.GetType()} raised in method TplSocketServer.DetermineTransferTypeAsync)");
            }

            return receiveResult;
        }

        async Task<Result<int>> DetermineMessageLengthAsync()
        {
            EventOccurred?.Invoke(this,
                new ServerEvent
                    {EventType = EventType.DetermineMessageLengthStarted});

            var readFromSocketResult = await ReadFromSocketAsync().ConfigureAwait(false);
            if (readFromSocketResult.Failure)
            {
                return readFromSocketResult;
            }

            _state.LastBytesReceivedCount = readFromSocketResult.Value;
            var numberOfUnreadBytes = _state.LastBytesReceivedCount - 4;
            var messageLength = MessageUnwrapper.ReadInt32(_state.Buffer);
            var messageLengthData = BitConverter.GetBytes(messageLength);

            SocketEventOccurred?.Invoke(this,
                new ServerEvent
                {
                    EventType = EventType.ReceivedMessageLengthFromSocket,
                    BytesReceived = _state.LastBytesReceivedCount,
                    MessageLengthInBytes = 4,
                    UnreadByteCount = numberOfUnreadBytes
                });

            if (_state.LastBytesReceivedCount > 4)
            {
                var unreadBytes = new byte[numberOfUnreadBytes];
                _state.Buffer.ToList().CopyTo(4, unreadBytes, 0, numberOfUnreadBytes);
                _state.UnreadBytes = unreadBytes.ToList();

                EventOccurred?.Invoke(this,
                    new ServerEvent
                    {
                        EventType = EventType.SaveUnreadBytesAfterReceiveMessageLength,
                        CurrentMessageBytesReceived = _state.LastBytesReceivedCount,
                        ExpectedByteCount = 4,
                        UnreadByteCount = numberOfUnreadBytes,
                    });
            }

            EventOccurred?.Invoke(this,
                new ServerEvent
                {
                    EventType = EventType.DetermineMessageLengthComplete,
                    MessageLengthInBytes = messageLength,
                    MessageLengthData = messageLengthData
                });

            return Result.Ok(messageLength);
        }

        async Task<Result<byte[]>> ReceiveAllMessageBytesAsync(int messageLength)
        {
            EventOccurred?.Invoke(this,
                new ServerEvent {EventType = EventType.ReceiveMessageBytesStarted});

            int messageByteCount;
            var socketReadCount = 0;
            var totalBytesReceived = 0;
            var bytesRemaining = messageLength;
            var messageData = new List<byte>();
            var newUnreadByteCount = 0;

            if (_state.UnreadBytes.Count > 0)
            {
                messageByteCount = Math.Min(messageLength, _state.UnreadBytes.Count);
                var messageBytes = new byte[messageByteCount];

                _state.UnreadBytes.CopyTo(0, messageBytes, 0, messageByteCount);
                messageData.AddRange(messageBytes.ToList());

                totalBytesReceived += messageByteCount;
                bytesRemaining -= messageByteCount;

                EventOccurred?.Invoke(this,
                    new ServerEvent
                    {
                        EventType = EventType.CopySavedBytesToMessageData,
                        UnreadByteCount = _state.UnreadBytes.Count,
                        TotalMessageBytesReceived = messageByteCount,
                        MessageLengthInBytes = messageLength,
                        MessageBytesRemaining = bytesRemaining
                    });

                if (_state.UnreadBytes.Count > messageLength)
                {
                    var fileByteCount = _state.UnreadBytes.Count - messageLength;
                    var fileBytes = new byte[fileByteCount];
                    _state.UnreadBytes.CopyTo(messageLength, fileBytes, 0, fileByteCount);
                    _state.UnreadBytes = fileBytes.ToList();
                }
                else
                {
                    _state.UnreadBytes = new List<byte>();
                }
            }

            messageByteCount = 0;
            while (bytesRemaining > 0)
            {
                var readFromSocketResult = await ReadFromSocketAsync().ConfigureAwait(false);
                if (readFromSocketResult.Failure)
                {
                    return Result.Fail<byte[]>(readFromSocketResult.Error);
                }

                _state.LastBytesReceivedCount = readFromSocketResult.Value;

                messageByteCount = Math.Min(bytesRemaining, _state.LastBytesReceivedCount);
                var receivedBytes = new byte[messageByteCount];

                _state.Buffer.ToList().CopyTo(0, receivedBytes, 0, messageByteCount);
                messageData.AddRange(receivedBytes.ToList());

                socketReadCount++;
                newUnreadByteCount = _state.LastBytesReceivedCount - messageByteCount;
                totalBytesReceived += messageByteCount;
                bytesRemaining -= messageByteCount;

                SocketEventOccurred?.Invoke(this,
                    new ServerEvent
                    {
                        EventType = EventType.ReceivedMessageBytesFromSocket,
                        SocketReadCount = socketReadCount,
                        BytesReceived = _state.LastBytesReceivedCount,
                        CurrentMessageBytesReceived = messageByteCount,
                        TotalMessageBytesReceived = totalBytesReceived,
                        MessageLengthInBytes = messageLength,
                        MessageBytesRemaining = bytesRemaining,
                        UnreadByteCount = newUnreadByteCount
                    });
            }

            if (newUnreadByteCount > 0)
            {
                var unreadBytes = new byte[newUnreadByteCount];
                _state.Buffer.ToList().CopyTo(messageByteCount, unreadBytes, 0, newUnreadByteCount);
                _state.UnreadBytes = unreadBytes.ToList();

                EventOccurred?.Invoke(this,
                    new ServerEvent
                    {
                        EventType = EventType.SaveUnreadBytesAfterReceiveMessage,
                        ExpectedByteCount = messageByteCount,
                        UnreadByteCount = newUnreadByteCount
                    });
            }

            EventOccurred?.Invoke(this,
                new ServerEvent
                {
                    EventType = EventType.ReceiveMessageBytesComplete,
                    MessageData = messageData.ToArray()
                });

            return Result.Ok(messageData.ToArray());
        }

        public Task<Result> ProcessNextMessageInQueueAsync()
        {
            return ProcessMessageFromQueueAsync(_messageId - 1);
        }

        public async Task<Result> ProcessMessageFromQueueAsync(int messageId)
        {
            if (Queue.Count <= 0)
            {
                return Result.Fail("Queue is empty");
            }
            if (!IsIdle)
            {
                return Result.Fail("Server is busy, please try again after the current operation has completed");
            }

            var checkQueue = Queue.Select(m => m).Where(m => m.Id == messageId).ToList();
            var checkArchive = Archive.Select(m => m).Where(m => m.Id == messageId).ToList();
            if (checkQueue.Count == 0 && checkArchive.Count == 1)
            {
                return Result.Fail($"Message ID# {messageId} has already been processed, event logs for this request are available in the archive.");
            }

            if (checkQueue.Count == 0 && checkArchive.Count == 0)
            {
                return Result.Fail($"Message ID# {messageId} appears to be invalid. No record of this request were found in the queue or the archive.");
            }

            if (checkQueue.Count == 1)
            {
                IsIdle = false;
                var message = checkQueue[0];
                Queue.Remove(message);
                var result = await ProcessRequestAsync(message);

                message.EventLog = _eventLog.Select(e => e).Where(e => e.MessageId == message.Id).ToList();
                Archive.Add(message);
                IsIdle = true;

                return result;
            }

            // Code should be unreachable, returning an error if somehow none of the conditions above were met
            return Result.Fail($"Unable to determine if message ID# {messageId} is valid");
        }

        async Task<Result> ProcessRequestAsync(Message message)
        {
            EventOccurred?.Invoke(this, new ServerEvent
            {
                EventType = EventType.ProcessRequestStarted,
                MessageType = message.Type,
                MessageId = message.Id
            });

            switch (message.Type)
            {
                case MessageType.TextMessage:
                    ReceiveTextMessage(message);
                    break;

                case MessageType.InboundFileTransferRequest:
                   await InboundFileTransferAsync(message, _token).ConfigureAwait(false);
                    break;

                case MessageType.OutboundFileTransferRequest:
                    await OutboundFileTransferAsync(message).ConfigureAwait(false);
                    break;

                case MessageType.FileTransferAccepted:
                    await HandleFileTransferAcceptedAsync(message, _token).ConfigureAwait(false);
                    break;

                case MessageType.FileTransferStalled:
                    HandleStalledFileTransfer(message);
                    break;

                case MessageType.RetryOutboundFileTransfer:
                    await HandleRetryLastFileTransferAsync(message).ConfigureAwait(false);
                    break;

                case MessageType.FileListRequest:
                    await SendFileListAsync(message).ConfigureAwait(false);
                    break;

                case MessageType.FileListResponse:
                    ReceiveFileList(message);
                    break;

                case MessageType.NoFilesAvailableForDownload:
                    HandleNoFilesAvailableForDownload(message);
                    break;

                case MessageType.FileTransferRejected:
                    HandleFileTransferRejected(message);
                    break;

                case MessageType.RequestedFolderDoesNotExist:
                    HandleRequestedFolderDoesNotExist(message);
                    break;

                case MessageType.ServerInfoRequest:
                    await SendServerInfoAsync(message).ConfigureAwait(false);
                    break;

                case MessageType.ServerInfoResponse:
                    ReceiveServerInfo(message);
                    break;

                case MessageType.ShutdownServerCommand:
                    HandleShutdownServerCommand(message);
                    break;

                default:
                    var error = $"Unable to determine transfer type, value of '{message.Type}' is invalid.";
                    return Result.Fail(error);
            }

            EventOccurred?.Invoke(this, new ServerEvent
            {
                EventType = EventType.ProcessRequestComplete,
                MessageType = message.Type,
                MessageId = message.Id,
                RemoteServerIpAddress = _ipAddressLastConnection
            });

            return Result.Ok();
        }

        public async Task<Result> SendTextMessageAsync(
            string message,
            string remoteServerIpAddress,
            int remoteServerPort)
        {
            if (string.IsNullOrEmpty(message))
            {
                return Result.Fail("Message is null or empty string.");
            }

            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);

            EventOccurred?.Invoke(this,
                new ServerEvent
                {
                    EventType = EventType.SendTextMessageStarted,
                    TextMessage = message,
                    RemoteServerIpAddress = RemoteServerSessionIpAddress,
                    RemoteServerPortNumber = RemoteServerPort
                });

            var connectResult = await ConnectToServerAsync().ConfigureAwait(false);
            if (connectResult.Failure)
            {
                return connectResult;
            }

            var messageData =
                MessageWrapper.ConstructRequestWithStringValue(
                    MessageType.TextMessage,
                    MyLocalIpAddress.ToString(),
                    MyServerPort,
                    message);

            var sendMessageDataResult = await SendMessageData(messageData).ConfigureAwait(false);
            if (sendMessageDataResult.Failure)
            {
                return sendMessageDataResult;
            }

            CloseServerSocket();

            EventOccurred?.Invoke(this,
                new ServerEvent
                    {EventType = EventType.SendTextMessageComplete});

            return Result.Ok();
        }

        void ReceiveTextMessage(Message message)
        {
            var (remoteServerIpAddress,
                remoteServerPort,
                textMessage) = MessageUnwrapper.ReadRequestWithStringValue(message.Data);

            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.ReceivedTextMessage,
                TextMessage = textMessage,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                MessageId = message.Id
            });

            EventOccurred?.Invoke(this, _eventLog.Last());
        }

        async Task<Result> OutboundFileTransferAsync(Message message)
        {
            var (requestedFilePath,
                remoteServerIpAddress,
                remoteServerPort,
                remoteFolderPath) = MessageUnwrapper.ReadOutboundFileTransferRequest(message.Data);

            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);
            OutgoingFilePath = requestedFilePath;
            RemoteServerTransferFolderPath = remoteFolderPath;

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.ReceivedOutboundFileTransferRequest,
                LocalFolder = Path.GetDirectoryName(OutgoingFilePath),
                FileName = Path.GetFileName(OutgoingFilePath),
                FileSizeInBytes = new FileInfo(OutgoingFilePath).Length,
                RemoteFolder = RemoteServerTransferFolderPath,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                MessageId = message.Id

            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            if (!System.IO.File.Exists(requestedFilePath))
            {
                //TODO: Add another event sequence here
                return Result.Fail("File does not exist: " + requestedFilePath);
            }

            return
                await SendFileAsync(
                    RemoteServerSessionIpAddress,
                    RemoteServerPort,
                    OutgoingFilePath,
                    RemoteServerTransferFolderPath,
                    message.Id).ConfigureAwait(false);
        }

        public Task<Result> SendFileAsync(
            IPAddress remoteServerIpAddress,
            int remoteServerPort,
            string localFilePath,
            string remoteFolderPath)
        {
            return
                SendFileAsync(
                    remoteServerIpAddress,
                    remoteServerPort,
                    localFilePath,
                    remoteFolderPath,
                    0);
        }

        Task<Result> SendFileAsync(
            IPAddress remoteServerIpAddress,
            int remoteServerPort,
            string localFilePath,
            string remoteFolderPath,
            int messageId)
        {
            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);
            OutgoingFilePath = localFilePath;
            RemoteServerTransferFolderPath = remoteFolderPath;

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.RequestOutboundFileTransferStarted,
                LocalIpAddress = MyLocalIpAddress,
                LocalPortNumber = MyServerPort,
                LocalFolder = Path.GetDirectoryName(OutgoingFilePath),
                FileName = Path.GetFileName(OutgoingFilePath),
                FileSizeInBytes = _state.OutgoingFileSize,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                RemoteFolder = RemoteServerTransferFolderPath,
                MessageId = messageId
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            return SendOutboundFileTransferRequestAsync(
                RemoteServerSessionIpAddress,
                RemoteServerPort,
                OutgoingFilePath,
                RemoteServerTransferFolderPath,
                messageId);
        }

        async Task<Result> SendOutboundFileTransferRequestAsync(
            IPAddress remoteServerIpAddress,
            int remoteServerPort,
            string localFilePath,
            string remoteFolderPath,
            int messageId)
        {
            if (!System.IO.File.Exists(localFilePath))
            {
                return Result.Fail("File does not exist: " + localFilePath);
            }

            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);
            OutgoingFilePath = localFilePath;
            RemoteServerTransferFolderPath = remoteFolderPath;
            RemoteServerAcceptedFileTransfer = false;
            RemoteServerRejectedFileTransfer = false;
            
            var connectResult = await ConnectToServerAsync().ConfigureAwait(false);
            if (connectResult.Failure)
            {
                return connectResult;
            }

            var messageData =
                MessageWrapper.ConstructOutboundFileTransferRequest(
                    OutgoingFilePath,
                    _state.OutgoingFileSize,
                    MyLocalIpAddress.ToString(),
                    MyServerPort,
                    RemoteServerTransferFolderPath);

            var sendMessageDataResult = await SendMessageData(messageData).ConfigureAwait(false);
            if (sendMessageDataResult.Failure)
            {
                return sendMessageDataResult;
            }

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.RequestOutboundFileTransferComplete,
                LocalIpAddress = MyLocalIpAddress,
                LocalPortNumber = MyServerPort,
                LocalFolder = Path.GetDirectoryName(OutgoingFilePath),
                FileName = Path.GetFileName(OutgoingFilePath),
                FileSizeInBytes = _state.OutgoingFileSize,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                RemoteFolder = RemoteServerTransferFolderPath,
                MessageId = messageId
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            return Result.Ok();
        }

        void HandleFileTransferRejected(Message message)
        {
            (string remoteServerIpAddress,
                int remoteServerPort) = MessageUnwrapper.ReadServerConnectionInfo(message.Data);

            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);
            RemoteServerRejectedFileTransfer = true;

            //TODO: Investigate why this causes a bug that fails my unit tests
            //OutgoingFilePath = string.Empty;
            //RemoteServerTransferFolderPath = string.Empty;

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.ClientRejectedFileTransfer,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                MessageId = message.Id
            });

            EventOccurred?.Invoke(this, _eventLog.Last());
            TransferInProgress = false;
        }

        Task<Result> HandleFileTransferAcceptedAsync(Message message, CancellationToken token)
        {
            (string remoteServerIpAddress,
                int remoteServerPort) = MessageUnwrapper.ReadServerConnectionInfo(message.Data);

            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);
            RemoteServerAcceptedFileTransfer = true;
            TransferInProgress = true;
            _outboundFileTransferMessageId = message.Id;

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.ClientAcceptedFileTransfer,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                MessageId = _outboundFileTransferMessageId
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            var sendFileBytesResult = SendFileBytesAsync(OutgoingFilePath, message, token);

            TransferInProgress = false;
            return sendFileBytesResult;
        }

        async Task<Result> SendFileBytesAsync(
            string localFilePath,
            Message message,
            CancellationToken token)
        {
            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.SendFileBytesStarted,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                MessageId = _outboundFileTransferMessageId
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            var bytesRemaining = _state.OutgoingFileSize;
            var fileChunkSentCount = 0;
            OutboundFileTransferIsStalled = false;

            using (var file = System.IO.File.OpenRead(localFilePath))
            {
                while (bytesRemaining > 0)
                {
                    if (token.IsCancellationRequested)
                    {
                        return Result.Ok();
                    }

                    var fileChunkSize = (int) Math.Min(BufferSize, bytesRemaining);
                    _state.Buffer = new byte[fileChunkSize];

                    var numberOfBytesToSend = file.Read(_state.Buffer, 0, fileChunkSize);
                    bytesRemaining -= numberOfBytesToSend;

                    var offset = 0;
                    var socketSendCount = 0;
                    while (numberOfBytesToSend > 0)
                    {
                        var sendFileChunkResult =
                            await _transferSocket.SendWithTimeoutAsync(
                                _state.Buffer,
                                offset,
                                fileChunkSize,
                                SocketFlags.None,
                                SendTimeoutMs).ConfigureAwait(false);

                        if (OutboundFileTransferIsStalled)
                        {
                            const string fileTransferStalledErrorMessage =
                                "Aborting file transfer, client says that data is no longer being received";

                            return Result.Fail(fileTransferStalledErrorMessage);
                        }

                        if (sendFileChunkResult.Failure)
                        {
                            return sendFileChunkResult;
                        }

                        _state.LastBytesSentCount = sendFileChunkResult.Value;
                        numberOfBytesToSend -= _state.LastBytesSentCount;
                        offset += _state.LastBytesSentCount;
                        socketSendCount++;
                    }

                    fileChunkSentCount++;

                    if (_state.OutgoingFileSize > 10 * BufferSize) continue;
                    SocketEventOccurred?.Invoke(this,
                        new ServerEvent
                        {
                            EventType = EventType.SentFileChunkToClient,
                            FileSizeInBytes = _state.OutgoingFileSize,
                            CurrentFileBytesSent = fileChunkSize,
                            FileBytesRemaining = bytesRemaining,
                            FileChunkSentCount = fileChunkSentCount,
                            SocketSendCount = socketSendCount
                        });
                }

                _eventLog.Add(new ServerEvent
                {
                    EventType = EventType.SendFileBytesComplete,
                    RemoteServerIpAddress = RemoteServerSessionIpAddress,
                    RemoteServerPortNumber = RemoteServerPort,
                    MessageId = _outboundFileTransferMessageId
                });

                EventOccurred?.Invoke(this, _eventLog.Last());

                var receiveConfirationMessageResult =
                    await ReceiveConfirmationFileTransferCompleteAsync(message).ConfigureAwait(false);

                if (receiveConfirationMessageResult.Failure)
                {
                    return receiveConfirationMessageResult;
                }

                _transferSocket.Shutdown(SocketShutdown.Both);
                _transferSocket.Close();

                TransferInProgress = false;

                return Result.Ok();
            }
        }

        async Task<Result> ReceiveConfirmationFileTransferCompleteAsync(Message message)
        {
            var buffer = new byte[BufferSize];
            Result<int> receiveMessageResult;
            int bytesReceived;

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.ReceiveConfirmationMessageStarted,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                MessageId = _outboundFileTransferMessageId
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            try
            {
                receiveMessageResult =
                    await _transferSocket.ReceiveAsync(
                        buffer,
                        0,
                        BufferSize,
                        SocketFlags.None).ConfigureAwait(false);

                bytesReceived = receiveMessageResult.Value;
            }
            catch (SocketException ex)
            {
                _log.Error("Error raised in method ReceiveConfirmationFileTransferCompleteAsync", ex);
                return Result.Fail(
                    $"{ex.Message} ({ex.GetType()} raised in method TplSocketServer.ReceiveConfirmationFileTransferCompleteAsync)");
            }
            catch (TimeoutException ex)
            {
                _log.Error("Error raised in method ReceiveConfirmationFileTransferCompleteAsync", ex);
                return Result.Fail(
                    $"{ex.Message} ({ex.GetType()} raised in method TplSocketServer.ReceiveConfirmationFileTransferCompleteAsync)");
            }

            if (receiveMessageResult.Failure || bytesReceived == 0)
            {
                return Result.Fail("Error receiving confirmation message from remote server");
            }

            var confirmationMessage = Encoding.ASCII.GetString(buffer, 0, bytesReceived);

            if (confirmationMessage != ConfirmationMessage)
            {
                return Result.Fail(
                    $"Confirmation message doesn't match:\n\tExpected:\t{ConfirmationMessage}\n\tActual:\t{confirmationMessage}");
            }

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.ReceiveConfirmationMessageComplete,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                ConfirmationMessage = confirmationMessage,
                MessageId = _outboundFileTransferMessageId
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            return Result.Ok();
        }

        public async Task<Result> GetFileAsync(
            string remoteServerIpAddress,
            int remoteServerPort,
            string remoteFilePath,
            string localFolderPath)
        {
            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);
            RemoteFilePath = remoteFilePath;
            RemoteServerTransferFolderPath = Path.GetDirectoryName(remoteFilePath);
            MyTransferFolderPath = localFolderPath;

            EventOccurred?.Invoke(this,
                new ServerEvent
                {
                    EventType = EventType.RequestInboundFileTransferStarted,
                    RemoteServerIpAddress = RemoteServerSessionIpAddress,
                    RemoteServerPortNumber = RemoteServerPort,
                    RemoteFolder = RemoteServerTransferFolderPath,
                    FileName = Path.GetFileName(RemoteFilePath),
                    LocalIpAddress = MyLocalIpAddress,
                    LocalPortNumber = MyServerPort,
                    LocalFolder = MyTransferFolderPath,
                });

            var connectResult = await ConnectToServerAsync().ConfigureAwait(false);
            if (connectResult.Failure)
            {
                return connectResult;
            }

            var messageData =
                MessageWrapper.ConstructInboundFileTransferRequest(
                    RemoteFilePath,
                    MyLocalIpAddress.ToString(),
                    MyServerPort,
                    MyTransferFolderPath);

            var sendMessageDataResult = await SendMessageData(messageData).ConfigureAwait(false);
            if (sendMessageDataResult.Failure)
            {
                return sendMessageDataResult;
            }

            CloseServerSocket();

            EventOccurred?.Invoke(this,
                new ServerEvent
                    {EventType = EventType.RequestInboundFileTransferComplete});

            return Result.Ok();
        }

        async Task<Result> InboundFileTransferAsync(Message message, CancellationToken token)
        {
            var (localFilePath,
                fileSizeBytes,
                remoteServerIpAddress,
                remoteServerPort) = MessageUnwrapper.ReadInboundFileTransferRequest(message.Data);

            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);
            IncomingFilePath = localFilePath;
            _state.IncomingFileSize = fileSizeBytes;
            _inboundFileTransferMessageId = message.Id;
            
            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.ReceivedInboundFileTransferRequest,
                LocalFolder = Path.GetDirectoryName(IncomingFilePath),
                FileName = Path.GetFileName(IncomingFilePath),
                FileSizeInBytes = _state.IncomingFileSize,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                MessageId = _inboundFileTransferMessageId
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            if (System.IO.File.Exists(IncomingFilePath))
            {
                return
                    await SendSimpleMessageToClientAsync(
                        new Message
                            { Type = MessageType.FileTransferRejected,
                                Id = _inboundFileTransferMessageId },
                        EventType.SendFileTransferRejectedStarted,
                        EventType.SendFileTransferRejectedComplete).ConfigureAwait(false);
            }

            var acceptTransferResult =
                await SendSimpleMessageToClientAsync(
                    new Message
                        { Type = MessageType.FileTransferAccepted,
                            Id = _inboundFileTransferMessageId },
                    EventType.SendFileTransferAcceptedStarted,
                    EventType.SendFileTransferAcceptedComplete).ConfigureAwait(false);

            if (acceptTransferResult.Failure)
            {
                return acceptTransferResult;
            }

            var receiveFileResult = await ReceiveFileAsync(message, token).ConfigureAwait(false);
            if (receiveFileResult.Failure)
            {
                return receiveFileResult;
            }

            var sendConfirmationMessageResult =
                await ConfirmFileTransferComplete().ConfigureAwait(false);

            return sendConfirmationMessageResult.Success
                ? Result.Ok()
                : sendConfirmationMessageResult;
        }

        async Task<Result> ReceiveFileAsync(Message message, CancellationToken token)
        {
            var startTime = DateTime.Now;

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.ReceiveFileBytesStarted,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                FileTransferStartTime = startTime,
                FileSizeInBytes = _state.IncomingFileSize,
                MessageId = _inboundFileTransferMessageId
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            var receiveCount = 0;
            long totalBytesReceived = 0;
            var bytesRemaining = _state.IncomingFileSize;
            float percentComplete = 0;
            InboundFileTransferIsStalled = false;

            if (_state.UnreadBytes.Count > 0)
            {
                totalBytesReceived += _state.UnreadBytes.Count;
                bytesRemaining -= _state.UnreadBytes.Count;

                var writeBytesResult =
                    FileHelper.WriteBytesToFile(
                        IncomingFilePath,
                        _state.UnreadBytes.ToArray(),
                        _state.UnreadBytes.Count);

                if (writeBytesResult.Failure)
                {
                    return writeBytesResult;
                }

                _eventLog.Add(new ServerEvent
                {
                    EventType = EventType.CopySavedBytesToIncomingFile,
                    CurrentFileBytesReceived = _state.UnreadBytes.Count,
                    TotalFileBytesReceived = totalBytesReceived,
                    FileSizeInBytes = _state.IncomingFileSize,
                    FileBytesRemaining = bytesRemaining,
                    MessageId = _inboundFileTransferMessageId
                });

                EventOccurred?.Invoke(this, _eventLog.Last());
            }

            // Read file bytes from transfer socket until 
            //      1. the entire file has been received OR 
            //      2. Data is no longer being received OR
            //      3, Transfer is canceled
            while (bytesRemaining > 0)
            {
                if (token.IsCancellationRequested)
                {
                    return Result.Ok();
                }

                var readFromSocketResult = await ReadFromSocketAsync().ConfigureAwait(false);
                if (readFromSocketResult.Failure)
                {
                    return Result.Fail(readFromSocketResult.Error);
                }

                _state.LastBytesReceivedCount = readFromSocketResult.Value;
                var receivedBytes = new byte[_state.LastBytesReceivedCount];

                if (_state.LastBytesReceivedCount == 0)
                {
                    return Result.Fail("Socket is no longer receiving data, must abort file transfer");
                }

                var writeBytesResult = FileHelper.WriteBytesToFile(
                    IncomingFilePath,
                    receivedBytes,
                    _state.LastBytesReceivedCount);

                if (writeBytesResult.Failure)
                {
                    return writeBytesResult;
                }

                receiveCount++;
                totalBytesReceived += _state.LastBytesReceivedCount;
                bytesRemaining -= _state.LastBytesReceivedCount;
                var checkPercentComplete = totalBytesReceived / (float) _state.IncomingFileSize;
                var changeSinceLastUpdate = checkPercentComplete - percentComplete;

                // this method fires on every socket read event, which could be hurdreds of thousands
                // of times depending on the file size and buffer size. Since this  event is only used
                // by myself when debugging small test files, I limited this event to only fire when 
                // the size of the file will result in less than 10 read events
                if (_state.IncomingFileSize < 10 * BufferSize)
                {
                    SocketEventOccurred?.Invoke(this,
                    new ServerEvent
                    {
                        EventType = EventType.ReceivedFileBytesFromSocket,
                        SocketReadCount = receiveCount,
                        BytesReceived = _state.LastBytesReceivedCount,
                        CurrentFileBytesReceived = _state.LastBytesReceivedCount,
                        TotalFileBytesReceived = totalBytesReceived,
                        FileSizeInBytes = _state.IncomingFileSize,
                        FileBytesRemaining = bytesRemaining,
                        PercentComplete = percentComplete
                    });
                }

                // Report progress in intervals which are set by the user in the settings file
                if (changeSinceLastUpdate < TransferUpdateInterval) continue;

                percentComplete = checkPercentComplete;

                FileTransferProgress?.Invoke(this, new ServerEvent
                {
                    EventType = EventType.UpdateFileTransferProgress,
                    TotalFileBytesReceived = totalBytesReceived,
                    FileSizeInBytes = _state.IncomingFileSize,
                    FileBytesRemaining = bytesRemaining,
                    PercentComplete = percentComplete,
                    MessageId = _inboundFileTransferMessageId
                });
            }

            if (InboundFileTransferIsStalled)
            {
                return Result.Fail(
                    "Data is no longer bring received from remote client, file transfer has been canceled");
            }

            if (RetryPreviousFileTransfer)
            {
                RetryPreviousFileTransfer = false;
            }

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.ReceiveFileBytesComplete,
                FileTransferStartTime = startTime,
                FileTransferCompleteTime = DateTime.Now,
                FileSizeInBytes = _state.IncomingFileSize,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                MessageId = _inboundFileTransferMessageId
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            return Result.Ok();
        }

        public Task<Result> SendNotificationFileTransferStalledAsync()
        {
            InboundFileTransferIsStalled = true;

            return
                SendSimpleMessageToClientAsync(
                    new Message
                    { Type = MessageType.FileTransferStalled,
                        Id = _inboundFileTransferMessageId },
                    EventType.SendFileTransferStalledStarted,
                    EventType.SendFileTransferStalledComplete);
        }

        void HandleStalledFileTransfer(Message message)
        {
            (string remoteServerIpAddress,
                int remoteServerPort) = MessageUnwrapper.ReadServerConnectionInfo(message.Data);

            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.FileTransferStalled,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                MessageId = _outboundFileTransferMessageId
            });

            EventOccurred?.Invoke(this, _eventLog.Last());
            OutboundFileTransferIsStalled = true;
        }

        public async Task<Result> RetryLastFileTransferAsync(
            IPAddress remoteServerIpAddress,
            int remoteServerPort)
        {
            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);
            RetryPreviousFileTransfer = true;

            EventOccurred?.Invoke(this,
                new ServerEvent
                {
                    EventType = EventType.RequestFileListStarted,
                    LocalIpAddress = MyLocalIpAddress,
                    LocalPortNumber = MyServerPort,
                    RemoteServerIpAddress = RemoteServerSessionIpAddress,
                    RemoteServerPortNumber = RemoteServerPort,
                    RemoteFolder = RemoteServerTransferFolderPath,
                    MessageId = _inboundFileTransferMessageId
                });

            var connectResult = await ConnectToServerAsync().ConfigureAwait(false);
            if (connectResult.Failure)
            {
                return connectResult;
            }

            var messageData =
                MessageWrapper.ConstructRequestWithStringValue(
                    MessageType.RetryOutboundFileTransfer,
                    MyLocalIpAddress.ToString(),
                    MyServerPort,
                    MyTransferFolderPath);

            var sendMessageDataResult = await SendMessageData(messageData).ConfigureAwait(false);
            if (sendMessageDataResult.Failure)
            {
                return sendMessageDataResult;
            }

            CloseServerSocket();

            EventOccurred?.Invoke(this,
                new ServerEvent { EventType = EventType.RequestFileListComplete });

            return Result.Ok();
        }

        Task<Result> HandleRetryLastFileTransferAsync(Message message)
        {
            (string remoteServerIpAddress,
                int remoteServerPort,
                string remoteFolderPath) = MessageUnwrapper.ReadRequestWithStringValue(message.Data);

            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);
            RemoteServerTransferFolderPath = remoteFolderPath;

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.ReceivedRetryOutboundFileTransferRequest,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                MessageId = _outboundFileTransferMessageId
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            return
                SendFileAsync(
                    RemoteServerSessionIpAddress,
                    RemoteServerPort,
                    OutgoingFilePath,
                    RemoteServerTransferFolderPath,
                    _outboundFileTransferMessageId);
        }

        async Task<Result> ConfirmFileTransferComplete()
        {
            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.SendConfirmationMessageStarted,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                ConfirmationMessage = ConfirmationMessage,
                MessageId = _inboundFileTransferMessageId
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            var confirmationMessageData = Encoding.ASCII.GetBytes(ConfirmationMessage);

            var sendConfirmatinMessageResult =
                await _clientSocket.SendWithTimeoutAsync(
                    confirmationMessageData,
                    0,
                    confirmationMessageData.Length,
                    SocketFlags.None,
                    SendTimeoutMs).ConfigureAwait(false);

            if (sendConfirmatinMessageResult.Failure)
            {
                return sendConfirmatinMessageResult;
            }

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.SendConfirmationMessageComplete,
                MessageId = _inboundFileTransferMessageId
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            return Result.Ok();
        }

        async Task<Result> SendSimpleMessageToClientAsync(
            Message message,
            EventType sendMessageStartedEventType,
            EventType sendMessageCompleteEventType)
        {
            _eventLog.Add(new ServerEvent
            {
                EventType = sendMessageStartedEventType,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                LocalIpAddress = MyLocalIpAddress,
                LocalPortNumber = MyServerPort,
                MessageId = message.Id
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            var connectResult = await ConnectToServerAsync().ConfigureAwait(false);
            if (connectResult.Failure)
            {
                return connectResult;
            }

            var messageData =
                MessageWrapper.ConstructBasicRequest(
                    message.Type,
                    MyLocalIpAddress.ToString(),
                    MyServerPort);

            var sendMessageDataResult = await SendMessageData(messageData).ConfigureAwait(false);
            if (sendMessageDataResult.Failure)
            {
                return sendMessageDataResult;
            }

            CloseServerSocket();

            _eventLog.Add(new ServerEvent
            {
                EventType = sendMessageCompleteEventType,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                LocalIpAddress = MyLocalIpAddress,
                LocalPortNumber = MyServerPort,
                MessageId = message.Id
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            return Result.Ok();
        }

        async Task<Result> ConnectToServerAsync()
        {
            _transferSocket =
                new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            EventOccurred?.Invoke(this,
                new ServerEvent
                {
                    EventType = EventType.ConnectToRemoteServerStarted,
                    RemoteServerIpAddress = RemoteServerSessionIpAddress,
                    RemoteServerPortNumber = RemoteServerPort
                });

            var connectResult =
                await _transferSocket.ConnectWithTimeoutAsync(
                    RemoteServerSessionIpAddress,
                    RemoteServerPort,
                    ConnectTimeoutMs).ConfigureAwait(false);

            if (connectResult.Failure)
            {
                return connectResult;
            }

            EventOccurred?.Invoke(this,
                new ServerEvent
                    {EventType = EventType.ConnectToRemoteServerComplete});

            return Result.Ok();
        }

        async Task<Result> SendMessageData(byte[] messageData)
        {
            var messageLength = BitConverter.GetBytes(messageData.Length);

            var sendMessageLengthResult =
                await _transferSocket.SendWithTimeoutAsync(
                    messageLength,
                    0,
                    messageLength.Length,
                    SocketFlags.None,
                    SendTimeoutMs).ConfigureAwait(false);

            if (sendMessageLengthResult.Failure)
            {
                return sendMessageLengthResult;
            }

            var sendMessageResult =
                await _transferSocket.SendWithTimeoutAsync(
                    messageData,
                    0,
                    messageData.Length,
                    SocketFlags.None,
                    SendTimeoutMs).ConfigureAwait(false);

            return sendMessageResult.Success
                ? Result.Ok()
                : sendMessageResult;
        }

        void CloseServerSocket()
        {
            _transferSocket.Shutdown(SocketShutdown.Both);
            _transferSocket.Close();
        }

        public async Task<Result> RequestFileListAsync(
            IPAddress remoteServerIpAddress,
            int remoteServerPort,
            string targetFolder)
        {
            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);
            RemoteServerTransferFolderPath = targetFolder;

            EventOccurred?.Invoke(this,
                new ServerEvent
                {
                    EventType = EventType.RequestFileListStarted,
                    LocalIpAddress = MyLocalIpAddress,
                    LocalPortNumber = MyServerPort,
                    RemoteServerIpAddress = RemoteServerSessionIpAddress,
                    RemoteServerPortNumber = RemoteServerPort,
                    RemoteFolder = RemoteServerTransferFolderPath
                });

            var connectResult = await ConnectToServerAsync().ConfigureAwait(false);
            if (connectResult.Failure)
            {
                return connectResult;
            }

            var messageData =
                MessageWrapper.ConstructRequestWithStringValue(
                    MessageType.FileListRequest,
                    MyLocalIpAddress.ToString(),
                    MyServerPort,
                    RemoteServerTransferFolderPath);

            var sendMessageDataResult = await SendMessageData(messageData).ConfigureAwait(false);
            if (sendMessageDataResult.Failure)
            {
                return sendMessageDataResult;
            }

            CloseServerSocket();

            EventOccurred?.Invoke(this,
            new ServerEvent {EventType = EventType.RequestFileListComplete});

            return Result.Ok();
        }

        async Task<Result> SendFileListAsync(Message message)
        {
            (string remoteServerIpAddress,
                int remoteServerPort,
                string targetFolderPath) = MessageUnwrapper.ReadFileListRequest(message.Data);

            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);
            MyTransferFolderPath = targetFolderPath;

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.ReceivedFileListRequest,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                RemoteFolder = MyTransferFolderPath,
                MessageId = message.Id
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            if (!Directory.Exists(MyTransferFolderPath))
            {
                return
                    await SendSimpleMessageToClientAsync(
                        new Message{ Type = MessageType.RequestedFolderDoesNotExist, Id = message.Id },
                        EventType.SendNotificationFolderDoesNotExistStarted,
                        EventType.SendNotificationFolderDoesNotExistComplete).ConfigureAwait(false);
            }

            List<string> listOfFiles;
            try
            {
                listOfFiles =
                    Directory.GetFiles(MyTransferFolderPath).ToList()
                        .Select(f => new FileInfo(f)).Where(fi => !fi.Name.StartsWith('.'))
                        .Select(fi => fi.ToString()).ToList();
            }
            catch (IOException ex)
            {
                _log.Error("Error raised in method SendFileListAsync", ex);
                return Result.Fail($"{ex.Message} ({ex.GetType()} raised in method TplSocketServer.SendFileListAsync)");
            }

            if (listOfFiles.Count == 0)
            {
                return await SendSimpleMessageToClientAsync(
                    new Message { Type = MessageType.NoFilesAvailableForDownload, Id = message.Id },
                    EventType.SendNotificationNoFilesToDownloadStarted,
                    EventType.SendNotificationNoFilesToDownloadComplete).ConfigureAwait(false);
            }

            var fileInfoList = new List<(string, long)>();
            foreach (var file in listOfFiles)
            {
                var fileSize = new FileInfo(file).Length;
                fileInfoList.Add((file, fileSize));
            }

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.SendFileListStarted,
                LocalIpAddress = MyLocalIpAddress,
                LocalPortNumber = MyServerPort,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                RemoteServerFileList = fileInfoList,
                LocalFolder = MyTransferFolderPath,
                MessageId = message.Id
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            var connectResult = await ConnectToServerAsync().ConfigureAwait(false);
            if (connectResult.Failure)
            {
                return connectResult;
            }

            var responseData =
                MessageWrapper.ConstructFileListResponse(
                    fileInfoList,
                    "*",
                    "|",
                    MyLocalIpAddress.ToString(),
                    MyServerPort,
                    MyTransferFolderPath);

            var sendMessageDataResult = await SendMessageData(responseData).ConfigureAwait(false);
            if (sendMessageDataResult.Failure)
            {
                return sendMessageDataResult;
            }

            CloseServerSocket();

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.SendFileListComplete,
                MessageId = message.Id
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            return Result.Ok();
        }

        void HandleRequestedFolderDoesNotExist(Message message)
        {
            (string remoteServerIpAddress,
                int remoteServerPort) = MessageUnwrapper.ReadServerConnectionInfo(message.Data);

            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.ReceivedNotificationFolderDoesNotExist,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                MessageId = message.Id
            });

            EventOccurred?.Invoke(this, _eventLog.Last());
        }

        void HandleNoFilesAvailableForDownload(Message message)
        {
            (string remoteServerIpAddress,
                int remoteServerPort) = MessageUnwrapper.ReadServerConnectionInfo(message.Data);

            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.ReceivedNotificationNoFilesToDownload,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                MessageId = message.Id
            });

            EventOccurred?.Invoke(this, _eventLog.Last());
        }

        void ReceiveFileList(Message message)
        {
            var (remoteServerIpAddress,
                remoteServerPort,
                transferFolder,
                fileList) = MessageUnwrapper.ReadFileListResponse(message.Data);

            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);
            RemoteServerTransferFolderPath = transferFolder;

            RemoteServerFileList = fileList;

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.ReceivedFileList,
                LocalIpAddress = MyLocalIpAddress,
                LocalPortNumber = MyServerPort,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                RemoteFolder = RemoteServerTransferFolderPath,
                RemoteServerFileList = RemoteServerFileList,
                MessageId = message.Id
            });

            EventOccurred?.Invoke(this, _eventLog.Last());
        }

        public Task<Result> RequestServerInfoAsync(IPAddress remoteServerIpAddress, int remoteServerPort)
        {
            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);

            return
                SendSimpleMessageToClientAsync(
                    new Message { Type = MessageType.ServerInfoRequest },
                    EventType.RequestServerInfoStarted,
                    EventType.RequestServerInfoComplete);
        }
        
        async Task<Result> SendServerInfoAsync(Message message)
        {
            (string remoteServerIpAddress,
                int remoteServerPort) = MessageUnwrapper.ReadServerConnectionInfo(message.Data);

            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.ReceivedServerInfoRequest,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                MessageId = message.Id
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            var publicIpResult = await NetworkUtilities.GetPublicIPv4AddressAsync().ConfigureAwait(false);
            Info.PublicIpAddress = publicIpResult.Success ? publicIpResult.Value : IPAddress.Loopback;

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.SendServerInfoStarted,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                LocalFolder = MyTransferFolderPath,
                LocalIpAddress = MyLocalIpAddress,
                LocalPortNumber = MyServerPort,
                PublicIpAddress = MyPublicIpAddress,
                MessageId = message.Id
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            var connectResult = await ConnectToServerAsync().ConfigureAwait(false);
            if (connectResult.Failure)
            {
                return connectResult;
            }

            var responseData =
                MessageWrapper.ConstructServerInfoResponse(
                    MyLocalIpAddress.ToString(),
                    MyServerPort,
                    MyPublicIpAddress.ToString(),
                    MyTransferFolderPath);

            var sendMessageDataResult = await SendMessageData(responseData).ConfigureAwait(false);
            if (sendMessageDataResult.Failure)
            {
                return sendMessageDataResult;
            }

            CloseServerSocket();

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.SendServerInfoComplete,
                MessageId = message.Id
            });

            EventOccurred?.Invoke(this, _eventLog.Last());

            return Result.Ok();
        }

        void ReceiveServerInfo(Message message)
        {
            var (remoteServerIpAddress,
                remoteServerPort,
                remoteServerPublicIpAddress,
                transferFolder) = MessageUnwrapper.ReadServerInfoResponse(message.Data);

            RemoteServerInfo = new ServerInfo
            {
                Port = remoteServerPort,
                LocalIpAddress = NetworkUtilities.ParseSingleIPv4Address(remoteServerIpAddress).Value,
                PublicIpAddress = NetworkUtilities.ParseSingleIPv4Address(remoteServerPublicIpAddress).Value,
                TransferFolder = transferFolder
            };

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.ReceivedServerInfo,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort,
                RemoteFolder = RemoteServerTransferFolderPath,
                LocalIpAddress = RemoteServerLocalIpAddress,
                PublicIpAddress = RemoteServerPublicIpAddress,
                MessageId = message.Id
            });

            EventOccurred?.Invoke(this, _eventLog.Last());
        }
        
        public async Task<Result> ShutdownAsync()
        {
            if (!IsListening)
            {
                return Result.Fail("Server is already shutdown");
            }

            //TODO: This looks awkward, change how shutdown command is sent to local server
            RemoteServerInfo = Info;

            var shutdownResult =
                await SendSimpleMessageToClientAsync(
                    new Message{ Type = MessageType.ShutdownServerCommand},
                    EventType.SendShutdownServerCommandStarted,
                    EventType.SendShutdownServerCommandComplete).ConfigureAwait(false);

            return shutdownResult.Success
                ? Result.Ok()
                : Result.Fail($"Error occurred shutting down the server + {shutdownResult.Error}");
        }

        void HandleShutdownServerCommand(Message message)
        {
            (string remoteServerIpAddress,
                int remoteServerPort) = MessageUnwrapper.ReadServerConnectionInfo(message.Data);

            RemoteServerInfo = new ServerInfo(remoteServerIpAddress, remoteServerPort);

            if (Info.IsEqualTo(RemoteServerInfo))
            {
                ShutdownInitiated = true;
            }

            _eventLog.Add(new ServerEvent
            {
                EventType = EventType.ReceivedShutdownServerCommand,
                RemoteServerIpAddress = RemoteServerSessionIpAddress,
                RemoteServerPortNumber = RemoteServerPort
            });

            EventOccurred?.Invoke(this, _eventLog.Last());
        }

        void ShutdownListenSocket()
        {
            EventOccurred?.Invoke(this,
                new ServerEvent
                { EventType = EventType.ShutdownListenSocketStarted });

            try
            {
                _listenSocket.Shutdown(SocketShutdown.Both);
                _listenSocket.Close();
            }
            catch (SocketException ex)
            {
                _log.Error("Error raised in method Shutdown_listenSocket", ex);
                var errorMessage = $"{ex.Message} ({ex.GetType()} raised in method TplSocketServer.Shutdown_listenSocket)";

                EventOccurred?.Invoke(this,
                    new ServerEvent
                    {
                        EventType = EventType.ShutdownListenSocketCompletedWithError,
                        ErrorMessage = errorMessage
                    });
            }

            EventOccurred?.Invoke(this,
                new ServerEvent
                    {EventType = EventType.ShutdownListenSocketCompletedWithoutError});
        }
    }
}