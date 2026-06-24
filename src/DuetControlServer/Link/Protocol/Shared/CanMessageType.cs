namespace DuetControlServer.Link.Protocol.Shared;

// TODO either auto generate this from CANlib or autogenerate CANlib from this

public enum CanMessageType : ushort
{
// High-priority requests sent by the main board
	EmergencyStop = 0,
	Startup = 10,
	ControlledStop = 20,
	TimeSync = 30,
	PowerFailing = 40,
	StopMovement = 45,
	InsertHiccup = 46,
	RevertPosition = 47,
	// unused_was_movement = 50,
	// unused_was_movementLinear = 51,
	MovementLinearShaped = 52,

	// High priority responses sent by expansion boards and Smart Tools
	// unused_was_inputStateChangedV0 = 100,
	Event = 102,
	EnterTestMode = 104, // sent by the ATE to the main board
	InputStateChangedV1 = 105,
	InputStateChangedV2 = 106,

	// Configuration messages sent by the main board
	SetAddressAndNormalTiming = 2010,
	SetFastTiming = 2011,
	Reset = 2012,

	// Medium priority messages sent by the main board
	WriteGpio = 4012,
	ReadInputsRequest = 4013,
	StartAccelerometer = 4014,
	StartClosedLoopDataCollection = 4015,

	// Configuration messages sent by the main board
	// unused_was_m950 = 6010,
	// unused_was_m308 = 6011,
	// unused_was_updateHeaterModelV0 = 6012,
	// unused was setHeaterTemperatureV0 = 6013,
	// unused_was_setPressureAdvanceV0 = 6014,
	SetDateTime = 6015,
	UpdateDeltaParameters = 6016,
	// unused_was_setMotorCurrents = 6017,
	M569 = 6018,
	FanParameters = 6019,
	M915 = 6020,
	// unused_was_setMicrostepping = 6021,
	// unused_was_setStandstillCurrentFactor = 6022,
	SetDriverStates = 6023,
	ReturnInfo = 6024,
	UpdateFirmware = 6025,
	M950Heater = 6026,
	M950Fan = 6027,
	M950Gpio = 6028,
	SetFanSpeed = 6029,
	SetHeaterFaultDetection = 6030,
	M308V1 = 6031,
	HeaterTuningCommand = 6032,
	// unused_was_heaterFeedForward = 6033,
	AccelerometerConfig = 6034,
	M950Led = 6035,

	// unused_was_createInputMonitorV0 = 6036,
	// unused_was_changeInputMonitorV0 = 6037,

	AcknowledgeAnnounce = 6038,
	SetHeaterMonitors = 6039,
	DiagnosticTest = 6040,

	M569P1 = 6041,
	SetStepsPerMmAndMicrostepping = 6042,
	SetMotorCurrents = 6043,
	SetPressureAdvanceV1 = 6044,
	SetStandstillCurrentFactor = 6045,
	CreateFilamentMonitor = 6046,
	DeleteFilamentMonitor = 6047,
	ConfigureFilamentMonitor = 6048,
	// unused_was_updateHeaterModelV1 = 6049,
	M569P2 = 6050,
	M569P6 = 6051,
	M569P7 = 6052,
	// unused_was_heaterModelV2 = 6053,
	// unused_was_setInputShaping = 6054,
	WriteLedStrip = 6055,
	M569P4 = 6056,

	// In RRF 3.5.0rc3 the message sent to report an input monitor state change has changed.
	// To prevent users successfully configuring endstops on remote boards which then don't work, we have changed the
	// IDs of createInputMonitorV1 and changeInputMonitorV1. With luck this will abort any moves involving endstops with
	// "Failed to enable endstops".

	// unused_was_createInputMonitorV1 = 6057,
	// unused_was_changeInputMonitorV1 = 6058,

	TestReport = 6059,
	CreateInputMonitorV1 = 6060, // was 6057 before 3.5.0-rc.3
	ChangeInputMonitorV1 = 6061, // was 6058 before 3.5.0-rc.3
	SetInputShapingV1 = 6062,
	HeaterFeedForwardV1 = 6063,
	M655 = 6064, // for M655, added in RRF 3.6
	EnableStallEndstop = 6065,
	M111 = 6066,
	SetDefaultHeaterModel = 6067,  // added in RRF 3.7
	SetHeaterTemperatureV1 = 6068, // added in RRF 3.7
	HeaterModelV3 = 6069,		   // added in RRF 3.7
	SetPressureAdvanceV2 = 6070,

	// Responses, broadcasts etc. sent by expansion boards
	StandardReply = 4510,
	BoardStatusReportV0 = 4511,
	AnnounceV0 = 4512, // announce message sent by firmware 3.4.0beta4 and earlier
	// FanTachoReport = 4513,					// unused
	SensorTemperaturesReport = 4514,
	HeatersStatusReport = 4515,
	// Unused_was_fansRpmReport = 4516,			// replaced by fansReport
	FansReport = 4517,
	ReadInputsReplyV0 = 4518,
	DriversStatusReport = 4519,
	// Unused_was_filamentMonitorsStatusReportV0 = 4520,
	HeaterTuningReport = 4521,
	AccelerometerData = 4522,
	ClosedLoopData = 4523,
	LogMessage = 4524,
	AnnounceV1 = 4525, // announce message sent by firmware 3.4.0beta5 and later
	DebugText = 4526,
	// Unused_was_filamentMonitorsStatusReportV1 = 4527,
	FilamentMonitorsStatusReportV2 = 4528,
	ReadInputsReplyV1 = 4529,
	BoardStatusReportV1 = 4530,
	HeaterModelReport = 4531, // added in firmware 3.7

	// Firmware updates
	FirmwareBlockRequest = 5000,
	FirmwareBlockResponse = 5001,

	UnusedMessageType = 0xFFFF
}