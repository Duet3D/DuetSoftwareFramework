namespace DuetControlServer.Link.Protocol.Shared;

/// <summary>
/// Enumeration to specify the result of attempting to process a GCode command
/// </summary>
/// <remarks>
/// This needs to stay in sync with the GCodeResult definition in RepRapFirmware
/// </remarks>
public enum CodeResult : byte
{
	/// <summary>
	/// We haven't finished processing this command
	/// </summary>
	NotFinished,
	
	/// <summary>
	/// We have finished processing this code in the current state, and if the GCodeState is 'normal' then we have finished it completely
	/// </summary>
	Ok,
	
	/// <summary>
	/// The command succeeded but a warning was generated
	/// </summary>
	Warning,
	
	/// <summary>
	/// The command is not supported, but for this command we issue a warning not an error
	/// </summary>
	WarningNotSupported,
	
	/// <summary>
	/// General error, the reason will be written to the associated reply buffer
	/// </summary>
	Error,
	
	/// <summary>
	/// Error: not supported
	/// </summary>
	ErrorNotSupported,
	
	/// <summary>
	/// Not supported in current mode
	/// </summary>
	NotSupportedInCurrentMode,
	
	/// <summary>
	/// We are halted because of an emergency stop
	/// </summary>
	Stopped,
	
	/// <summary>
	/// Bad or missing parameter
	/// </summary>
	BadOrMissingParameter,
	
	/// <summary>
	/// Only used if CAN expansion is supported - can be sent by expansion boards, so don't change its number!
	/// </summary>
	RemoteInternalError,
	
	/// <summary>
	/// M291 cancelled
	/// </summary>
	M291Cancelled,
	
	/// <summary>
	/// We are waiting for a message box to be acknowledged so the command has been ignored
	/// </summary>
	WaitingForAckSoIgnored,
	
	/// <summary>
	/// Only used if CAN expansion is supported - we failed to allocate a CAN buffer to send a message to an expansion board
	/// </summary>
	NoCanBuffer,
	
	/// <summary>
	/// Only used if CAN expansion is supported - timed out waiting for a response to a CAN message - the associated reply buffer may contain more info
	/// </summary>
	CanResponseTimeout
}
