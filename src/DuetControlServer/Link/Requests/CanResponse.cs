using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using System.Runtime.InteropServices;
using System.Text;

namespace DuetControlServer.Link;

/// <summary>
/// Result of a CAN request, exposing the reassembled reply.
/// </summary>
/// <param name="Status">Status of the reply</param>
/// <param name="ResponseType">Actual type of the reply</param>
/// <param name="SrcAddress">Source address of the replying board</param>
/// <param name="Payload">Reassembled payload of the reply</param>
public readonly record struct CanResponse(CanStatus Status, CanMessageType ResponseType, byte SrcAddress, byte[] Payload)
{
    /// <summary>
    /// Create a response from a completed request
    /// </summary>
    /// <param name="request">Completed CAN request</param>
    internal static CanResponse FromRequest(CanRequest request)
        => new(request.Status, request.ResponseType, request.SrcAddress, request.ResponsePayload);

    /// <summary>
    /// Interpret the start of the reply payload as a CAN message body struct
    /// </summary>
    /// <typeparam name="T">CAN message body type</typeparam>
    /// <returns>Deserialized message body</returns>
    public readonly T As<T>() where T : struct => MemoryMarshal.Read<T>(Payload);

    public string PayloadString => Encoding.ASCII.GetString(Payload);
}
