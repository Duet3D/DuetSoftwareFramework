using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Enumeration of supported analog sensor types
    /// </summary>
    [JsonConverter(typeof(AnalogSensorTypeConverter))]
    public enum AnalogSensorType
    {
        /// <summary>
        /// Regular temperature thermistor
        /// </summary>
        Thermistor,

        /// <summary>
        /// PT1000 sensor
        /// </summary>
        PT1000,

        /// <summary>
        /// RTD MAX31865
        /// </summary>
        MAX31865,

        /// <summary>
        /// MAX31855 thermocouple
        /// </summary>
        MAX31855,

        /// <summary>
        /// MAX31856 thermocouple
        /// </summary>
        MAX31856,

        /// <summary>
        /// Linear analog sensor
        /// </summary>
        LinearAnalaog,

        /// <summary>
        /// DHT11 sensor
        /// </summary>
        DHT11,

        /// <summary>
        /// DHT21 sensor
        /// </summary>
        DHT21,

        /// <summary>
        /// DHT22 sensor
        /// </summary>
        DHT22,

        /// <summary>
        /// DHT humidity sensor
        /// </summary>
        DHTHumidity,

        /// <summary>
        /// BME280 sensor
        /// </summary>
        BME280,

        /// <summary>
        /// BME280 pressure sensor
        /// </summary>
        BME280Pressure,

        /// <summary>
        /// BME280 humidity sensor
        /// </summary>
        BME280Humidity,

        /// <summary>
        /// Current loop sensor
        /// </summary>
        CurrentLoop,

        /// <summary>
        /// MCU temperature
        /// </summary>
        McuTemp,

        /// <summary>
        /// On-board stepper driver sensors
        /// </summary>
        Drivers,

        /// <summary>
        /// Stepper driver sensors on the DueX expansion board
        /// </summary>
        DriversDuex,

        /// <summary>
        /// Unknown temperature sensor
        /// </summary>
        Unknown
	}

    /// <summary>
    /// Class to convert an <see cref="AnalogSensorType"/> to and from JSON
    /// </summary>
    public class AnalogSensorTypeConverter : JsonConverter<AnalogSensorType>
    {
        /// <summary>
        /// Read an <see cref="AnalogSensorType"/> from JSON
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="typeToConvert">Type to convert</param>
        /// <param name="options">Serializer options</param>
        /// <returns>Read value</returns>
        public override AnalogSensorType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return reader.GetString()!.ToLowerInvariant() switch
                {
                    "thermistor" => AnalogSensorType.Thermistor,
                    "pt1000" => AnalogSensorType.PT1000,
                    "rtdmax31865" => AnalogSensorType.MAX31865,
                    "thermocouplemax31855" => AnalogSensorType.MAX31855,
                    "thermocouplemax31856" => AnalogSensorType.MAX31856,
                    "linearanalog" => AnalogSensorType.LinearAnalaog,
                    "dht11" => AnalogSensorType.DHT11,
                    "dht21" => AnalogSensorType.DHT21,
                    "dht22" => AnalogSensorType.DHT22,
                    "bme280" => AnalogSensorType.BME280,
                    "bme280-pressure" => AnalogSensorType.BME280Pressure,
                    "bme280-humidity" => AnalogSensorType.BME280Humidity,
                    "dhthumidity" => AnalogSensorType.DHTHumidity,
                    "currentloooppyro" => AnalogSensorType.CurrentLoop,
                    "mcutemp" => AnalogSensorType.McuTemp,
                    "drivers" => AnalogSensorType.Drivers,
                    "driversduex" => AnalogSensorType.DriversDuex,
                    _ => AnalogSensorType.Unknown,
                };
            }
            throw new JsonException($"Invalid type for {nameof(AnalogSensorType)}");
        }

        /// <summary>
        /// Write an <see cref="AnalogSensorType"/> to JSON
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="value">Value to serialize</param>
        /// <param name="options">Write options</param>
        public override void Write(Utf8JsonWriter writer, AnalogSensorType value, JsonSerializerOptions options)
        {
            switch (value)
            {
                case AnalogSensorType.Thermistor:
                    writer.WriteStringValue("thermistor");
                    break;
                case AnalogSensorType.PT1000:
                    writer.WriteStringValue("pt1000");
                    break;
                case AnalogSensorType.MAX31865:
                    writer.WriteStringValue("rtdmax31865");
                    break;
                case AnalogSensorType.MAX31855:
                    writer.WriteStringValue("thermocouplemax31855");
                    break;
                case AnalogSensorType.MAX31856:
                    writer.WriteStringValue("thermocouplemax31856");
                    break;
                case AnalogSensorType.LinearAnalaog:
                    writer.WriteStringValue("linearanalog");
                    break;
                case AnalogSensorType.DHT11:
                    writer.WriteStringValue("dht11");
                    break;
                case AnalogSensorType.DHT21:
                    writer.WriteStringValue("dht21");
                    break;
                case AnalogSensorType.DHT22:
                    writer.WriteStringValue("dht22");
                    break;
                case AnalogSensorType.DHTHumidity:
                    writer.WriteStringValue("dhthumidity");
                    break;
                case AnalogSensorType.BME280:
                    writer.WriteStringValue("bme280");
                    break;
                case AnalogSensorType.BME280Pressure:
                    writer.WriteStringValue("bme280-pressure");
                    break;
                case AnalogSensorType.BME280Humidity:
                    writer.WriteStringValue("bme280-humidity");
                    break;
                case AnalogSensorType.CurrentLoop:
                    writer.WriteStringValue("currentlooppyro");
                    break;
                case AnalogSensorType.McuTemp:
                    writer.WriteStringValue("mcutemp");
                    break;
                case AnalogSensorType.Drivers:
                    writer.WriteStringValue("drivers");
                    break;
                case AnalogSensorType.DriversDuex:
                    writer.WriteStringValue("driversduex");
                    break;
                default:
                    writer.WriteStringValue("unknown");
                    break;
            }
        }
    }
}
