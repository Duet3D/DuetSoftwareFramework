using DuetAPI.ObjectModel;
using DuetControlServer.Commands;
using MQTTnet;
using MQTTnet.Client;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Utility
{
    public static class MQTT
    {
        private static MqttClientOptionsBuilder? _clientOptionsBuilder = null;
        private static MqttClientSubscribeOptions? _subscribeOptions = null;

        /// <summary>
        /// Configure MQTT client options
        /// </summary>
        /// <param name="code">M586.4 code</param>
        /// <returns></returns>
        public static Message Configure(Code code)
        {
            if (_client != null && _client.IsConnected)
            {
                return new Message(MessageType.Error, "MQTT client is still connected");
            }

            // Basic client options
            _clientOptionsBuilder = new MqttClientOptionsBuilder()
                .WithClientId(code.GetString('C'))
                .WithCredentials(code.GetString('U'), code.GetString('K'));

            // Optional will message
            if (code.TryGetString('W', out string? willMessage))
            {
                _clientOptionsBuilder = _clientOptionsBuilder
                    .WithWillTopic(code.GetString('T'))
                    .WithWillPayload(willMessage)
                    .WithWillQualityOfServiceLevel((MQTTnet.Protocol.MqttQualityOfServiceLevel)code.GetInt('Q', 0))
                    .WithWillRetain(code.GetBool('R', false));
            }

            // Optional subscription
            if (code.TryGetString('S', out string? subscribeTopic))
            {
                if (!string.IsNullOrEmpty(subscribeTopic))
                {
                    _subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(subscribeTopic, (MQTTnet.Protocol.MqttQualityOfServiceLevel)code.GetInt('O'))
                    .Build();
                }
                else
                {
                    _subscribeOptions = null;
                }
            }

            // Done
            return new Message();
        }

        private static MqttFactory? _factory = null;
        private static IMqttClient? _client = null;

        /// <summary>
        /// Configure MQTT protocol state via M586 P4
        /// </summary>
        /// <returns></returns>
        public static async Task<Message> ConfigureProtocolAsync(Code code)
        {
            if (code.TryGetBool('S', out bool enableOrDisable))
            {
                // Disconnect from the server
                if (!enableOrDisable)
                {
                    if (_client != null)
                    {
                        if (_client.IsConnected)
                        {
                            await _client.DisconnectAsync();
                        }
                        _client.ApplicationMessageReceivedAsync -= MessageReceived;
                    }
                    _client = null;
                    return new Message();
                }

                // Set up client
                if (_clientOptionsBuilder == null)
                {
                    return new Message(MessageType.Error, "Use M586.4 to configure MQTT client options first");
                }
                if (_client != null)
                {
                    return new Message(MessageType.Error, "MQTT client is already connected");
                }

                // Connect to the server
                try
                {
                    var options = _clientOptionsBuilder
                        .WithTcpServer(code.GetString('H'), code.GetInt('R', 1883))
                        .Build();

                    _factory ??= new MqttFactory();
                    var client = _factory.CreateMqttClient();
                    await client.ConnectAsync(options, code.CancellationToken);
                    _client = client;
                }
                catch (Exception e)
                {
                    return new Message(MessageType.Error, "Failed to connect to MQTT server: " + e.Message);
                }

                // Subscribe to a topic if requested
                if (_subscribeOptions != null)
                {
                    try
                    {
                        _client.ApplicationMessageReceivedAsync += MessageReceived;
                        await _client.SubscribeAsync(_subscribeOptions, code.CancellationToken);
                    }
                    catch (Exception e)
                    {
                        return new Message(MessageType.Error, "Failed to subscribe to MQTT topic: " + e.Message);
                    }
                }

                // Done
                return new Message();
            }
            return new Message(MessageType.Success, $"MQTT is {(_client == null ? "disabled" : "enabled")}");
        }

        private static async Task MessageReceived(MqttApplicationMessageReceivedEventArgs arg)
        {
            await Logger.LogOutputAsync(new Message(MessageType.Success, arg.ApplicationMessage.ConvertPayloadToString()));
        }

        /// <summary>
        /// Publish a MQTT message using M118 P6
        /// </summary>
        /// <param name="code"></param>
        /// <returns></returns>
        public static async Task<Message> PublishAsync(Code code)
        {
            if (_client == null ||  !_client.IsConnected)
            {
                return new Message(MessageType.Error, "MQTT client is not connected");
            }

            try
            {
                var messageBuilder = new MqttApplicationMessageBuilder()
                    .WithTopic(code.GetString('T'))
                    .WithPayload(code.GetString('S'))
                    .WithQualityOfServiceLevel((MQTTnet.Protocol.MqttQualityOfServiceLevel)code.GetInt('Q', 0))
                    .WithRetainFlag(code.GetBool('R', false));
                await _client.PublishAsync(messageBuilder.Build(), code.CancellationToken);
            }
            catch (Exception e)
            {
                return new Message(MessageType.Error, "Failed to publish MQTT message: " + e.Message);
            }
            return new Message();
        }
    }
}
