namespace DuetAPI.ObjectModel;

/// <summary>
/// List of connected boards
/// </summary>
public class Boards : StaticModelCollection<Board>
{
    /// <inheritdoc />
    /// <remarks>The first board is the mainboard, every other one is an expansion board connected over CAN</remarks>
    protected override Board CreateItem(int index) => (index == 0) ? new MainBoard() : new ExpansionBoard();
}
