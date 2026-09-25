using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;

namespace DuetAPI.Commands;

/// <summary>
/// A parsed representation of a generic G/M/T-code
/// </summary>
[RequiredPermissions(SbcPermissions.CommandExecution)]
public partial class Code : Command<Message?>
{
    /// <summary>
    /// Create an empty Code representation
    /// </summary>
    public Code() { }

    /// <summary>
    /// Create a new Code instance and attempt to parse the given code string
    /// </summary>
    /// <param name="code">UTF8-encoded G/M/T-Code</param>
    public Code(string code)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(code));
        using StreamReader reader = new(stream);
        Parse(reader, this);
    }

    /// <summary>
    /// The connection ID this code was received from. If this is 0, the code originates from an internal DCS task
    /// </summary>
    /// <remarks>
    /// Usually there is no need to populate this property. It is internally overwritten by the control server on receipt
    /// </remarks>
    public int SourceConnection { get; set; }

    /// <summary>
    /// Result of this code. This property is only set when the code has finished its excution.
    /// It remains null if the code has been cancelled
    /// </summary>
    /// <remarks>
    /// This used to be of type CodeResult but since v3.2 CodeResult can read Message JSON so it should remain compatible
    /// </remarks>
    public Message? Result { get; set; }

    /// <summary>
    /// Type of the code
    /// </summary>
    public CodeType Type { get; set; } = CodeType.None;

    /// <summary>
    /// Code channel to send this code to
    /// </summary>
    public CodeChannel Channel { get; set; } = Defaults.InputChannel;

    /// <summary>
    /// Check if this code is from a file channel
    /// </summary>
    [JsonIgnore]
    public bool IsFromFileChannel { get => Channel is CodeChannel.File or CodeChannel.File2; }

    /// <summary>
    /// Line number of this code
    /// </summary>
    public long? LineNumber { get; set; }

    /// <summary>
    /// Explicit line number of this code (if any, e.g. 12 as in N12 G1 X10)
    /// </summary>
    public long? ExplicitLineNumber { get => Flags.HasFlag(CodeFlags.HasExplicitLineNumber) ? LineNumber : null; }

    /// <summary>
    /// Number of whitespaces prefixing the command content
    /// </summary>
    public byte Indent { get; set; }

    /// <summary>
    /// Type of conditional G-code (if any)
    /// </summary>
    public KeywordType Keyword { get; set; } = KeywordType.None;

    /// <summary>
    /// Argument of the conditional G-code (if any)
    /// </summary>
    public string? KeywordArgument { get; set; }

    /// <summary>
    /// Major code number (e.g. 28 in G28)
    /// </summary>
    public int? MajorNumber { get; set; }

    /// <summary>
    /// Minor code number (e.g. 3 in G54.3) or -1 if not present
    /// </summary>
    public int MinorNumber { get; set; } = -1;

    /// <summary>
    /// Flags of this code
    /// </summary>
    public CodeFlags Flags { get; set; } = CodeFlags.None;

    /// <summary>
    /// Comment of the G/M/T-code. May be null if no comment is present
    /// </summary>
    /// <remarks>
    /// The parser combines different comment segments and concatenates them as a single value.
    /// So for example a code like 'G28 (Do homing) ; via G28' causes the Comment field to be filled with 'Do homing via G28'
    /// </remarks>
    public string? Comment { get; set; }

    /// <summary>
    /// File position of this code in bytes (optional)
    /// </summary>
    public long? FilePosition { get; set; }

    /// <summary>
    /// Length of the original code in bytes (optional)
    /// </summary>
    public int? Length { get; set; }

    /// <summary>
    /// List of parsed code parameters (see <see cref="CodeParameter"/> for further information)
    /// </summary>
    /// <seealso cref="CodeParameter"/>
    public List<CodeParameter> Parameters { get; set; } = [];

    /// <summary>
    /// Copy the properties of another code instance. This is used when a code is rewritten
    /// </summary>
    /// <param name="code"></param>
    public void CopyFrom(Code code)
    {
        Type = code.Type;
        LineNumber = code.LineNumber;
        Indent = code.Indent;
        Keyword = code.Keyword;
        KeywordArgument = code.KeywordArgument;
        MajorNumber = code.MajorNumber;
        MinorNumber = code.MinorNumber;
        Flags = code.Flags;
        Comment = code.Comment;
        FilePosition = code.FilePosition;
        Length = code.Length;
        Parameters = code.Parameters;
    }

    /// <summary>
    /// Reset this instance
    /// </summary>
    public virtual void Reset()
    {
        SourceConnection = 0;
        Result = null;
        Type = CodeType.None;
        Channel = Defaults.InputChannel;
        LineNumber = null;
        Indent = 0;
        Keyword = KeywordType.None;
        KeywordArgument = null;
        MajorNumber = null;
        MinorNumber = -1;
        Flags = CodeFlags.None;
        Comment = null;
        FilePosition = Length = null;
        Length = null;
        Parameters.Clear();
    }

    /// <summary>
    /// Check if a given parameter exists
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <returns>If the parameter is present</returns>
    public bool HasParameter(char letter) => Parameters.Any(p => p.Letter == letter);

    /// <summary>
    /// Insist a parameter is there at all
    /// </summary>
    /// <param name="letter">Letter of the parameter that has to be there</param>
    /// <exception cref="MissingParameterException">Parameter not found</exception>
    /// <remarks>
    /// RepRapFirmware's GCodeBuffer::MustSee, which throws where the guard stands: a handler that
    /// has to have the parameter has nothing to do without it, so there is no result for it to go on
    /// and build
    /// </remarks>
    public void MustSee(char letter)
    {
        if (!HasParameter(letter))
        {
            throw new MissingParameterException(letter);
        }
    }

    /// <summary>
    /// Find the parameter whose letter equals c
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <returns>The parameter, or null if the letter is not in the code</returns>
    /// <remarks>
    /// The lookup every accessor is built on. It is private because a caller that wants the letter
    /// without insisting on it has <see cref="TryGetParameter"/>, which keeps the Get/TryGet pair
    /// reading the same way here as it does for every value type
    /// </remarks>
    private CodeParameter? FindParameter(char letter) => Parameters.FirstOrDefault(p => p.Letter == letter);

    /// <summary>
    /// Find a parameter that has to carry a value, refusing a letter written with nothing after it
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <returns>The parameter, or null if the letter is not in the code at all</returns>
    /// <exception cref="GCodeException">The letter carries no value</exception>
    /// <remarks>
    /// A letter with no number after it is a parse error rather than a missing parameter: the letter
    /// was given, so the code means to set something, and there is nothing to set it to. The refusal
    /// quotes the column, because it is about one value in the line rather than about the command.
    /// A letter that is not there at all is the caller's to answer, because only the caller knows
    /// whether it had a default to fall back on
    /// </remarks>
    private CodeParameter? FindValuedParameter(char letter)
    {
        CodeParameter? parameter = FindParameter(letter);
        return (parameter is not null) ? CheckValued(parameter) : null;
    }

    /// <summary>
    /// Refuse a letter that was written with nothing usable after it
    /// </summary>
    /// <param name="parameter">The parameter as it was parsed</param>
    /// <returns>The parameter, which carries a value</returns>
    /// <exception cref="GCodeException">The letter carries no value</exception>
    private static CodeParameter CheckValued(CodeParameter parameter)
        => parameter.IsNull
            ? throw new GCodeException($"expected number after '{parameter.Letter}'", parameter.Column)
            : parameter;

    /// <summary>
    /// Find a parameter that has to be there and has to carry a value
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <returns>The parameter</returns>
    /// <exception cref="MissingParameterException">Parameter not found</exception>
    /// <exception cref="GCodeException">The letter carries no value</exception>
    public CodeParameter GetValuedParameter(char letter)
        => FindValuedParameter(letter) ?? throw new MissingParameterException(letter);

    /// <summary>
    /// Try to find a parameter that has to carry a value, skipping one that is still an expression
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="parameter">Parameter if found, else null</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value</exception>
    public bool TryGetValuedParameter(char letter, [NotNullWhen(true)] out CodeParameter? parameter)
    {
        if (TryGetParameter(letter, out parameter))
        {
            CheckValued(parameter);
            return true;
        }
        return false;
    }


    /// <summary>
    /// Retrieve the parameter whose letter equals c
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="defaultValue">Value to stand in for the letter if it is not in the code (no expression),
    /// or null to require it</param>
    /// <returns>The parsed parameter instance, or one built from the default value</returns>
    /// <exception cref="MissingParameterException">Parameter not found and no default was given</exception>
    /// <remarks>
    /// A null default says the caller has nothing to fall back on, so the missing letter is refused
    /// here instead of being handed back for the caller to check, which is how every other Get
    /// accessor reads its default. A caller that wants the letter without insisting on it has
    /// <see cref="TryGetParameter"/>
    /// </remarks>
    public CodeParameter GetParameter(char letter, object? defaultValue = null)
    {
        CodeParameter? parameter = FindParameter(letter);
        if (parameter is not null)
        {
            return parameter;
        }
        return (defaultValue is not null) ? new CodeParameter(letter, defaultValue) : throw new MissingParameterException(letter);
    }

    /// <summary>
    /// Try to get a parameter by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="parameter">Parameter if found, else null</param>
    /// <returns>True if the requested parameter could be found</returns>
    public bool TryGetParameter(char letter, [NotNullWhen(true)] out CodeParameter? parameter)
    {
        foreach (CodeParameter param in Parameters)
        {
            if (param.Letter == letter && !param.IsExpression)
            {
                parameter = param;
                return true;
            }
        }
        parameter = null;
        return false;
    }

    /// <summary>
    /// Refuse a float value that falls outside the limits it was read under
    /// </summary>
    /// <param name="value">Value that was read</param>
    /// <param name="min">Lowest value that is allowed, or null for no lower limit</param>
    /// <param name="max">Highest value that is allowed, or null for no upper limit</param>
    /// <param name="parameter">Parameter the value came from</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>The value, which lies within the limits</returns>
    /// <exception cref="GCodeException">Value is outside the limits</exception>
    /// <remarks>
    /// RepRapFirmware's GCodeBuffer::GetLimitedFValue (GCodeBuffer.cpp:553). Both ends are
    /// inclusive, and the refusal quotes the column, because it is about one value in the line
    /// rather than about the command.
    /// <para>
    /// An <paramref name="errorString"/> replaces the whole refusal, column and all: it is there for
    /// a handler that refuses the value in RepRapFirmware's own words, and those come from a
    /// <c>reply.printf</c> in the handler rather than from the read that quoted a position
    /// </para>
    /// </remarks>
    private static float CheckLimits(float value, float? min, float? max, CodeParameter parameter,
                                     Func<float, string>? errorString)
    {
        // TODO exception wording matches RRF for now but should give the min/max values to be more useful after feature parity is reached
        if (min is float lowest && value < lowest)
        {
            throw (errorString is not null)
                ? new GCodeException(errorString(value))
                : new GCodeException($"parameter '{parameter.Letter}' too low", parameter.Column);
        }
        if (max is float highest && value > highest)
        {
            throw (errorString is not null)
                ? new GCodeException(errorString(value))
                : new GCodeException($"parameter '{parameter.Letter}' too high", parameter.Column);
        }
        return value;
    }

    /// <summary>
    /// Refuse an integer value that falls outside the limits it was read under
    /// </summary>
    /// <param name="value">Value that was read</param>
    /// <param name="min">Lowest value that is allowed, or null for no lower limit</param>
    /// <param name="max">Highest value that is allowed, or null for no upper limit</param>
    /// <param name="parameter">Parameter the value came from</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>The value, which lies within the limits</returns>
    /// <exception cref="GCodeException">Value is outside the limits</exception>
    /// <remarks>
    /// RepRapFirmware's GCodeBuffer::GetLimitedIValue (GCodeBuffer.cpp:592) and
    /// ::GetLimitedUIValue (GCodeBuffer.cpp:614). Both ends are inclusive; RepRapFirmware's unsigned
    /// form writes its upper limit as one past the last value it allows, so a call ported from it
    /// passes <c>maxValuePlusOne - 1</c> here. The signed and unsigned forms share one check because
    /// every value either of them can carry fits in a long.
    /// <para>
    /// The refusal quotes the column and an <paramref name="errorString"/> replaces it along with the
    /// wording, as the float form does: a value refused for what it is belongs to the place in the
    /// line it stood, whatever type went looking for it, and a handler that refuses it in
    /// RepRapFirmware's own words is writing a sentence about the command instead. RepRapFirmware
    /// quotes a column only from its float form, having only there a read that knows one
    /// </para>
    /// </remarks>
    private static long CheckLimits(long value, long? min, long? max, CodeParameter parameter,
                                    Func<long, string>? errorString)
    {
        if (min is long lowest && value < lowest)
        {
            throw (errorString is not null)
                ? new GCodeException(errorString(value))
                : new GCodeException($"parameter '{parameter.Letter}' too low", parameter.Column);
        }
        if (max is long highest && value > highest)
        {
            throw (errorString is not null)
                ? new GCodeException(errorString(value))
                : new GCodeException($"parameter '{parameter.Letter}' too high", parameter.Column);
        }
        return value;
    }

    /// <summary>
    /// Get a float parameter value
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="defaultValue">Value to return if the letter is not in the code, or null to require it</param>
    /// <param name="min">Lowest value the parameter may have, or null for no lower limit</param>
    /// <param name="max">Highest value the parameter may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>Parameter value</returns>
    /// <exception cref="MissingParameterException">Parameter not found and no default was given</exception>
    /// <exception cref="GCodeException">The letter carries no value, or one outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    /// <remarks>
    /// The limits are about what the line said, so a default is returned as it stands: it comes from
    /// the caller rather than from the code, and there is nothing to refuse it to
    /// </remarks>
    public float GetFloat(char letter, float? defaultValue = null, float? min = null, float? max = null,
                          Func<float, string>? errorString = null)
    {
        CodeParameter? parameter = FindValuedParameter(letter);
        return (parameter is not null)
            ? CheckLimits((float)parameter, min, max, parameter, errorString)
            : defaultValue ?? throw new MissingParameterException(letter);
    }

    /// <summary>
    /// Get a float parameter value that has to be greater than zero
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <returns>Parameter value</returns>
    /// <exception cref="MissingParameterException">Parameter not found</exception>
    /// <exception cref="GCodeException">The letter carries no value, or one that is not greater than zero</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    /// <remarks>
    /// RepRapFirmware's GCodeBuffer::GetPositiveFValue. The values read through it are the ones a
    /// zero would make meaningless rather than merely unusual - steps per millimetre, a timeout -
    /// which is why it is refused where it stands instead of being stored
    /// </remarks>
    public float GetPositiveFloat(char letter)
    {
        CodeParameter parameter = GetValuedParameter(letter);
        float value = (float)parameter;
        return (value > 0.0f) ? value : throw new GCodeException("value must be greater than zero", parameter.Column);
    }

    /// <summary>
    /// Try to get a float parameter value by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="parameter">Parameter if found, else default</param>
    /// <param name="min">Lowest value the parameter may have, or null for no lower limit</param>
    /// <param name="max">Highest value the parameter may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value, or one outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetFloat(char letter, out float parameter, float? min = null, float? max = null,
                            Func<float, string>? errorString = null)
    {
        if (TryGetValuedParameter(letter, out CodeParameter? param))
        {
            parameter = CheckLimits((float)param, min, max, param, errorString);
            return true;
        }
        parameter = default;
        return false;
    }

    /// <summary>
    /// Try to get a float parameter value by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="parameter">Parameter if found, else null</param>
    /// <param name="min">Lowest value the parameter may have, or null for no lower limit</param>
    /// <param name="max">Highest value the parameter may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value, or one outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetFloat(char letter, [NotNullWhen(true)] out float? parameter, float? min = null,
                            float? max = null, Func<float, string>? errorString = null)
    {
        if (TryGetValuedParameter(letter, out CodeParameter? param))
        {
            parameter = CheckLimits((float)param, min, max, param, errorString);
            return true;
        }
        parameter = null;
        return false;
    }

    /// <summary>
    /// Get an integer parameter value
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="defaultValue">Value to return if the letter is not in the code, or null to require it</param>
    /// <param name="min">Lowest value the parameter may have, or null for no lower limit</param>
    /// <param name="max">Highest value the parameter may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>Parameter value</returns>
    /// <exception cref="MissingParameterException">Parameter not found and no default was given</exception>
    /// <exception cref="GCodeException">The letter carries no value, or one outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    /// <remarks>
    /// The limits are about what the line said, so a default is returned as it stands: it comes from
    /// the caller rather than from the code, and there is nothing to refuse it to
    /// </remarks>
    public int GetInt(char letter, int? defaultValue = null, int? min = null, int? max = null,
                      Func<long, string>? errorString = null)
    {
        CodeParameter? parameter = FindValuedParameter(letter);
        return (parameter is not null)
            ? (int)CheckLimits((int)parameter, min, max, parameter, errorString)
            : defaultValue ?? throw new MissingParameterException(letter);
    }

    /// <summary>
    /// Try to get an integer parameter value by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="parameter">Parameter if found, else default</param>
    /// <param name="min">Lowest value the parameter may have, or null for no lower limit</param>
    /// <param name="max">Highest value the parameter may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value, or one outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetInt(char letter, out int parameter, int? min = null, int? max = null,
                          Func<long, string>? errorString = null)
    {
        if (TryGetValuedParameter(letter, out CodeParameter? param))
        {
            parameter = (int)CheckLimits((int)param, min, max, param, errorString);
            return true;
        }
        parameter = default;
        return false;
    }

    /// <summary>
    /// Try to get an integer parameter value by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="parameter">Parameter if found, else null</param>
    /// <param name="min">Lowest value the parameter may have, or null for no lower limit</param>
    /// <param name="max">Highest value the parameter may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value, or one outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetInt(char letter, [NotNullWhen(true)] out int? parameter, int? min = null, int? max = null,
                          Func<long, string>? errorString = null)
    {
        if (TryGetValuedParameter(letter, out CodeParameter? param))
        {
            parameter = (int)CheckLimits((int)param, min, max, param, errorString);
            return true;
        }
        parameter = null;
        return false;
    }

    /// <summary>
    /// Get an unsigned integer parameter value
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="defaultValue">Value to return if the letter is not in the code, or null to require it</param>
    /// <param name="min">Lowest value the parameter may have, or null for no lower limit</param>
    /// <param name="max">Highest value the parameter may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>Parameter value</returns>
    /// <exception cref="MissingParameterException">Parameter not found and no default was given</exception>
    /// <exception cref="GCodeException">The letter carries no value, or one outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    /// <remarks>
    /// The limits are about what the line said, so a default is returned as it stands: it comes from
    /// the caller rather than from the code, and there is nothing to refuse it to
    /// </remarks>
    public uint GetUInt(char letter, uint? defaultValue = null, uint? min = null, uint? max = null,
                        Func<long, string>? errorString = null)
    {
        CodeParameter? parameter = FindValuedParameter(letter);
        return (parameter is not null)
            ? (uint)CheckLimits((uint)parameter, min, max, parameter, errorString)
            : defaultValue ?? throw new MissingParameterException(letter);
    }

    /// <summary>
    /// Try to get an unsigned integer parameter value by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="parameter">Parameter if found, else default</param>
    /// <param name="min">Lowest value the parameter may have, or null for no lower limit</param>
    /// <param name="max">Highest value the parameter may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value, or one outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetUInt(char letter, out uint parameter, uint? min = null, uint? max = null,
                           Func<long, string>? errorString = null)
    {
        if (TryGetValuedParameter(letter, out CodeParameter? param))
        {
            parameter = (uint)CheckLimits((uint)param, min, max, param, errorString);
            return true;
        }
        parameter = default;
        return false;
    }

    /// <summary>
    /// Try to get an unsigned integer parameter value by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="parameter">Parameter if found, else null</param>
    /// <param name="min">Lowest value the parameter may have, or null for no lower limit</param>
    /// <param name="max">Highest value the parameter may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value, or one outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetUInt(char letter, [NotNullWhen(true)] out uint? parameter, uint? min = null,
                           uint? max = null, Func<long, string>? errorString = null)
    {
        if (TryGetValuedParameter(letter, out CodeParameter? param))
        {
            parameter = (uint)CheckLimits((uint)param, min, max, param, errorString);
            return true;
        }
        parameter = null;
        return false;
    }

    /// <summary>
    /// Get a long parameter value
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="defaultValue">Value to return if the letter is not in the code, or null to require it</param>
    /// <param name="min">Lowest value the parameter may have, or null for no lower limit</param>
    /// <param name="max">Highest value the parameter may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>Parameter value</returns>
    /// <exception cref="MissingParameterException">Parameter not found and no default was given</exception>
    /// <exception cref="GCodeException">The letter carries no value, or one outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    /// <remarks>
    /// The limits are about what the line said, so a default is returned as it stands: it comes from
    /// the caller rather than from the code, and there is nothing to refuse it to
    /// </remarks>
    public long GetLong(char letter, long? defaultValue = null, long? min = null, long? max = null,
                        Func<long, string>? errorString = null)
    {
        CodeParameter? parameter = FindValuedParameter(letter);
        return (parameter is not null)
            ? CheckLimits((long)parameter, min, max, parameter, errorString)
            : defaultValue ?? throw new MissingParameterException(letter);
    }

    /// <summary>
    /// Try to get a long parameter value by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="parameter">Parameter if found, else default</param>
    /// <param name="min">Lowest value the parameter may have, or null for no lower limit</param>
    /// <param name="max">Highest value the parameter may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value, or one outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetLong(char letter, out long parameter, long? min = null, long? max = null,
                           Func<long, string>? errorString = null)
    {
        if (TryGetValuedParameter(letter, out CodeParameter? param))
        {
            parameter = CheckLimits((long)param, min, max, param, errorString);
            return true;
        }
        parameter = default;
        return false;
    }

    /// <summary>
    /// Try to get a long parameter value by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="parameter">Parameter if found, else null</param>
    /// <param name="min">Lowest value the parameter may have, or null for no lower limit</param>
    /// <param name="max">Highest value the parameter may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value, or one outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetLong(char letter, [NotNullWhen(true)] out long? parameter, long? min = null,
                           long? max = null, Func<long, string>? errorString = null)
    {
        if (TryGetValuedParameter(letter, out CodeParameter? param))
        {
            parameter = CheckLimits((long)param, min, max, param, errorString);
            return true;
        }
        parameter = null;
        return false;
    }

    /// <summary>
    /// The lowest value an enumeration declares, worked out once per enumeration
    /// </summary>
    /// <typeparam name="T">The enumeration</typeparam>
    /// <remarks>
    /// Only the lowest is needed: a value at or above it that is still not a member is above every
    /// member the code could have meant, which is what makes "too high" the right half of
    /// RepRapFirmware's wording for it
    /// </remarks>
    private static class EnumBounds<T> where T : struct, Enum
    {
        /// <summary>Lowest value <typeparamref name="T"/> declares</summary>
        public static readonly long Lowest = LowestDeclared();

        /// <summary>
        /// Read the enumeration's members once, when it is first asked about
        /// </summary>
        /// <returns>The lowest value declared, or zero if it declares none</returns>
        /// <remarks>
        /// The generic overload is the one that survives AOT compilation, which cannot always build an
        /// array of an enumeration's type at run time (IL3050). It arrived in .NET 5, and DuetAPI also
        /// targets netstandard2.0, which is what the other arm is for
        /// </remarks>
        private static long LowestDeclared()
        {
#if NET5_0_OR_GREATER
            IEnumerable<T> declared = Enum.GetValues<T>();
#else
            IEnumerable<T> declared = Enum.GetValues(typeof(T)).Cast<T>();
#endif
            return declared.Select(value => Convert.ToInt64(value, CultureInfo.InvariantCulture))
                           .DefaultIfEmpty(0)
                           .Min();
        }
    }

    private static bool EnumValueExists<T>(long value) where T : struct, Enum
    {
        T member = (T)Enum.ToObject(typeof(T), value);
        return Enum.IsDefined(typeof(T), member);
    }

    /// <summary>
    /// Refuse a value that names none of an enumeration's members
    /// </summary>
    /// <typeparam name="T">Enumeration the value selects a member of</typeparam>
    /// <param name="value">Value that was read</param>
    /// <param name="letter">Letter of the parameter the value came from</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>The member the value names</returns>
    /// <exception cref="GCodeException">The value names no member</exception>
    /// <remarks>
    /// RepRapFirmware reads a value like this with GCodeBuffer::GetLimitedUIValue against the count of
    /// the enumeration's members - <c>gb.GetLimitedUIValue('A', MaxHeaterMonitorAction + 1)</c> - so
    /// the refusal is the same "too low" or "too high" any other limited read gives, and quotes no
    /// column for the same reason. That holds exactly for the enumerations a code selects from,
    /// because they number their members from one end to the other with no gaps
    /// </remarks>
    private static T CheckEnum<T>(long value, char letter, Func<long, string>? errorString) where T : struct, Enum
    {
        T member = (T)Enum.ToObject(typeof(T), value);
        if (!Enum.IsDefined(typeof(T), member))
        {
            // TODO this error message is misleading for an enum which has gaps in valid values
            // it is kept for consistent error messages with RRF but should be changed ultimately
            throw new GCodeException(errorString?.Invoke(value)
                                     ?? $"parameter '{letter}' too {(value < EnumBounds<T>.Lowest ? "low" : "high")}");
        }
        return member;
    }

    /// <summary>
    /// Get a parameter value as a member of an enumeration
    /// </summary>
    /// <typeparam name="T">Enumeration the value selects a member of</typeparam>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="defaultValue">Value to return if the letter is not in the code, or null to require it</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>Parameter value</returns>
    /// <exception cref="MissingParameterException">Parameter not found and no default was given</exception>
    /// <exception cref="GCodeException">The letter carries no value, or one that names no member</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    /// <remarks>
    /// There are no limits to give, because the enumeration is the limits: a G-code parameter that
    /// selects one of a fixed set of behaviours is refused for naming none of them, and the set is
    /// what the enumeration declares. A default is returned as it stands, as it is everywhere else
    /// here - it comes from the caller rather than from the code, and there is nothing to refuse it to
    /// </remarks>
    public T GetEnum<T>(char letter, T? defaultValue = null, Func<long, string>? errorString = null) where T : struct, Enum
    {
        CodeParameter? parameter = FindValuedParameter(letter);
        return (parameter is not null)
            ? CheckEnum<T>((long)parameter, letter, errorString)
            : defaultValue ?? throw new MissingParameterException(letter);
    }

    /// <summary>
    /// Try to get a parameter value as a member of an enumeration
    /// </summary>
    /// <typeparam name="T">Enumeration the value selects a member of</typeparam>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="parameter">Parameter if found, else default</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value, or one that names no member</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetEnum<T>(char letter, out T parameter, Func<long, string>? errorString = null) where T : struct, Enum
    {
        if (TryGetValuedParameter(letter, out CodeParameter? param))
        {
            parameter = CheckEnum<T>((long)param, letter, errorString);
            return true;
        }
        parameter = default;
        return false;
    }

    /// <summary>
    /// Try to get a parameter value as a member of an enumeration
    /// </summary>
    /// <typeparam name="T">Enumeration the value selects a member of</typeparam>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="parameter">Parameter if found, else null</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value, or one that names no member</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetEnum<T>(char letter, [NotNullWhen(true)] out T? parameter, Func<long, string>? errorString = null)
        where T : struct, Enum
    {
        if (TryGetValuedParameter(letter, out CodeParameter? param))
        {
            parameter = CheckEnum<T>((long)param, letter, errorString);
            return true;
        }
        parameter = null;
        return false;
    }

    /// <summary>
    /// Get a boolean parameter value
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="defaultValue">Value to return if the letter is not in the code, or null to require it</param>
    /// <returns>Parameter value</returns>
    /// <exception cref="MissingParameterException">Parameter not found and no default was given</exception>
    /// <exception cref="GCodeException">The letter carries no value</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    /// <remarks>
    /// There are no limits to give, because the two values a boolean can hold are both inside any
    /// range that would admit either of them
    /// </remarks>
    public bool GetBool(char letter, bool? defaultValue = null)
    {
        CodeParameter? parameter = FindValuedParameter(letter);
        return (parameter is not null) ? (bool)parameter : defaultValue ?? throw new MissingParameterException(letter);
    }

    /// <summary>
    /// Try to get a boolean parameter value by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="parameter">Parameter if found, else default</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetBool(char letter, out bool parameter)
    {
        if (TryGetValuedParameter(letter, out CodeParameter? param))
        {
            parameter = (bool)param;
            return true;
        }
        parameter = default;
        return false;
    }

    /// <summary>
    /// Try to get a boolean parameter value by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="parameter">Parameter if found, else null</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetBool(char letter, [NotNullWhen(true)] out bool? parameter)
    {
        if (TryGetValuedParameter(letter, out CodeParameter? param))
        {
            parameter = (bool)param;
            return true;
        }
        parameter = null;
        return false;
    }

    /// <summary>
    /// Get a string parameter value
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="defaultValue">Value to return if the letter is not in the code, or null to require it</param>
    /// <returns>Parameter value</returns>
    /// <exception cref="MissingParameterException">Parameter not found and no default was given</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public string GetString(char letter, string? defaultValue = null) => (string)GetParameter(letter, defaultValue);

    /// <summary>
    /// Get a string parameter value
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <returns>Parameter value or null</returns>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    /// <remarks>
    /// The one string reader that answers a missing letter with null rather than refusing it, for a
    /// caller that has no default to put in its place and still has something to do without it
    /// </remarks>
    public string? GetOptionalString(char letter) => (string?)FindParameter(letter);

    /// <summary>
    /// Try to get a string parameter value by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="parameter">Parameter if found, else null</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetString(char letter, [NotNullWhen(true)] out string? parameter)
    {
        foreach (CodeParameter param in Parameters)
        {
            if (param.Letter == letter && !param.IsExpression)
            {
                parameter = (string)param;
                return true;
            }
        }
        parameter = null;
        return false;
    }

    /// <summary>
    /// Get a driver ID parameter value
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="defaultValue">Value to return if the letter is not in the code, or null to require it</param>
    /// <returns>Parameter value</returns>
    /// <exception cref="MissingParameterException">Parameter not found and no default was given</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public DriverId GetDriverId(char letter, DriverId? defaultValue = null) => (DriverId)GetParameter(letter, defaultValue);

    /// <summary>
    /// Try to get a driver ID parameter value by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="parameter">Parameter if found, else null</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetDriverId(char letter, [NotNullWhen(true)] out DriverId? parameter)
    {
        foreach (CodeParameter param in Parameters)
        {
            if (param.Letter == letter && !param.IsExpression)
            {
                parameter = (DriverId)param;
                return true;
            }
        }
        parameter = null;
        return false;
    }

    /// <summary>
    /// Get an IP address parameter value
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="defaultValue">Value to return if the letter is not in the code, or null to require it</param>
    /// <returns>Parameter value</returns>
    /// <exception cref="MissingParameterException">Parameter not found and no default was given</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public IPAddress GetIPAddress(char letter, IPAddress? defaultValue = null) => (IPAddress)GetParameter(letter, defaultValue);

    /// <summary>
    /// Try to get a driver ID parameter value by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="parameter">Parameter if found, else null</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetIPAddress(char letter, [NotNullWhen(true)] out IPAddress? parameter)
    {
        foreach (CodeParameter param in Parameters)
        {
            if (param.Letter == letter && !param.IsExpression)
            {
                parameter = (IPAddress)param;
                return true;
            }
        }
        parameter = null;
        return false;
    }

    /// <summary>
    /// Refuse a float array whose values do not all fall inside the limits they were read under
    /// </summary>
    /// <param name="values">Values that were read</param>
    /// <param name="min">Lowest value that is allowed, or null for no lower limit</param>
    /// <param name="max">Highest value that is allowed, or null for no upper limit</param>
    /// <param name="parameter">Parameter the values came from</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>The values, which all lie within the limits</returns>
    /// <exception cref="GCodeException">A value is outside the limits</exception>
    /// <remarks>
    /// Every item is held to the same limits, and the first one outside them refuses the code. The
    /// refusal names the parameter rather than the item, because the whole list stands or falls
    /// together: a code that meant to set four drives and named an impossible value for the third
    /// asked for something the machine cannot do, not for three quarters of it
    /// </remarks>
    private static float[] CheckLimits(float[] values, float? min, float? max, CodeParameter parameter,
                                       Func<float, string>? errorString)
    {
        foreach (float value in values)
        {
            CheckLimits(value, min, max, parameter, errorString);
        }
        return values;
    }

    /// <summary>
    /// Refuse an integer array whose values do not all fall inside the limits they were read under
    /// </summary>
    /// <param name="values">Values that were read</param>
    /// <param name="min">Lowest value that is allowed, or null for no lower limit</param>
    /// <param name="max">Highest value that is allowed, or null for no upper limit</param>
    /// <param name="parameter">Parameter the values came from</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>The values, which all lie within the limits</returns>
    /// <exception cref="GCodeException">A value is outside the limits</exception>
    /// <remarks>
    /// The array counterpart of the scalar check, refusing on the first item outside the limits and
    /// naming the parameter rather than the item, for the reason the float form gives. There is one
    /// of these per array type because an int[] is not a long[], where the scalar forms share a
    /// single check through the widening every value has
    /// </remarks>
    private static int[] CheckLimits(int[] values, long? min, long? max, CodeParameter parameter,
                                     Func<long, string>? errorString)
    {
        foreach (int value in values)
        {
            CheckLimits(value, min, max, parameter, errorString);
        }
        return values;
    }

    /// <inheritdoc cref="CheckLimits(int[], long?, long?, CodeParameter, Func{long, string})" />
    private static uint[] CheckLimits(uint[] values, long? min, long? max, CodeParameter parameter,
                                      Func<long, string>? errorString)
    {
        foreach (uint value in values)
        {
            CheckLimits(value, min, max, parameter, errorString);
        }
        return values;
    }

    /// <inheritdoc cref="CheckLimits(int[], long?, long?, CodeParameter, Func{long, string})" />
    private static long[] CheckLimits(long[] values, long? min, long? max, CodeParameter parameter,
                                      Func<long, string>? errorString)
    {
        foreach (long value in values)
        {
            CheckLimits(value, min, max, parameter, errorString);
        }
        return values;
    }

    /// <summary>
    /// Build the refusal of a list that carries more items than the caller can take
    /// </summary>
    /// <param name="parameter">Parameter the items came from</param>
    /// <param name="index">Index of the first item past what the caller can take</param>
    /// <returns>The refusal</returns>
    /// <remarks>
    /// RepRapFirmware reads a list into an array of the size the caller gives and refuses the item
    /// that would not fit as it reaches it, so a line naming more than the machine has is refused
    /// rather than quietly losing its tail (StringParser::CheckArrayLength). The column quoted is
    /// the one that item begins at
    /// </remarks>
    private static GCodeException ArrayTooLong(CodeParameter parameter, int index)
        => new($"array too long for parameter '{parameter.Letter}'", parameter.GetColumn(index));

    /// <summary>
    /// Hold a list to the length it was read under, padding it if the caller allows that
    /// </summary>
    /// <typeparam name="T">Type of the items</typeparam>
    /// <param name="parameter">Parameter the items came from</param>
    /// <param name="values">Items that were read</param>
    /// <param name="maxLength">Most items the caller can take</param>
    /// <param name="pad">Whether one item stands for all of them</param>
    /// <param name="exactLength">Whether the caller needs the length it asked for</param>
    /// <returns>The items, at a length the caller can take</returns>
    /// <exception cref="GCodeException">Too many items, or not as many as the caller needs</exception>
    /// <remarks>
    /// The three rules RepRapFirmware's array reads share, in the order it applies them. The length
    /// is checked as the list is parsed, so a list that is too long is refused before a value in it
    /// can be: the firmware never reads the values of a line it has already refused. The padding
    /// happens on the way out of <c>GCodeBuffer::GetFloatArray</c> and its siblings, which is what
    /// lets one value stand for every drive. A caller that needs a fixed number of items checks the
    /// length it got afterwards, so padding can satisfy it (<c>GCodeBuffer::TryGetFloatArray</c>)
    /// </remarks>
    private static T[] CheckArray<T>(CodeParameter parameter, T[] values, int maxLength, bool pad, bool exactLength)
    {
        if (values.Length > maxLength)
        {
            throw ArrayTooLong(parameter, maxLength);
        }
        if (pad && values.Length == 1 && maxLength > 1)
        {
            values = [.. Enumerable.Repeat(values[0], maxLength)];
        }
        if (exactLength && values.Length != maxLength)
        {
            throw new GCodeException($"Wrong number of values in array, expected {maxLength}");
        }
        return values;
    }

    /// <summary>
    /// Read the list a parameter carries, or nothing from a letter written with no list after it
    /// </summary>
    /// <typeparam name="T">Type of the items</typeparam>
    /// <param name="parameter">Parameter the list came from</param>
    /// <param name="convert">Reads the parameter as a list of its type</param>
    /// <param name="maxLength">Most items the caller can take</param>
    /// <param name="pad">Whether one item stands for all of them</param>
    /// <param name="exactLength">Whether the caller needs the length it asked for</param>
    /// <returns>The items, at a length the caller can take</returns>
    /// <exception cref="GCodeException">Too many items, or not as many as the caller needs</exception>
    /// <remarks>
    /// The letter with nothing after it only reaches here when the caller allowed it, and then it
    /// means the empty list rather than a value that could not be read, so there is nothing to
    /// convert and no length to hold it to: the caller asked for what the letter names and the
    /// letter names none of them
    /// </remarks>
    private static T[] ReadArray<T>(CodeParameter parameter, Func<CodeParameter, T[]> convert,
                                    int maxLength, bool pad, bool exactLength)
        => parameter.IsNull ? [] : CheckArray(parameter, convert(parameter), maxLength, pad, exactLength);

    /// <summary>
    /// Find a parameter that has to carry a list of no more than the given length
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="maxLength">Most items the caller can take</param>
    /// <param name="allowZeroLength">Whether the letter may be written with no list after it</param>
    /// <returns>The parameter, or null if the letter is not in the code at all</returns>
    /// <exception cref="GCodeException">The caller can take nothing, or the letter carries no value</exception>
    /// <remarks>
    /// A caller that can take nothing refuses the letter before the value is looked at, because
    /// RepRapFirmware checks the length before reading each item: a machine with no extruders
    /// refuses <c>M17 E</c> as a list too long rather than as a letter with no number after it.
    /// <paramref name="allowZeroLength"/> comes before that, because a caller that reads the bare
    /// letter as an empty list has been given one rather than nothing usable
    /// </remarks>
    private CodeParameter? FindArrayParameter(char letter, int maxLength, bool allowZeroLength)
    {
        CodeParameter? parameter = FindParameter(letter);
        if (parameter is null)
        {
            return null;
        }
        if (parameter.IsNull && allowZeroLength)
        {
            return parameter;
        }
        if (maxLength == 0)
        {
            throw ArrayTooLong(parameter, 0);
        }
        return CheckValued(parameter);
    }

    /// <summary>
    /// Try to find a parameter that has to carry a list of no more than the given length, skipping
    /// one that is still an expression
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="maxLength">Most items the caller can take</param>
    /// <param name="allowZeroLength">Whether the letter may be written with no list after it</param>
    /// <param name="parameter">Parameter if found, else null</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The caller can take nothing, or the letter carries no value</exception>
    private bool TryGetArrayParameter(char letter, int maxLength, bool allowZeroLength,
                                      [NotNullWhen(true)] out CodeParameter? parameter)
    {
        if (TryGetParameter(letter, out parameter))
        {
            if (parameter.IsNull && allowZeroLength)
            {
                return true;
            }
            if (maxLength == 0)
            {
                throw ArrayTooLong(parameter, 0);
            }
            CheckValued(parameter);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Get a float array parameter value
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="maxLength">Most values the caller can take</param>
    /// <param name="defaultValue">Value to return if the letter is not in the code, or null to require it</param>
    /// <param name="pad">Whether a single value stands for every one of them</param>
    /// <param name="exactLength">Whether the caller needs as many values as it can take</param>
    /// <param name="allowZeroLength">Whether the letter may be written with no values after it, which
    /// the caller then reads as none of them</param>
    /// <param name="min">Lowest value each item may have, or null for no lower limit</param>
    /// <param name="max">Highest value each item may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>Parameter value</returns>
    /// <exception cref="MissingParameterException">Parameter not found and no default was given</exception>
    /// <exception cref="GCodeException">The letter carries no value, the list is the wrong length,
    /// or one item is outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    /// <remarks>
    /// The limits and the length are about what the line said, so a default is returned as it
    /// stands: it comes from the caller rather than from the code, and there is nothing to refuse it
    /// to
    /// </remarks>
    public float[] GetFloatArray(char letter, int maxLength, float[]? defaultValue = null, bool pad = false,
                                 bool exactLength = false, bool allowZeroLength = false,
                                 float? min = null, float? max = null,
                                 Func<float, string>? errorString = null)
    {
        CodeParameter? parameter = FindArrayParameter(letter, maxLength, allowZeroLength);
        return (parameter is not null)
            ? CheckLimits(ReadArray(parameter, static p => (float[])p, maxLength, pad, exactLength), min, max, parameter, errorString)
            : defaultValue ?? throw new MissingParameterException(letter);
    }

    /// <summary>
    /// Try to get a float array parameter value by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="maxLength">Most values the caller can take</param>
    /// <param name="parameter">Parameter if found, else null</param>
    /// <param name="pad">Whether a single value stands for every one of them</param>
    /// <param name="exactLength">Whether the caller needs as many values as it can take</param>
    /// <param name="allowZeroLength">Whether the letter may be written with no values after it, which
    /// the caller then reads as none of them</param>
    /// <param name="min">Lowest value each item may have, or null for no lower limit</param>
    /// <param name="max">Highest value each item may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value, the list is the wrong length,
    /// or one item is outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetFloatArray(char letter, int maxLength, [NotNullWhen(true)] out float[]? parameter,
                                 bool pad = false, bool exactLength = false, bool allowZeroLength = false,
                                 float? min = null, float? max = null,
                                 Func<float, string>? errorString = null)
    {
        if (TryGetArrayParameter(letter, maxLength, allowZeroLength, out CodeParameter? param))
        {
            parameter = CheckLimits(ReadArray(param, static p => (float[])p, maxLength, pad, exactLength), min, max, param, errorString);
            return true;
        }
        parameter = null;
        return false;
    }

    /// <summary>
    /// Get an integer array parameter value
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="maxLength">Most values the caller can take</param>
    /// <param name="defaultValue">Value to return if the letter is not in the code, or null to require it</param>
    /// <param name="pad">Whether a single value stands for every one of them</param>
    /// <param name="exactLength">Whether the caller needs as many values as it can take</param>
    /// <param name="allowZeroLength">Whether the letter may be written with no values after it, which
    /// the caller then reads as none of them</param>
    /// <param name="min">Lowest value each item may have, or null for no lower limit</param>
    /// <param name="max">Highest value each item may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>Parameter value</returns>
    /// <exception cref="MissingParameterException">Parameter not found and no default was given</exception>
    /// <exception cref="GCodeException">The letter carries no value, the list is the wrong length,
    /// or one item is outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    /// <remarks>
    /// An array read without <paramref name="pad"/> is the one RepRapFirmware reads with
    /// <c>GetUnsignedArray(..., false)</c>, which will not pad what it was given: the letter alone
    /// carries nothing to use, so it cannot stand for "all of them". A default supplied by the
    /// caller is a different thing and is returned as it stands
    /// </remarks>
    public int[] GetIntArray(char letter, int maxLength, int[]? defaultValue = null, bool pad = false,
                             bool exactLength = false, bool allowZeroLength = false,
                             int? min = null, int? max = null,
                             Func<long, string>? errorString = null)
    {
        CodeParameter? parameter = FindArrayParameter(letter, maxLength, allowZeroLength);
        return (parameter is not null)
            ? CheckLimits(ReadArray(parameter, static p => (int[])p, maxLength, pad, exactLength), min, max, parameter, errorString)
            : defaultValue ?? throw new MissingParameterException(letter);
    }

    /// <summary>
    /// Try to get an integer array parameter value by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="maxLength">Most values the caller can take</param>
    /// <param name="parameter">Parameter if found, else null</param>
    /// <param name="pad">Whether a single value stands for every one of them</param>
    /// <param name="exactLength">Whether the caller needs as many values as it can take</param>
    /// <param name="allowZeroLength">Whether the letter may be written with no values after it, which
    /// the caller then reads as none of them</param>
    /// <param name="min">Lowest value each item may have, or null for no lower limit</param>
    /// <param name="max">Highest value each item may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value, the list is the wrong length,
    /// or one item is outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetIntArray(char letter, int maxLength, [NotNullWhen(true)] out int[]? parameter,
                               bool pad = false, bool exactLength = false, bool allowZeroLength = false,
                               int? min = null, int? max = null,
                               Func<long, string>? errorString = null)
    {
        if (TryGetArrayParameter(letter, maxLength, allowZeroLength, out CodeParameter? param))
        {
            parameter = CheckLimits(ReadArray(param, static p => (int[])p, maxLength, pad, exactLength), min, max, param, errorString);
            return true;
        }
        parameter = null;
        return false;
    }

    /// <summary>
    /// Get an unsigned integer array parameter value
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="maxLength">Most values the caller can take</param>
    /// <param name="defaultValue">Value to return if the letter is not in the code, or null to require it</param>
    /// <param name="pad">Whether a single value stands for every one of them</param>
    /// <param name="exactLength">Whether the caller needs as many values as it can take</param>
    /// <param name="allowZeroLength">Whether the letter may be written with no values after it, which
    /// the caller then reads as none of them</param>
    /// <param name="min">Lowest value each item may have, or null for no lower limit</param>
    /// <param name="max">Highest value each item may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>Parameter value</returns>
    /// <exception cref="MissingParameterException">Parameter not found and no default was given</exception>
    /// <exception cref="GCodeException">The letter carries no value, the list is the wrong length,
    /// or one item is outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    /// <remarks>
    /// The limits and the length are about what the line said, so a default is returned as it
    /// stands: it comes from the caller rather than from the code, and there is nothing to refuse it
    /// to
    /// </remarks>
    public uint[] GetUIntArray(char letter, int maxLength, uint[]? defaultValue = null, bool pad = false,
                               bool exactLength = false, bool allowZeroLength = false,
                               uint? min = null, uint? max = null,
                               Func<long, string>? errorString = null)
    {
        CodeParameter? parameter = FindArrayParameter(letter, maxLength, allowZeroLength);
        return (parameter is not null)
            ? CheckLimits(ReadArray(parameter, static p => (uint[])p, maxLength, pad, exactLength), min, max, parameter, errorString)
            : defaultValue ?? throw new MissingParameterException(letter);
    }

    /// <summary>
    /// Try to get an unsigned integer array parameter value by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="maxLength">Most values the caller can take</param>
    /// <param name="parameter">Parameter if found, else null</param>
    /// <param name="pad">Whether a single value stands for every one of them</param>
    /// <param name="exactLength">Whether the caller needs as many values as it can take</param>
    /// <param name="allowZeroLength">Whether the letter may be written with no values after it, which
    /// the caller then reads as none of them</param>
    /// <param name="min">Lowest value each item may have, or null for no lower limit</param>
    /// <param name="max">Highest value each item may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value, the list is the wrong length,
    /// or one item is outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetUIntArray(char letter, int maxLength, [NotNullWhen(true)] out uint[]? parameter,
                                bool pad = false, bool exactLength = false, bool allowZeroLength = false,
                                uint? min = null, uint? max = null,
                                Func<long, string>? errorString = null)
    {
        if (TryGetArrayParameter(letter, maxLength, allowZeroLength, out CodeParameter? param))
        {
            parameter = CheckLimits(ReadArray(param, static p => (uint[])p, maxLength, pad, exactLength), min, max, param, errorString);
            return true;
        }
        parameter = null;
        return false;
    }

    /// <summary>
    /// Get a long array parameter value
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="maxLength">Most values the caller can take</param>
    /// <param name="defaultValue">Value to return if the letter is not in the code, or null to require it</param>
    /// <param name="pad">Whether a single value stands for every one of them</param>
    /// <param name="exactLength">Whether the caller needs as many values as it can take</param>
    /// <param name="allowZeroLength">Whether the letter may be written with no values after it, which
    /// the caller then reads as none of them</param>
    /// <param name="min">Lowest value each item may have, or null for no lower limit</param>
    /// <param name="max">Highest value each item may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>Parameter value</returns>
    /// <exception cref="MissingParameterException">Parameter not found and no default was given</exception>
    /// <exception cref="GCodeException">The letter carries no value, the list is the wrong length,
    /// or one item is outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    /// <remarks>
    /// The limits and the length are about what the line said, so a default is returned as it
    /// stands: it comes from the caller rather than from the code, and there is nothing to refuse it
    /// to
    /// </remarks>
    public long[] GetLongArray(char letter, int maxLength, long[]? defaultValue = null, bool pad = false,
                               bool exactLength = false, bool allowZeroLength = false,
                               long? min = null, long? max = null,
                               Func<long, string>? errorString = null)
    {
        CodeParameter? parameter = FindArrayParameter(letter, maxLength, allowZeroLength);
        return (parameter is not null)
            ? CheckLimits(ReadArray(parameter, static p => (long[])p, maxLength, pad, exactLength), min, max, parameter, errorString)
            : defaultValue ?? throw new MissingParameterException(letter);
    }

    /// <summary>
    /// Try to get a long array parameter value by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="maxLength">Most values the caller can take</param>
    /// <param name="parameter">Parameter if found, else null</param>
    /// <param name="pad">Whether a single value stands for every one of them</param>
    /// <param name="exactLength">Whether the caller needs as many values as it can take</param>
    /// <param name="allowZeroLength">Whether the letter may be written with no values after it, which
    /// the caller then reads as none of them</param>
    /// <param name="min">Lowest value each item may have, or null for no lower limit</param>
    /// <param name="max">Highest value each item may have, or null for no upper limit</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value, the list is the wrong length,
    /// or one item is outside the limits</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetLongArray(char letter, int maxLength, [NotNullWhen(true)] out long[]? parameter,
                                bool pad = false, bool exactLength = false, bool allowZeroLength = false,
                                long? min = null, long? max = null,
                                Func<long, string>? errorString = null)
    {
        if (TryGetArrayParameter(letter, maxLength, allowZeroLength, out CodeParameter? param))
        {
            parameter = CheckLimits(ReadArray(param, static p => (long[])p, maxLength, pad, exactLength), min, max, param, errorString);
            return true;
        }
        parameter = null;
        return false;
    }

    /// <summary>
    /// Refuse a list whose values do not all name a member of an enumeration
    /// </summary>
    /// <typeparam name="T">Enumeration the values select members of</typeparam>
    /// <param name="values">Values that were read</param>
    /// <param name="letter">Letter of the parameter the values came from</param>
    /// <param name="ignoreInvalid">Whether a value naming no member is passed over instead of
    /// refusing the list</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>The members the values name, without the ones that were passed over</returns>
    /// <exception cref="GCodeException">A value names no member</exception>
    /// <remarks>
    /// The array counterpart of <see cref="CheckEnum{T}" />, refusing on the first item that names
    /// no member and naming the parameter rather than the item, for the reason the limit checks
    /// give: the list stands or falls together.
    /// </remarks>
    private static T[] CheckEnums<T>(long[] values, char letter, bool ignoreInvalid,
                                     Func<long, string>? errorString) where T : struct, Enum
    {
        List<T> members = new(values.Length);
        foreach (long value in values)
        {
            try
            {
                members.Add(CheckEnum<T>(value, letter, errorString));
            }
            catch (GCodeException) when (ignoreInvalid)
            {
                // Passed over
            }
        }
        return [.. members];
    }

    /// <summary>
    /// Get a parameter value as a list of members of an enumeration
    /// </summary>
    /// <typeparam name="T">Enumeration the values select members of</typeparam>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="maxLength">Most values the caller can take</param>
    /// <param name="defaultValue">Value to return if the letter is not in the code, or null to require it</param>
    /// <param name="pad">Whether a single value stands for every one of them</param>
    /// <param name="exactLength">Whether the caller needs as many values as it can take</param>
    /// <param name="allowZeroLength">Whether the letter may be written with no values after it, which
    /// the caller then reads as none of them</param>
    /// <param name="ignoreInvalid">Whether a value naming no member is passed over instead of
    /// refusing the list, which shortens what comes back</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>Parameter value</returns>
    /// <exception cref="MissingParameterException">Parameter not found and no default was given</exception>
    /// <exception cref="GCodeException">The letter carries no value, the list is the wrong length,
    /// or one item names no member</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    /// <remarks>
    /// Every item is held to the enumeration, because the enumeration is the limits: one value per
    /// drive, each selecting one of a fixed set of behaviours, is refused as a whole for naming one
    /// that does not exist. There are no <c>min</c> and <c>max</c> for the same reason the scalar
    /// form has none. The length is about what the line said, so a default is returned as it stands
    /// </remarks>
    public T[] GetEnumArray<T>(char letter, int maxLength, T[]? defaultValue = null, bool pad = false,
                               bool exactLength = false, bool allowZeroLength = false, bool ignoreInvalid = false,
                               Func<long, string>? errorString = null) where T : struct, Enum
    {
        CodeParameter? parameter = FindArrayParameter(letter, maxLength, allowZeroLength);
        return (parameter is not null)
            ? CheckEnums<T>(ReadArray(parameter, static p => (long[])p, maxLength, pad, exactLength), letter, ignoreInvalid, errorString)
            : defaultValue ?? throw new MissingParameterException(letter);
    }

    /// <summary>
    /// Try to get a parameter value as a list of members of an enumeration
    /// </summary>
    /// <typeparam name="T">Enumeration the values select members of</typeparam>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="maxLength">Most values the caller can take</param>
    /// <param name="parameter">Parameter if found, else null</param>
    /// <param name="pad">Whether a single value stands for every one of them</param>
    /// <param name="exactLength">Whether the caller needs as many values as it can take</param>
    /// <param name="allowZeroLength">Whether the letter may be written with no values after it, which
    /// the caller then reads as none of them</param>
    /// <param name="ignoreInvalid">Whether a value naming no member is passed over instead of
    /// refusing the list, which shortens what comes back</param>
    /// <param name="errorString">Builds the refusal from the value it would not take, in place of the usual
    /// "too low" or "too high", or null for those</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value, the list is the wrong length,
    /// or one item names no member</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetEnumArray<T>(char letter, int maxLength, [NotNullWhen(true)] out T[]? parameter,
                                   bool pad = false, bool exactLength = false, bool allowZeroLength = false,
                                   bool ignoreInvalid = false, Func<long, string>? errorString = null)
        where T : struct, Enum
    {
        if (TryGetArrayParameter(letter, maxLength, allowZeroLength, out CodeParameter? param))
        {
            parameter = CheckEnums<T>(ReadArray(param, static p => (long[])p, maxLength, pad, exactLength), letter, ignoreInvalid, errorString);
            return true;
        }
        parameter = null;
        return false;
    }

    /// <summary>
    /// Get a driver ID array parameter value
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="maxLength">Most drivers the caller can take</param>
    /// <param name="defaultValue">Value to return if the letter is not in the code, or null to require it</param>
    /// <param name="exactLength">Whether the caller needs as many drivers as it can take</param>
    /// <param name="allowZeroLength">Whether the letter may be written with no drivers after it, which
    /// the caller then reads as none of them</param>
    /// <returns>Parameter value</returns>
    /// <exception cref="MissingParameterException">Parameter not found and no default was given</exception>
    /// <exception cref="GCodeException">The letter carries no value, or the list is the wrong length</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    /// <remarks>
    /// There are no limits to give, because a driver ID is a board and a port rather than a
    /// magnitude: which of two drivers is the greater is not a question a code asks. Nor is there
    /// padding, for the same reason RepRapFirmware's <c>GetDriverIdArray</c> has none: one driver
    /// cannot stand for every driver of a drive, because each of them is a different motor
    /// </remarks>
    public DriverId[] GetDriverIdArray(char letter, int maxLength, DriverId[]? defaultValue = null,
                                       bool exactLength = false, bool allowZeroLength = false)
    {
        CodeParameter? parameter = FindArrayParameter(letter, maxLength, allowZeroLength);
        return (parameter is not null)
            ? ReadArray(parameter, static p => (DriverId[])p, maxLength, pad: false, exactLength)
            : defaultValue ?? throw new MissingParameterException(letter);
    }

    /// <summary>
    /// Try to get a driver ID array parameter value by letter
    /// </summary>
    /// <param name="letter">Letter of the parameter to find</param>
    /// <param name="maxLength">Most drivers the caller can take</param>
    /// <param name="parameter">Parameter if found, else null</param>
    /// <param name="exactLength">Whether the caller needs as many drivers as it can take</param>
    /// <param name="allowZeroLength">Whether the letter may be written with no drivers after it, which
    /// the caller then reads as none of them</param>
    /// <returns>True if the requested parameter could be found</returns>
    /// <exception cref="GCodeException">The letter carries no value, or the list is the wrong length</exception>
    /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
    public bool TryGetDriverIdArray(char letter, int maxLength, [NotNullWhen(true)] out DriverId[]? parameter,
                                    bool exactLength = false, bool allowZeroLength = false)
    {
        if (TryGetArrayParameter(letter, maxLength, allowZeroLength, out CodeParameter? param))
        {
            parameter = ReadArray(param, static p => (DriverId[])p, maxLength, pad: false, exactLength);
            return true;
        }
        parameter = null;
        return false;
    }

    /// <summary>
    /// Reconstruct an unprecedented string from the parameter list or
    /// retrieve the parameter which does not have a letter assigned
    /// </summary>
    /// <param name="quoteStrings">Encapsulate strings in double quotes</param>
    /// <returns>Unprecedented string</returns>
    /// <remarks>
    /// If no parameter is present, an empty string is returned
    /// </remarks>
    public string GetUnprecedentedString(bool quoteStrings = false)
    {
        foreach (CodeParameter p in Parameters)
        {
            if (p.Letter == '@')
            {
                return quoteStrings ? $"\"{p.StringValue.Replace("\"", "\"\"")}\"" : p.StringValue;
            }
        }

        StringBuilder builder = new();
        foreach (CodeParameter p in Parameters)
        {
            if (builder.Length is not 0)
            {
                builder.Append(' ');
            }

            builder.Append(p.Letter);
            if (quoteStrings && !p.IsExpression && p.Type == typeof(string))
            {
                builder.Append('"');
                builder.Append(quoteStrings ? p.StringValue.Replace("\"", "\"\"") : p.StringValue);
                builder.Append('"');
            }
            else
            {
                builder.Append(p.StringValue);
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// Convert the parsed code back to a text-based G/M/T-code
    /// </summary>
    /// <returns>Reconstructed code string</returns>
    public override string ToString()
    {
        if (Keyword is not KeywordType.None)
        {
            string asString = KeywordToString() + ((KeywordArgument is null) ? string.Empty : " " + KeywordArgument);
            if (Result is not null && !string.IsNullOrEmpty(Result.Content))
            {
                asString += " => ";
                asString += Result.ToString().TrimEnd();
            }
            return asString;
        }

        if (Type == CodeType.Comment)
        {
            return ";" + Comment;
        }

        // Because it is neither always feasible nor reasonable to keep track of the original code,
        // attempt to rebuild it here. First, assemble the code letter, then the major+minor numbers (e.g. G53.4)
        StringBuilder builder = new();
        builder.Append(ToShortString());

        // After this append each parameter and encapsulate it in double quotes
        foreach (CodeParameter parameter in Parameters)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            if (parameter.Letter is not '@')
            {
                builder.Append(parameter.Letter);
            }

            if (parameter.ParsedValue is not null)
            {
                if (parameter.Type == typeof(string) && !parameter.IsExpression)
                {
                    builder.Append('"');
                    builder.Append(parameter.StringValue.Replace("\"", "\"\""));
                    builder.Append('"');
                }
                else
                {
                    builder.Append(parameter.StringValue);
                }
            }
        }

        // Then the comment is appended (if applicable)
        if (!string.IsNullOrEmpty(Comment))
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(';');
            builder.Append(Comment);
        }

        // If this code has finished, append the code result
        if (Result is not null && !string.IsNullOrEmpty(Result.Content))
        {
            builder.Append(" => ");
            builder.Append(Result.ToString().TrimEnd());
        }

        return builder.ToString();
    }

    /// <summary>
    /// Convert only the command portion to a text-based G/M/T-code (e.g. G28)
    /// </summary>
    /// <returns>Command fraction of the code</returns>
    public string ToShortString()
    {
        if (Keyword is not KeywordType.None)
        {
            return KeywordToString();
        }

        if (Type == CodeType.None)
        {
            return string.Empty;
        }

        if (Type == CodeType.Comment)
        {
            return "(comment)";
        }

        string prefix = Flags.HasFlag(CodeFlags.EnforceAbsolutePosition) ? "G53 " : string.Empty;
        if (MajorNumber is not null)
        {
            if (MinorNumber >= 0)
            {
                return prefix + $"{(char)Type}{MajorNumber}.{MinorNumber}";
            }
            return prefix + $"{(char)Type}{MajorNumber}";
        }
        return prefix + $"{(char)Type}";
    }

    /// <summary>
    /// Convert the keyword to a string
    /// </summary>
    /// <returns></returns>
    private string KeywordToString()
    {
        return Keyword switch
        {
            KeywordType.If => "if",
            KeywordType.ElseIf => "elif",
            KeywordType.Else => "else",
            KeywordType.While => "while",
            KeywordType.Break => "break",
            KeywordType.Continue => "continue",
            KeywordType.Abort => "abort",
            KeywordType.Var => "var",
            KeywordType.Set => "set",
            KeywordType.Echo => "echo",
            KeywordType.Global => "global",
            KeywordType.Skip => "skip",
            _ => throw new NotImplementedException(),
        };
    }
}
