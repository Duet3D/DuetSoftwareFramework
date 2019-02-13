namespace DuetAPI.Commands
{
    public abstract class BaseResponse : JsonObject
    {
        protected BaseResponse() { }

        public bool Success { get; protected set; }
    }

    public sealed class Response : BaseResponse
    {
        public object Data { get; set; }

        public Response(object data)
        {
            Success = true;
            Data = data;
        }
    }

    public sealed class EmptyResponse : BaseResponse
    {
        public EmptyResponse()
        {
            Success = true;
        }
    }

    public sealed class ErrorResponse : BaseResponse
    {
        public string ErrorType { get; private set; }
        public string ErrorMessage { get; private set; }

        public ErrorResponse(string type, string message)
        {
            Success = false;
            ErrorType = type;
            ErrorMessage = message;
        }
    }
}