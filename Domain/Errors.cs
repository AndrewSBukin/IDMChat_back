namespace IDMChat.Domain
{
    public class NotFoundException : Exception
    {
        public NotFoundException(string entityName, object id)
            : base($"{entityName} с id '{id}' не найден") { }

        public NotFoundException(string message) : base(message) { }
    }

    public class ForbiddenException : Exception
    {
        public ForbiddenException(string message) : base(message) { }
    }

    //public class ValidationException : Exception
    //{
    //    public ValidationException(string message) : base(message) { }
    //}

    public class UnauthorizedException : Exception
    {
        public UnauthorizedException(string message) : base(message) { }
    }

    public class RateLimitException : Exception
    {
        public int RetryAfterSeconds { get; }

        public RateLimitException(string message) : base(message) { }

        public RateLimitException(int retryAfterSeconds)
            : base($"Too many requests. Try again after {retryAfterSeconds} seconds")
        {
            RetryAfterSeconds = retryAfterSeconds;
        }

        public RateLimitException(string message, int retryAfterSeconds)
            : base(message)
        {
            RetryAfterSeconds = retryAfterSeconds;
        }
    }
}
