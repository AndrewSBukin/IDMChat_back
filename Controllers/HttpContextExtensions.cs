using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace IDMChat.Controllers
{

    public static class HubCallerContextExtensions
    {
        public static Guid GetUserId(this HubCallerContext context)
        {
            var userIdClaim = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdClaim, out var userId))
                throw new HubException("INVALID_USER_ID");

            return userId;
        }
    }
}
