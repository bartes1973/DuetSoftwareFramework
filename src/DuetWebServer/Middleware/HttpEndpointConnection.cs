﻿using System;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Utility;

namespace DuetWebServer.Middleware
{
    /// <summary>
    /// Base class for connections that access the control server via the Duet API using a UNIX socket
    /// </summary>
    public sealed class HttpEndpointConnection : IDisposable
    {
        /// <summary>
        /// Socket used for inter-process communication
        /// </summary>
        private readonly Socket _unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        /// <summary>
        /// Establishes a connection to the given UNIX socket file
        /// </summary>
        /// <param name="socketPath">Path to the UNIX socket file</param>
        /// <exception cref="IOException">Connection mode is unavailable</exception>
        public void Connect(string socketPath)
        {
            UnixDomainSocketEndPoint endPoint = new UnixDomainSocketEndPoint(socketPath);
            _unixSocket.Connect(endPoint);
        }

        /// <summary>
        /// Indicates if this instance has been disposed
        /// </summary>
        private bool disposed;

        /// <summary>
        /// Cleans up the current connection and all resources associated to it
        /// </summary>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            _unixSocket.Dispose();

            disposed = true;
        }

        /// <summary>
        /// Returns true if the socket is still connected
        /// </summary>
        public bool IsConnected
        {
            get => !disposed && _unixSocket.Connected;
        }

        /// <summary>
        /// Closes the current connection and disposes it
        /// </summary>
        public void Close()
        {
            Dispose();
        }

        /// <summary>
        /// Send an HTTP request notification to the endpoint provider
        /// </summary>
        /// <param name="httpRequest">Received HTTP request</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        public Task SendHttpRequest(ReceivedHttpRequest httpRequest, CancellationToken cancellationToken = default)
        {
            return Send(httpRequest, cancellationToken);
        }

        /// <summary>
        /// Read a response to an HTTP request from the endpoint provider
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>HTTP response to send</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        public Task<SendHttpResponse> GetHttpResponse(CancellationToken cancellationToken = default)
        {
            return Receive<SendHttpResponse>(cancellationToken);
        }

        /// <summary>
        /// Receive a deserialized object from the server
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <typeparam name="T">Type of the received object</typeparam>
        /// <returns>Received object</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        private async Task<T> Receive<T>(CancellationToken cancellationToken)
        {
            using JsonDocument jsonDoc = await ReceiveJson(cancellationToken);
            return JsonSerializer.Deserialize<T>(jsonDoc.RootElement.GetRawText(), JsonHelper.DefaultJsonOptions);
        }

        /// <summary>
        /// Receive partially deserialized object from the server
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Partially deserialized data</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        private async Task<JsonDocument> ReceiveJson(CancellationToken cancellationToken)
        {
            using MemoryStream json = await JsonHelper.ReceiveUtf8Json(_unixSocket, cancellationToken);
            return await JsonDocument.ParseAsync(json);
        }

        /// <summary>
        /// Serialize an arbitrary object into JSON and send it to the server plus NL
        /// </summary>
        /// <param name="obj">Object to send</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        private async Task Send(object obj, CancellationToken cancellationToken)
        {
            byte[] jsonToWrite = JsonSerializer.SerializeToUtf8Bytes(obj, obj.GetType(), JsonHelper.DefaultJsonOptions);
            await _unixSocket.SendAsync(jsonToWrite, SocketFlags.None, cancellationToken);
        }
    }
}