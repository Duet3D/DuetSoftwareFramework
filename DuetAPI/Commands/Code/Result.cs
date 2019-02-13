using System.Collections.Generic;

namespace DuetAPI.Commands
{
    public class CodeResult : List<Message>
    {
        public Code Code { get; }

        public CodeResult(Code code)
        {
            Code = code;
        }
    }
}
