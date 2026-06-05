using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes.Handlers;
using DuetControlServer.Files;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines;

/// <summary>
/// Pipeline element for dealing with codes that have been resolved or cancelled.
/// This is the only pipeline stage that cannot maintain more than one stack level
/// </summary>
public sealed class Executed : PipelineBase
{
    // Private fields
    private readonly ChannelProcessor _channelProcessor;
    private readonly Utility.EventLogger _eventLogger;
    private readonly LinkInterface _linkInterface;
    private readonly Model.ObjectModel _model;
    private readonly ICodeHandler _gCodes;
    private readonly ICodeHandler _mCodes;
    private readonly ICodeHandler _tCodes;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IOptions<Settings> _settings;
    private readonly PipelineStackItem _stackItem;

    /// <summary>
    /// Constructor of this class
    /// </summary>
    /// <param name="channelProcessor">Channel processor</param>
    /// <param name="codeProcessor">Code processor</param>
    /// <param name="eventLogger">Event logger</param>
    /// <param name="model">Object model</param>
    /// <param name="gCodes">G-code handler</param>
    /// <param name="mCodes">M-code handler</param>
    /// <param name="tCodes">T-code handler</param>
    /// <param name="lifetime">Application lifetime</param>
    /// <param name="settings">Application settings</param>
    public Executed(ChannelProcessor channelProcessor,
        CodeProcessor codeProcessor,
        Utility.EventLogger eventLogger,
        LinkInterface linkInterface,
        Model.ObjectModel model,
        [FromKeyedServices(Keys.GCodes)] ICodeHandler gCodes,
        [FromKeyedServices(Keys.MCodes)] ICodeHandler mCodes,
        [FromKeyedServices(Keys.TCodes)] ICodeHandler tCodes,
        IHostApplicationLifetime lifetime,
        IOptions<Settings> settings) : base(PipelineStage.Executed, channelProcessor, codeProcessor, lifetime, settings)
    {
        _channelProcessor = channelProcessor;
        _eventLogger = eventLogger;
        _linkInterface = linkInterface;
        _model = model;
        _gCodes = gCodes;
        _mCodes = mCodes;
        _tCodes = tCodes;
        _lifetime = lifetime;
        _settings = settings;

        _stackItem = _stack.Peek();
    }

    /// <inheritdoc />
    public override async Task ProcessCodeAsync(Commands.Code code)
    {
        if (code.Result is not null)
        {
            // Update the file position
            await code.UpdateNextFilePositionAsync(code.CancellationToken);

            // Notify code handlers
            switch (code.Type)
            {
                case CodeType.GCode:
                    await _gCodes.CodeExecutedAsync(code, code.CancellationToken);
                    break;

                case CodeType.MCode:
                    await _mCodes.CodeExecutedAsync(code, code.CancellationToken);
                    break;

                case CodeType.TCode:
                    await _tCodes.CodeExecutedAsync(code, code.CancellationToken);
                    break;
            }

            // Check if the result came from a DSF-only source
            if (!code.Flags.HasFlag(CodeFlags.IsPostProcessed))
            {
                // RepRapFirmware generally prefixes error messages with the code itself, mimic this behavior if DSF resolved this code
                if (code.Result.Type == MessageType.Error)
                {
                    code.Result.Content = code.ToShortString() + ": " + code.Result.Content;
                }

                // Messages from RRF and replies to file print codes are logged somewhere else,
                // so we only need to log internal code replies that are not part of file prints
                if (code.File is null || !code.IsFromFileChannel)
                {
                    if (code.ReplyLogLevel is not null)
                    {
                        // Use the log level specified by the firmware
                        await _eventLogger.LogAsync(code.ReplyLogLevel.Value, code.Result);
                    }
                    else
                    {
                        await _eventLogger.LogAsync(code.Result);
                    }
                }
            }
            else if (code.ReplyLogLevel is not null and not EventLogLevel.Off)
            {
                // Firmware-handled code with explicit log level - respect it
                if (code.File is null || !code.IsFromFileChannel)
                {
                    await _eventLogger.LogAsync(code.ReplyLogLevel.Value, code.Result);
                }
            }

            // Deal with firmware emulation
            if (!code.Flags.HasFlag(CodeFlags.IsFromMacro))
            {
                if (await _model.IsEmulatingMarlinAsync(code.Channel))
                {
                    if (code.Flags.HasFlag(CodeFlags.IsLastCode))
                    {
                        if (code.Result is null || string.IsNullOrEmpty(code.Result.Content))
                        {
                            code.Result = new Message(MessageType.Success, "ok\n");
                        }
                        else if (code.Type == CodeType.MCode && code.MajorNumber == 105)
                        {
                            code.Result.Content = "ok " + code.Result.Content + "\n";
                        }
                        else
                        {
                            code.Result.AppendLine("ok\n");
                        }
                    }
                }
                else if (code.Result is null || string.IsNullOrEmpty(code.Result.Content))
                {
                    code.Result = new Message(MessageType.Success, "\n");
                }
                else
                {
                    code.Result.AppendLine(string.Empty);
                }
            }
        }

        try
        {
            // Send it to the Executed processor
            await IPC.Processors.CodeInterception.InterceptAsync(code, InterceptionMode.Executed);

            // Deal with its result if applicable
            if (code.Result is not null)
            {
                // Output and log the result from async codes
                if (code.Flags.HasFlag(CodeFlags.Asynchronous))
                {
                    if (code.Flags.HasFlag(CodeFlags.IsFromFirmware) ||
                        code.Channel is CodeChannel.USB or CodeChannel.USB2 or CodeChannel.Aux or CodeChannel.Aux2)
                    {
                        // Check what kind of message this is
                        MessageTypeFlags flags = (MessageTypeFlags)(1 << (int)code.Channel);
                        if (code.Result.Type != MessageType.Success)
                        {
                            flags |= (code.Result.Type == MessageType.Error) ? MessageTypeFlags.ErrorMessageFlag : MessageTypeFlags.WarningMessageFlag;
                        }

                        // Split the message into multiple chunks so RRF can output it
                        Memory<byte> encodedMessage = Encoding.UTF8.GetBytes(code.Result.ToString());
                        for (int i = 0; i < encodedMessage.Length; i += _settings.Value.MaxMessageLength)
                        {
                            if (i + _settings.Value.MaxMessageLength >= encodedMessage.Length)
                            {
                                Memory<byte> partialMessage = encodedMessage[i..];
                                _linkInterface.SendMessage(flags, Encoding.UTF8.GetString(partialMessage.ToArray()));
                            }
                            else
                            {
                                Memory<byte> partialMessage = encodedMessage.Slice(i, Math.Min(encodedMessage.Length - i, _settings.Value.MaxMessageLength));
                                _linkInterface.SendMessage(flags | MessageTypeFlags.PushFlag, Encoding.UTF8.GetString(partialMessage.ToArray()));
                            }
                        }
                    }
                    else if (code.IsFromFileChannel)
                    {
                        await _eventLogger.LogOutputAsync(code.Result);
                    }
                    else
                    {
                        await _model.OutputAsync(code.Result, _lifetime.ApplicationStopping);
                    }
                }

                // Done
                _channelProcessor.Logger.LogDebug("Finished code {Code}", code);
                code.SetFinished();
            }
            else
            {
                // Cancelled
                _channelProcessor.Logger.LogDebug("Cancelled code {Code}", code);
                code.SetCancelled();
            }
        }
        catch (Exception e)
        {
            // Failed to finish code (IPC error?)
            if ((e is OperationCanceledException) != _lifetime.ApplicationStopping.IsCancellationRequested)
            {
                ChannelProcessor.Logger.LogError(e, "Executed interceptor threw an exception when finishing code {Code}", code);
            }
            code.SetException(e);
        }
    }

    /// <inheritdoc />
    public override Task<bool> FlushAsync(CodeFile file, CancellationToken cancellationToken = default) => _stackItem.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override Task<bool> FlushAsync(Commands.Code code, CancellationToken cancellationToken = default) => _stackItem.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override void WriteCode(Commands.Code code) => _stackItem.WriteCode(code);

    /// <inheritdoc />
    public override ValueTask WriteCodeAsync(Commands.Code code) => _stackItem.WriteCodeAsync(code);
}
