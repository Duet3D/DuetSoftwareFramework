using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Standard direct-connect display screen
    /// </summary>
    [JsonDerivedType(typeof(DirectDisplayScreenST7567))]
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

            if (jsonElement.TryGetProperty("type", out JsonElement typeProperty))
            {
                if (typeProperty.GetString() == "ST7567")
                {
                    if (this is not DirectDisplayScreenST7567)
                    {
                        DirectDisplayScreenST7567 newInstance = new();
                        return newInstance.UpdateFromJson(jsonElement, ignoreSbcProperties);
                    }
                }
                else if (this is DirectDisplayScreenST7567)
                {
                    DirectDisplayScreen newInstance = new();
                    return newInstance.UpdateFromJson(jsonElement, ignoreSbcProperties);
                }
            }
            return GeneratedUpdateFromJson(jsonElement, ignoreSbcProperties);
        }

        /// <summary>
        /// Update this instance from a given JSON reader
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <returns>Updated instance</returns>
        /// <exception cref="JsonException">Failed to deserialize data</exception>

        public IDynamicModelObject? UpdateFromJsonReader(ref Utf8JsonReader reader, bool ignoreSbcProperties)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("expected start of object");
            }

            Utf8JsonReader readerCopy = reader;
            while (readerCopy.Read() && readerCopy.TokenType != JsonTokenType.EndObject)
            {
                if (readerCopy.TokenType == JsonTokenType.PropertyName)
                {
                    if (readerCopy.ValueTextEquals("type"u8) && readerCopy.Read())
                    {
                        if (readerCopy.GetString() == "ST7567")
                        {
                            if (this is not DirectDisplayScreenST7567)
                            {
                                DirectDisplayScreenST7567 newInstance = new();
                                return newInstance.UpdateFromJsonReader(ref reader, ignoreSbcProperties);
                            }
                        }
                        else if (this is DirectDisplayScreenST7567)
                        {
                            DirectDisplayScreen newInstance = new();
                            return newInstance.UpdateFromJsonReader(ref reader, ignoreSbcProperties);
                        }
                    }
                    else
                    {
                        readerCopy.Skip();
                    }
                }
            }
            return GeneratedUpdateFromJsonReader(ref reader, ignoreSbcProperties);
        }
    }
}
