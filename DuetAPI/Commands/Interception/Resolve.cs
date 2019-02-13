namespace DuetAPI.Commands
{
    public class Resolve : EmptyResponseCommand
    {
        public MessageType Type { get; set; } = MessageType.Success;
        public string Content { get; set; } = "";
    }
}