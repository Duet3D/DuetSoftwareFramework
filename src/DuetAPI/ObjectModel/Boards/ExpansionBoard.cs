namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about an expansion board connected over CAN
/// </summary>
public partial class ExpansionBoard : Board
{
    /// <summary>
    /// Closed loop data of this board or null if unknown
    /// </summary>
    public BoardClosedLoop? ClosedLoop
    {
        get => _closedLoop;
        set => SetPropertyValue(ref _closedLoop, value);
    }
    private BoardClosedLoop? _closedLoop;

    /// <summary>
    /// Information about an inductive sensor or null if not present
    /// </summary>
    public InductiveSensor? InductiveSensor
    {
        get => _inductiveSensor;
        set => SetPropertyValue(ref _inductiveSensor, value);
    }
    private InductiveSensor? _inductiveSensor;

    /// <summary>
    /// State of this board
    /// </summary>
    public BoardState State
    {
        get => _state;
        set => SetPropertyValue(ref _state, value);
    }
    private BoardState _state;

    /// <summary>
    /// Connection timeout of this board (in s)
    /// </summary>
    public int Timeout
    {
        get => _timeout;
        set => SetPropertyValue(ref _timeout, value);
    }
    private int _timeout = 10;
}
