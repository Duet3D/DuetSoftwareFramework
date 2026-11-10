using System;
using System.Globalization;
using System.Text;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Heat;

/// <summary>
/// A heater's process model, a first order process with dead time
/// </summary>
/// <remarks>
/// RepRapFirmware's <c>FopDt</c>. The object model's <see cref="DuetAPI.ObjectModel.HeaterModel"/> is where a
/// model is kept, and <see cref="CanMessageHeaterModelV3"/>, which carries everything <c>FopDt</c> stores, is
/// what a model is checked and sent as
/// </remarks>
public static class FopDt
{
    /// <summary>
    /// RepRapFirmware's <c>NormalAmbientTemperature</c>, in C
    /// </summary>
    public const float NormalAmbientTemperature = 25.0f;

    /// <summary>
    /// Temperature the PID constants of a model set by M307 are calculated at, in C
    /// </summary>
    public const float SetModelPidTemperature = 100.0f;

    /// <summary>
    /// Print fan PWM the PID constants are calculated for
    /// </summary>
    private const float PidFanPwm = 0.2f;

    /// <summary>
    /// PID controller parameters, as RepRapFirmware stores them
    /// </summary>
    /// <param name="KP">Controller gain</param>
    /// <param name="RecipTi">Reciprocal of the controller integral time</param>
    /// <param name="TD">Controller differential time</param>
    public readonly record struct PidParameters(float KP, float RecipTi, float TD)
    {
        /// <summary>
        /// Append the parameters as M301 reports them, with the gain scaled from 0..1 to 0..255
        /// </summary>
        public void AppendM301(StringBuilder builder)
        {
            float reportedKP = KP * 255.0f;
            builder.Append(CultureInfo.InvariantCulture, $"P{reportedKP:F1} I{RecipTi * reportedKP:F3} D{TD * reportedKP:F1}");
        }
    }

    /// <summary>
    /// The model a new heater has: CANlib's <c>DefaultToolHeaterModel</c>, disabled
    /// </summary>
    /// <remarks>RepRapFirmware's <c>FopDt::Reset</c></remarks>
    public static void Reset(DuetAPI.ObjectModel.HeaterModel model)
    {
        CanMessageHeaterModelV3 message = new() { BasicModel = HeaterModel.DefaultToolHeaterModel, MaxPwm = 1.0f };
        Apply(message, model, message.BasicModel.TypicalTemperature);
    }

    /// <summary>
    /// Store a default model a board reported, enabled, with its PID constants at the model's typical temperature
    /// </summary>
    /// <remarks>RepRapFirmware's <c>FopDt::SetDefaultModel</c></remarks>
    public static void SetDefaultModel(DuetAPI.ObjectModel.HeaterModel model, in HeaterModel basicModel)
    {
        CanMessageHeaterModelV3 message = new() { BasicModel = basicModel, MaxPwm = 1.0f, Enabled = true };
        Apply(message, model, basicModel.TypicalTemperature);
    }

    /// <summary>
    /// Recalculate the PID constants of a model for a target temperature
    /// </summary>
    /// <remarks>RepRapFirmware's <c>FopDt::CalcPidConstants</c></remarks>
    public static void CalcPidConstants(DuetAPI.ObjectModel.HeaterModel model, HeaterFunction function, float targetTemperature)
        => Apply(ToCanMessage(0, function, model), model, targetTemperature);

    /// <summary>
    /// Why a model cannot be used, or null if it can
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>FopDt::SetParameters</c>, which a board applies to a model it is sent as well. S may go
    /// down to 0.01, hence 0.0099
    /// </remarks>
    public static string? Validate(in CanMessageHeaterModelV3 message)
    {
        HeaterModel model = message.BasicModel;
        return (model.HeatingRate / model.BasicCoolingRate < 0.1f) ? "estimated temperature rise too small"
             : (model.FanCoolingRate < 0.0f) ? "fan reduces cooling rate"
             : (model.CoolingRateExponent < 1.0f || model.CoolingRateExponent > 1.6f) ? "cooling rate exponent out of range"
             : (model.DeadTime <= 0.0099f) ? "dead time too small"
             : (model.DeadTime * model.BasicCoolingRate > 50.0f) ? "dead time too close to cooling time constant"
             : (message.MaxPwm <= 0.0099f || message.MaxPwm > 1.0f) ? "PWM out of range"
             : null;
    }

    /// <summary>
    /// CANlib's default model for a heater function
    /// </summary>
    public static HeaterModel DefaultModel(HeaterFunction function) => function switch
    {
        HeaterFunction.Bed => HeaterModel.DefaultBedHeaterModel,
        HeaterFunction.Chamber => HeaterModel.DefaultChamberHeaterModel,
        _ => HeaterModel.DefaultToolHeaterModel,
    };

    /// <summary>
    /// The message that gives the object model's model of a heater to the board running it
    /// </summary>
    /// <param name="heaterNumber">The heater</param>
    /// <param name="function">What the heater is for, which decides the fields the object model does not keep</param>
    /// <param name="model">The heater's model</param>
    /// <remarks>
    /// RepRapFirmware's <c>FopDt::SetupCanMessage</c>. Every default model has a temperature coefficient of 0 and the
    /// typical temperature of its function, and nothing else sets them, so they come from the default for the
    /// heater's function. RRF keeps those of the last default model applied instead, which differs only for a heater
    /// given a model by M307 before its function changed
    /// </remarks>
    public static CanMessageHeaterModelV3 ToCanMessage(int heaterNumber, HeaterFunction function,
                                                       DuetAPI.ObjectModel.HeaterModel model)
        => new()
        {
            Heater = (byte)heaterNumber,
            Enabled = model.Enabled,
            Inverted = model.Inverted,
            MaxPwm = model.MaxPwm,
            BasicModel = new HeaterModel
            {
                HeatingRate = model.HeatingRate,
                BasicCoolingRate = model.CoolingRate,
                FanCoolingRate = model.FanCoolingRate,
                CoolingRateExponent = model.CoolingExp,
                DeadTime = model.DeadTime,
                TemperatureCoefficient = DefaultModel(function).TemperatureCoefficient,
                TypicalTemperature = DefaultModel(function).TypicalTemperature,
                StandardVoltage = model.StandardVoltage,
                UsePid = model.PID.Used,
            },
        };

    /// <summary>
    /// Store a model in the object model, with PID constants for <paramref name="pidTemperature"/>
    /// </summary>
    public static void Apply(in CanMessageHeaterModelV3 message, DuetAPI.ObjectModel.HeaterModel model, float pidTemperature)
    {
        model.HeatingRate = message.BasicModel.HeatingRate;
        model.CoolingRate = message.BasicModel.BasicCoolingRate;
        model.FanCoolingRate = message.BasicModel.FanCoolingRate;
        model.CoolingExp = message.BasicModel.CoolingRateExponent;
        model.DeadTime = message.BasicModel.DeadTime;
        model.StandardVoltage = message.BasicModel.StandardVoltage;
        model.MaxPwm = message.MaxPwm;
        model.Inverted = message.Inverted;
        model.Enabled = message.Enabled;
        model.PID.Used = message.BasicModel.UsePid;

        PidParameters load = CalcLoadChangePid(message.BasicModel, pidTemperature);
        model.PID.P = load.KP;
        model.PID.I = load.RecipTi * load.KP;
        model.PID.D = load.TD * load.KP;
    }

    /// <summary>
    /// Cooling rate per K of temperature rise at a target temperature, with the print fan at <see cref="PidFanPwm"/>
    /// </summary>
    private static float AverageCoolingRatePerDegC(in HeaterModel model, float targetTemperature)
    {
        float temperatureRise = MathF.Max(targetTemperature - NormalAmbientTemperature, 1.0f);
        return model.GetTotalCoolingRate(temperatureRise, PidFanPwm) / temperatureRise;
    }

    /// <summary>
    /// PID constants for responding to a setpoint change at a target temperature
    /// </summary>
    /// <remarks>RepRapFirmware's <c>FopDt::CalcPidConstants</c>, <c>setpointChangeParams</c></remarks>
    public static PidParameters CalcSetpointChangePid(in HeaterModel model, float targetTemperature)
        => new(0.7f / (model.HeatingRate * model.DeadTime),
               MathF.Sqrt(AverageCoolingRatePerDegC(model, targetTemperature) / model.DeadTime),
               model.DeadTime * 0.7f);

    /// <summary>
    /// PID constants for holding a target temperature against load changes, which the object model reports
    /// </summary>
    /// <remarks>RepRapFirmware's <c>FopDt::CalcPidConstants</c>, <c>loadChangeParams</c></remarks>
    public static PidParameters CalcLoadChangePid(in HeaterModel model, float targetTemperature)
        => new(0.7f / (model.HeatingRate * model.DeadTime),
               MathF.Pow(AverageCoolingRatePerDegC(model, targetTemperature), 0.25f) / (1.14f * MathF.Pow(model.DeadTime, 0.75f)),
               model.DeadTime * 0.7f);

    /// <summary>
    /// Append the report M307 gives for a model
    /// </summary>
    /// <param name="builder">Where to append it</param>
    /// <param name="message">The model</param>
    /// <param name="includeVoltage">Whether to report the calibration voltage, which only tool heaters do</param>
    /// <param name="pidTemperature">Temperature the reported PID constants are calculated at</param>
    /// <remarks>RepRapFirmware's <c>FopDt::AppendModelParameters</c></remarks>
    public static void AppendModelParameters(StringBuilder builder, in CanMessageHeaterModelV3 message, bool includeVoltage,
                                             float pidTemperature)
    {
        HeaterModel model = message.BasicModel;
        builder.Append(CultureInfo.InvariantCulture,
                       $"Heater {message.Heater}: heating rate {model.HeatingRate:F3}, cooling rate {model.BasicCoolingRate:F3}");
        if (model.FanCoolingRate > 0.0f)
        {
            builder.Append(CultureInfo.InvariantCulture, $"/{model.FanCoolingRate:F3}");
        }
        builder.Append(CultureInfo.InvariantCulture,
                       $", dead time {model.DeadTime:F2}, max PWM {message.MaxPwm:F2}, mode {(model.UsePid ? "PID" : "bang-bang")}");
        if (message.Inverted)
        {
            builder.Append(", reverse control");
        }
        if (includeVoltage)
        {
            builder.Append(CultureInfo.InvariantCulture, $", calibrated at {model.StandardVoltage:F1}V");
        }
        builder.Append(CultureInfo.InvariantCulture,
                       $"\nPredicted max temperature rise {(int)model.EstimateMaxTemperatureRise()}°C");
        if (model.UsePid)
        {
            builder.Append("\nPID parameters: heating ");
            CalcSetpointChangePid(model, pidTemperature).AppendM301(builder);
            builder.Append(", steady ");
            CalcLoadChangePid(model, pidTemperature).AppendM301(builder);
        }
    }

    /// <summary>
    /// Append the M307 command that sets a model, followed by a newline
    /// </summary>
    /// <param name="builder">Where to append it</param>
    /// <param name="message">The model</param>
    /// <param name="includeVoltage">Whether to write V, which only tool heaters do</param>
    /// <remarks>RepRapFirmware's <c>FopDt::AppendM307Command</c></remarks>
    public static void AppendM307Command(StringBuilder builder, in CanMessageHeaterModelV3 message, bool includeVoltage)
    {
        HeaterModel model = message.BasicModel;
        builder.Append(CultureInfo.InvariantCulture,
                       $"M307 H{message.Heater} R{model.HeatingRate:F3} K{model.BasicCoolingRate:F3}:{model.FanCoolingRate:F3} "
                       + $"D{model.DeadTime:F2} E{model.CoolingRateExponent:F2} S{message.MaxPwm:F2} B{(model.UsePid ? 0 : 1)}");
        if (message.Inverted)
        {
            builder.Append(" I1");
        }
        if (includeVoltage)
        {
            builder.Append(CultureInfo.InvariantCulture, $" V{model.StandardVoltage:F1}");
        }
        builder.Append('\n');
    }
}
