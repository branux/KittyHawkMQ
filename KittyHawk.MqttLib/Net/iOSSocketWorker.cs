﻿
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using KittyHawk.MqttLib.Interfaces;
using KittyHawk.MqttLib.Messages;
using KittyHawk.MqttLib.Plugins.Logging;
using CoreFoundation;
using Foundation;

namespace KittyHawk.MqttLib.Net
{
    internal delegate NSStreamPair GetStreamHandler(CFSocket socket, NSRunLoop runLoop, SslProtocols encryption);

    internal class ConnectedClientInfo : IDisposable
    {
        //public CFSocket Socket { get; set; }

        public int Port { get; set; }

        public NSStreamPair Stream { get; set; }

        public SslProtocols Encryption { get; set; }
        public string ClientUid { get; set; }

        private Timer _closeConnectionTimer;
        private int _timeout;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="timeout">Timeout time in seconds</param>
        /// <param name="disconnectCallback"></param>
        public void StartConnectionTimer(int timeout, TimerCallback disconnectCallback)
        {
            // Per spec, disconnect after 1.5X the timeout time
            _timeout = timeout*1500;
            _closeConnectionTimer = new Timer(disconnectCallback, ClientUid, _timeout, _timeout);
        }

        public void ResetTimeout()
        {
            if (_closeConnectionTimer != null)
            {
                _closeConnectionTimer.Change(_timeout, _timeout);
            }
        }

        public void Dispose()
        {
            if (_closeConnectionTimer != null)
                _closeConnectionTimer.Dispose();

            Stream.Input.Dispose();
            Stream.Output.Dispose();
           // Socket.Dispose ();
        }
    }

    internal class iOSSocketWorker : IDisposable
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, ConnectedClientInfo> _connectedClients = new Dictionary<string, ConnectedClientInfo>();
        private CancellationTokenSource _tokenSource;
        private bool _disposed = false;

        public NSRunLoop RunLoop { get; private set; }

        public iOSSocketWorker(ILogger logger)
        {
            _logger = logger;

            ClientReceiverThreadProc ();
            // Start the receiver thread
           // ClientReceiverThreadProc();
        }

        // Callback handlers
        private NetworkReceiverEventHandler _messageReceivedHandler;
        private ClientDisconnectedHandler _clientDisconnectedHandler;

        public void OnMessageReceived(NetworkReceiverEventHandler handler)
        {
            _messageReceivedHandler = handler;
        }

        public void OnClientTimeout(ClientDisconnectedHandler handler)
        {
            _clientDisconnectedHandler = handler;
        }

      //  private GetStreamHandler _getStreamHandler;

//        public void OnGetStream(GetStreamHandler handler)
//        {
//            _getStreamHandler = handler;
//        }

        public bool IsEncrypted(string clientUid)
        {
            lock (_connectedClients)
            {
                if (_connectedClients.ContainsKey(clientUid))
                {
                    return _connectedClients[clientUid].Encryption != SslProtocols.None;
                }
                return false;
            }
        }

        // ISocketAdapter helpers
        public bool IsConnected(string clientUid)
        {
            if (clientUid == null)
            {
                return false;
            }

            lock (_connectedClients)
            {
                return _connectedClients.ContainsKey(clientUid);
            }
        }

        public void ConnectTcpClient(NSStreamPair streams, int port, SocketEncryption encryption, string connectionKey)
        {
            var encryptionLevel = SslProtocols.None;

            switch (encryption)
            {
                case SocketEncryption.None:
                    encryptionLevel = SslProtocols.None;
                    break;
                case SocketEncryption.Ssl:
                    encryptionLevel = SslProtocols.Ssl3;
                    break;
                case SocketEncryption.Tls10:
                    encryptionLevel = SslProtocols.Tls;
                    break;
                case SocketEncryption.Tls11:
                    encryptionLevel = SslProtocols.Tls11;
                    break;
                case SocketEncryption.Tls12:
                    encryptionLevel = SslProtocols.Tls12;
                    break;
            }

            lock (_connectedClients)
            {
                _logger.LogMessage("Socket", LogLevel.Verbose, string.Format("Adding new TCP client: key={0}", connectionKey));

                var clientInfo = new ConnectedClientInfo
                {
                    //Socket = socket,
                    Stream = streams,
                    Port = port,
                    Encryption = encryptionLevel,
                    ClientUid = connectionKey
                };
                _connectedClients.Add(connectionKey, clientInfo);

                ListeningInputStream (clientInfo);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="uid"></param>
        /// <param name="oldConnectionKey"></param>
        /// <param name="timeout">Timeout time in seconds</param>
        public void ConnectMqttClient(string uid, string oldConnectionKey, int timeout)
        {
            // If timeout == 0, we never disconnect the client due to a keep alive timer
            if (timeout == 0)
            {
                return;
            }

            lock (_connectedClients)
            {
                if (_connectedClients.ContainsKey(oldConnectionKey))
                {
                    _logger.LogMessage("Socket", LogLevel.Verbose, string.Format("Converting TCP client (key={0}) to MQTT client (ClientUID={1})", oldConnectionKey, uid));

                    var connectionInfo = _connectedClients[oldConnectionKey];
                    connectionInfo.ClientUid = uid;
                    connectionInfo.StartConnectionTimer(timeout, DisconnectOnTimeout);

                    // Now that MQTT client is officially connected, track connection under client uid instead of hashcode
                    _connectedClients.Remove(oldConnectionKey);  // remove but do not Dispose!

                    // Note: It is possible that the client was never disconnected/removed before issuing another connect request
                    // Can also happen if client sends multiple connect requests
                    // If so, cleanup stale connection info
                    if (_connectedClients.ContainsKey(uid))
                    {
                        _logger.LogMessage("Socket", LogLevel.Verbose, "Recyling MQTT connection information for ClientUID=" + uid);
                        var stale = _connectedClients[uid];
                        stale.Dispose();
                        _connectedClients.Remove(uid);
                    }
                    else
                    {
                        _logger.LogMessage("Socket", LogLevel.Verbose, "Finished adding MQTT client (ClientUID=" + uid + ")");
                    }

                    // Now re-add new connection
                    _connectedClients.Add(uid, connectionInfo);
                }
            }
        }

        public void ResetConnectionTimer(string clientUid)
        {
            lock (_connectedClients)
            {
                if (_connectedClients.ContainsKey(clientUid))
                {
                    var connectionInfo = _connectedClients[clientUid];
                    connectionInfo.ResetTimeout();
                }
            }
        }

        public void WriteAsync(SocketEventArgs args)
        {
            NSOutputStream stream = GetStreamForConnectionConext (args);

            if (stream == null)
            {
                // OnCompleted called in GetStreamForConnectionConext(), just return here.
                return;
            }

            try
            {
                EventHandler<NSStreamEventArgs> handler = null;
                handler = (_, e1) =>
                {
                    stream.OnEvent -= handler;

                    if (e1.StreamEvent == NSStreamEvent.ErrorOccurred)
                    {
                        args.SocketException = new Exception ("Something unexpected happened. " + e1.StreamEvent.ToString ());
                        args.Complete ();
                    }

                    if (e1.StreamEvent != NSStreamEvent.HasSpaceAvailable)
                        return;

                    WriteAsyncInternal (stream, args);
                };

                if (stream.HasSpaceAvailable ())
                    WriteAsyncInternal (stream, args);
                else
                    stream.OnEvent += handler;

            }
            catch (ObjectDisposedException)
            {
                // Effectively ignoring this
                args.Complete ();
            }
            catch (Exception ex)
            {
                args.SocketException =
                    new Exception ("Unable to write to the TCP connection. See inner exception for details.", ex);
                args.Complete ();
            }
        }

        private void WriteAsyncInternal(NSOutputStream stream, SocketEventArgs args)
        {
            byte[] sendBuffer = args.MessageToSend.Serialize();

            EventHandler<NSStreamEventArgs> completedHandler = null;
            completedHandler = (sender, e) =>
            {
                stream.OnEvent -= completedHandler;

                if (args.MessageToSend is IMqttIdMessage)
                {
                    var msgWithId = args.MessageToSend as IMqttIdMessage;
                    _logger.LogMessage("Socket", LogLevel.Verbose,
                        string.Format("Sent message type '{0}', ID={1}.", msgWithId.MessageType,
                            msgWithId.MessageId));
                }
                else
                {
                    _logger.LogMessage("Socket", LogLevel.Verbose,
                        string.Format("Sent message type '{0}'.", args.MessageToSend.MessageType));
                }

                if (e.StreamEvent == NSStreamEvent.ErrorOccurred)
                {
                    args.SocketException = new Exception("Socket error occured: " + e.StreamEvent.ToString());
                }

                args.Complete();
            };

            stream.OnEvent += completedHandler;
            stream.Write(sendBuffer, (nuint) sendBuffer.Length);
        }

        public void Disconnect(string clientUid)
        {
            ConnectedClientInfo clientInfo = null;

            lock (_connectedClients)
            {
                if (_connectedClients.ContainsKey(clientUid))
                {
                    clientInfo = _connectedClients[clientUid];
                    _connectedClients.Remove(clientUid);
                }
            }

            if (clientInfo != null)
            {
                if (clientInfo.Stream != null)
                {
                    clientInfo.Stream.Input.Close();
                    clientInfo.Stream.Output.Close();
                }

                clientInfo.Dispose();
            }
        }

        private void DisconnectOnTimeout(object key)
        {
            _logger.LogMessage("Socket", LogLevel.Verbose, "Disconnecting a client due to keep alive timer expiration. ClientUID=" + key);
            Disconnect((string)key);
            _clientDisconnectedHandler((string)key, ClientDisconnectedReason.KeepAliveTimeExpired);
        }

        public void DisconnectAllOnPort(int port)
        {
            lock (_connectedClients)
            {
                var clientsOnPort = _connectedClients.Where(kvp => kvp.Value.Port == port).ToList();

                Parallel.ForEach(clientsOnPort, kvp =>
                {
                    if (kvp.Value.Stream != null)
                    {
                        kvp.Value.Stream.Input.Close();
                        kvp.Value.Stream.Output.Close();
                    }
                    //kvp.Value.Socket.Dispose();
                });

                foreach (var kvp in clientsOnPort)
                {
                    kvp.Value.Dispose();
                    _connectedClients.Remove(kvp.Key);
                }
            }
        }

        public void DisconnectAll()
        {
            lock (_connectedClients)
            {
                Parallel.ForEach(_connectedClients.Values, clientInfo =>
                {
                    if (clientInfo.Stream != null)
                    {
                        clientInfo.Stream.Input.Close();
                        clientInfo.Stream.Output.Close();
                    }
                //    clientInfo.Socket.Dispose();
                    clientInfo.Dispose();
                });

                _connectedClients.Clear();
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
            }

            _tokenSource.Cancel();
            _disposed = true;
        }

        ~iOSSocketWorker()
        {
            Dispose(false);
        }

        private void ListeningInputStream(ConnectedClientInfo info)
        {
            NSInputStream stream = info.Stream.Input;

            stream.OnEvent += (_, e) => 
            {
                if(e.StreamEvent == NSStreamEvent.HasBytesAvailable)
                {
                    var buffer = ReadFromInputStream(stream, info.ClientUid);
                    if (buffer != null && buffer.Length > 0)
                    {
                        ProcessBuffer(buffer, info);
                    }
                }
                else if(e.StreamEvent == NSStreamEvent.ErrorOccurred)
                {
                    _logger.LogMessage("Socket", LogLevel.Error, "Some error occured within the input stream");
                }
            };
        }

        private void ClientReceiverThreadProc()
        {
            _tokenSource = new CancellationTokenSource();
            var token = _tokenSource.Token;

            var receiverThread = new Thread(() =>  
            {
                _logger.LogMessage("Socket", LogLevel.Verbose, "Starting receiver thread loop.");

                RunLoop = NSRunLoop.Current;

                do
                {
                    RunLoop.Run();

                    Thread.Sleep(MqttProtocolInformation.InternalSettings.SocketReceiverThreadLoopDelay);
                }while(!token.IsCancellationRequested);
            });

            receiverThread.Start();
        }

        private byte[] ReadFromInputStream(NSInputStream stream, string clientUid)
        {
            var header = new MqttFixedHeader();
            var headerByte = new byte[1];
            nint receivedSize;

            // Read the fixed header
            do
            {
                receivedSize = stream.Read(headerByte, 0, (nuint) headerByte.Length);
            } while (receivedSize > 0 && header.AppendByte(headerByte[0]));

            if (!header.IsComplete)
            {
                _logger.LogMessage("Socket", LogLevel.Error,
                    string.Format("Read header operation could not read header, aborting."));
                return null;
            }

            _logger.LogMessage("Socket", LogLevel.Verbose,
                string.Format("Received message header type '{0}' from client {1}.", header.MessageType, clientUid));
            //_logger.LogMessage("Socket", LogLevel.Warning,
            //    string.Format("Received message header=0x{0:X}, Remaining length={1}.", header.Buffer[0], header.RemainingLength));

            // Create a buffer and read the remaining message
            var completeBuffer = header.CreateMessageBuffer();

            receivedSize = 0;
            while (receivedSize < header.RemainingLength)
            {
                receivedSize += stream.Read(completeBuffer, header.HeaderSize + (int)receivedSize, (nuint)(header.RemainingLength - receivedSize));
            }
            //_logger.LogMessage("Socket", LogLevel.Warning,
            //    string.Format("                              Bytes read=      {0}.", receivedSize));

            return completeBuffer;
        }

        private void ProcessBuffer(byte[] buffer, ConnectedClientInfo info)
        {
            ThreadPool.QueueUserWorkItem(state =>
            {
                // When receiving the ConnAck message, we are still using the ConnectionKey param
                // All other cases we've connected the client and use the ClientUid param
                var args = new MqttNetEventArgs
                {
                    ClientUid = info.ClientUid
                };

                try
                {
                    // Process incomming messages
                    args.Message = MqttMessageDeserializer.Deserialize(buffer);
                    if (args.Message is IMqttIdMessage)
                    {
                        var msgWithId = args.Message as IMqttIdMessage;
                        _logger.LogMessage("Socket", LogLevel.Verbose,
                            string.Format("Received message type '{0}', ID={1}, from client {2}.", msgWithId.MessageType,
                                msgWithId.MessageId, info.ClientUid));
                    }
                    else
                    {
                        _logger.LogMessage("Socket", LogLevel.Verbose,
                            string.Format("Received message type '{0}' from client {1}.", args.Message.MessageType, info.ClientUid));
                    }
                }
                catch (Exception ex)
                {
                    var outer = new Exception(string.Format("Error deserializing message from network buffer. Buffer may be corrupt. Details: {0}", ex.Message), ex);
                    args.Exception = outer;
                    _logger.LogMessage("Socket", LogLevel.Error, outer.Message);
                }

                if (_messageReceivedHandler != null)
                {
                    _messageReceivedHandler(args);
                }
            });
        }

        private void ProcessException(Exception ex)
        {
            ThreadPool.QueueUserWorkItem(state => _messageReceivedHandler(new MqttNetEventArgs
            {
                Exception = ex
            }));
        }

        private NSOutputStream GetStreamForConnectionConext(SocketEventArgs args)
        {
            NSOutputStream stream;
            lock (_connectedClients)
            {
                if (!_connectedClients.ContainsKey(args.ClientUid))
                {
                    args.SocketException = new InvalidOperationException("No remote connection has been established.");
                    args.Complete();
                    return null;
                }

                ConnectedClientInfo info = _connectedClients[args.ClientUid];

                try
                {
                    stream = info.Stream.Output;
                }
                catch (Exception ex)
                {
                    args.SocketException = ex;
                    args.Complete();
                    return null;
                }
            }
            return stream;
        }
    }
}
