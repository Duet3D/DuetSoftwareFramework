using System;
using System.Text.Json;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Standard direct-connect display screen
    /// </summary>
    public partial class DirectDisplayScreen : ModelObject, IDynamicModelObject
    {
        /// <summary>
        /// Number of colour bits
        /// </summary>
        public int ColourBits
        {
            get => _colourBits;
            set => SetPropertyValue(ref _colourBits, value);
        }
        private int _colourBits = 1;

        /// <summary>
        /// Display type
        /// </summary>
        public DirectDisplayController Controller
        {
            get => _controller;
            set => SetPropertyValue(ref _controller, value);
        }
        private DirectDisplayController _controller = DirectDisplayController.ST7920;

        /// <summary>
        /// Height of the display screen in pixels
        /// </summary>
        public int Height
        {
            get => _height;
            set => SetPropertyValue(ref _height, value);
        }
        private int _height = 64;

        /// <summary>
        /// SPI frequency of the display (in Hz)
        /// </summary>
        public int SpiFreq
        {
            get => _spiFreq;
            set => SetPropertyValue(ref _spiFreq, value);
        }
        private int _spiFreq;

        /// <summary>
        /// Width of the display screen in pixels
        /// </summary>
        public int Width
        {
            get => _width;
            set => SetPropertyValue(ref _width, value);
        }
        private int _width = 128;

        /// <summary>
        /// Figure out the required type for the given direct display screen
        /// </summary>
        /// <param name="controller">Display controller type</param>
        /// <returns>Required type</returns>
        private static Type GetDirectDisplayScreenType(DirectDisplayController? controller)
        {
            return controller switch
            {
                DirectDisplayController.ST7920 => typeof(DirectDisplayScreen),
                DirectDisplayController.ST7567 => typeof(DirectDisplayScreenST7567),
                DirectDisplayController.ILI9488 => typeof(DirectDisplayScreen),
                _ => typeof(DirectDisplayScreen)
            };
        }

        /// <summary>
        /// Update this instance from a given JSON element
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <returns>Updated instance</returns>
        /// <exception cref="JsonException">Failed to deserialize data</exception>
        public IDynamicModelObject? UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties)
        {
            if (jsonElement.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (jsonElement.TryGetProperty("type", out JsonElement nameProperty))
            {
                DirectDisplayController directDisplayController = JsonSerializer.Deserialize<DirectDisplayController>(nameProperty.GetString()!);
                Type requiredType = GetDirectDisplayScreenType(directDisplayController);
                if (GetType() != requiredType)
                {
                    DirectDisplayScreen newInstance = (DirectDisplayScreen)Activator.CreateInstance(requiredType)!;
                    return newInstance.UpdateFromJson(jsonElement, ignoreSbcProperties);
                }
            }
            return GeneratedUpdateFromJson(jsonElement, ignoreSbcProperties);
        }

#if false
        /// <summary>
        /// Update this instance from a given JSON reader
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <returns>Updated instance</returns>
        /// <exception cref="JsonException">Failed to deserialize data</exception>

        public IDynamicModelObject? UpdateFromJsonReader(ref Utf8JsonReader reader, bool ignoreSbcProperties)
        {
            Utf8JsonReader readerCopy = reader;
            while (readerCopy.Read())
            {
                if (readerCopy.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("type"u8))
                {
                    DirectDisplayController directDisplayController = JsonSerializer.Deserialize<DirectDisplayController>(readerCopy.GetString()!);
                    Type requiredType = GetDirectDisplayScreenType(directDisplayController);
                    if (GetType() != requiredType)
                    {
                        DirectDisplayScreen newInstance = (DirectDisplayScreen)Activator.CreateInstance(requiredType)!;
                        return newInstance.UpdateFromJsonReader(ref reader, ignoreSbcProperties);
                    }
                }
            }
            return GeneratedUpdateFromJsonReader(ref reader, ignoreSbcProperties);
        }
#endif
    }
}
