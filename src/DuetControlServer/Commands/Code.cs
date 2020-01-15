﻿using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Machine;
using DuetControlServer.Codes;
using DuetControlServer.IPC.Processors;
using DuetControlServer.SPI;
using Nito.AsyncEx;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.Code"/> command
    /// </summary>
    public class Code : DuetAPI.Commands.Code
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        #region Code Scheduling
        /// <summary>
        /// Number of code types. This is roughly equivalent to a priority level but since
        /// there is no real preference, it is not called that way
        /// </summary>
        private const int NumCodeTypes = 4;

        /// <summary>
        /// Array of AsyncLocks to guarantee the ordered start of incoming G/M/T-codes
        /// </summary>
        /// <remarks>
        /// AsyncLock implements an internal waiter queue, so it is safe to rely on it for
        /// maintaining the right order of codes being executed per code channel
        /// </remarks>
        private static readonly AsyncLock[,] _codeStartLocks = new AsyncLock[Channels.Total, NumCodeTypes];

        /// <summary>
        /// Array of AsyncLocks to guarantee the ordered finishing of G/M/T-codes
        /// </summary>
        private static readonly AsyncLock[,] _codeFinishLocks = new AsyncLock[Channels.Total, NumCodeTypes];

        /// <summary>
        /// List of cancellation tokens to cancel pending codes while they are waiting for their execution
        /// </summary>
        private static readonly CancellationTokenSource[] _cancellationTokenSources = new CancellationTokenSource[Channels.Total];

        /// <summary>
        /// Initialize the code scheduler
        /// </summary>
        public static void Init()
        {
            for (int i = 0; i < Channels.Total; i++)
            {
                for (int k = 0; k < NumCodeTypes; k++)
                {
                    _codeStartLocks[i, k] = new AsyncLock();
                    _codeFinishLocks[i, k] = new AsyncLock();
                }
                _cancellationTokenSources[i] = CancellationTokenSource.CreateLinkedTokenSource(Program.CancelSource.Token);

                FileLocks[i] = new AsyncLock();
            }
        }

        /// <summary>
        /// Cancel pending codes of the given channel
        /// </summary>
        /// <param name="channel">Channel to cancel codes from</param>
        public static void CancelPending(CodeChannel channel)
        {
            lock (_cancellationTokenSources)
            {
                // Cancel and dispose the existing CTS
                CancellationTokenSource oldCTS = _cancellationTokenSources[(int)channel];
                oldCTS.Cancel();
                oldCTS.Dispose();

                // Create a new one
                _cancellationTokenSources[(int)channel] = new CancellationTokenSource();
            }
        }

        /// <summary>
        /// Internal type assigned by the code scheduler
        /// </summary>
        private int _codeType;

        /// <summary>
        /// Lock that is maintained as long as this code blocks the execution of the next code
        /// </summary>
        private IDisposable _codeChannelLock;

        /// <summary>
        /// Create a task that waits until this code can be executed.
        /// It may be cancelled if this code is supposed to be cancelled before it is started
        /// </summary>
        /// <returns>Lock to maintain while the code is being executed internally</returns>
        /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
        private Task<IDisposable> WaitForExecution()
        {
            // Enqueued codes may be cancelled as long as they're not being executed
            CancellationToken cancellationToken;
            lock (_cancellationTokenSources)
            {
                cancellationToken = _cancellationTokenSources[(int)Channel].Token;
            }

            // Assign a priority to this code and create a task that completes when it can be started
            if (Interception.IsInterceptingConnection(SourceConnection))
            {
                _codeType = 3;
            }
            else if (Flags.HasFlag(CodeFlags.IsPrioritized))
            {
                _codeType = 2;
            }
            else if (Flags.HasFlag(CodeFlags.IsFromMacro))
            {
                _codeType = 1;
            }
            return _codeStartLocks[(int)Channel, _codeType].LockAsync(cancellationToken);
        }

        /// <summary>
        /// Start the next available G/M/T-code unless this code has already started one
        /// </summary>
        private void StartNextCode()
        {
            _codeChannelLock?.Dispose();
            _codeChannelLock = null;
        }
        #endregion

        /// <summary>
        /// Lock around the files being written
        /// </summary>
        public static AsyncLock[] FileLocks = new AsyncLock[Channels.Total];

        /// <summary>
        /// Current stream writer of the files being written to (M28/M29)
        /// </summary>
        public static StreamWriter[] FilesBeingWritten = new StreamWriter[Channels.Total];

        /// <summary>
        /// Constructor of a new code
        /// </summary>
        public Code() : base() { }

        /// <summary>
        /// Constructor of a new code which also parses the given text-based G/M/T-code
        /// </summary>
        public Code(string code) : base(code) { }

        /// <summary>
        /// Check if Marlin is being emulated
        /// </summary>
        /// <returns>True if Marlin is being emulated</returns>
        public async Task<bool> EmulatingMarlin()
        {
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                Compatibility compatibility = Model.Provider.Get.Channels[Channel].Compatibility;
                return compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP;
            }
        }

        /// <summary>
        /// Run an arbitrary G/M/T-code and wait for it to finish
        /// </summary>
        /// <returns>Result of the code</returns>
        /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
        public override Task<CodeResult> Execute()
        {
            // Wait until this code can be executed and then start it
            if (_codeType > 0)
            {
                _logger.Debug("Waiting for execution of {0} (type {1})", this, _codeType);
            }
            else
            {
                _logger.Debug("Waiting for execution of {0}", this);
            }

            Task<CodeResult> executingTask = WaitForExecution().ContinueWith(async task =>
            {
                _codeChannelLock = await task;
                return await ExecuteInternally();
            }).Unwrap();

            // Return either the task itself or null and let it finish in the background
            return Flags.HasFlag(CodeFlags.Asynchronous) ? null : executingTask;
        }

        /// <summary>
        /// Indicates whether the code has been internally processed
        /// </summary>
        public bool InternallyProcessed;

        /// <summary>
        /// Execute the given code internally
        /// </summary>
        /// <returns>Result of the code</returns>
        private async Task<CodeResult> ExecuteInternally()
        {
            string logSuffix = Flags.HasFlag(CodeFlags.Asynchronous) ? " asynchronously" : string.Empty;

            try
            {
                // Check if this code is supposed to be written to a file
                int numChannel = (int)Channel;
                using (await FileLocks[numChannel].LockAsync())
                {
                    if (FilesBeingWritten[numChannel] != null && Type != CodeType.MCode && MajorNumber != 29)
                    {
                        _logger.Debug("Writing {0}{1}", this, logSuffix);
                        FilesBeingWritten[numChannel].WriteLine(this);
                        return new CodeResult();
                    }
                }

                // Execute this code
                try
                {
                    _logger.Debug("Processing {0}{1}", this, logSuffix);
                    await Process();
                    _logger.Debug("Completed {0}{1}", this, logSuffix);
                }
                catch (OperationCanceledException oce)
                {
                    // Code has been cancelled
                    if (_logger.IsTraceEnabled)
                    {
                        _logger.Debug(oce, "Cancelled {0}{1}", this, logSuffix);
                    }
                    else
                    {
                        _logger.Debug("Cancelled {0}{1}", this, logSuffix);
                    }
                }
                catch (NotSupportedException)
                {
                    // Some codes may not be supported yet
                    Result = new CodeResult(MessageType.Error, "Code is not supported");
                    _logger.Debug("{0} is not supported{1}", this, logSuffix);
                }
                catch (Exception e)
                {
                    // This code is no longer processed if an exception has occurred
                    _logger.Error(e, "Code {0} has thrown an exception{1}", this, logSuffix);
                    throw;
                }
            }
            finally
            {
                // Make sure the next code is started no matter what happened before
                StartNextCode();
            }
            return Result;
        }

        /// <summary>
        /// Process the code
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private async Task Process()
        {
            // Attempt to process the code internally first
            if (!InternallyProcessed && await ProcessInternally())
            {
                await CodeExecuted();
                return;
            }

            // Comments are resolved in DCS but they may be interpreted by third-party plugins
            if (Type == CodeType.Comment)
            {
                Result = new CodeResult();
                await CodeExecuted();
                return;
            }

            // Let RepRapFirmware process this code and start the next one while it is busy
            Task<CodeResult> executingTask = Interface.ProcessCode(this);
            StartNextCode();

            // Obtain a lock here to maintain the order of finishing codes since TCSs may resume execution in the wrong order
            using (await _codeFinishLocks[(int)Channel, _codeType].LockAsync())
            {
                try
                {
                    // Wait for the code to be processed by RepRapFirmware
                    Result = await executingTask;
                    await CodeExecuted();
                }
                catch (OperationCanceledException)
                {
                    // Cancelling a code clears the result
                    Result = null;
                    await CodeExecuted();
                    throw;
                }
            }
        }

        /// <summary>
        /// Attempt to process this code internally
        /// </summary>
        /// <returns>Whether the code could be processed internally</returns>
        private async Task<bool> ProcessInternally()
        {
            // Pre-process this code
            if (!Flags.HasFlag(CodeFlags.IsPreProcessed))
            {
                bool resolved = await Interception.Intercept(this, InterceptionMode.Pre);
                Flags |= CodeFlags.IsPreProcessed;

                if (resolved)
                {
                    InternallyProcessed = true;
                    return true;
                }
            }

            // Attempt to process the code internally
            switch (Type)
            {
                case CodeType.GCode:
                    Result = await GCodes.Process(this);
                    break;

                case CodeType.MCode:
                    Result = await MCodes.Process(this);
                    break;

                case CodeType.TCode:
                    Result = await TCodes.Process(this);
                    break;
            }

            if (Result != null)
            {
                InternallyProcessed = true;
                return true;
            }

            // If the code could not be interpreted internally, post-process it
            if (!Flags.HasFlag(CodeFlags.IsPostProcessed))
            {
                bool resolved = await Interception.Intercept(this, InterceptionMode.Post);
                Flags |= CodeFlags.IsPostProcessed;

                if (resolved)
                {
                    InternallyProcessed = true;
                    return true;
                }
            }

            // Code has not been interpreted yet - let RRF deal with it
            return false;
        }

        /// <summary>
        /// Executed when the code has finished
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private async Task CodeExecuted()
        {
            if (Result != null)
            {
                // Process the code result
                switch (Type)
                {
                    case CodeType.GCode:
                        await GCodes.CodeExecuted(this);
                        break;

                    case CodeType.MCode:
                        await MCodes.CodeExecuted(this);
                        break;

                    case CodeType.TCode:
                        await TCodes.CodeExecuted(this);
                        break;
                }

                // RepRapFirmware generally prefixes error messages with the code itself.
                // Do this only for error messages that originate either from a print or from a macro file
                if (Flags.HasFlag(CodeFlags.IsFromMacro) || Channel == CodeChannel.File)
                {
                    foreach (Message msg in Result)
                    {
                        if (msg.Type == MessageType.Error)
                        {
                            msg.Content = ToShortString() + ": " + msg.Content;
                        }
                    }
                }

                // Deal with firmware emulation
                if (!Flags.HasFlag(CodeFlags.IsFromMacro) && await EmulatingMarlin())
                {
                    if (Result.Count != 0 && Type == CodeType.MCode && MajorNumber == 105)
                    {
                        Result[0].Content = "ok " + Result[0].Content;
                    }
                    else if (Result.IsEmpty)
                    {
                        Result.Add(MessageType.Success, "ok\n");
                    }
                    else
                    {
                        Result[^1].Content += "\nok\n";
                    }
                }

                // Log warning and error replies after the code has been processed internally
                if (InternallyProcessed)
                {
                    foreach (Message msg in Result)
                    {
                        if (msg.Type != MessageType.Success && Channel != CodeChannel.File)
                        {
                            await Utility.Logger.Log(msg);
                        }
                    }
                }
            }

            // Done
            await Interception.Intercept(this, InterceptionMode.Executed);
        }
    }
}